// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Authentication / confirmation transaction model.
/// The central entity of the Transaction Engine. Contains all lifecycle fields,
/// snapshot data and metadata for optimistic concurrency.
/// </summary>
public sealed class Transaction
{
    /// <summary>
    /// Unique transaction identifier. Base62, 43 characters.
    /// Generated at creation. Immutable.
    /// </summary>
    public required TransactionId Id { get; init; }

    /// <summary>
    /// Transaction type (login, confirmation). Immutable after creation.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Current state of the state machine.
    /// </summary>
    public TransactionState State { get; internal set; }

    /// <summary>
    /// Reason code for terminal states (Failed, Expired).
    /// Null for non-terminal states.
    /// </summary>
    public string? StateReasonCode { get; internal set; }

    /// <summary>
    /// Transaction creation time (UTC). Immutable.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Time of the last state update (UTC).
    /// Updated on every transition.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; internal set; }

    /// <summary>
    /// TTL expiration time (UTC). Computed as CreatedAt + TTL. Immutable.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Client request identifier for end-to-end tracing.
    /// Provided by the client at creation.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Idempotency key. Provided by the client at creation.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Idempotency scope — a stable identifier of the calling client.
    /// Determined by the Engine from the authentication context.
    /// </summary>
    public string? IdempotencyScope { get; init; }

    /// <summary>
    /// Channel type explicitly requested by the client. Null — all allowed ones are available.
    /// </summary>
    public string? RequestedChannelType { get; init; }

    /// <summary>
    /// Allowed channel types for this transaction.
    /// Default — all registered channels.
    /// Stored as a truly immutable collection (FrozenSet).
    /// </summary>
    public required IReadOnlySet<string> AllowedChannelTypes { get; init; }

    /// <summary>
    /// Arbitrary client data associated with the transaction (raw JSON string).
    /// Not interpreted by the Transaction Engine. Maximum size: 4 KB.
    /// Stored as a string for clear ownership (no IDisposable dependency on JsonDocument).
    /// </summary>
    public string? ClientContext { get; init; }

    /// <summary>
    /// Action data for confirmation (confirmation type).
    /// Populated at creation.
    /// </summary>
    public ConfirmationSnapshot? ConfirmationSnapshot { get; init; }

    /// <summary>
    /// User data received from the channel on confirmation.
    /// Populated on transition to Confirmed.
    /// </summary>
    public ChannelIdentitySnapshot? ChannelIdentitySnapshot { get; internal set; }

    /// <summary>
    /// Channel subject the transaction is bound to (SPEC-007 UI-018, UI-022; SPEC-039 N34); null — no
    /// binding was written, which is the case of every transaction stored before the field existed and
    /// of every one confirmed without a prior binding.
    /// </summary>
    /// <remarks>
    /// Written by the engine through an ordinary <c>ITransactionStore.UpdateAsync</c>: by
    /// <c>ITransactionService.BindChannelSubjectAsync</c> and together with the snapshot by
    /// <c>ITransactionService.AttachChannelIdentityAsync</c>. Read it through
    /// <see cref="IsBoundToAnotherSubject"/>: without a binding, the subject of
    /// <see cref="ChannelIdentitySnapshot"/> is the bound one.
    /// </remarks>
    public ChannelSubjectBinding? SubjectBinding { get; internal set; }

    /// <summary>
    /// Final resolved identity. Populated on transition to Completed.
    /// </summary>
    public ResolvedIdentitySnapshot? ResolvedIdentitySnapshot { get; internal set; }

    /// <summary>
    /// Final transaction completion data. Populated at Completed.
    /// </summary>
    public CompletionSnapshot? CompletionSnapshot { get; internal set; }

