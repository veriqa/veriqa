// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// The one computation of the claims completion (SPEC-041 L91): which claims of the requirement set
/// are missing when the user confirms, and whether nothing is missing any more.
/// </summary>
/// <remarks>
/// Scoped: it reads the channel adapters, whose lifetime a host may narrow to the request, and the
/// engine resolves it from a scope of its own per operation. The collected claims store and the rules
/// its values are accepted by are optional and asked for from that same scope when needed; the store is
/// read here and nowhere else on the sign-in (SPEC-041 C37, E86).
/// </remarks>
internal sealed class ClaimCompletionPlanner
{
    /// <summary>
    /// Registered channel adapters — the source of the phone capability of the channel of a transaction.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Services of the scope of the operation — the source of the optional collected claims store and of
    /// the rules its values are accepted by (<see cref="IStoredClaimValueRules"/>).
    /// </summary>
    private readonly IServiceProvider _services;

    /// <summary>
    /// Transaction store — the write of the list of missing claims when a stored value vanished (E86).
    /// </summary>
    private readonly ITransactionStore _store;

    /// <summary>
    /// Transaction service — the one writer of the failed state, for a required claim that became
    /// unobtainable when its stored value vanished.
    /// </summary>
    private readonly ITransactionService _transactionService;

    /// <summary>
    /// Clock of the last sign-in time of a key and of the write metadata.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ClaimCompletionPlanner> _logger;

