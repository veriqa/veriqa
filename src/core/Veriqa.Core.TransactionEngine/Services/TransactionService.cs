// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Constants;
using Veriqa.Core.TransactionEngine.Diagnostics;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.RestrictionPolicies;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// Transaction management service implementation.
/// Coordinates creation, storage, state transitions and event publishing.
/// </summary>
internal sealed class TransactionService : ITransactionService
{
    /// <summary>
    /// Transaction store.
    /// </summary>
    private readonly ITransactionStore _store;

    /// <summary>
    /// Event publisher.
    /// </summary>
    private readonly ITransactionEventPublisher _eventPublisher;

    /// <summary>
    /// Transaction Engine configuration.
    /// </summary>
    private readonly IOptionsMonitor<TransactionEngineOptions> _options;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<TransactionService> _logger;

    /// <summary>
    /// Configuration resolver: reads protective settings across levels.
    /// </summary>
    private readonly IConfigurationResolver _configResolver;

    /// <summary>
    /// Clock behind every TTL, timestamp and event moment produced by the service.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Lifecycle metrics: the transaction entering the lifecycle and the terminal transitions this
    /// service is the sole writer of.
    /// </summary>
    private readonly TransactionMetrics _metrics;

    /// <summary>
    /// The expiry step this service shares with the cleanup pass: a decision that arrives after the
    /// deadline moves the transaction into Expired by it, instead of leaving that to the next pass.
    /// </summary>
    private readonly TransactionExpiry _expiry;

    /// <summary>
    /// Identity-match axis of the engine: the verdict written where the resolved identity is, and the
    /// expectations term of the idempotent-repeat comparison (SPEC-039 R40).
    /// </summary>
    private readonly IdentityMatchService _identityMatch;

    /// <summary>
    /// Source of the per-operation scopes the claims completion resolves its collaborators from — the
    /// planner, the step starters and the identity resolution — none of which this singleton holds: a
    /// host may register any of them with a narrower lifetime.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Evaluates the resolved restriction policies against the claims of the sender. Stateless; the
    /// admissibility gate is its only caller.
    /// </summary>
    private readonly IRestrictionPolicyEvaluator _restrictionPolicyEvaluator;

    /// <summary>
    /// Admissibility checks the deployment registered, in the order of their registration. Empty in
    /// the shipped composition — the core registers none of its own.
    /// </summary>
    private readonly IChannelAdmissibilityCheck[] _admissibilityChecks;

    /// <summary>
    /// Maximum ClientContext size in bytes (4 KB).
    /// </summary>
    private const int MaxClientContextSize = 4096;

    /// <summary>
    /// Maximum IdempotencyKey length.
    /// </summary>
    private const int MaxIdempotencyKeyLength = 128;

    /// <summary>
    /// Maximum snapshot data size in bytes (128 KB).
    /// Applies to ConfirmationSnapshot, ChannelIdentitySnapshot, ResolvedIdentitySnapshot.
    /// </summary>
    private const int MaxSnapshotSize = 128 * 1024;

    /// <summary>
    /// Upper bound of read-check-write attempts of a one-time issuance mark — the identity token mark
    /// and the authorization code mark alike. On a Completed transaction a lost lock is settled by the
    /// next read, so the bound only keeps a store whose update never applies from turning the method
    /// into a spin.
    /// </summary>
    private const int MaxIdentityTokenMarkAttempts = 3;

    /// <summary>
    /// What the caller is told when the transaction store fails. Deliberately free of any provider
    /// detail: the message travels outward with the Result, while the exception stays in the log.
    /// </summary>
    private const string StoreUnavailableMessage = "The transaction store is unavailable.";

    /// <summary>
    /// What the caller is told when a restriction policy or an external admissibility check refuses
    /// the sender. One wording for every such refusal, free of the rule, the field and the level that
    /// refused (RP-006): the message travels outward with the Result, the reason stays in the audit
    /// record and in the log.
    /// </summary>
    private const string SenderNotAdmittedMessage = "The sender is not admitted for this transaction.";

    /// <summary>
    /// Creates a transaction service instance.
    /// </summary>
    public TransactionService(
        ITransactionStore store,
        ITransactionEventPublisher eventPublisher,
        IOptionsMonitor<TransactionEngineOptions> options,
        ILogger<TransactionService> logger,
        IConfigurationResolver configResolver,
        TimeProvider timeProvider,
        TransactionMetrics metrics,
        IdentityMatchService identityMatch,
        IServiceScopeFactory scopeFactory,
        IRestrictionPolicyEvaluator restrictionPolicyEvaluator,
        IEnumerable<IChannelAdmissibilityCheck> admissibilityChecks)
    {
        _store = store;
        _eventPublisher = eventPublisher;
        _options = options;
        _logger = logger;
        _configResolver = configResolver;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _expiry = new TransactionExpiry(store, eventPublisher, timeProvider, metrics);
        _identityMatch = identityMatch;
        _scopeFactory = scopeFactory;
        _restrictionPolicyEvaluator = restrictionPolicyEvaluator;
        _admissibilityChecks = [.. admissibilityChecks];
    }

    /// <inheritdoc />
    public async Task<Result<Transaction>> CreateTransactionAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Root span of the sign-in trace. No null branching below: a host that subscribed to nothing
        // gets null here, and `activity?.` keeps every tag a no-op instead of a special case.
        using var activity = TransactionActivitySource.Source.StartActivity(TransactionTelemetry.CreateActivityName);
        activity?.SetTag(TransactionTelemetry.TransactionTypeTag, request.Type);

        // Every refusal of this method leaves the span through one funnel, so a creation that failed
        // is visible as an errored span with its code instead of a silently successful one.
        Result<Transaction> Fail(
            string code,
            string message,
            TransactionErrorCategory category = TransactionErrorCategory.BusinessRule)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(ChannelTelemetry.ErrorCodeTag, code);