    /// <summary>
    /// Optimistic concurrency token. Updated on every change.
    /// </summary>
    public string ConcurrencyToken { get; internal set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// OIDC request context. Populated for transactions initiated by the OIDC flow.
    /// </summary>
    public OidcContext? OidcContext { get; init; }

    /// <summary>
    /// Request context of a transaction created outside the OIDC flow (SPEC-039 C19).
    /// Populated once at creation; null — the transaction states no such context.
    /// Read it through <c>TransactionContextExtensions</c>, never directly.
    /// </summary>
    public TransactionRequestContext? RequestContext { get; init; }

    /// <summary>
    /// Container of the check between the confirming party and the relying party's expectations
    /// (SPEC-039 C23). Populated at creation; null — no expectations were stated.
    /// </summary>
    /// <remarks>
    /// The setter is internal for the same reason the lifecycle fields above carry one: the container
    /// changes exactly twice after creation, and both moves are the engine's own — the verdict is
    /// written where the resolved identity is (L41), and the expectations are erased by the transition
    /// into a terminal state, whichever one it is.
    /// </remarks>
    public IdentityMatchState? IdentityMatch { get; internal set; }

    /// <summary>
    /// Transaction initiator context (SPEC-017, field #19 of SPEC-001 §3.1).
    /// Populated once at creation when Initiator Context Check is enabled;
    /// null — the context was not collected. Immutable after creation (ICC-030).
    /// </summary>
    public InitiatorContextSnapshot? InitiatorContextSnapshot { get; init; }

    /// <summary>
    /// Requirement set of the transaction (SPEC-041 C35): the claims the sign-in has to deliver, each
    /// with its level and the source of that level. Written once at creation and never changed after
    /// it — a configuration changed during the transaction does not reach it (SPEC-041 E63).
    /// </summary>
    /// <remarks>
    /// Never null; at most one element per claim name (compared case-insensitively, as claim names
    /// are); the order carries no meaning. Empty — the transaction states no requirements, which is
    /// the case of every transaction created outside the authorization endpoint and of every one
    /// stored before the field existed.
    /// </remarks>
    public IReadOnlyList<ClaimRequirement> ClaimRequirements { get; init; } = [];

    /// <summary>
    /// Claims completion fields (SPEC-041 C90); null — the user has not confirmed the sign-in while
    /// claims were missing, which is the case of every transaction stored before the field existed.
    /// </summary>
    /// <remarks>
    /// The mark and the list of missing claims are written by the engine together with the channel
    /// snapshot, through an ordinary <c>ITransactionStore.UpdateAsync</c>. The surface of the web step,
    /// the outcomes and the form token fingerprint are written only by the conditional writes of the store
    /// (<c>ITransactionStore.TrySetWebStepSurfaceAsync</c>, <c>TryRecordClaimResultAsync</c>,
    /// <c>TrySetClaimFormTokenHashAsync</c>), and <c>UpdateAsync</c> ignores whatever values a passed
    /// transaction carries in them.
    /// </remarks>
    public ClaimCompletionState? ClaimCompletion { get; internal set; }

    /// <summary>
    /// Values from the collected claims store this transaction hands out (SPEC-041 C37): read before the
    /// user confirms the sign-in and kept here, so a deletion from the store after the confirmation does
    /// not change the tokens of this sign-in. Only records carrying a value — a skip is never handed out.
    /// </summary>
    /// <remarks>
    /// Never null; empty — nothing was taken from the store, which is the case of every transaction without
    /// a registered store and of every one stored before the field existed. Written by the engine through
    /// an ordinary <c>ITransactionStore.UpdateAsync</c>, like any other field.
    /// </remarks>
    public IReadOnlyList<ClaimCompletionResult> StoredClaimValues { get; internal set; } = [];

    /// <summary>
    /// Moment the authorization code of this sign-in was issued to the client (UTC); null — no code was
    /// issued. Written once, on a Completed transaction, and never cleared: a transaction carrying it
    /// issues no second code.
    /// </summary>
    /// <remarks>
    /// An ordinary field, written by the engine through <c>ITransactionStore.UpdateAsync</c> under the
    /// concurrency token (<c>ITransactionService.TryMarkAuthorizationCodeIssuedAsync</c>). The mark does
    /// not change <see cref="State"/> and does not move <see cref="UpdatedAt"/>, so the retention of the
    /// transaction keeps counting from its completion.
    /// </remarks>
    public DateTimeOffset? AuthorizationCodeIssuedAt { get; internal set; }

    /// <summary>
    /// Context of the device that scanned the hop link (SPEC-025 §8.1). Null — not captured.
    /// A separate observation point from <see cref="InitiatorContextSnapshot"/> (HOP-093).
    /// </summary>
    /// <remarks>
    /// This field and the four hop marks below are observations, not state.
    /// <c>ITransactionStore.AddAsync</c> stores the values a new transaction carries in them; once it is
    /// stored, a store writes them only through its one-time conditional writes
    /// (<c>ITransactionStore.TryAttachScannerContextAsync</c>, <c>TryFillScannerFingerprintAsync</c>,
    /// <c>TryMarkHopObservationAsync</c>), and <c>ITransactionStore.UpdateAsync</c> ignores whatever
    /// values a passed transaction carries in them.
    /// </remarks>
    public ScannerContextSnapshot? ScannerContextSnapshot { get; init; }

    /// <summary>
    /// Moment of the first human resolve of the hop link (UTC); null — none yet (HOP-071a).
    /// </summary>
    public DateTimeOffset? HopFirstResolvedAt { get; init; }

    /// <summary>
    /// Moment the interstitial page reported being shown (UTC); null — no such signal (HOP-190).
    /// </summary>
    public DateTimeOffset? HopInterstitialOpenedAt { get; init; }

    /// <summary>
    /// Moment the browser started the transition into the channel, by itself or by a tap (UTC); null — no
    /// such signal (HOP-190).
    /// </summary>
    public DateTimeOffset? HopChannelLaunchedAt { get; init; }

    /// <summary>
    /// Moment a human resolve of the hop link found the first resolve already marked and came from a
    /// browser without a valid browser mark of this transaction (UTC); null — no such resolve (SPEC-041 L93).
    /// A risk signal only: it grants and denies nothing.
    /// </summary>
    public DateTimeOffset? HopRepeatResolvedAt { get; init; }

    /// <summary>
    /// The five hop observations of this transaction as one value.
    /// </summary>
    internal HopObservations HopObservations => new(
        ScannerContextSnapshot,
        HopFirstResolvedAt,
        HopInterstitialOpenedAt,
        HopChannelLaunchedAt,
        HopRepeatResolvedAt);

    /// <summary>
    /// Restores a transaction from its stored state — the published path for a store implementation,
    /// including one written outside the Veriqa assemblies.
    /// </summary>
    /// <remarks>
    /// Restoring is not a transition: the state, the timestamps and the concurrency token are taken
    /// from the snapshot exactly as they were stored, and no new token is generated. Moving a
    /// transaction remains the sole business of <see cref="TransactionStateMachine"/>.
    /// <see cref="AllowedChannelTypes"/> is frozen here, so the caller may pass any read-only set and
    /// a later mutation of it cannot leak into a transaction that is immutable by contract.
    /// <see cref="ClaimRequirements"/> is copied into a read-only list for the same reason; a null there
    /// is read as the empty set rather than refused, because a document stored before the field
    /// existed legitimately carries none.
    /// <para>
    /// The members the snapshot declares non-nullable are checked, not assumed. <c>required</c> makes
    /// the compiler demand that a member be <i>assigned</i>, and nullable reference types are a
    /// compile-time analysis only — neither survives a deserializer. A store rehydrating a document
    /// whose field is missing or explicitly null therefore reaches this method with a null in a
    /// non-nullable member, and the guards turn that into a named
    /// <see cref="ArgumentNullException"/> at the boundary instead of an opaque failure later.
    /// </para>
    /// </remarks>
    /// <param name="snapshot">Complete stored state of the transaction.</param>
    /// <returns>Transaction carrying exactly the state the snapshot states.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="snapshot"/> is null, or one of its non-nullable members
    /// (<see cref="TransactionSnapshot.Type"/>, <see cref="TransactionSnapshot.AllowedChannelTypes"/>,
    /// <see cref="TransactionSnapshot.ConcurrencyToken"/>) carries null.
    /// </exception>
    public static Transaction Restore(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(
            snapshot.Type, $"{nameof(snapshot)}.{nameof(snapshot.Type)}");
        ArgumentNullException.ThrowIfNull(
            snapshot.AllowedChannelTypes,
            $"{nameof(snapshot)}.{nameof(snapshot.AllowedChannelTypes)}");
        ArgumentNullException.ThrowIfNull(
            snapshot.ConcurrencyToken,
            $"{nameof(snapshot)}.{nameof(snapshot.ConcurrencyToken)}");

        return new Transaction
        {
            Id = snapshot.Id,
            Type = snapshot.Type,
            State = snapshot.State,
            StateReasonCode = snapshot.StateReasonCode,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.UpdatedAt,
            ExpiresAt = snapshot.ExpiresAt,
            CorrelationId = snapshot.CorrelationId,
            IdempotencyKey = snapshot.IdempotencyKey,
            IdempotencyScope = snapshot.IdempotencyScope,
            RequestedChannelType = snapshot.RequestedChannelType,
            AllowedChannelTypes = snapshot.AllowedChannelTypes.ToFrozenSet(StringComparer.Ordinal),
            ClientContext = snapshot.ClientContext,
            ConfirmationSnapshot = snapshot.ConfirmationSnapshot,
            ChannelIdentitySnapshot = snapshot.ChannelIdentitySnapshot,
            SubjectBinding = snapshot.SubjectBinding,
            ResolvedIdentitySnapshot = snapshot.ResolvedIdentitySnapshot,
            CompletionSnapshot = snapshot.CompletionSnapshot,
            ConcurrencyToken = snapshot.ConcurrencyToken,
            OidcContext = snapshot.OidcContext,
            RequestContext = snapshot.RequestContext,
            IdentityMatch = snapshot.IdentityMatch,
            InitiatorContextSnapshot = snapshot.InitiatorContextSnapshot,
            ClaimRequirements = FreezeClaimRequirements(snapshot.ClaimRequirements),
            ClaimCompletion = FreezeClaimCompletion(snapshot.ClaimCompletion),
            StoredClaimValues = FreezeStoredClaimValues(snapshot.StoredClaimValues),
            AuthorizationCodeIssuedAt = snapshot.AuthorizationCodeIssuedAt,
            ScannerContextSnapshot = snapshot.ScannerContextSnapshot,
            HopFirstResolvedAt = snapshot.HopFirstResolvedAt,
            HopInterstitialOpenedAt = snapshot.HopInterstitialOpenedAt,
            HopChannelLaunchedAt = snapshot.HopChannelLaunchedAt,
            HopRepeatResolvedAt = snapshot.HopRepeatResolvedAt
        };
    }

    /// <summary>
    /// Takes the complete state of the transaction as one object — the symmetric half of
    /// <see cref="Restore"/>, so a store writes a transaction out without reflecting over properties.
    /// </summary>
    /// <returns>Snapshot carrying every field of the transaction.</returns>
    public TransactionSnapshot ToSnapshot() => ToSnapshot(HopObservations, ClaimCompletionProgress);

    /// <summary>
    /// Takes the complete state of the transaction with the fields written by conditional writes
    /// replaced — for a store that keeps them apart from the transaction record and lays its own values
    /// over the record.
    /// </summary>
    /// <param name="observations">Hop observations the snapshot carries instead of this transaction's.</param>
    /// <param name="progress">
    /// Surface, outcomes and form token fingerprint of the claims completion the snapshot carries instead
    /// of this transaction's; ignored when the transaction carries no completion mark.
    /// </param>
    /// <returns>
    /// Snapshot carrying every field of the transaction, <paramref name="observations"/> and
    /// <paramref name="progress"/>.
    /// </returns>
    internal TransactionSnapshot ToSnapshot(HopObservations observations, ClaimCompletionProgress progress) => new()
    {
        Id = Id,
        Type = Type,
        State = State,
        StateReasonCode = StateReasonCode,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ExpiresAt = ExpiresAt,
        CorrelationId = CorrelationId,
        IdempotencyKey = IdempotencyKey,
        IdempotencyScope = IdempotencyScope,
        RequestedChannelType = RequestedChannelType,
        AllowedChannelTypes = AllowedChannelTypes,
        ClientContext = ClientContext,
        ConfirmationSnapshot = ConfirmationSnapshot,
        ChannelIdentitySnapshot = ChannelIdentitySnapshot,
        SubjectBinding = SubjectBinding,
        ResolvedIdentitySnapshot = ResolvedIdentitySnapshot,
        CompletionSnapshot = CompletionSnapshot,
        ConcurrencyToken = ConcurrencyToken,
        OidcContext = OidcContext,
        RequestContext = RequestContext,
        IdentityMatch = IdentityMatch,
        InitiatorContextSnapshot = InitiatorContextSnapshot,
        ClaimRequirements = ClaimRequirements,
        ClaimCompletion = ClaimCompletion is null
            ? null
            : ClaimCompletion with
            {
                Surface = progress.Surface,
                Results = progress.Results,
                FormTokenHash = progress.FormTokenHash
            },
        StoredClaimValues = StoredClaimValues,
        AuthorizationCodeIssuedAt = AuthorizationCodeIssuedAt,
        ScannerContextSnapshot = observations.ScannerContextSnapshot,
        HopFirstResolvedAt = observations.FirstResolvedAt,
        HopInterstitialOpenedAt = observations.InterstitialOpenedAt,
        HopChannelLaunchedAt = observations.ChannelLaunchedAt,
        HopRepeatResolvedAt = observations.RepeatResolvedAt
    };

    /// <summary>
    /// Surface, outcomes and form token fingerprint of the claims completion of this transaction as one
    /// value; none of them when the transaction carries no completion mark.
    /// </summary>
    internal ClaimCompletionProgress ClaimCompletionProgress => ClaimCompletion is null
        ? ClaimCompletionProgress.None
        : new(ClaimCompletion.Surface, ClaimCompletion.Results, ClaimCompletion.FormTokenHash);

    /// <summary>
    /// Copies a requirement set into a list nobody holds a mutable reference to — the transaction is
    /// immutable in this field by contract (SPEC-041 E63), and a store shares the list by reference.
    /// </summary>
    /// <param name="requirements">Requirement set as it was passed; null — none.</param>
    /// <returns>Read-only copy of the set; the empty set for null or empty input.</returns>
    internal static IReadOnlyList<ClaimRequirement> FreezeClaimRequirements(
        IReadOnlyList<ClaimRequirement>? requirements)
    {
        if (requirements is null || requirements.Count == 0)
        {
            return Array.Empty<ClaimRequirement>();
        }

        return Array.AsReadOnly(requirements.ToArray());
    }

    /// <summary>
    /// Copies the lists of the claims completion fields into lists nobody holds a mutable reference to
    /// — a store shares the fields by reference, and the lists come from outside code. A missing list
    /// is read as an empty one rather than refused: a document of a store written outside the Veriqa
    /// assemblies may omit the outcomes nobody recorded yet.
    /// </summary>
    /// <param name="completion">Completion fields as they were passed; null — none.</param>
    /// <returns>The fields with read-only copies of both lists, or null.</returns>
    internal static ClaimCompletionState? FreezeClaimCompletion(ClaimCompletionState? completion)
    {
        if (completion is null)
        {
            return null;
        }

        return completion with
        {
            Items = completion.Items is { Count: > 0 } items
                ? Array.AsReadOnly(items.ToArray())
                : Array.Empty<ClaimCompletionItem>(),
            Results = completion.Results is { Count: > 0 } results
                ? Array.AsReadOnly(results.ToArray())
                : Array.Empty<ClaimCompletionResult>()
        };
    }

    /// <summary>
    /// Copies the values taken from the collected claims store into a list nobody holds a mutable
    /// reference to — a store shares the field by reference, and the list comes from outside code. A
    /// null is read as the empty list rather than refused: a document stored before the field existed
    /// legitimately carries none.
    /// </summary>
    /// <param name="values">Values as they were passed; null — none.</param>
    /// <returns>Read-only copy of the values; the empty list for null or empty input.</returns>
    internal static IReadOnlyList<ClaimCompletionResult> FreezeStoredClaimValues(
        IReadOnlyList<ClaimCompletionResult>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Array.Empty<ClaimCompletionResult>();
        }

        return Array.AsReadOnly(values.ToArray());
    }