    /// <summary>
    /// Creates the planner.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="services">Services of the scope the planner is resolved from.</param>
    /// <param name="store">Transaction store.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ClaimCompletionPlanner(
        IEnumerable<IChannelAdapter> adapters,
        IServiceProvider services,
        ITransactionStore store,
        ITransactionService transactionService,
        TimeProvider timeProvider,
        ILogger<ClaimCompletionPlanner> logger)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Computes the claims missing at the moment the user confirms with the given channel snapshot.
    /// </summary>
    /// <remarks>
    /// The candidates are the elements of the requirement set the channel gave no value for (L43 — the
    /// projection the identity resolution issues claims from, read by
    /// <see cref="DefaultIdentityResolutionService.IsGiven"/>: an empty value is not given); a value the
    /// channel gave is never replaced by a stored one. The collected claims store, when registered, is read
    /// once for the candidates (C37): a stored value the rules accept (L58) is taken whatever the level and
    /// the path of the element (L42, L92), and a stored skip of an <c>Optional</c> element leaves it out
    /// (L75). Every other candidate is decided by <see cref="PlanItem"/>: <c>IfAvailable</c> asks nothing of
    /// the user, an element without a completion path is not asked for, the phone number is collected in
    /// the channel and narrowed by it (L92, L48), everything else on the web step.
    /// </remarks>
    /// <param name="transaction">Transaction being confirmed.</param>
    /// <param name="snapshot">Channel snapshot of the confirmation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The missing claims, phone step first, with the stored values the transaction hands out; or
    /// the verdict that a required claim is unobtainable.</returns>
    public async ValueTask<ClaimCompletionPlan> ComputeMissingAsync(
        Transaction transaction,
        ChannelIdentitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (transaction.ClaimRequirements.Count == 0)
        {
            return ClaimCompletionPlan.Nothing;
        }

        // Dropped adapter claims are reported once, by the resolution that issues the claims; this
        // reading of the same projection stays silent about them.
        var given = DefaultIdentityResolutionService.BuildClaims(snapshot, NullLogger.Instance);

        var candidates = transaction.ClaimRequirements
            .Where(requirement => !(given.TryGetValue(requirement.Claim, out var value)
                && DefaultIdentityResolutionService.IsGiven(value)))
            .ToList();

        var stored = candidates.Count == 0
            ? null
            : await ReadStoredRecordsAsync(transaction, snapshot, cancellationToken);

        var storedValues = new List<ClaimCompletionResult>();
        var phoneItems = new List<ClaimCompletionItem>();
        var webItems = new List<ClaimCompletionItem>();

        foreach (var requirement in candidates)
        {
            if (stored is not null && stored.TryGetValue(requirement.Claim, out var record))
            {
                if (record.Value is not null)
                {
                    if (await IsAcceptableAsync(transaction, record, cancellationToken))
                    {
                        storedValues.Add(record);
                        continue;
                    }
                }
                else if (requirement.Level is ClaimRequirementLevel.Optional)
                {
                    continue;
                }
            }

            var plan = PlanItem(requirement, snapshot.ChannelType);

            if (plan.Unobtainable)
            {
                return ClaimCompletionPlan.Unobtainable(requirement.Claim);
            }

            if (plan.Item is { Step: ClaimCompletionStep.Phone } phoneItem)
            {
                phoneItems.Add(phoneItem);
            }
            else if (plan.Item is { } webItem)
            {
                webItems.Add(webItem);
            }
        }

        return ClaimCompletionPlan.Of([.. phoneItems, .. webItems]) with
        {
            StoredValues = storedValues.AsReadOnly()
        };
    }

    /// <summary>
    /// The single check of the end of the claims completion: every missing claim of the transaction has
    /// an outcome.
    /// </summary>
    /// <remarks>
    /// A transaction handing out stored values checks first that they are still in the store (E86): the
    /// store is read again, and a value whose claim has no stored value any more is taken off the
    /// transaction, and its element, when it is to be asked for, joins the missing claims — by the same
    /// <see cref="PlanItem"/> the computation of the missing claims uses. That is one write with the token,
    /// made on the passed instance: after it the instance carries the token of that write, which is the
    /// one a confirmation that follows has to present. The answer says whose write the transaction carries:
    /// a take-off whose own write moved the current step answers
    /// <see cref="ClaimCompletionCheck.ValuesTakenOff"/>, a lost token <see cref="ClaimCompletionCheck.Conflict"/>.
    /// A required phone number the channel cannot ask for fails the transaction as unobtainable. A read of
    /// the store that fails takes nothing off.
    /// </remarks>
    /// <param name="transaction">Transaction in the claims completion, as it is stored now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether nothing is missing any more, and when something is, whose write the transaction
    /// carries.</returns>
    public async ValueTask<ClaimCompletionCheck> IsCompleteAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction.StoredClaimValues.Count > 0
            && transaction.ChannelIdentitySnapshot is { } snapshot
            && transaction.ClaimCompletion is { } completion)
        {
            // Nothing read (the store failed, or it is not registered any more) — nothing is taken off.
            var records = await ReadStoredRecordsAsync(transaction, snapshot, cancellationToken);
            var removed = records is null
                ? new List<ClaimCompletionResult>()
                : transaction.StoredClaimValues
                    .Where(value => !(records.TryGetValue(value.Claim, out var record) && record.Value is not null))
                    .ToList();

            if (removed.Count > 0)
            {
                return await TakeOffRemovedValuesAsync(transaction, snapshot, completion, removed, cancellationToken);
            }
        }

        return transaction.ClaimCompletion is { } current && !current.OpenItems().Any()
            ? ClaimCompletionCheck.Complete
            : ClaimCompletionCheck.Missing;
    }

    /// <summary>
    /// Takes the vanished stored values off the transaction (E86) and asks for their claims again, in one
    /// write with the token.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion, as it is stored now.</param>
    /// <param name="snapshot">Frozen channel snapshot of the transaction.</param>
    /// <param name="completion">Claims completion state of the transaction.</param>
    /// <param name="removed">Stored values whose claims have no stored value any more.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nothing is missing after the write, the write moved the current step, the token was lost,
    /// or the step stayed where it was or the transaction failed as unobtainable
    /// (<see cref="ClaimCompletionCheck.Missing"/>).</returns>
    private async ValueTask<ClaimCompletionCheck> TakeOffRemovedValuesAsync(
        Transaction transaction,
        ChannelIdentitySnapshot snapshot,
        ClaimCompletionState completion,
        IReadOnlyList<ClaimCompletionResult> removed,
        CancellationToken cancellationToken)
    {
        var added = new List<ClaimCompletionItem>();

        foreach (var value in removed)
        {
            var requirement = transaction.ClaimRequirements
                .FirstOrDefault(candidate => VeriqaClaimTypes.NameComparer.Equals(candidate.Claim, value.Claim));
            if (requirement is null)
            {
                continue;
            }

            var plan = PlanItem(requirement, snapshot.ChannelType);

            if (plan.Unobtainable)
            {
                _logger.LogInformation(
                    "A stored value vanished and its required claim cannot be obtained in the channel of the transaction. TransactionId: {TransactionId}, Claim: {Claim}, ChannelType: {ChannelType}",
                    transaction.Id.ToString(),
                    requirement.Claim,
                    snapshot.ChannelType);

                var failed = await _transactionService.FailTransactionAsync(
                    transaction.Id,
                    TransactionErrorCodes.RequiredClaimUnobtainable,
                    transaction.ConcurrencyToken,
                    cancellationToken);

                // A refusal means another writer moved the transaction first; the completion is not over
                // either way.
                if (failed.IsFailure)
                {
                    _logger.LogInformation(
                        "The transaction was not failed as unobtainable. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                        transaction.Id.ToString(),
                        failed.Error.Code);

                    return ClaimCompletionCheck.Conflict;
                }

                return ClaimCompletionCheck.Missing;
            }

            if (plan.Item is { } item
                && !completion.Items.Any(existing => VeriqaClaimTypes.NameComparer.Equals(existing.Claim, item.Claim)))
            {
                added.Add(item);
            }
        }

        var previousToken = transaction.ConcurrencyToken;
        var now = _timeProvider.GetUtcNow();
        var stepBeforeTakeOff = transaction.GetCurrentClaimCompletionStep();

        transaction.StoredClaimValues = transaction.StoredClaimValues
            .Where(value => !removed.Contains(value))
            .ToList()
            .AsReadOnly();

        // The phone step stays before the web step; OrderBy is stable, so the order within a step is kept.
        transaction.ClaimCompletion = completion with
        {
            Items = completion.Items.Concat(added).OrderBy(item => item.Step).ToList().AsReadOnly()
        };
        transaction.UpdatedAt = now;
        transaction.ConcurrencyToken = Guid.NewGuid().ToString("N");

        if (!await _store.UpdateAsync(transaction, previousToken, cancellationToken))
        {
            return ClaimCompletionCheck.Conflict;
        }

        // Claim names only: the values are never logged (SPEC-041 N68).
        _logger.LogInformation(
            "Stored claim values vanished from the collected claims store. TransactionId: {TransactionId}, RemovedClaims: {RemovedClaims}, AskedAgain: {AskedAgain}",
            transaction.Id.ToString(),
            string.Join(',', removed.Select(value => value.Claim)),
            string.Join(',', added.Select(item => item.Claim)));

        if (added.Count == 0 && !transaction.ClaimCompletion.OpenItems().Any())
        {
            return ClaimCompletionCheck.Complete;
        }

        // Only a write that moved the current step answers that it took the values off: a later take-off
        // that leaves the step where an earlier writer already moved it must not begin that step again.
        return transaction.GetCurrentClaimCompletionStep() != stepBeforeTakeOff
            ? ClaimCompletionCheck.ValuesTakenOff
            : ClaimCompletionCheck.Missing;
    }

    /// <summary>
    /// How one element of the requirement set is asked for when no value is at hand — the one rule both the
    /// computation of the missing claims and the return of a vanished stored value go through.
    /// </summary>
    /// <remarks>
    /// <c>IfAvailable</c> asks nothing of the user (L42), and neither does an element without a completion
    /// path (L92). The phone number is collected in the channel (step <see cref="ClaimCompletionStep.Phone"/>),
    /// everything else on the web step; a channel that cannot ask for the number leaves an <c>Optional</c>
    /// phone out, and makes a <c>Required</c> one unobtainable (L92, L48).
    /// </remarks>
    /// <param name="requirement">Element of the requirement set.</param>
    /// <param name="channelType">Channel type of the confirmation.</param>
    /// <returns>The missing claim to ask for, nothing, or the verdict that it is unobtainable.</returns>
    private ItemPlan PlanItem(ClaimRequirement requirement, string channelType)
    {
        if (requirement.Level is ClaimRequirementLevel.IfAvailable || !requirement.HasCompletionPath)
        {
            return ItemPlan.None;
        }

        if (!VeriqaClaimTypes.NameComparer.Equals(requirement.Claim, VeriqaClaimTypes.PhoneNumber))
        {
            return new ItemPlan(
                new ClaimCompletionItem(requirement.Claim, requirement.Level, ClaimCompletionStep.WebStep),
                Unobtainable: false);
        }

        if (CanRequestPhoneNumber(channelType))
        {
            return new ItemPlan(
                new ClaimCompletionItem(requirement.Claim, requirement.Level, ClaimCompletionStep.Phone),
                Unobtainable: false);
        }

        return requirement.Level is ClaimRequirementLevel.Required ? ItemPlan.CannotObtain : ItemPlan.None;
    }

    /// <summary>
    /// Reads the records of the collected claims store for the transaction — keyed by its tenant and the
    /// core subject of the snapshot (SPEC-041 C37) — refreshing the last sign-in time of the key.
    /// </summary>
    /// <remarks>
    /// Nothing is read without a registered store, or when the tenant of the transaction is unknown because
    /// its resolution failed: a null tenant would read the records of another tenant. A store that fails is
    /// logged and treated as absent (E62).
    /// </remarks>
    /// <param name="transaction">The transaction.</param>
    /// <param name="snapshot">Channel snapshot the core subject is taken from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The records by claim name (empty — the key has none); null — nothing was read.</returns>
    private async ValueTask<IReadOnlyDictionary<string, ClaimCompletionResult>?> ReadStoredRecordsAsync(
        Transaction transaction,
        ChannelIdentitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (transaction.HasFailedTenantResolution()
            || _services.GetService<ICollectedClaimsStore>() is not { } store)
        {
            return null;
        }

        IReadOnlyList<ClaimCompletionResult> records;

        try
        {
            records = await store.ReadForSignInAsync(
                transaction.GetTenantId(),
                CoreSubject.From(snapshot),
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (Exception exception)
            when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Only the cancellation of the caller propagates; a cancellation from inside the store (its own
            // timeout) is a store that did not answer.
            _logger.LogWarning(
                exception,
                "The collected claims store could not be read; the sign-in goes on as without it. TransactionId: {TransactionId}",
                transaction.Id.ToString());

            return null;
        }

        var byClaim = new Dictionary<string, ClaimCompletionResult>(VeriqaClaimTypes.NameComparer);

        foreach (var record in records)
        {
            byClaim[record.Claim] = record;
        }

        return byClaim;
    }

    /// <summary>
    /// Whether the rules in force accept a stored value for the transaction (SPEC-041 L58); without the
    /// rules registered, none is accepted.
    /// </summary>
    /// <param name="transaction">Transaction being confirmed.</param>
    /// <param name="record">Stored record carrying a value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — the value is handed out.</returns>
    private ValueTask<bool> IsAcceptableAsync(
        Transaction transaction,
        ClaimCompletionResult record,
        CancellationToken cancellationToken) =>
        _services.GetService<IStoredClaimValueRules>() is { } rules
            ? rules.IsAcceptableAsync(transaction, record, cancellationToken)
            : ValueTask.FromResult(false);

    /// <summary>
    /// Whether the channel can ask its user for the phone number (SPEC-041 C36).
    /// </summary>
    /// <param name="channelType">Channel type of the confirmation.</param>
    /// <returns>true when the adapter of the channel declares <see cref="PhoneNumberAcquisition.OnRequest"/>.</returns>
    private bool CanRequestPhoneNumber(string channelType)
        => _adapters.FirstOrDefault(adapter => string.Equals(adapter.ChannelType, channelType, StringComparison.Ordinal))
            is { } adapter
           && adapter.Capabilities.PhoneNumber.Acquisition.HasFlag(PhoneNumberAcquisition.OnRequest);

    /// <summary>
    /// How one element of the requirement set is asked for (<see cref="PlanItem"/>).
    /// </summary>
    /// <param name="Item">The missing claim to ask for; null — nothing is asked.</param>
    /// <param name="Unobtainable">A required claim nothing can obtain.</param>
    private readonly record struct ItemPlan(ClaimCompletionItem? Item, bool Unobtainable)
    {
        /// <summary>
        /// Nothing is asked.
        /// </summary>
        public static ItemPlan None => new(null, Unobtainable: false);

        /// <summary>
        /// A required claim nothing can obtain.
        /// </summary>
        public static ItemPlan CannotObtain => new(null, Unobtainable: true);
    }
}

/// <summary>
/// Outcome of <see cref="ClaimCompletionPlanner.ComputeMissingAsync"/>: the list of missing claims, or
/// the name of a required claim nothing can obtain.
/// </summary>
/// <param name="Items">Missing claims, phone step first; empty — nothing is missing.</param>
/// <param name="UnobtainableClaim">A <c>Required</c> claim without a way to obtain it; null — none.</param>
internal sealed record ClaimCompletionPlan(
    IReadOnlyList<ClaimCompletionItem> Items,
    string? UnobtainableClaim)
{
    /// <summary>
    /// Values from the collected claims store the transaction hands out instead of asking for them
    /// (SPEC-041 C37); the confirmation writes them to <see cref="Transaction.StoredClaimValues"/> with the
    /// same write as the completion mark or the confirmation itself.
    /// </summary>
    public IReadOnlyList<ClaimCompletionResult> StoredValues { get; init; } = [];

    /// <summary>
    /// Nothing is missing.
    /// </summary>
    public static ClaimCompletionPlan Nothing { get; } = new(Array.Empty<ClaimCompletionItem>(), null);

    /// <summary>
    /// The plan of the given missing claims.
    /// </summary>
    /// <param name="items">Missing claims.</param>
    /// <returns>The plan.</returns>
    public static ClaimCompletionPlan Of(IReadOnlyList<ClaimCompletionItem> items) => new(items, null);

    /// <summary>
    /// The plan of a required claim nothing can obtain.
    /// </summary>
    /// <param name="claim">Name of the claim.</param>
    /// <returns>The plan.</returns>
    public static ClaimCompletionPlan Unobtainable(string claim) => new(Array.Empty<ClaimCompletionItem>(), claim);
}