            return Result<Transaction>.Failure(code, message, category);
        }

        // The same funnel for a storage failure: one place decides what the caller is told about a
        // dependency that did not answer, and the details stay in the log rather than in a Result
        // that travels outward.
        Result<Transaction> FailStoreUnavailable(Exception exception)
        {
            _logger.LogError(exception, "Transaction store call failed while creating a transaction");

            return Fail(
                TransactionErrorCodes.StoreUnavailable,
                StoreUnavailableMessage,
                TransactionErrorCategory.Infrastructure);
        }

        // Validate the transaction type
        if (!TransactionTypes.IsValid(request.Type))
        {
            return Fail(
                TransactionErrorCodes.InvalidTransactionType,
                $"Unknown transaction type: '{request.Type}'");
        }

        // A preferred channel is a choice INSIDE the allowed set (SPEC-001 §10.4), and a request that
        // names one outside a stated set contradicts itself. Accepting it would create a transaction
        // whose only offered way in leads to a channel whose answer this very service refuses later
        // (channel_not_allowed on every write of channel data, §10.1) — a dead end silent until the
        // TTL. It is refused here instead, at the acceptance, with the code that already names this
        // fact. An empty (or absent) allowed set states no restriction and restricts nothing.
        if (!string.IsNullOrWhiteSpace(request.RequestedChannelType)
            && request.AllowedChannelTypes is { Count: > 0 }
            && !request.AllowedChannelTypes.Contains(request.RequestedChannelType, StringComparer.Ordinal))
        {
            return Fail(
                TransactionErrorCodes.ChannelNotAllowed,
                $"Channel '{request.RequestedChannelType}' is not among the allowed channel types of this transaction");
        }

        // Validate the IdempotencyKey
        if (request.IdempotencyKey is not null && request.IdempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return Fail(
                TransactionErrorCodes.InvalidIdempotencyKey,
                $"IdempotencyKey length exceeds the maximum ({MaxIdempotencyKeyLength} characters)");
        }

        // Validate that IdempotencyKey and IdempotencyScope are paired: both set or both absent
        var hasKey = request.IdempotencyKey is not null;
        var hasScope = request.IdempotencyScope is not null;
        if (hasKey != hasScope)
        {
            return Fail(
                TransactionErrorCodes.IdempotencyParamsMismatch,
                "IdempotencyKey and IdempotencyScope must be set as a pair (both or neither)");
        }

        // Validate the ClientContext (maximum 4 KB in UTF-8)
        string? clientContextJson = null;
        if (request.ClientContext is not null)
        {
            clientContextJson = request.ClientContext;
            var byteCount = Encoding.UTF8.GetByteCount(clientContextJson);
            if (byteCount > MaxClientContextSize)
            {
                return Fail(
                    TransactionErrorCodes.ClientContextTooLarge,
                    $"ClientContext size exceeds the limit ({MaxClientContextSize} bytes)");
            }
        }

        // Freeze ConfirmationSnapshot.SlotValues to guarantee immutability —
        // before the size validation, so the same instance is measured and stored
        var confirmationSnapshot = request.ConfirmationSnapshot;
        if (confirmationSnapshot?.SlotValues is not null)
        {
            confirmationSnapshot = new ConfirmationSnapshot
            {
                ActionType = confirmationSnapshot.ActionType,
                SlotValues = confirmationSnapshot.SlotValues.ToFrozenDictionary(StringComparer.Ordinal)
            };
        }

        // Freeze IdentityMatchState.Expectations to guarantee immutability — the store shares the
        // container by reference (InMemoryTransactionStore.CloneTransaction) and relies on its
        // collections being already immutable, exactly as for ConfirmationSnapshot.SlotValues
        var identityMatch = request.IdentityMatch;
        if (identityMatch?.Expectations is not null)
        {
            identityMatch = new IdentityMatchState
            {
                Expectations = identityMatch.Expectations.ToFrozenDictionary(StringComparer.Ordinal),
                MatchedType = identityMatch.MatchedType,
                IdentityTokenIssuedAt = identityMatch.IdentityTokenIssuedAt
            };
        }

        // Validate the ConfirmationSnapshot size (maximum 128 KB).
        // The serialization is memoized per instance and reused by the store on
        // persist (TransactionEntityMapper.SerializeImmutableSnapshot) — exactly
        // the JSON that will be written is measured, without re-serialization.
        if (confirmationSnapshot is not null)
        {
            var snapshotJson = TransactionEntityMapper.SerializeImmutableSnapshot(confirmationSnapshot);
            if (Encoding.UTF8.GetByteCount(snapshotJson) > MaxSnapshotSize)
            {
                return Fail(
                    TransactionErrorCodes.SnapshotTooLarge,
                    $"ConfirmationSnapshot size exceeds the limit ({MaxSnapshotSize} bytes)");
            }
        }

        // Validate the InitiatorContextSnapshot size (shared 128 KB snapshot limit, ICC-031) —
        // with the same memoized JSON that will go to the store
        if (request.InitiatorContext is not null)
        {
            var initiatorContextJson = TransactionEntityMapper.SerializeImmutableSnapshot(request.InitiatorContext);
            if (Encoding.UTF8.GetByteCount(initiatorContextJson) > MaxSnapshotSize)
            {
                return Fail(
                    TransactionErrorCodes.SnapshotTooLarge,
                    $"InitiatorContextSnapshot size exceeds the limit ({MaxSnapshotSize} bytes)");
            }
        }

        // Check idempotency
        if (request.IdempotencyKey is not null && request.IdempotencyScope is not null)
        {
            var lookup = await CallStoreAsync(() => _store.GetByIdempotencyKeyAsync(
                request.IdempotencyScope, request.IdempotencyKey, cancellationToken));

            if (lookup.IsFailure)
            {
                return Fail(lookup.Error.Code, lookup.Error.Message, lookup.Error.Category);
            }

            var existing = lookup.Value;

            if (existing is not null)
            {
                // Compare the immutable creation parameters. If the key is reused with different
                // parameters — conflict (SPEC §6, §9.1).
                if (!CreationParametersMatch(existing, request))
                {
                    _logger.LogWarning(
                        "IdempotencyKey conflict — creation parameters do not match. TransactionId: {TransactionId}, ExistingType: {ExistingType}, RequestType: {RequestType}",
                        existing.Id.ToString(),
                        existing.Type,
                        request.Type);

                    return Fail(
                        TransactionErrorCodes.IdempotencyKeyConflict,
                        "IdempotencyKey has already been used with different creation parameters");
                }

                _logger.LogInformation(
                    "Existing transaction found by IdempotencyKey. TransactionId: {TransactionId}",
                    existing.Id.ToString());

                return Result<Transaction>.Success(existing);
            }
        }

        // Compute the TTL
        var engineOptions = _options.CurrentValue;
        var ttlSeconds = request.TtlSeconds ?? engineOptions.TransactionTtlSeconds;
        ttlSeconds = Math.Clamp(
            ttlSeconds,
            TransactionEngineOptions.MinTransactionTtlSeconds,
            TransactionEngineOptions.MaxTransactionTtlSeconds);

        var now = _timeProvider.GetUtcNow();

        // Determine the allowed channels (an immutable collection to prevent external mutation)
        var allowedChannels = request.AllowedChannelTypes is { Count: > 0 }
            ? request.AllowedChannelTypes.ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;

        // Create the transaction
        var transaction = new Transaction
        {
            Id = TransactionId.NewId(),
            Type = request.Type,
            State = TransactionState.Created,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddSeconds(ttlSeconds),
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            IdempotencyScope = request.IdempotencyScope,
            RequestedChannelType = request.RequestedChannelType,
            AllowedChannelTypes = allowedChannels,
            ClientContext = clientContextJson,
            ConfirmationSnapshot = confirmationSnapshot,
            OidcContext = request.OidcContext,
            RequestContext = request.RequestContext,
            IdentityMatch = identityMatch,
            InitiatorContextSnapshot = request.InitiatorContext,
            ClaimRequirements = Transaction.FreezeClaimRequirements(request.ClaimRequirements)
        };

        // The identifier of the operation the trace follows is the point of a trace, and it is the
        // very value the log records of this method already carry. Metric tags stay free of it.
        activity?.SetTag(TransactionTelemetry.TransactionIdTag, transaction.Id.ToString());

        // Save the transaction.
        // StoreCapacityExceededException is converted into Result.Failure so that
        // the calling code gets a controlled business error instead of a 500.
        // DuplicateIdempotencyKeyException arises on a race condition:
        // two requests with the same key arrived simultaneously — look up by key again
        // and return the transaction successfully saved by the first thread.
        try
        {
            await _store.AddAsync(transaction, cancellationToken);
        }
        catch (StoreCapacityExceededException ex)
        {
            _logger.LogError(
                ex,
                "Transaction store is full. Limit: {MaxEntries}",
                ex.MaxEntries);

            return Fail(
                TransactionErrorCodes.StoreCapacityExceeded,
                ex.Message);
        }
        catch (DuplicateIdempotencyKeyException ex)
        {
            _logger.LogInformation(
                ex,
                "Idempotency race while creating the transaction. Returning the existing one. Scope: {Scope}",
                ex.Scope);

            var lookup = await CallStoreAsync(() => _store.GetByIdempotencyKeyAsync(
                ex.Scope,
                ex.Key,
                cancellationToken));

            if (lookup.IsFailure)
            {
                return Fail(lookup.Error.Code, lookup.Error.Message, lookup.Error.Category);
            }

            var existing = lookup.Value;

            if (existing is not null)
            {
                return Result<Transaction>.Success(existing);
            }

            // Extremely unlikely case: the transaction has already expired while
            // we were waiting. Return a concurrency error.
            return Fail(
                TransactionErrorCodes.ConcurrencyConflict,
                "Idempotency conflict: the concurrent transaction is no longer available");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Everything the engine does NOT define itself: a provider failure of the store
            // (a database, a cache) reaches here as its own exception type, which this assembly
            // neither sees nor is entitled to name. It must not leave a method that declares
            // Result<Transaction> — the caller would get a 500 where the contract promises a
            // verdict. Cancellation is excluded above: it is a shutdown, not a failure.
            return FailStoreUnavailable(ex);
        }

        _logger.LogInformation(
            "Transaction created. TransactionId: {TransactionId}, Type: {TransactionType}, ExpiresAt: {ExpiresAt}",
            transaction.Id.ToString(),
            transaction.Type,
            transaction.ExpiresAt);

        // Publish the creation event
        await _eventPublisher.PublishAsync(
            new TransactionCreatedEvent
            {
                TransactionId = transaction.Id,
                OccurredAt = _timeProvider.GetUtcNow(),
                Type = transaction.Type,
                ExpiresAt = transaction.ExpiresAt,
                Context = TransactionEventContext.FromTransaction(transaction)
            },
            cancellationToken);

        // Activate the transaction (Created → Pending)
        var previousToken = transaction.ConcurrencyToken;
        var activationResult = TransactionStateMachine.TryTransition(transaction, TransactionState.Pending, _timeProvider.GetUtcNow());
        if (activationResult.IsFailure)
        {
            // The state machine already built the error, so the refusal is not rebuilt — only marked
            // on the span, the way the funnel above marks the ones this method builds itself.
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(ChannelTelemetry.ErrorCodeTag, activationResult.Error.Code);

            return activationResult;
        }

        // Update the store after activation, checking the previous token
        var activation = await CallStoreAsync(
            () => _store.UpdateAsync(transaction, previousToken, cancellationToken));
        if (activation.IsFailure)
        {
            return Fail(activation.Error.Code, activation.Error.Message, activation.Error.Category);
        }

        if (!activation.Value)
        {
            return Fail(
                TransactionErrorCodes.ConcurrencyConflict,
                "Concurrency conflict while activating the transaction");
        }

        _logger.LogInformation(
            "Transaction activated. TransactionId: {TransactionId}, State: {TransactionState}",
            transaction.Id.ToString(),
            transaction.State);

        // Counted here and not on entry: the idempotent branches above return an already existing
        // transaction, and counting them would report one sign-in attempt as several.
        _metrics.RecordInitiated(transaction.Type);

        // Publish the activation event
        await _eventPublisher.PublishAsync(
            new TransactionActivatedEvent
            {
                TransactionId = transaction.Id,
                OccurredAt = _timeProvider.GetUtcNow(),
                Context = TransactionEventContext.FromTransaction(transaction)
            },
            cancellationToken);

        return Result<Transaction>.Success(transaction);
    }

    /// <summary>
    /// Decides whether a repeat under an already used idempotency key describes the SAME creation.
    /// </summary>
    /// <remarks>
    /// The terms are the creation parameters that decide what is being confirmed and how it is
    /// presented: the type and the allowed channels, the snapshot (the action type and the caller
    /// values), the request-context container (the language and the <c>ui_config</c> record) and the
    /// expectations about who confirms. A repeat that changed any of them is not a retry of the first
    /// request but a different request wearing its key, and returning the first transaction would hand
    /// the caller an operation about something else (SPEC-039 E30).
    /// <para>
    /// Values are compared as strings, ordinally; a map matches when its keys and values match; and a
    /// value absent on both sides is a match. The two containers are read directly rather than through
    /// the accessor that merges them, and deliberately: what is compared here is what a REQUEST
    /// states, and the OIDC container is not among the terms — the sign-in path states no idempotency
    /// key at all, and pulling its language into the comparison would add a term nobody asked for.
    /// </para>
    /// <para>
    /// The expectations of the relying party about who confirms are a term of this same list, and the
    /// only one that cannot be compared in the form it is stored in: the protection of a value is
    /// randomized, so two protections of one and the same value differ. Both sides are restored
    /// through the protection port and the restored values are compared, which is what keeps an
    /// honest retry from reading as a conflict.
    /// </para>
    /// <para>
    /// That same term is also the only one a transaction STOPS carrying: the transition to a terminal
    /// state drops the expectations and keeps the verdict (SPEC-039 C23, L41). A repeat that finds a
    /// terminal transaction therefore has nothing left to compare against, and holding the emptied
    /// side against a stated one would turn every honest repeat after the outcome into a conflict —
    /// while the repeat is owed the same transaction. The term is dropped exactly where its evidence
    /// is: on a terminal transaction, and only there. Every other term stays comparable, so a repeat
    /// that changed the action, the values, the language, the time zone or the record is still a
    /// conflict.
    /// </para>
    /// </remarks>
    /// <param name="existing">Transaction found by the idempotency key.</param>
    /// <param name="request">Creation request of the repeat.</param>
    /// <returns><see langword="true"/> when the parameters describe the same creation.</returns>
    private bool CreationParametersMatch(Transaction existing, CreateTransactionRequest request)
    {
        if (!string.Equals(existing.Type, request.Type, StringComparison.Ordinal))
        {
            return false;
        }

        var requestedChannels = request.AllowedChannelTypes is { Count: > 0 }
            ? request.AllowedChannelTypes
            : Array.Empty<string>();
        var existingChannels = existing.AllowedChannelTypes ?? FrozenSet<string>.Empty;

        if (!existingChannels.SetEquals(requestedChannels))
        {
            return false;
        }

        if (!SameText(existing.ConfirmationSnapshot?.ActionType, request.ConfirmationSnapshot?.ActionType)
            || !SameEntries(existing.ConfirmationSnapshot?.SlotValues, request.ConfirmationSnapshot?.SlotValues))
        {
            return false;
        }

        if (!SameText(existing.RequestContext?.UiLocale, request.RequestContext?.UiLocale)
            || !SameText(existing.RequestContext?.UiTimeZone, request.RequestContext?.UiTimeZone)
            || !SameText(existing.RequestContext?.UiConfigCode, request.RequestContext?.UiConfigCode))
        {
            return false;
        }

        // A terminal transaction no longer carries the expectations — the transition erased them
        // together with writing the verdict. There is nothing left to hold the repeat against, and
        // an emptied side compared with a stated one would answer a conflict to a repeat that
        // changed nothing. Every other term above has already matched, so the repeat gets the same
        // transaction, as the semantics of a repeat after the outcome require.
        if (existing.IsTerminal())
        {
            return true;
        }

        // The expectations of the relying party are a creation parameter like any other, and they are
        // the one term that cannot be compared as it is stored: the protection is randomized, so two
        // protections of the same value differ. The axis restores both sides and compares those.
        return _identityMatch.ExpectationsMatch(
            existing.IdentityMatch?.Expectations,
            request.IdentityMatch?.Expectations);
    }

    /// <summary>
    /// Ordinal equality of two optional values, where "absent on both sides" is equality.
    /// </summary>
    /// <param name="left">First value.</param>
    /// <param name="right">Second value.</param>
    /// <returns><see langword="true"/> when the two are the same value.</returns>
    private static bool SameText(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// Equality of two optional maps: same set of keys, same value under each. An absent map and an
    /// empty one are the same thing here — neither states a value.
    /// </summary>
    /// <param name="left">First map.</param>
    /// <param name="right">Second map.</param>
    /// <returns><see langword="true"/> when the two state the same entries.</returns>
    private static bool SameEntries(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;

        if (leftCount != rightCount)
        {
            return false;
        }

        if (leftCount is 0)
        {
            return true;
        }

        foreach (var entry in left!)
        {
            if (!right!.TryGetValue(entry.Key, out var value)
                || !string.Equals(entry.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<Result<Transaction>> GetTransactionAsync(
        TransactionId id,
        CancellationToken cancellationToken = default)
    {
        // Get the transaction from the store
        var transaction = await _store.GetByIdAsync(id, cancellationToken);

        if (transaction is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        // A transaction whose deadline has passed must not be handed back as a live one — but only
        // where the deadline is what decides its fate. Expiry ends a transaction out of Created and
        // Pending and out of nothing else (SPEC-001 §4.4 item 4), so a confirmed one awaiting
        // finalization is returned with the state it is actually in: its wait ends on the
        // finalization timeout, and the sweep that enforces it writes Failed, never Expired — how
        // long the wait can run is therefore a fact about the store this deployment runs, not about
        // the engine (ITransactionStore.GetStalledConfirmedAsync names the store that ends it
        // never). Asking "not terminal" instead would answer "expired" about a transaction the
        // state machine cannot expire, and every reader of this method would carry that answer —
        // the status surface among them, which maps any refusal to "no such transaction".
        if (transaction.IsSubjectToExpiry() && transaction.IsExpired(_timeProvider.GetUtcNow()))
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionExpired,
                "Transaction expired by TTL");
        }

        return Result<Transaction>.Success(transaction);
    }

    /// <inheritdoc />
    public async Task<Result<Transaction>> ConfirmTransactionAsync(
        TransactionId id,
        ChannelIdentitySnapshot channelIdentity,
        string concurrencyToken,
        CancellationToken cancellationToken = default)
    {
        // Get the transaction
        var transaction = await _store.GetByIdAsync(id, cancellationToken);
        if (transaction is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        // Idempotency: if already Confirmed or Completed — return the current state.
        // Checked before the TTL, so idempotency is not lost for already completed transactions.
        // The repeat is idempotent only for the subject of the snapshot the transaction was confirmed
        // with; any other sender is refused as on a Pending transaction bound to someone else.
        if (transaction.State is TransactionState.Confirmed or TransactionState.Completed)
        {
            if (transaction.ChannelIdentitySnapshot is { } confirmedWith
                && !ChannelSubjectBinding.Of(confirmedWith, answerBrowserBound: false).IsSameSubject(channelIdentity))
            {
                return SubjectMismatch();
            }

            _logger.LogInformation(
                "Repeated confirmation — the transaction is already in state {TransactionState}. TransactionId: {TransactionId}",
                transaction.State,
                transaction.Id.ToString());

            return Result<Transaction>.Success(transaction);
        }

        // Check the TTL (only for non-terminal, not yet confirmed transactions) — asked in the ONE
        // reading of a closed window that everything answering about one uses, and not as a bare
        // deadline. A bare deadline says "expired" about a transaction a decision already ended: the
        // sender who declined and then pressed the other button would be told their sign-in ran out
        // of time instead of what they themselves decided, while the same press on the refusal path
        // (which has no deadline gate at all) is answered with that decision. The refusal below is
        // what the caller branches on, so the two buttons have to reach it by the same reading.
        // A transaction still Pending past its deadline is moved into Expired on the spot (SPEC-001
        // §8.3): the decision is the moment the engine learns of the expiry, and the next cleanup pass
        // would only learn of it later.
        if (transaction.HasRunOutOfTime(_timeProvider.GetUtcNow()))
        {
            await ExpireAtLateDecisionAsync(transaction);

            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionExpired,
                "Transaction expired by TTL");
        }

        // The subject binding (SPEC-039 N34) — in the same place in the order the attachment checks it,
        // and before the claims completion below, whose planner may already write.
        if (transaction.IsBoundToAnotherSubject(channelIdentity))
        {
            return SubjectMismatch();
        }

        // Check that the sender may act on this transaction at all (allow-list, bot policy)
        var admissibility = await ValidateChannelAdmissibilityAsync(
            transaction, channelIdentity, cancellationToken);
        if (admissibility.IsFailure)
        {
            return Result<Transaction>.Failure(admissibility.Error);
        }

        // Validate the ChannelIdentitySnapshot size (maximum 128 KB).
        // The serialization is memoized per instance and reused by the store on
        // persist (TransactionEntityMapper.SerializeImmutableSnapshot) — exactly
        // the JSON that will be written is measured, without re-serialization.
        // The instance already carries frozen AdditionalClaims: the snapshot freezes them in its
        // init accessor, so an unfrozen state cannot reach the cache no matter who constructed it —
        // the order "frozen → memoized → validated → stored" holds by construction.
        var channelSnapshotJson = TransactionEntityMapper.SerializeImmutableSnapshot(channelIdentity);
        if (Encoding.UTF8.GetByteCount(channelSnapshotJson) > MaxSnapshotSize)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.SnapshotTooLarge,
                $"ChannelIdentitySnapshot size exceeds the limit ({MaxSnapshotSize} bytes)");
        }

        // Check the ConcurrencyToken
        if (!string.Equals(transaction.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.ConcurrencyConflict,
                "Concurrency conflict — ConcurrencyToken does not match");
        }

        // Claims completion (SPEC-041 L91, C90) — decided after the token check, over the transaction as
        // it is stored. The snapshot the transaction is confirmed with is the passed one, except once the
        // completion mark is set: the mark froze the snapshot of the transaction.
        var confirmingIdentity = channelIdentity;

        if (transaction.IsCompletingClaims())
        {
            // The one decision "Confirm while the mark is set" belongs to the planner: a missing claim
            // still open refuses the confirmation and changes nothing. When the planner took vanished stored
            // values off the transaction (SPEC-041 E86), it wrote this very instance, so the token the
            // confirmation below checks against is the one of that write, never the one read above. A write
            // of the planner that lost the token is the race the token check above answers a moment
            // earlier, and is answered the same way — so a refusal "missing claims" after this check always
            // means the write, if any, was this confirmation's own.
            var check = await AskPlannerAsync(planner => planner.IsCompleteAsync(transaction, cancellationToken));

            if (check is ClaimCompletionCheck.Conflict)
            {
                return Result<Transaction>.Failure(
                    TransactionErrorCodes.ConcurrencyConflict,
                    "Concurrency conflict while updating the store");
            }

            if (check is not ClaimCompletionCheck.Complete)
            {
                return Result<Transaction>.Failure(
                    TransactionErrorCodes.ClaimCompletionInProgress,
                    "The transaction waits for missing claims; its channel snapshot is frozen");
            }

            confirmingIdentity = transaction.ChannelIdentitySnapshot ?? channelIdentity;
        }
        else if (transaction.State is TransactionState.Pending && transaction.ClaimRequirements.Count > 0)
        {
            var plan = await AskPlannerAsync(
                planner => planner.ComputeMissingAsync(transaction, channelIdentity, cancellationToken));

            if (plan.UnobtainableClaim is { } unobtainableClaim)
            {
                return await FailUnobtainableClaimAsync(transaction, unobtainableClaim, channelIdentity, cancellationToken);
            }

            if (plan.Items.Count > 0)
            {
                return await EnterClaimCompletionAsync(transaction, channelIdentity, plan, cancellationToken);
            }

            // Nothing is missing: the values taken from the collected claims store (SPEC-041 C37) are written
            // with the confirmation itself.
            transaction.StoredClaimValues = plan.StoredValues;
        }

        // Whether this write is the one that gives the sign-in its channel subject — read before the
        // write, because after it the answer is always "bound".
        var firstBinding = SignInSubjectScope.Of(transaction) is null;

        // Save the current token for the update check
        var previousToken = transaction.ConcurrencyToken;

        // Perform the Pending → Confirmed transition
        var transitionResult = TransactionStateMachine.TryTransition(transaction, TransactionState.Confirmed, _timeProvider.GetUtcNow());
        if (transitionResult.IsFailure)
        {
            return transitionResult;
        }

        // Store the channel data — the same instance whose JSON was measured above, or the frozen
        // snapshot of the transaction when the confirmation ends the claims completion
        transaction.ChannelIdentitySnapshot = confirmingIdentity;

        // Update the store
        var updated = await _store.UpdateAsync(transaction, previousToken, cancellationToken);
        if (!updated)
        {
            return await RefuseLostWriteAsync(id, channelIdentity, cancellationToken);
        }

        _logger.LogInformation(
            "Transaction confirmed. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
            transaction.Id.ToString(),
            confirmingIdentity.ChannelType);

        // Publish the confirmation event
        await _eventPublisher.PublishAsync(
            new TransactionConfirmedEvent
            {
                TransactionId = transaction.Id,
                OccurredAt = _timeProvider.GetUtcNow(),
                ChannelType = confirmingIdentity.ChannelType,
                Context = TransactionEventContext.FromTransaction(transaction)
            },
            cancellationToken);

        // After the event. CancellationToken.None — the closing follows a committed write and is not
        // abandoned with the request.
        if (firstBinding)
        {
            await SupersedeEarlierSignInsAsync(transaction, CancellationToken.None);
        }

        return Result<Transaction>.Success(transaction);
    }

    /// <inheritdoc />
    public async Task<Result<Transaction>> AttachChannelIdentityAsync(
        TransactionId id,
        ChannelIdentitySnapshot identity,
        string concurrencyToken,
        bool bindAnswerToBrowser = false,
        CancellationToken cancellationToken = default)
    {
        // Get the transaction
        var transaction = await _store.GetByIdAsync(id, cancellationToken);
        if (transaction is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        // The operation is defined only while the transaction is still awaiting an answer. Any other
        // state (already confirmed, declined, or promoted to Expired by the cleanup pass) is a late
        // delivery of the channel event and must not silently rewrite the identity of a decided
        // transaction.
        if (transaction.State is not TransactionState.Pending)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.InvalidStateTransition,
                $"Channel identity can only be attached to a Pending transaction; current state: {transaction.State}");
        }

        // The TTL is a second gate, because the state alone does not close it: a transaction that ran
        // out of time stays Pending until the cleanup pass or a late decision promotes it, and on every
        // store that window is observable. Late delivery is late on either side of that promotion —
        // the answer this identity would sign can no longer be given — so the deadline is checked
        // here, in the same place in the order the confirmation path checks it. It is read directly,
        // and not through Transaction.HasRunOutOfTime, because the state gate above has already
        // narrowed this call to Pending, and on Pending the two readings reduce to the same deadline
        // check in today's delivery: Pending is expirable by the transition table, so the wider
        // reading adds nothing.
        if (transaction.IsExpired(_timeProvider.GetUtcNow()))
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionExpired,
                "Transaction expired by TTL");
        }

        // The subject binding (SPEC-039 N34): the snapshot of a transaction bound to someone else is not
        // replaced by this sender's — the web answer would otherwise sign in whoever attached last.
        if (transaction.IsBoundToAnotherSubject(identity))
        {
            return SubjectMismatch();
        }

        // Check that the sender may act on this transaction at all (allow-list, bot policy). The same
        // gate the confirmation applies: the attached identity is what the web answer later signs in,
        // so a sender rejected at confirmation time must not get that far in the first place.
        var admissibility = await ValidateChannelAdmissibilityAsync(
            transaction, identity, cancellationToken);
        if (admissibility.IsFailure)
        {
            return Result<Transaction>.Failure(admissibility.Error);
        }

        // Validate the ChannelIdentitySnapshot size — the same 128 KB limit the confirmation applies,
        // measured on the memoized JSON that the store will persist.
        var channelSnapshotJson = TransactionEntityMapper.SerializeImmutableSnapshot(identity);
        if (Encoding.UTF8.GetByteCount(channelSnapshotJson) > MaxSnapshotSize)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.SnapshotTooLarge,
                $"ChannelIdentitySnapshot size exceeds the limit ({MaxSnapshotSize} bytes)");
        }

        // Check the ConcurrencyToken
        if (!string.Equals(transaction.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.ConcurrencyConflict,
                "Concurrency conflict — ConcurrencyToken does not match");
        }

        // The claims completion mark froze the channel snapshot (SPEC-041 L91): the user has already
        // answered, and a later delivery must not replace the identity the completion signs in.
        if (transaction.ClaimCompletion is not null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.ClaimCompletionInProgress,
                "The transaction waits for missing claims; its channel snapshot is frozen");
        }

        // Whether this write is the one that gives the sign-in its channel subject — read before the
        // write, because after it the answer is always "bound".
        var firstBinding = SignInSubjectScope.Of(transaction) is null;

        // Save the current token for the update check
        var previousToken = transaction.ConcurrencyToken;

        // No state transition here: the transaction keeps waiting. Only the snapshot, the binding of its
        // subject and the concurrency metadata move, so the metadata is refreshed the same way a
        // transition would. A browser-bound answer, once set, is never cleared by a later delivery.
        var answerBrowserBound = bindAnswerToBrowser || transaction.SubjectBinding?.AnswerBrowserBound == true;
        transaction.ChannelIdentitySnapshot = identity;
        transaction.SubjectBinding = ChannelSubjectBinding.Of(identity, answerBrowserBound);
        transaction.UpdatedAt = _timeProvider.GetUtcNow();
        transaction.ConcurrencyToken = Guid.NewGuid().ToString("N");

        // Update the store
        var updated = await _store.UpdateAsync(transaction, previousToken, cancellationToken);
        if (!updated)
        {
            return await RefuseLostWriteAsync(id, identity, cancellationToken);
        }

        _logger.LogInformation(
            "Channel identity attached to a pending transaction. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
            transaction.Id.ToString(),
            identity.ChannelType);

        // Publish the attachment event
        await _eventPublisher.PublishAsync(
            new TransactionChannelIdentityAttachedEvent
            {
                TransactionId = transaction.Id,
                OccurredAt = _timeProvider.GetUtcNow(),
                ChannelType = identity.ChannelType,
                AnswerBrowserBound = answerBrowserBound,
                Context = TransactionEventContext.FromTransaction(transaction)
            },
            cancellationToken);

        // After the event. CancellationToken.None — the closing follows a committed write and is not
        // abandoned with the request.
        if (firstBinding)
        {
            await SupersedeEarlierSignInsAsync(transaction, CancellationToken.None);
        }

        return Result<Transaction>.Success(transaction);
    }

    /// <inheritdoc />
    public async Task<Result<Transaction>> BindChannelSubjectAsync(
        TransactionId id,
        ChannelIdentitySnapshot sender,
        CancellationToken cancellationToken = default)
    {
        // The binding is decided over the transaction as it is stored: a subject already bound decides
        // first, whatever the state, so another sender learns nothing else about the transaction.
        var transaction = await _store.GetByIdAsync(id, cancellationToken);
        if (transaction is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        if (transaction.IsBoundToAnotherSubject(sender))
        {
            return SubjectMismatch();
        }

        // Nothing to write: the sender is already the bound subject, or the transaction no longer waits
        // for a sender to enter — a later operation answers about its state, not this one.
        if (transaction.IsBoundTo(sender)
            || transaction.State is not TransactionState.Pending
            || transaction.IsCompletingClaims())
        {
            return Result<Transaction>.Success(transaction);
        }

        if (transaction.IsExpired(_timeProvider.GetUtcNow()))
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionExpired,
                "Transaction expired by TTL");
        }

        // A sender the transaction does not admit (allow-list, bot policy) binds nothing.
        var admissibility = await ValidateChannelAdmissibilityAsync(transaction, sender, cancellationToken);
        if (admissibility.IsFailure)
        {
            return Result<Transaction>.Failure(admissibility.Error);
        }

        // Whether this write is the one that gives the sign-in its channel subject — read before the
        // write, because after it the answer is always "bound".
        var firstBinding = SignInSubjectScope.Of(transaction) is null;

        // The state and the snapshot are left as they are: only the binding and the concurrency metadata
        // move, and the metadata is refreshed the way any write with the token refreshes it.
        var previousToken = transaction.ConcurrencyToken;
        transaction.SubjectBinding = ChannelSubjectBinding.Of(sender, answerBrowserBound: false);
        transaction.UpdatedAt = _timeProvider.GetUtcNow();
        transaction.ConcurrencyToken = Guid.NewGuid().ToString("N");

        var updated = await _store.UpdateAsync(transaction, previousToken, cancellationToken);
        if (!updated)
        {
            // Lost to a concurrent writer. When that writer bound this very subject (a repeated delivery
            // of the same entry), the sender is bound all the same and the stored transaction is the answer.
            var current = await _store.GetByIdAsync(id, cancellationToken);
            if (current is not null && current.IsBoundTo(sender))
            {
                return Result<Transaction>.Success(current);
            }

            return RefuseLostWrite(current, sender);
        }

        _logger.LogInformation(
            "Transaction bound to its channel subject. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
            transaction.Id.ToString(),
            sender.ChannelType);

        if (firstBinding)
        {
            await SupersedeEarlierSignInsAsync(transaction, CancellationToken.None);
        }

        return Result<Transaction>.Success(transaction);
    }

    /// <summary>
    /// Refusal of a sender other than the subject the transaction is bound to. It names no subject.
    /// </summary>
    /// <returns>The refusal.</returns>
    private static Result<Transaction> SubjectMismatch() => Result<Transaction>.Failure(
        TransactionErrorCodes.ChannelSubjectMismatch,
        "The transaction is bound to another channel subject");

    /// <summary>
    /// Answers a write of the sender's channel data that lost the concurrency token: when the winner
    /// bound the transaction to another subject, the sender is refused as that subject's rival rather
    /// than invited to retry — a retry would be refused anyway.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="sender">Channel snapshot of the sender whose write was lost.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subject mismatch refusal or the concurrency conflict.</returns>
    private async Task<Result<Transaction>> RefuseLostWriteAsync(
        TransactionId id,
        ChannelIdentitySnapshot sender,
        CancellationToken cancellationToken)
        => RefuseLostWrite(await _store.GetByIdAsync(id, cancellationToken), sender);

    /// <summary>
    /// Answers a lost write over the transaction as the winner left it.
    /// </summary>
    /// <param name="current">The transaction re-read after the lost write; null — it is gone.</param>
    /// <param name="sender">Channel snapshot of the sender whose write was lost.</param>
    /// <returns>The subject mismatch refusal or the concurrency conflict.</returns>
    private static Result<Transaction> RefuseLostWrite(Transaction? current, ChannelIdentitySnapshot sender)
        => current is not null && current.IsBoundToAnotherSubject(sender)
            ? SubjectMismatch()
            : Result<Transaction>.Failure(
                TransactionErrorCodes.ConcurrencyConflict,
                "Concurrency conflict while updating the store");

    /// <summary>
    /// Sets the claims completion mark instead of confirming (SPEC-041 C90): one write with the token
    /// stores the channel snapshot, the moment of the confirmation, the list of missing claims and the
    /// values taken from the collected claims store (C37), and the transaction stays Pending. Then the
    /// first step of the completion begins.
    /// </summary>
    /// <remarks>
    /// One write for both is what leaves no moment "snapshot without a mark" (L91), on both paths: in
    /// the channel the snapshot arrives with this very confirmation, on the web confirmation page it is
    /// already stored and "no mark → mark" rests on the same token. The confirmation event is not
    /// published — the transaction is not confirmed; the step signal takes its place, and the channel
    /// attachment event is not published either, since it would send the page to the callback.
    /// </remarks>
    /// <param name="transaction">Pending transaction without a mark, read by the confirmation.</param>
    /// <param name="channelIdentity">Channel snapshot of the confirmation, already measured.</param>
    /// <param name="plan">Plan of the completion: the missing claims, phone step first (not empty), and the
    /// stored values the transaction hands out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction in the claims completion (or already confirmed by the writer that
    /// finished it); a concurrency conflict; or the refusal of a transaction another writer ended during
    /// the start of the step.</returns>
    private async Task<Result<Transaction>> EnterClaimCompletionAsync(
        Transaction transaction,
        ChannelIdentitySnapshot channelIdentity,
        ClaimCompletionPlan plan,
        CancellationToken cancellationToken)
    {
        var items = plan.Items;
        var previousToken = transaction.ConcurrencyToken;
        var now = _timeProvider.GetUtcNow();

        // Whether this write is the one that gives the sign-in its channel subject — read before the
        // write, because after it the answer is always "bound".
        var firstBinding = SignInSubjectScope.Of(transaction) is null;

        // No state transition: only the snapshot, the mark and the concurrency metadata move, and the
        // metadata is refreshed the way any write with the token refreshes it.
        transaction.ChannelIdentitySnapshot = channelIdentity;
        transaction.ClaimCompletion = new ClaimCompletionState
        {
            ConfirmedAt = now,
            Items = items
        };
        transaction.StoredClaimValues = plan.StoredValues;
        transaction.UpdatedAt = now;
        transaction.ConcurrencyToken = Guid.NewGuid().ToString("N");

        var updated = await _store.UpdateAsync(transaction, previousToken, cancellationToken);
        if (!updated)
        {
            return await RefuseLostWriteAsync(transaction.Id, channelIdentity, cancellationToken);
        }

        // Claim names only: the values the completion collects are never logged (SPEC-041 N68).
        _logger.LogInformation(
            "Transaction entered the claims completion. TransactionId: {TransactionId}, ChannelType: {ChannelType}, MissingClaims: {MissingClaims}",
            transaction.Id.ToString(),
            channelIdentity.ChannelType,
            string.Join(',', items.Select(item => item.Claim)));

        if (firstBinding)
        {
            await SupersedeEarlierSignInsAsync(transaction, CancellationToken.None);
        }

        var current = await BeginClaimCompletionStepAsync(transaction, cancellationToken);

        // The record is re-read after the step starter, and another writer may have ended the
        // transaction in between (a cancellation, the sweep). Success tells the caller the transaction
        // is either still waiting for the claims or already confirmed by the writer that finished the
        // completion; a transaction ended otherwise is refused, or the finalizer would resolve and try
        // to complete a sign-in nobody confirmed.
        if (current.IsCompletingClaims()
            || current.State is TransactionState.Confirmed or TransactionState.Completed)
        {
            return Result<Transaction>.Success(current);
        }

        return current.State is TransactionState.Expired
            ? Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionExpired,
                "Transaction expired by TTL")
            : Result<Transaction>.Failure(
                TransactionErrorCodes.InvalidStateTransition,
                $"The transaction left the claims completion before it was confirmed; current state: {current.State}");
    }

    /// <summary>
    /// Fails a transaction whose required claim nothing can obtain (SPEC-041 L48): the channel did not
    /// give the phone number and cannot ask for it. No mark is set; the transaction goes to Failed
    /// through the one writer of that state, and the confirmation is refused with the same code.
    /// </summary>
    /// <param name="transaction">Pending transaction read by the confirmation.</param>
    /// <param name="claim">The unobtainable claim.</param>
    /// <param name="channelIdentity">Channel snapshot of the confirmation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refusal with <see cref="TransactionErrorCodes.RequiredClaimUnobtainable"/>, or the refusal of the failing write.</returns>
    private async Task<Result<Transaction>> FailUnobtainableClaimAsync(
        Transaction transaction,
        string claim,
        ChannelIdentitySnapshot channelIdentity,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "A required claim cannot be obtained in the channel of the transaction. TransactionId: {TransactionId}, Claim: {Claim}, ChannelType: {ChannelType}",
            transaction.Id.ToString(),
            claim,
            channelIdentity.ChannelType);

        var failed = await FailTransactionAsync(
            transaction.Id,
            TransactionErrorCodes.RequiredClaimUnobtainable,
            transaction.ConcurrencyToken,
            cancellationToken);

        if (failed.IsFailure)
        {
            return failed;
        }

        return Result<Transaction>.Failure(
            TransactionErrorCodes.RequiredClaimUnobtainable,
            "A required claim cannot be obtained in the channel of this transaction");
    }

    /// <inheritdoc />
    public Task<Result<Transaction>> RecordClaimValueAsync(
        TransactionId id,
        string claim,
        string value,
        bool verified,
        ClaimCollectionMethod method,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claim);
        ArgumentException.ThrowIfNullOrEmpty(value);

        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown claim collection method.");
        }

        return RecordClaimOutcomeAsync(id, claim, value, verified, method, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<Transaction>> SkipClaimAsync(
        TransactionId id,
        string claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claim);

        return RecordClaimOutcomeAsync(id, claim, value: null, verified: false, method: null, cancellationToken);
    }

    /// <summary>
    /// Records the outcome of a missing claim — a value, or a skip when <paramref name="value"/> is
    /// null — and runs the completion check.
    /// </summary>
    /// <remarks>
    /// The conditions are checked on the transaction as read, then the store writes conditionally on
    /// its own state, the mark and the recorded outcomes. "The claim belongs to the current step" is
    /// checked here only, on the transaction as read: the store does not check the step. A recorded outcome
    /// moves the step forward, so a concurrent outcome can only make the check refuse once more; stored
    /// values taken off (SPEC-041 E86) move it back, and an outcome read before that write is still
    /// recorded — its value only arrives earlier, and its writer does not begin the step it moved back to.
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="claim">Name of the claim.</param>
    /// <param name="value">Collected value; null — a skip.</param>
    /// <param name="verified">Whether the value was verified.</param>
    /// <param name="method">How the value was obtained; null — a skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction after the completion check, or the refusal.</returns>
    private async Task<Result<Transaction>> RecordClaimOutcomeAsync(
        TransactionId id,
        string claim,
        string? value,
        bool verified,
        ClaimCollectionMethod? method,
        CancellationToken cancellationToken)
    {
        var transaction = await _store.GetByIdAsync(id, cancellationToken);
        if (transaction is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        var now = _timeProvider.GetUtcNow();

        if (transaction.HasRunOutOfTime(now))
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionExpired,
                "Transaction expired by TTL");
        }

        var step = transaction.GetCurrentClaimCompletionStep();
        var item = transaction.ClaimCompletion?.Items
            .FirstOrDefault(candidate => VeriqaClaimTypes.NameComparer.Equals(candidate.Claim, claim));

        if (step is not { } currentStep
            || item is null
            || item.Step != currentStep
            || (value is null && item.Level is not ClaimRequirementLevel.Optional))
        {
            return RefuseClaimWrite(transaction, claim);
        }

        var result = new ClaimCompletionResult(item.Claim, value, verified, method, now);

        if (!await _store.TryRecordClaimResultAsync(id, result, cancellationToken))
        {
            return RefuseClaimWrite(transaction, claim);
        }

        _logger.LogInformation(
            "Claims completion outcome recorded. TransactionId: {TransactionId}, Claim: {Claim}, Skipped: {Skipped}, Method: {Method}",
            id.ToString(),
            item.Claim,
            value is null,
            method);

        return await CheckClaimCompletionAsync(id, currentStep, cancellationToken);
    }

    /// <summary>
    /// Refuses a write of a missing claim — the one refusal of the writes, whatever condition failed.
    /// </summary>
    /// <param name="transaction">Transaction the write was addressed to.</param>
    /// <param name="claim">Name of the claim.</param>
    /// <returns>The refusal with <see cref="TransactionErrorCodes.ClaimCompletionWriteRefused"/>.</returns>
    private Result<Transaction> RefuseClaimWrite(Transaction transaction, string claim)
    {
        _logger.LogInformation(
            "Claims completion write refused. TransactionId: {TransactionId}, Claim: {Claim}, State: {TransactionState}, Step: {Step}",
            transaction.Id.ToString(),
            claim,
            transaction.State,
            transaction.GetCurrentClaimCompletionStep());

        return Result<Transaction>.Failure(
            TransactionErrorCodes.ClaimCompletionWriteRefused,
            "The claim cannot be written: the transaction is not waiting for it");
    }

    /// <summary>
    /// The completion check after a recorded outcome (SPEC-041 L91): nothing missing any more — the
    /// transaction is confirmed with its own snapshot and finalized; the completion moved to another
    /// step — that step begins.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="stepBefore">Step the recorded outcome belonged to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction after the check.</returns>
    private async Task<Result<Transaction>> CheckClaimCompletionAsync(
        TransactionId id,
        ClaimCompletionStep stepBefore,
        CancellationToken cancellationToken)
    {
        var transaction = await _store.GetByIdAsync(id, cancellationToken);
        if (transaction is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        // Another writer already ended the completion (a concurrent last outcome, a cancellation):
        // nothing is left to decide here.
        if (!transaction.IsCompletingClaims())
        {
            return Result<Transaction>.Success(transaction);
        }

        // A write the check made (vanished stored values taken off, SPEC-041 E86) was made on this instance,
        // so the finalization presents the token of that write.
        var check = await AskPlannerAsync(planner => planner.IsCompleteAsync(transaction, cancellationToken));

        if (check is ClaimCompletionCheck.Complete)
        {
            return await FinalizeClaimCompletionAsync(transaction, stepBefore, cancellationToken);
        }

        // A take-off was written on this instance, so the step it reads now is the one that write moved to.
        var movedTo = check is ClaimCompletionCheck.ValuesTakenOff
            ? transaction.GetCurrentClaimCompletionStep()
            : null;

        return await ContinueClaimCompletionAsync(id, stepBefore, movedTo, cancellationToken);
    }

    /// <summary>
    /// Continues a claims completion that is not over: re-reads the transaction and begins its current step
    /// when the write of this writer moved the completion to it.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="stepBefore">Step the recorded outcome belonged to.</param>
    /// <param name="movedTo">Step the take-off of this writer moved the completion to (SPEC-041 E86); null —
    /// this writer took nothing off, or its take-off left the step where it was.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction as stored, after the start of a new step if one began.</returns>
    private async Task<Result<Transaction>> ContinueClaimCompletionAsync(
        TransactionId id,
        ClaimCompletionStep stepBefore,
        ClaimCompletionStep? movedTo,
        CancellationToken cancellationToken)
    {
        var current = await _store.GetByIdAsync(id, cancellationToken);
        if (current is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        return Result<Transaction>.Success(
            await BeginClaimCompletionStepIfMovedAsync(current, stepBefore, movedTo, cancellationToken));
    }

    /// <summary>
    /// Begins the current step of a re-read transaction when the write of this writer moved the completion
    /// to it.
    /// </summary>
    /// <remarks>
    /// The step is read off the record as it stands after the check — the list of missing claims may have
    /// grown inside it (SPEC-041 E86). Two writes move the step: a recorded outcome moves it forward, stored
    /// values taken off (E86) move it back or reopen the step whose last claim was just closed, and in both
    /// cases the step signal goes out again (L91). A step the take-off moved to begins only for the writer
    /// whose own take-off moved it there, and only while it is still current: a writer that only reads that
    /// write — two last outcomes checked at once — or whose later take-off leaves the step where it already
    /// is, and a writer whose step another writer has moved on since, would otherwise begin a step a second
    /// time, with a second run of its starter and a second step signal. A step closed and reopened by another
    /// writer between this writer's take-off and its read is still the same step and begins here too; the
    /// starters end such a repeated start, and the step signal goes out twice.
    /// </remarks>
    /// <param name="current">The transaction as stored now.</param>
    /// <param name="stepBefore">Step the recorded outcome belonged to.</param>
    /// <param name="movedTo">Step the take-off of this writer moved the completion to; null — none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction as stored, after the start of a new step if one began.</returns>
    private async Task<Transaction> BeginClaimCompletionStepIfMovedAsync(
        Transaction current,
        ClaimCompletionStep stepBefore,
        ClaimCompletionStep? movedTo,
        CancellationToken cancellationToken)
    {
        if (current.GetCurrentClaimCompletionStep() is { } step
            && (step > stepBefore || step == movedTo))
        {
            return await BeginClaimCompletionStepAsync(current, cancellationToken);
        }

        return current;
    }

    /// <summary>
    /// Finalizes a transaction whose claims completion is over through the one finalization sequence
    /// (confirm → resolve → complete, with compensation), with the frozen snapshot of the transaction
    /// and its current token.
    /// </summary>
    /// <remarks>
    /// The confirmation goes through the automaton with optimistic concurrency, so of two writers of
    /// the last outcomes exactly one moves the transaction to Confirmed. The other one either loses the
    /// token — its read preceded that write — or reads the transaction already Confirmed or Completed:
    /// then the idempotent branch of the confirmation answers it with success, and it goes on to resolve
    /// and complete alongside the winner, the way a repeated delivery of a channel event does. Of the two
    /// completions one writes Completed; the other is answered by the idempotency of Completed or
    /// refused by the token, and its compensation finds the transaction already terminal. A refusal
    /// of the finalization does not travel out: the outcome is recorded either way, and the writer is
    /// answered with the transaction as it stands after the attempt. A confirmation refused because a
    /// claim went missing again between the check and the confirmation (a stored value vanished,
    /// SPEC-041 E86) continues the completion as the check itself does when something is missing: the
    /// step now open begins, otherwise nobody would begin it and the sign-in would wait out its TTL.
    /// That refusal names the confirmation's own write: the confirmation reads the transaction with the
    /// token of this check, and a take-off it loses to another writer is refused as a concurrency conflict.
    /// Its take-off moved the completion from nothing missing to a step, and the step current on the record
    /// read after the refusal begins — even when it is the step of the recorded outcome itself. The refusal
    /// does not carry the step the take-off moved to, so this takes the step on the record for it: an
    /// outcome of the reopened step recorded between that write and the read, before anyone began the step,
    /// would begin the next step a second time — a repeated step signal, the starters ending a repeated start.
    /// </remarks>
    /// <param name="transaction">Transaction in the completion with nothing missing, as stored.</param>
    /// <param name="stepBefore">Step the recorded outcome belonged to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The finalized transaction, or the one re-read after a refused finalization.</returns>
    private async Task<Result<Transaction>> FinalizeClaimCompletionAsync(
        Transaction transaction,
        ClaimCompletionStep stepBefore,
        CancellationToken cancellationToken)
    {
        var snapshot = transaction.ChannelIdentitySnapshot
            ?? throw new InvalidOperationException(
                $"Transaction '{transaction.Id}' carries a claims completion mark without a channel snapshot.");

        Result<Transaction> finalized;

        // The identity resolution is asked for from a scope of its own: a host may register it Scoped.
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            finalized = await ChannelTransactionFinalizer.ConfirmAndCompleteAsync(
                snapshot,
                transaction.Id,
                this,
                scope.ServiceProvider.GetRequiredService<IIdentityResolutionService>(),
                transaction.ConcurrencyToken,
                _logger,
                cancellationToken);
        }

        if (finalized.IsSuccess)
        {
            return finalized;
        }

        var after = await _store.GetByIdAsync(transaction.Id, cancellationToken);
        if (after is null)
        {
            return Result<Transaction>.Failure(TransactionErrorCodes.TransactionNotFound, "Transaction not found");
        }

        if (string.Equals(
                finalized.Error.Code,
                TransactionErrorCodes.ClaimCompletionInProgress,
                StringComparison.Ordinal))
        {
            after = await BeginClaimCompletionStepIfMovedAsync(
                after, stepBefore, after.GetCurrentClaimCompletionStep(), cancellationToken);
        }

        return Result<Transaction>.Success(after);
    }

    /// <summary>
    /// Begins the current step of the claims completion: awaits the starter registered for the step,
    /// re-reads the transaction and publishes the step signal with the step and the surface of the
    /// re-read record — the instance at hand does not see what the starter wrote.
    /// </summary>
    /// <param name="transaction">Transaction in the completion, as the caller holds it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction as stored after the start of the step.</returns>
    private async Task<Transaction> BeginClaimCompletionStepAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        if (transaction.GetCurrentClaimCompletionStep() is not { } step)
        {
            return transaction;
        }

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            // At most one starter per step; when several are registered, the last registration wins,
            // the way a single-service resolution answers.
            var starter = scope.ServiceProvider
                .GetServices<IClaimCompletionStepStarter>()
                .LastOrDefault(candidate => candidate.Step == step);

            if (starter is not null)
            {
                try
                {
                    await starter.StartAsync(transaction, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Claims completion step starter failed; the step begins with its signal alone. TransactionId: {TransactionId}, Step: {Step}",
                        transaction.Id.ToString(),
                        step);
                }
            }
        }

        var current = await _store.GetByIdAsync(transaction.Id, cancellationToken) ?? transaction;

        if (current.GetCurrentClaimCompletionStep() is not { } currentStep)
        {
            return current;
        }

        await _eventPublisher.PublishAsync(
            new TransactionClaimCompletionStepEvent
            {
                TransactionId = current.Id,
                OccurredAt = _timeProvider.GetUtcNow(),
                Step = currentStep,
                Surface = current.ClaimCompletion?.Surface,
                Context = TransactionEventContext.FromTransaction(current)
            },
            cancellationToken);

        return current;
    }

    /// <summary>
    /// Asks the claims completion planner one question, from a scope of its own: the planner reads the
    /// channel adapters, whose lifetime a host may narrow.
    /// </summary>
    /// <typeparam name="T">Answer of the question.</typeparam>
    /// <param name="question">The question to the planner.</param>
    /// <returns>The answer.</returns>
    private async Task<T> AskPlannerAsync<T>(Func<ClaimCompletionPlanner, ValueTask<T>> question)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        return await question(scope.ServiceProvider.GetRequiredService<ClaimCompletionPlanner>());
    }

    /// <inheritdoc />
    public async Task<Result<Transaction>> CompleteTransactionAsync(
        TransactionId id,
        ResolvedIdentitySnapshot resolvedIdentity,
        string concurrencyToken,
        CancellationToken cancellationToken = default)
    {
        // Get the transaction
        var transaction = await _store.GetByIdAsync(id, cancellationToken);
        if (transaction is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        // Idempotency: if already Completed — return it
        if (transaction.State is TransactionState.Completed)
        {
            return Result<Transaction>.Success(transaction);
        }

        // Check the ConcurrencyToken
        if (!string.Equals(transaction.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.ConcurrencyConflict,
                "Concurrency conflict — ConcurrencyToken does not match");
        }

        // Save the current token for the update check
        var previousToken = transaction.ConcurrencyToken;

        // Invariant: ChannelIdentitySnapshot must be populated on transition to Completed
        // (populated at Confirmed, and Completed is only reachable from Confirmed).
        // Checked BEFORE the state mutation, so the transaction is not left in a corrupted state.
        if (transaction.ChannelIdentitySnapshot is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.InvalidStateTransition,
                "ChannelIdentitySnapshot is not populated — cannot complete the transaction without channel data");
        }

        // Freeze the Claims to guarantee immutability — before the size validation,
        // so the same instance is measured and stored
        var frozenResolvedIdentity = resolvedIdentity.WithFrozenClaims();

        // Validate the ResolvedIdentitySnapshot size (maximum 128 KB).
        // Checked BEFORE the state mutation, so the transaction is not left in a corrupted state.
        // The serialization is memoized per instance and reused by the store on
        // persist (TransactionEntityMapper.SerializeImmutableSnapshot) — exactly
        // the JSON that will be written is measured, without re-serialization
        var resolvedSnapshotJson = TransactionEntityMapper.SerializeImmutableSnapshot(frozenResolvedIdentity);
        if (Encoding.UTF8.GetByteCount(resolvedSnapshotJson) > MaxSnapshotSize)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.SnapshotTooLarge,
                $"ResolvedIdentitySnapshot size exceeds the limit ({MaxSnapshotSize} bytes)");
        }

        // The verdict of the identity match (SPEC-039 L41) is computed HERE — at the point the resolved
        // identity is written, and not at the surface that finalized the transaction: there are several
        // such surfaces (the channel, the core page) and the verdict must be one and the same wherever
        // the person pressed "yes". Computed BEFORE the transition, because the transition is what
        // erases the expectations it reads. It refuses nothing: a verdict is not a gate (E43).
        var matchedType = await _identityMatch.ComputeVerdictAsync(
            transaction,
            frozenResolvedIdentity,
            cancellationToken);

        // Perform the Confirmed → Completed transition
        var transitionResult = TransactionStateMachine.TryTransition(transaction, TransactionState.Completed, _timeProvider.GetUtcNow());
        if (transitionResult.IsFailure)
        {
            return transitionResult;
        }

        // The transition above erased the expectations; what survives the transaction is the name of
        // the matched type alone. A transaction that stated no expectations keeps its empty container
        // (null) rather than growing one that says nothing.
        if (transaction.IdentityMatch is not null)
        {
            transaction.IdentityMatch = new IdentityMatchState
            {
                MatchedType = matchedType,
                IdentityTokenIssuedAt = transaction.IdentityMatch.IdentityTokenIssuedAt
            };
        }

        // Store the resolved identity and the completion snapshot. The claim completion state stays on the
        // transaction: it is the record of what was collected, and the update below keeps it as read.
        transaction.ResolvedIdentitySnapshot = frozenResolvedIdentity;
        transaction.CompletionSnapshot = new CompletionSnapshot
        {
            CompletedAt = _timeProvider.GetUtcNow(),
            ChannelType = transaction.ChannelIdentitySnapshot.ChannelType,
            Claims = WithCollectedClaims(
                frozenResolvedIdentity.Claims, transaction.ClaimCompletion, transaction.StoredClaimValues)
        };

        // Update the store
        var updated = await _store.UpdateAsync(transaction, previousToken, cancellationToken);
        if (!updated)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.ConcurrencyConflict,
                "Concurrency conflict while updating the store");
        }

        // The point of the actual state transition — the idempotent branch at the top of the method
        // returns before it, so a repeated delivery of the same channel event counts once.
        _metrics.RecordTerminated(transaction, TransactionOutcomes.Completed, _timeProvider.GetUtcNow());

        _logger.LogInformation(
            "Transaction completed. TransactionId: {TransactionId}, SubjectHash: {SubjectHash}",
            transaction.Id.ToString(),
            LogMasking.Fingerprint(resolvedIdentity.Subject));

        // Before the completion event: the page it wakes goes on to issue the code, which records the
        // subject handed to the application under the same key.
        await SaveCollectedClaimsAsync(transaction);

        // Publish the completion event
        await _eventPublisher.PublishAsync(
            new TransactionCompletedEvent
            {
                TransactionId = transaction.Id,
                OccurredAt = _timeProvider.GetUtcNow(),
                Subject = resolvedIdentity.Subject,
                Context = TransactionEventContext.FromTransaction(transaction)
            },
            cancellationToken);

        return Result<Transaction>.Success(transaction);
    }

    /// <summary>
    /// Saves the outcomes of the claims completion of a completed sign-in to the collected claims store
    /// (SPEC-041 C37, L57): the values and the skips, so they are not asked again at the next sign-in. What
    /// the transaction took from the store is not written back.
    /// </summary>
    /// <remarks>
    /// Nothing is saved without a registered store, without outcomes, or when the tenant of the transaction
    /// is unknown because its resolution failed. The sign-in is already completed: a store that fails is
    /// logged — the number of the records and the names of their claims, never a value — and the completion
    /// stands (E62). <see cref="CancellationToken.None"/> — the save follows a committed write and is not
    /// abandoned with the request.
    /// </remarks>
    /// <param name="transaction">Transaction just written as Completed.</param>
    /// <returns>Task representing the completion of the operation.</returns>
    private async Task SaveCollectedClaimsAsync(Transaction transaction)
    {
        if (transaction.ClaimCompletion is not { Results.Count: > 0 } completion
            || transaction.ChannelIdentitySnapshot is not { } snapshot
            || transaction.HasFailedTenantResolution())
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<ICollectedClaimsStore>() is not { } store)
        {
            return;
        }

        try
        {
            await store.SaveAsync(
                transaction.GetTenantId(),
                CoreSubject.From(snapshot),
                completion.Results,
                _timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Nobody cancels a save made with CancellationToken.None: a cancellation here is the store's own
            // timeout, a store that did not answer like any other failure.
            _logger.LogWarning(
                exception,
                "The collected claims could not be saved; the sign-in stays completed. TransactionId: {TransactionId}, RecordCount: {RecordCount}, Claims: {Claims}",
                transaction.Id.ToString(),
                completion.Results.Count,
                string.Join(',', completion.Results.Select(result => result.Claim)));
        }
    }

    /// <summary>
    /// The final claims of a sign-in: the claims of the resolved identity joined by the values collected
    /// during the claim completion and the values taken from the collected claims store (SPEC-041 R22 —
    /// both go to the claims mapper, built-in or substituted, through the completion snapshot and no
    /// other way; C37).
    /// </summary>
    /// <remarks>
    /// Both sources go through one rule. A name decides by what the channel gave for it
    /// (<see cref="DefaultIdentityResolutionService.IsGiven"/>, the same reading the planner counted the
    /// claim missing by):
    /// <list type="bullet">
    /// <item>a value the channel gave is kept, and neither a collected nor a stored one replaces it
    /// (SPEC-041 L43);</item>
    /// <item>a name the channel left empty, or did not state, takes the value — under the key the identity
    /// stores it under, or as the result spells it when the identity lacks it;</item>
    /// <item>a skipped claim has no value and changes nothing — an empty value of the channel stays as
    /// it was.</item>
    /// </list>
    /// No verification mark of the channel travels with such a value: the projection states one only next
    /// to a value the channel gave (SPEC-041 L50). A phone number and an email take their own mark from
    /// their result instead — <c>phone_number_verified</c> and <c>email_verified</c> are the
    /// <c>Verified</c> of that result (SPEC-041 R10, R11, C38, L50), each written only where the value
    /// itself is: a mark stated next to a value the channel did not give describes no value the token
    /// carries, so the value brings its own.
    /// <para>
    /// <c>updated_at</c> (C38, OIDC Core §5.1) is stated only when at least one stored value was joined —
    /// the profile then carries information older than this sign-in: the latest moment any joined value
    /// was collected, stored or of this sign-in, in seconds since the epoch. A value the identity already
    /// gives under that name is kept, as any other.
    /// </para>
    /// </remarks>
    /// <param name="identityClaims">Claims of the resolved identity (frozen).</param>
    /// <param name="claimCompletion">Claim completion state of the transaction; null — no completion.</param>
    /// <param name="storedValues">Values the transaction took from the collected claims store.</param>
    /// <returns>The claims of the resolved identity as they are when nothing was collected or stored;
    /// otherwise a frozen union.</returns>
    private static IReadOnlyDictionary<string, string>? WithCollectedClaims(
        IReadOnlyDictionary<string, string>? identityClaims,
        ClaimCompletionState? claimCompletion,
        IReadOnlyList<ClaimCompletionResult> storedValues)
    {
        var collected = (claimCompletion?.Results ?? [])
            .Where(result => result.Value is not null)
            .Select(result => (Result: result, Stored: false))
            .Concat(storedValues
                .Where(result => result.Value is not null)
                .Select(result => (Result: result, Stored: true)))
            .ToList();
        if (collected.Count == 0)
        {
            return identityClaims;
        }

        var merged = identityClaims is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(identityClaims, StringComparer.Ordinal);
        // The key each name is stored under: a name given in another case is the same claim (L43).
        var keyOfName = new Dictionary<string, string>(VeriqaClaimTypes.NameComparer);
        foreach (var key in merged.Keys)
        {
            keyOfName.TryAdd(key, key);
        }

        var storedJoined = false;
        DateTimeOffset? latest = null;

        foreach (var (result, fromStore) in collected)
        {
            if (!TryJoin(result.Claim, result.Value!))
            {
                continue;
            }

            storedJoined |= fromStore;
            latest = latest is { } known && known >= result.RecordedAt ? known : result.RecordedAt;

            if (VeriqaClaimTypes.NameComparer.Equals(result.Claim, VeriqaClaimTypes.PhoneNumber))
            {
                merged[KeyOf(VeriqaClaimTypes.PhoneNumberVerified)] =
                    DefaultIdentityResolutionService.FormatBooleanClaim(result.Verified);
            }
            else if (VeriqaClaimTypes.NameComparer.Equals(result.Claim, VeriqaClaimTypes.Email))
            {
                merged[KeyOf(VeriqaClaimTypes.EmailVerified)] =
                    DefaultIdentityResolutionService.FormatBooleanClaim(result.Verified);
            }
        }

        if (storedJoined && latest is { } updatedAt)
        {
            TryJoin(
                VeriqaClaimTypes.UpdatedAt,
                updatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        return merged.ToFrozenDictionary(StringComparer.Ordinal);

        // Writes a value under a name the channel gave no value for; false — the given value is kept (L43).
        bool TryJoin(string name, string value)
        {
            var key = KeyOf(name);
            if (merged.TryGetValue(key, out var given) && DefaultIdentityResolutionService.IsGiven(given))
            {
                return false;
            }

            merged[key] = value;
            return true;
        }

        // The key a name is stored under, or the name as spelled when the identity lacks it.
        string KeyOf(string name)
        {
            if (!keyOfName.TryGetValue(name, out var stored))
            {
                stored = name;
                keyOfName.Add(stored, stored);
            }

            return stored;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkIdentityTokenIssuedAsync(
        TransactionId id,
        CancellationToken cancellationToken = default)
    {
        var marked = await TryWriteIssuanceMarkAsync(
            id,
            static transaction => transaction.IdentityMatch?.IdentityTokenIssuedAt is not null,

            // A transaction that stated no expectations has no container yet, so one is created. The
            // verdict is kept; the expectations are not carried — the completion is what erased them.
            static (transaction, issuedAt) => transaction.IdentityMatch = new IdentityMatchState
            {
                MatchedType = transaction.IdentityMatch?.MatchedType,
                IdentityTokenIssuedAt = issuedAt
            },
            cancellationToken);

        if (marked)
        {
            _logger.LogDebug(
                "Identity token issuance marked. TransactionId: {TransactionId}",
                id.ToString());
        }

        return marked;
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkAuthorizationCodeIssuedAsync(
        TransactionId id,
        CancellationToken cancellationToken = default)
    {
        var marked = await TryWriteIssuanceMarkAsync(
            id,
            static transaction => transaction.AuthorizationCodeIssuedAt is not null,
            static (transaction, issuedAt) => transaction.AuthorizationCodeIssuedAt = issuedAt,
            cancellationToken);

        if (marked)
        {
            _logger.LogDebug(
                "Authorization code issuance marked. TransactionId: {TransactionId}",
                id.ToString());
        }

        return marked;
    }

    /// <summary>
    /// Writes a one-time issuance mark on a Completed transaction: read, check, write under the
    /// concurrency token read in the same attempt.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="carriesMark">Tells whether the transaction already carries the mark.</param>
    /// <param name="writeMark">Puts the mark with the given moment on the transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this call wrote the mark; <see langword="false"/> — the transaction
    /// is not stored, is not Completed, already carries the mark, or the attempts ran out.
    /// </returns>
    private async Task<bool> TryWriteIssuanceMarkAsync(
        TransactionId id,
        Func<Transaction, bool> carriesMark,
        Action<Transaction, DateTimeOffset> writeMark,
        CancellationToken cancellationToken)
    {
        // The mark is written by a compare-and-swap on the concurrency token. A Completed transaction
        // is terminal, so the only writers competing for it are another mark and the retention sweep: a
        // lost lock is settled by the next read, which sees either the winner's mark, a mark of the
        // other kind under a new token, or no transaction at all. The attempts are bounded all the same,
        // so a store whose update never applies ends in a refusal rather than a spin — and a refusal
        // issues nothing.
        for (var attempt = 0; attempt < MaxIdentityTokenMarkAttempts; attempt++)
        {
            var transaction = await _store.GetByIdAsync(id, cancellationToken);
            if (transaction is null
                || transaction.State is not TransactionState.Completed
                || carriesMark(transaction))
            {
                return false;
            }

            var previousToken = transaction.ConcurrencyToken;
            writeMark(transaction, _timeProvider.GetUtcNow());

            // Only the concurrency token moves with the mark. UpdatedAt stays as the completion wrote it:
            // the retention of a terminal transaction counts from it, and issuing by the transaction
            // must not keep the resolved identity stored any longer than it already would be.
            transaction.ConcurrencyToken = Guid.NewGuid().ToString("N");

            if (await _store.UpdateAsync(transaction, previousToken, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<Result<Transaction>> FailTransactionAsync(
        TransactionId id,
        string reasonCode,
        string concurrencyToken,
        CancellationToken cancellationToken = default)
    {
        // Get the transaction
        var transaction = await _store.GetByIdAsync(id, cancellationToken);
        if (transaction is null)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.TransactionNotFound,
                "Transaction not found");
        }

        // If already in a terminal state — error
        if (transaction.IsTerminal())
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.InvalidStateTransition,
                $"Transaction is already in a terminal state: {transaction.State}");
        }

        // Check the ConcurrencyToken
        if (!string.Equals(transaction.ConcurrencyToken, concurrencyToken, StringComparison.Ordinal))
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.ConcurrencyConflict,
                "Concurrency conflict — ConcurrencyToken does not match");
        }

        // Save the current token for the update check
        var previousToken = transaction.ConcurrencyToken;

        // Perform the → Failed transition
        var transitionResult = TransactionStateMachine.TryTransition(
            transaction, TransactionState.Failed, _timeProvider.GetUtcNow(), reasonCode);
        if (transitionResult.IsFailure)
        {
            return transitionResult;
        }

        // Update the store
        var updated = await _store.UpdateAsync(transaction, previousToken, cancellationToken);
        if (!updated)
        {
            return Result<Transaction>.Failure(
                TransactionErrorCodes.ConcurrencyConflict,
                "Concurrency conflict while updating the store");
        }

        // The user's refusal and a system failure arrive at the same method — it is the single writer
        // of the Failed state — so the outcome is told from the reason code rather than from the call
        // site: giving "declined" a method of its own would mean duplicating the state transition.
        // A sign-in closed by a newer one is a third outcome here: counted as a failure, every repeated
        // attempt would raise the share of failed sign-ins.
        var superseded = string.Equals(reasonCode, TransactionErrorCodes.SupersededByNewSignIn, StringComparison.Ordinal);

        _metrics.RecordTerminated(
            transaction,
            reasonCode switch
            {
                TransactionErrorCodes.DeclinedByUser => TransactionOutcomes.Declined,
                TransactionErrorCodes.SupersededByNewSignIn => TransactionOutcomes.Superseded,
                _ => TransactionOutcomes.Failed
            },
            _timeProvider.GetUtcNow());

        // Superseding is the ordinary course of a repeated sign-in, not something for an operator to
        // look into, so it is not reported at the level of a failure.
        _logger.Log(
            superseded ? LogLevel.Information : LogLevel.Warning,
            "Transaction failed. TransactionId: {TransactionId}, ReasonCode: {ReasonCode}",
            transaction.Id.ToString(),
            reasonCode);

        // Publish the event
        await _eventPublisher.PublishAsync(
            new TransactionFailedEvent
            {
                TransactionId = transaction.Id,
                OccurredAt = _timeProvider.GetUtcNow(),
                ReasonCode = reasonCode,
                Context = TransactionEventContext.FromTransaction(transaction)
            },
            cancellationToken);

        return Result<Transaction>.Success(transaction);
    }

    /// <inheritdoc />
    public Task<Result<Transaction>> CancelTransactionAsync(
        TransactionId id,
        string concurrencyToken,
        CancellationToken cancellationToken = default)
    {
        // Cancellation is a Fail with the cancelled_by_client code
        return FailTransactionAsync(id, TransactionErrorCodes.CancelledByClient, concurrencyToken, cancellationToken);
    }

    /// <summary>
    /// Closes the earlier sign-ins the given one supersedes: every stored sign-in of the same tenant,
    /// application and channel subject that is still Pending goes to Failed with
    /// <see cref="TransactionErrorCodes.SupersededByNewSignIn"/>.
    /// </summary>
    /// <remarks>
    /// Called after the write that gave <paramref name="newSignIn"/> its channel subject, and only for
    /// that write. Nothing here may change the result of that write: a store that fails, a closing that
    /// is refused and any exception but a cancellation are logged and swallowed, and the sign-in left
    /// behind lives until its deadline, as it did before this rule existed.
    /// The callers pass <see cref="CancellationToken.None"/>: the closing follows a committed write and
    /// is not abandoned with the request.
    /// <para>
    /// The invariant "only an earlier pending sign-in of the same scope is closed" is held here and not
    /// by the store: each transaction the store returned is checked again before it is closed. The
    /// closing goes through <see cref="FailTransactionAsync"/> with the token the candidate was read
    /// with, so a candidate that was answered, confirmed or swept in the meantime is not overwritten;
    /// it is re-read once and closed only when it still passes the check.
    /// </para>
    /// <para>
    /// Nothing is sent to the channel: the receipts of an outcome are sent by the caller that wrote
    /// the outcome, and this method has none.
    /// </para>
    /// </remarks>
    /// <param name="newSignIn">The sign-in as it was just written, bound to its channel subject.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task SupersedeEarlierSignInsAsync(Transaction newSignIn, CancellationToken cancellationToken)
    {
        if (SignInSubjectScope.Of(newSignIn) is not { } scope)
        {
            return;
        }

        // A failing store is already logged by the wrapper; the binding stands either way.
        var lookup = await CallStoreAsync(() => _store.GetPendingSignInsAsync(scope, cancellationToken));
        if (lookup.IsFailure)
        {
            return;
        }

        foreach (var candidate in lookup.Value)
        {
            try
            {
                await SupersedeAsync(candidate, scope, newSignIn.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Closing an earlier sign-in superseded by a new one failed. TransactionId: {TransactionId}, NewTransactionId: {NewTransactionId}",
                    candidate.Id.ToString(),
                    newSignIn.Id.ToString());
            }
        }
    }

    /// <summary>
    /// Closes one earlier sign-in, when it is still one the new sign-in supersedes.
    /// </summary>
    /// <param name="candidate">Transaction the store returned as a pending sign-in of the scope.</param>
    /// <param name="scope">Scope of the new sign-in.</param>
    /// <param name="newSignInId">Identifier of the new sign-in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task SupersedeAsync(
        Transaction candidate,
        SignInSubjectScope scope,
        TransactionId newSignInId,
        CancellationToken cancellationToken)
    {
        if (!IsSupersededBy(candidate, scope, newSignInId))
        {
            return;
        }

        var closed = await FailTransactionAsync(
            candidate.Id,
            TransactionErrorCodes.SupersededByNewSignIn,
            candidate.ConcurrencyToken,
            cancellationToken);

        if (closed.IsFailure
            && string.Equals(closed.Error.Code, TransactionErrorCodes.ConcurrencyConflict, StringComparison.Ordinal))
        {
            // Another writer moved the candidate between the selection and the closing. It is read once
            // more: a write that left it a pending sign-in of the same scope (a conditional field, a
            // repeated delivery) does not save it, anything else does.
            var current = await _store.GetByIdAsync(candidate.Id, cancellationToken);
            if (current is null || !IsSupersededBy(current, scope, newSignInId))
            {
                return;
            }

            closed = await FailTransactionAsync(
                current.Id,
                TransactionErrorCodes.SupersededByNewSignIn,
                current.ConcurrencyToken,
                cancellationToken);
        }

        if (closed.IsSuccess)
        {
            // Both identifiers and nothing else: the channel user is not named in the log.
            _logger.LogInformation(
                "Earlier sign-in superseded by a new one. TransactionId: {TransactionId}, NewTransactionId: {NewTransactionId}",
                candidate.Id.ToString(),
                newSignInId.ToString());

            return;
        }

        // Lost to another writer once more, or ended by one: the candidate is somebody else's now.
        if (closed.Error.Code is TransactionErrorCodes.ConcurrencyConflict
            or TransactionErrorCodes.InvalidStateTransition
            or TransactionErrorCodes.TransactionNotFound)
        {
            _logger.LogDebug(
                "Earlier sign-in was not superseded: another writer got to it first. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                candidate.Id.ToString(),
                closed.Error.Code);

            return;
        }

        _logger.LogWarning(
            "Closing an earlier sign-in superseded by a new one was refused. TransactionId: {TransactionId}, NewTransactionId: {NewTransactionId}, ErrorCode: {ErrorCode}",
            candidate.Id.ToString(),
            newSignInId.ToString(),
            closed.Error.Code);
    }

    /// <summary>
    /// The check the engine makes before closing a sign-in as superseded, whatever the store returned:
    /// it is Pending, it has the scope of the new sign-in, and it is not the new sign-in itself.
    /// </summary>
    /// <param name="candidate">Transaction being checked.</param>
    /// <param name="scope">Scope of the new sign-in.</param>
    /// <param name="newSignInId">Identifier of the new sign-in.</param>
    /// <returns>true when the transaction is an earlier pending sign-in of the same scope.</returns>
    private static bool IsSupersededBy(Transaction candidate, SignInSubjectScope scope, TransactionId newSignInId)
        => candidate.State is TransactionState.Pending
            && candidate.Id != newSignInId
            && SignInSubjectScope.Of(candidate) == scope;

    /// <inheritdoc />
    public async Task ExpireOverdueAsync(TransactionId id, CancellationToken cancellationToken = default)
    {
        // Read afresh rather than taken from the caller: the surface read the transaction before it
        // answered, and the write must check the token of the record as it stands now.
        var lookup = await CallStoreAsync(() => _store.GetByIdAsync(id, cancellationToken));
        if (lookup.IsFailure || lookup.Value is not { } transaction)
        {
            return;
        }

        await ExpireAtLateDecisionAsync(transaction);
    }

    /// <summary>
    /// Takes the shared expiry step for a decision that met the deadline, without letting it change
    /// the answer to that decision.
    /// </summary>
    /// <remarks>
    /// <see cref="CancellationToken.None"/>: once the write lands, the event must go out with it — a
    /// transaction left Expired without its event is one the cleanup pass no longer selects, so its
    /// chat would never receive the receipt. A failing store or publisher is logged and swallowed: the
    /// decision is answered as an expiry either way, and the cleanup pass still finds a transaction
    /// whose write did not land.
    /// </remarks>
    /// <param name="transaction">Transaction as it was read from the store.</param>
    /// <returns>The completed operation.</returns>
    private async Task ExpireAtLateDecisionAsync(Transaction transaction)
    {
        try
        {
            if (await _expiry.TryExpireAsync(transaction, CancellationToken.None))
            {
                _logger.LogInformation(
                    "Transaction moved to Expired by a decision after its deadline. TransactionId: {TransactionId}",
                    transaction.Id.ToString());
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Moving a transaction to Expired at a late decision failed; the cleanup pass will. TransactionId: {TransactionId}",
                transaction.Id.ToString());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every operation that puts channel data onto a transaction applies this gate — the confirmation
    /// and the attachment that keeps the transaction Pending until the answer comes from the core web
    /// page. They are one decision made at two moments, so a single implementation keeps them from
    /// drifting apart: an identity accepted by one of them is signed in through the other. Since it is
    /// stated on the contract, a surface that only discloses an already recorded outcome applies that
    /// same implementation rather than a copy of it.
    /// <para>
    /// Within an action of a channel (<see cref="ChannelAdmissionScope"/>) the verdict of one pair
    /// "transaction × identity" is evaluated once: the first call — the gate of the channel — decides, and a
    /// later call of the same action — the write of the engine among them — gets that
    /// verdict back without asking the registered checks again and without journaling the refusal a second
    /// time. A verdict that is not an answer of the checks — one of them failed to answer, or the call was
    /// cancelled — is not remembered, and the next call asks again.
    /// </para>
    /// </remarks>
    public async ValueTask<Result> ValidateChannelAdmissibilityAsync(
        Transaction transaction,
        ChannelIdentitySnapshot channelIdentity,
        CancellationToken cancellationToken = default)
    {
        if (ChannelAdmissionScope.TryRecall(transaction.Id, channelIdentity, out var remembered))
        {
            return remembered;
        }

        var (verdict, isAnswered) = await EvaluateChannelAdmissibilityAsync(
            transaction, channelIdentity, cancellationToken);

        return isAnswered
            ? ChannelAdmissionScope.Remember(transaction.Id, channelIdentity, verdict)
            : verdict;
    }

    /// <summary>
    /// Evaluates whether the sender may act on the transaction: the allow-list of channels, the bot policy,
    /// the restriction policies and the checks the deployment registered, in this order, up to the first
    /// refusal (<see cref="ValidateChannelAdmissibilityAsync"/>).
    /// </summary>
    /// <param name="transaction">Transaction the sender acts on.</param>
    /// <param name="channelIdentity">Identity snapshot of the sender.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verdict, and whether it is an answer of the checks — false when a registered check
    /// failed to answer and the sender is refused for that failure.</returns>
    private async ValueTask<(Result Verdict, bool IsAnswered)> EvaluateChannelAdmissibilityAsync(
        Transaction transaction,
        ChannelIdentitySnapshot channelIdentity,
        CancellationToken cancellationToken)
    {
        // Check that the channel is allowed
        if (transaction.AllowedChannelTypes.Count > 0
            && !transaction.AllowedChannelTypes.Contains(channelIdentity.ChannelType))
        {
            return (
                Result.Failure(
                    TransactionErrorCodes.ChannelNotAllowed,
                    $"Channel '{channelIdentity.ChannelType}' is not allowed for this transaction"),
                true);
        }

        // Reject requests from bots (security policy).
        // RejectBots is read via the resolver with the transaction's ApplicationId (CFG-222):
        // a protective setting (ProtectiveCeiling). Self-hosted = core value 1:1 (criterion met).
        // The application level is bound over the OIDC client entry (per-client OidcClientOptions.RejectBots):
        // a per-app relaxation takes effect only when the core/tenant gate RejectBotsAllowApplicationOverride is
        // open (CFG-212), otherwise the stricter upper-level value wins. Extending this to per-tenant is a future track.
        // The tenant override is written out as null by hand: this assembly has no ambient tenant
        // scope to read (it lives in the channel contour), so the tenant of the transaction is the
        // only one there is.
        var resolutionContext = transaction.ToResolutionContext(tenantId: null);
        var rejectBots = (await _configResolver.ResolveAsync(
            TransactionEngineConfigKeys.RejectBots,
            resolutionContext,
            ConfigDimensionValues.None,
            cancellationToken)).Value;

        if (channelIdentity.IsBot && rejectBots)
        {
            _logger.LogWarning(
                "Channel event rejected: the sender is a bot. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transaction.Id.ToString(),
                channelIdentity.ChannelType);

            return (
                Result.Failure(
                    TransactionErrorCodes.BotRejected,
                    "Bot authentication is forbidden by the security policy"),
                true);
        }

        // SPEC-018 §6: the restriction policies. Two independent keys — the cross-channel policy and
        // the policy of the channel the event came through, the channel being a dimension of the key
        // (RP-045) — evaluated in this order; the first refusal ends the gate, so the second key is
        // not even resolved (RP-046). Nothing stated on either key admits the sender (RP-047).
        (string Scope, ConfigKey<RestrictionPolicySet?> Key, ConfigDimensionValues Dimensions)[] restrictionPolicies =
        [
            (RestrictionPolicyScopes.Common, RestrictionPolicyConfigKeys.Common, ConfigDimensionValues.None),
            (
                RestrictionPolicyScopes.Channel,
                RestrictionPolicyConfigKeys.Channel,
                ConfigDimensionValues.Of((RestrictionPolicyConfigKeys.ChannelDimensionName, channelIdentity.ChannelType)))
        ];

        foreach (var (scope, key, dimensions) in restrictionPolicies)
        {
            var policies = (await _configResolver.ResolveAsync(
                key,
                resolutionContext,
                dimensions,
                cancellationToken)).Value;

            var decision = _restrictionPolicyEvaluator.Evaluate(channelIdentity, policies);

            if (!decision.IsDenied)
            {
                continue;
            }

            await RecordRestrictionPolicyRefusalAsync(transaction, channelIdentity.ChannelType, scope, decision, cancellationToken);

            // The transaction is not touched: the event is refused, not the sign-in (RP-042).
            return (Result.Failure(TransactionErrorCodes.RestrictionPolicyViolation, SenderNotAdmittedMessage), true);
        }

        // SPEC-018 §6.3: the checks a deployment registered, after the checks of the core and in the
        // order of their registration, up to the first refusal (RP-100). A check can only refuse
        // (RP-101), so nothing it answers reopens a refusal made above.
        foreach (var check in _admissibilityChecks)
        {
            var checkName = check.GetType().Name;
            string reasonCode;
            var isAnswered = true;

            try
            {
                var verdict = await check.EvaluateAsync(
                    transaction.Id,
                    channelIdentity,
                    resolutionContext,
                    cancellationToken);

                if (!verdict.IsDenied)
                {
                    continue;
                }

                // The code is the check's own and is not interpreted (RP-103); only an empty one is
                // replaced, so that the audit record never carries an empty field.
                reasonCode = string.IsNullOrWhiteSpace(verdict.ReasonCode)
                    ? RestrictionPolicyResultCodes.Unspecified
                    : verdict.ReasonCode;

                _logger.LogWarning(
                    "Channel event rejected by an external admissibility check. TransactionId: {TransactionId}, ChannelType: {ChannelType}, Check: {CheckName}, Reason: {ReasonCode}",
                    transaction.Id.ToString(),
                    channelIdentity.ChannelType,
                    checkName,
                    reasonCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Fail-closed: a check the deployment registered and that could not answer is not
                // "no objection" — reading it so would relax the perimeter by a failure.
                reasonCode = RestrictionPolicyResultCodes.CheckFailed;
                isAnswered = false;

                _logger.LogError(
                    exception,
                    "An external admissibility check failed; the channel event is rejected. TransactionId: {TransactionId}, ChannelType: {ChannelType}, Check: {CheckName}",
                    transaction.Id.ToString(),
                    channelIdentity.ChannelType,
                    checkName);
            }

            await PublishAdmissibilityAuditAsync(
                transaction,
                channelIdentity.ChannelType,
                RestrictionPolicyResultCodes.ExternalCheckDenied,
                $"check={checkName}; reason={reasonCode}",
                cancellationToken);

            return (Result.Failure(TransactionErrorCodes.SecurityPolicyViolation, SenderNotAdmittedMessage), isAnswered);
        }

        return (Result.Success(), true);
    }

    /// <summary>
    /// Writes the refusal of a restriction policy to the log and to the audit trail.
    /// </summary>
    /// <remarks>
    /// Two different occurrences share the refusal the caller sees. A rule that refused is an expected
    /// outcome — a warning and the event of a denial, with the generalized category and the NAME of the
    /// field. A level whose policy could not be read is a fault of the configuration — an error and the
    /// event of a misconfiguration, with the section to repair. Neither carries a claim value or the
    /// text of a rule (RP-061a, RP-091): everything written here comes off the decision, which holds
    /// none. The level is the one the refusing element was read at, not the one the resolver reports
    /// for the composed value (RP-075).
    /// </remarks>
    /// <param name="transaction">Transaction the refused event acts on.</param>
    /// <param name="channelType">Channel the event came through.</param>
    /// <param name="scope">Which policy refused — one of <see cref="RestrictionPolicyScopes"/>.</param>
    /// <param name="decision">The refusal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task RecordRestrictionPolicyRefusalAsync(
        Transaction transaction,
        string channelType,
        string scope,
        RestrictionPolicyDecision decision,
        CancellationToken cancellationToken)
    {
        var level = decision.Level?.ToString();

        if (decision.IsUnreadablePolicy)
        {
            // Core section of the policy that refused: where a deployment states it, as it would be
            // looked up in the host configuration.
            var section = scope == RestrictionPolicyScopes.Common
                ? RestrictionPoliciesOptions.CommonAddress
                : RestrictionPoliciesOptions.ChannelAddress(channelType);

            _logger.LogError(
                "Channel event rejected: the restriction policy could not be read and refuses every sender until the configuration is repaired. TransactionId: {TransactionId}, ChannelType: {ChannelType}, Policy: {PolicyScope}, Level: {PolicyLevel}, Section: {PolicySection}",
                transaction.Id.ToString(),
                channelType,
                scope,
                level,
                section);

            await PublishAdmissibilityAuditAsync(
                transaction,
                channelType,
                RestrictionPolicyResultCodes.Misconfigured,
                $"policy={scope}; level={level}",
                cancellationToken);

            return;
        }

        _logger.LogWarning(
            "Channel event rejected by the restriction policy. TransactionId: {TransactionId}, ChannelType: {ChannelType}, Category: {DenyCategory}, Field: {PolicyField}, Policy: {PolicyScope}, Level: {PolicyLevel}",
            transaction.Id.ToString(),
            channelType,
            decision.Category,
            decision.Field,
            scope,
            level);

        await PublishAdmissibilityAuditAsync(
            transaction,
            channelType,
            RestrictionPolicyResultCodes.PolicyDenied,
            $"policy={scope}; level={level}; category={decision.Category}; field={decision.Field}",
            cancellationToken);
    }

    /// <summary>
    /// Publishes the audit event of a refusal of the admissibility gate. The audit trail is not on
    /// the critical path: a failed publication is logged and the refusal stands either way.
    /// </summary>
    /// <param name="transaction">Transaction the refused event acts on.</param>
    /// <param name="channelType">Channel the event came through.</param>
    /// <param name="eventCode">Audit event code.</param>
    /// <param name="details">Details of the closed "key=value; key=value" form, free of claim values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task PublishAdmissibilityAuditAsync(
        Transaction transaction,
        string channelType,
        string eventCode,
        string details,
        CancellationToken cancellationToken)
    {
        try
        {
            await _eventPublisher.PublishAsync(
                new TransactionChannelAuditEvent
                {
                    TransactionId = transaction.Id,
                    OccurredAt = _timeProvider.GetUtcNow(),
                    ChannelType = channelType,
                    EventCode = eventCode,
                    Details = details,
                    Context = TransactionEventContext.FromTransaction(transaction)
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal request cancellation — the event is not published, and it is not a failure
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to publish audit event {EventCode}. TransactionId: {TransactionId}",
                eventCode,
                transaction.Id.ToString());
        }
    }

    /// <summary>
    /// Runs one call into the transaction store, keeping a failure of the storage inside the
    /// <see cref="Result{T}"/> boundary of the operation.
    /// </summary>
    /// <remarks>
    /// The engine classifies only the two failures it defines itself
    /// (<see cref="StoreCapacityExceededException"/>, <see cref="DuplicateIdempotencyKeyException"/>),
    /// and those are handled where they arise, as business outcomes. Everything else is a provider
    /// type this assembly neither references nor is entitled to name — a Npgsql or a Redis failure —
    /// so it is reported by category rather than by type. Cancellation is not a storage failure and
    /// is left to propagate as the shutdown it is.
    /// </remarks>
    /// <typeparam name="T">Value the store call produces.</typeparam>
    /// <param name="storeCall">The store call to run.</param>
    /// <returns>The value of the call, or an infrastructure error.</returns>
    private async Task<Result<T>> CallStoreAsync<T>(Func<Task<T>> storeCall)
    {
        try
        {
            return Result<T>.Success(await storeCall());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Transaction store call failed");

            return Result<T>.Failure(
                TransactionErrorCodes.StoreUnavailable,
                StoreUnavailableMessage,
                TransactionErrorCategory.Infrastructure);
        }
    }
}