    /// <summary>
    /// Checks whether the transaction is in a terminal state.
    /// </summary>
    /// <returns>true if the state is terminal (Completed, Expired, Failed).</returns>
    public bool IsTerminal() => State is TransactionState.Completed
        or TransactionState.Expired
        or TransactionState.Failed;

    /// <summary>
    /// Checks whether the core is waiting for the user's answer on its own confirmation page.
    /// </summary>
    /// <remarks>
    /// The single expression of that state, used by every reader (the status endpoint, the authorize
    /// callback). The channel snapshot is written before the state machine moves by
    /// <c>ITransactionService.AttachChannelIdentityAsync</c> and by the confirmation that sets the
    /// claims completion mark; every other path populates it together with the transition to
    /// Confirmed. So "still Pending, the channel data is already here, and no completion mark"
    /// unambiguously means the question has been handed over to the core page — with the mark, the
    /// user has already answered and the transaction waits for missing claims instead
    /// (<see cref="IsCompletingClaims"/>, SPEC-041 L91).
    /// </remarks>
    /// <returns>true when the transaction waits for a web answer.</returns>
    public bool IsAwaitingWebConfirmation() =>
        State is TransactionState.Pending && ChannelIdentitySnapshot is not null && ClaimCompletion is null;

    /// <summary>
    /// Checks whether the transaction waits for missing claims: the user confirmed the sign-in, and
    /// the transaction stays Pending until the claims are collected or skipped (SPEC-041 L91).
    /// </summary>
    /// <returns>true while the claims completion is in progress.</returns>
    public bool IsCompletingClaims() => State is TransactionState.Pending && ClaimCompletion is not null;

    /// <summary>
    /// Checks whether the transaction waits for the answer in the channel: a sender entered it, and
    /// neither the decision nor a hand-over of the question to the core page has been written yet.
    /// </summary>
    /// <remarks>
    /// The single expression of "the bot reacted to the start", shared by the live signal and by its
    /// replay to a page that connects later, so the two cannot disagree. It reads facts every store
    /// already keeps: the subject binding is written when the sender enters the transaction, the
    /// channel snapshot — when the question goes to the core page (<see cref="IsAwaitingWebConfirmation"/>)
    /// or the decision arrives, the completion mark — when the user has confirmed
    /// (<see cref="IsCompletingClaims"/>). A question that failed to be sent after the binding is not
    /// told apart from one that was: the binding is the only fact common to every channel.
    /// </remarks>
    /// <returns>true while the answer is awaited in the channel.</returns>
    public bool IsAwaitingAnswerInChannel() =>
        State is TransactionState.Pending
        && SubjectBinding is not null
        && ChannelIdentitySnapshot is null
        && ClaimCompletion is null;

    /// <summary>
    /// Checks whether the transaction is bound to a channel subject other than the sender
    /// (SPEC-007 UI-022, SPEC-039 N34): such a sender's action is refused and changes nothing.
    /// </summary>
    /// <remarks>
    /// The bound subject is <see cref="SubjectBinding"/>, and without one — the subject of
    /// <see cref="ChannelIdentitySnapshot"/>: a transaction stored before the binding existed, or
    /// confirmed without a prior binding, is bound to whoever its snapshot names. A transaction with
    /// neither is bound to nobody, and the first sender binds it.
    /// </remarks>
    /// <param name="sender">Channel snapshot of the sender.</param>
    /// <returns>true when a subject is bound and the sender is not that subject.</returns>
    public bool IsBoundToAnotherSubject(ChannelIdentitySnapshot sender)
    {
        ArgumentNullException.ThrowIfNull(sender);

        return BoundSubject is { } bound && !bound.IsSameSubject(sender);
    }

    /// <summary>
    /// Checks whether the transaction is bound to exactly the sender's channel subject.
    /// </summary>
    /// <param name="sender">Channel snapshot of the sender.</param>
    /// <returns>true when a subject is bound and the sender is that subject.</returns>
    internal bool IsBoundTo(ChannelIdentitySnapshot sender)
        => BoundSubject is { } bound && bound.IsSameSubject(sender);

    /// <summary>
    /// The bound channel subject: <see cref="SubjectBinding"/>, or the subject of
    /// <see cref="ChannelIdentitySnapshot"/> when no binding was written; null — nobody is bound.
    /// </summary>
    private ChannelSubjectBinding? BoundSubject => SubjectBinding
        ?? (ChannelIdentitySnapshot is { } snapshot ? ChannelSubjectBinding.Of(snapshot, answerBrowserBound: false) : null);

    /// <summary>
    /// The step of the claims completion the transaction is on: the step of the first missing claim
    /// without an outcome, the phone step before the web step (SPEC-041 L91).
    /// </summary>
    /// <returns>
    /// The current step; null — the transaction is not in the claims completion, or every missing
    /// claim already has an outcome.
    /// </returns>
    public ClaimCompletionStep? GetCurrentClaimCompletionStep()
    {
        if (!IsCompletingClaims())
        {
            return null;
        }

        ClaimCompletionStep? current = null;

        foreach (var item in ClaimCompletion!.OpenItems())
        {
            if (current is null || item.Step < current)
            {
                current = item.Step;
            }
        }

        return current;
    }

    /// <summary>
    /// Checks whether the transaction TTL has expired.
    /// </summary>
    /// <param name="now">Current point in time.</param>
    /// <returns>true if the TTL has expired.</returns>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>
    /// Checks whether running out of time is still what ends this transaction.
    /// </summary>
    /// <remarks>
    /// True in Created and Pending only. A transaction the user has already confirmed is past the
    /// part its TTL guards — the answer is in, and only finalization is left, a wait the TTL does
    /// not govern (SPEC-001 §4.4 item 4: expiry never happens out of Confirmed).
    /// So its deadline passing states nothing about it, and a reader must not read it as an ending.
    /// What does end that wait is the finalization timeout, and it is the STORE that enforces it —
    /// see the remarks on <c>ITransactionStore.GetStalledConfirmedAsync</c> for the store that does
    /// not, where a confirmed transaction is bounded by nothing.
    /// <para>
    /// This is NOT the complement of <see cref="IsTerminal"/>, and the difference is the whole point:
    /// Confirmed is neither terminal nor expirable. Pair the answer with <see cref="IsExpired"/> —
    /// this one says whether the deadline decides anything, that one whether it has passed.
    /// </para>
    /// </remarks>
    /// <returns>true when the TTL of this transaction may still end it.</returns>
    public bool IsSubjectToExpiry() => TransactionStateMachine.ExpirableStates.Contains(State);

    /// <summary>
    /// Checks whether running out of time is what ENDED this transaction — in either of the two
    /// forms that one fact takes on the two sides of the cleanup pass.
    /// </summary>
    /// <remarks>
    /// Before the pass the transaction is still subject to expiry and its deadline is simply behind;
    /// after it, the state itself is the record of the expiry. Any other state is not an expiry,
    /// whatever its deadline says: a decision was recorded on it, and that is a different answer.
    /// That includes Confirmed, which is not terminal yet is past the part the TTL guards
    /// (SPEC-001 §4.4 item 4: expiry never happens out of Confirmed), and Failed, whose deadline
    /// passing afterwards changes nothing about how it ended.
    /// <para>
    /// This is the question asked by everything that ANSWERS about a closed window — the channel
    /// pipeline before it writes anything, and the confirmation write itself — so it is asked in one
    /// place. Two spellings of it answer one question with two different things: a decision already
    /// recorded is told as a decision by the button that races the write and as an expiry by the
    /// button that arrives after the deadline.
    /// </para>
    /// <para>
    /// It is NOT the question <c>ITransactionService.GetTransactionAsync</c> asks. That one decides
    /// whether to withhold a transaction whose fate the deadline still decides, and hands back a
    /// transaction already IN Expired with the state it is in — pairing
    /// <see cref="IsSubjectToExpiry"/> with <see cref="IsExpired"/> and nothing else.
    /// </para>
    /// </remarks>
    /// <param name="now">Current point in time.</param>
    /// <returns>true when running out of time is how this transaction ended.</returns>
    public bool HasRunOutOfTime(DateTimeOffset now)
        => State is TransactionState.Expired || (IsSubjectToExpiry() && IsExpired(now));
}
