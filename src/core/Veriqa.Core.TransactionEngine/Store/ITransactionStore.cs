// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Store;

/// <summary>
/// Transaction store abstraction.
/// Defines the contract for all storage implementations (in-memory, EF Core, Redis).
/// Only one implementation may be registered at a time.
/// </summary>
public interface ITransactionStore
{
    /// <summary>
    /// Adds a new transaction to the store.
    /// </summary>
    /// <remarks>
    /// The hop observations the transaction carries are stored with it; once it is stored, they are
    /// set only by the one-time writes (see <see cref="TryMarkHopObservationAsync"/>).
    /// </remarks>
    /// <param name="transaction">Transaction to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the completion of the operation.</returns>
    /// <exception cref="DuplicateIdempotencyKeyException">
    /// A transaction with the same (<see cref="Transaction.IdempotencyScope"/>,
    /// <see cref="Transaction.IdempotencyKey"/>) pair is already stored. Throwing is an obligation of
    /// the implementation, not an option: idempotency in the engine rests on this exception, and the
    /// caller answers it by re-reading the stored transaction. An implementation that instead returns
    /// the existing transaction, or silently overwrites it, breaks idempotency without any failure to
    /// observe. The check must be atomic with the insert (a unique index, a conditional write), not a
    /// read followed by a write — otherwise two concurrent requests both pass it.
    /// </exception>
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a transaction by its identifier.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction, or null if not found.</returns>
    Task<Transaction?> GetByIdAsync(TransactionId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a transaction by the IdempotencyScope + IdempotencyKey pair.
    /// </summary>
    /// <remarks>
    /// An implementation is required even though no shipped entry point fills the pair in: the
    /// creation path calls this method whenever a caller supplies one, and the s2s entry of
    /// SPEC-039 is where that becomes the normal case. Returning null unconditionally would turn a
    /// repeated request into a second transaction silently.
    /// </remarks>
    /// <param name="scope">Idempotency scope.</param>
    /// <param name="key">Idempotency key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction, or null if not found.</returns>
    Task<Transaction?> GetByIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a transaction with a ConcurrencyToken check.
    /// Returns true on a successful update, false on a conflict.
    /// </summary>
    /// <remarks>
    /// A state the stored transaction cannot reach must not be written. The state machine decides
    /// what it can reach — <see cref="TransactionStateMachine.CanTransition"/> over the state
    /// currently stored and the state the passed transaction carries — and a state equal to the
    /// stored one is not a transition at all, so a write that changes only other fields passes.
    /// Refuse the forbidden write by throwing <see cref="InvalidStateTransitionException"/>, never by
    /// returning <see langword="false"/>: <see langword="false"/> means, and keeps meaning, only a
    /// lost optimistic lock or a transaction that is no longer stored, and a caller answers it by
    /// re-reading and retrying — which for a forbidden transition never terminates.
    /// <para>
    /// The two refusals do not compete: throw only when <paramref name="expectedConcurrencyToken"/>
    /// still matches the stored token, that is, when the write would otherwise have been applied. A
    /// caller working off a stale copy has already lost the optimistic lock, and the transition its
    /// copy appears to make is an artefact of that copy rather than a broken invariant — answer it
    /// with <see langword="false"/>, the lost-lock answer. Retrying terminates in that case: the re-read copy
    /// carries the state actually stored, over which the state machine refuses an impossible move up
    /// front.
    /// </para>
    /// <para>
    /// A store composed through <c>AddVeriqaTransactionEngine</c> is given this check by the engine
    /// and need not repeat it. The obligation is stated here for a store reached by any other path.
    /// </para>
    /// <para>
    /// The hop observations of the passed transaction are ignored: the write neither sets nor erases
    /// them (see <see cref="TryMarkHopObservationAsync"/>). The same holds for the surface of the web
    /// step, the outcomes of the claims completion and the fingerprint of its form token (see
    /// <see cref="TrySetWebStepSurfaceAsync"/>, <see cref="TrySetClaimFormTokenHashAsync"/>); the completion mark and the list of missing claims (<see cref="ClaimCompletionState.ConfirmedAt"/>,
    /// <see cref="ClaimCompletionState.Items"/>) are ordinary fields and are written, and so are the values
    /// taken from the collected claims store (<see cref="Transaction.StoredClaimValues"/>) and the bound
    /// channel subject (<see cref="Transaction.SubjectBinding"/>).
    /// </para>
    /// </remarks>
    /// <param name="transaction">Updated transaction.</param>
    /// <param name="expectedConcurrencyToken">Expected ConcurrencyToken.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true if the update was applied; false on a concurrency conflict.</returns>
    /// <exception cref="InvalidStateTransitionException">
    /// The write would move the transaction along a transition the state machine does not allow.
    /// </exception>
    Task<bool> UpdateAsync(Transaction transaction, string expectedConcurrencyToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a transaction (for background cleanup).
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the completion of the operation.</returns>
    Task DeleteAsync(TransactionId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a batch of transactions the passing of <c>ExpiresAt</c> ends: those whose state is one
    /// <see cref="TransactionStateMachine.ExpirableStates"/> holds, with a deadline at or before
    /// <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// "Not terminal" is a wider condition and the wrong one: a confirmed transaction is neither
    /// terminal nor ended by its deadline (SPEC-001 §4.4 item 4 — the answer is in, and only
    /// finalization is left, a wait the TTL does not govern). A store selecting by it hands back
    /// records the caller can do nothing with, and they take the slots of the batch away from the
    /// transactions that do have to be expired. A store that cannot ask a rehydrated transaction
    /// (<c>Transaction.IsSubjectToExpiry</c>) — one selecting over stored state names — asks
    /// <see cref="TransactionStateMachine.ExpirableStates"/> instead of writing the set out a second
    /// time.
    /// </remarks>
    /// <param name="now">Current point in time.</param>
    /// <param name="batchSize">Maximum batch size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of expired transactions.</returns>
    Task<IReadOnlyList<Transaction>> GetExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets terminal transactions ready for cleanup.
    /// </summary>
    /// <param name="olderThan">Transactions updated earlier than this moment.</param>
    /// <param name="batchSize">Maximum batch size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of transactions to delete.</returns>
    Task<IReadOnlyList<Transaction>> GetStaleTerminalAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets transactions in the Confirmed state that have exceeded the finalization timeout.
    /// </summary>
    /// <remarks>
    /// This selection is the ONLY thing that bounds a confirmed transaction in time: its TTL no
    /// longer decides anything about it (SPEC-001 §4.4 item 4), so what ends the wait is the sweep
    /// reading this member and writing Failed with <c>finalization_timeout</c> — a selection every
    /// store owes (SPEC-001 §8.3 item 3). The threshold is the state and the moment of the last
    /// write: a transaction is selected when it is <c>Confirmed</c> and its <c>UpdatedAt</c> is
    /// earlier than <paramref name="confirmedBefore"/>.
    /// </remarks>
    /// <param name="confirmedBefore">Transactions confirmed earlier than this moment.</param>
    /// <param name="batchSize">Maximum batch size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of stalled Confirmed transactions.</returns>
    Task<IReadOnlyList<Transaction>> GetStalledConfirmedAsync(DateTimeOffset confirmedBefore, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the sign-ins of one scope that still wait for an answer: every stored transaction in the
    /// Pending state whose <see cref="SignInSubjectScope.Of"/> equals <paramref name="scope"/>.
    /// </summary>
    /// <remarks>
    /// The deadline does not narrow the selection: a sign-in past its TTL that the cleanup pass has
    /// not promoted yet is returned as well. The order is not significant. The transactions carry
    /// the current values of the conditionally written fields, as every other read of the store does.
    /// <para>
    /// The engine closes what this member returns when a newer sign-in of the same scope gets its
    /// channel subject, and it does not take the answer on trust: before closing, it checks again
    /// that each returned transaction is Pending, has the same scope and is not the new sign-in
    /// itself. A store that returns too much therefore cannot close someone else's sign-in — but a
    /// store that returns too little leaves earlier sign-ins alive until their deadline. A store
    /// that cannot run this selection must say so in its documentation rather than answer with an
    /// empty list in silence.
    /// </para>
    /// </remarks>
    /// <param name="scope">Scope of the sign-ins to select.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pending sign-ins of the scope; an empty list when there are none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is null.</exception>
    Task<IReadOnlyList<Transaction>> GetPendingSignInsAsync(
        SignInSubjectScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a hop observation mark onto the transaction once: the field is written only while it is
    /// empty, by the first writer (SPEC-025 HOP-071a).
    /// </summary>
    /// <remarks>
    /// Obligations shared by the three hop observation writes — this one,
    /// <see cref="TryAttachScannerContextAsync"/> and <see cref="TryFillScannerFingerprintAsync"/>:
    /// <list type="bullet">
    /// <item><description>
    /// The write is an atomic conditional write on the field, "empty → value" (<c>WHERE … IS NULL</c>,
    /// <c>HSETNX</c>, a compare-and-swap), never a read followed by a write: of two concurrent calls
    /// for one field exactly one gets <see langword="true"/>. A backend without atomicity on a field
    /// runs its own read → conditional write → retry loop over an unchanged
    /// <see cref="Transaction.ConcurrencyToken"/> / <see cref="Transaction.UpdatedAt"/>.
    /// </description></item>
    /// <item><description>
    /// An observation is not state: the write changes neither <see cref="Transaction.ConcurrencyToken"/>,
    /// nor <see cref="Transaction.UpdatedAt"/>, nor <see cref="Transaction.State"/>, and never makes a
    /// concurrent <see cref="UpdateAsync"/> with a valid token return <see langword="false"/>.
    /// </description></item>
    /// <item><description>
    /// <see cref="UpdateAsync"/> neither writes nor erases observations: the values a passed
    /// transaction carries in them are ignored, and the stored ones stay.
    /// </description></item>
    /// <item><description>
    /// Every read (<see cref="GetByIdAsync"/>, <see cref="GetByIdempotencyKeyAsync"/>,
    /// <see cref="GetExpiredAsync"/>, <see cref="GetStaleTerminalAsync"/>,
    /// <see cref="GetStalledConfirmedAsync"/>) returns the transaction with the current observation
    /// values.
    /// </description></item>
    /// <item><description>
    /// Observations live and die with the transaction: <see cref="DeleteAsync"/>, cleanup and the TTL
    /// of the store remove them together with it, and nothing keeps them longer (HOP-112).
    /// </description></item>
    /// <item><description>
    /// The state of the transaction (terminal, expired) is not checked here — the conditions of
    /// acceptance belong to the callers; the write goes through as long as the record exists.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="mark">Which mark to write.</param>
    /// <param name="markedAt">Moment the mark carries — stated by the caller, not by the store's clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// true — the mark was written by this call; false — the mark is already there or the transaction
    /// is not stored.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mark"/> is not a defined value.</exception>
    Task<bool> TryMarkHopObservationAsync(TransactionId id, HopObservationMark mark, DateTimeOffset markedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches the scanner context snapshot to the transaction once, by the first writer (SPEC-025
    /// HOP-080). The obligations of <see cref="TryMarkHopObservationAsync"/> apply.
    /// </summary>
    /// <remarks>
    /// <paramref name="snapshot"/>.<see cref="ScannerContextSnapshot.ScannedAt"/> equals the
    /// <c>markedAt</c> of the <see cref="HopObservationMark.FirstResolve"/> mark — the snapshot projects
    /// that mark (HOP-071a). The caller keeps the two equal; the store does not check it.
    /// The fingerprint never arrives here: it is filled later by
    /// <see cref="TryFillScannerFingerprintAsync"/>.
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="snapshot">Scanner context without a fingerprint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// true — the snapshot was attached by this call; false — a snapshot is already there or the
    /// transaction is not stored.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="snapshot"/> carries a fingerprint.</exception>
    Task<bool> TryAttachScannerContextAsync(TransactionId id, ScannerContextSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fills <see cref="ScannerContextSnapshot.Fingerprint"/> of the attached snapshot once, by the
    /// first writer — the single field of the snapshot written after it is captured (SPEC-025
    /// HOP-080). The obligations of <see cref="TryMarkHopObservationAsync"/> apply.
    /// </summary>
    /// <remarks>
    /// The form of the value is checked by the caller, not by the store.
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="fingerprint">Fingerprint to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// true — the fingerprint was written by this call; false — a fingerprint is already there, no
    /// snapshot is attached, or the transaction is not stored.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="fingerprint"/> is null, empty or whitespace.</exception>
    Task<bool> TryFillScannerFingerprintAsync(TransactionId id, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the surface of the web step of the claims completion (SPEC-041 C90, L53) when it holds
    /// the value the caller expects — the two moves the surface makes: "none → any" when the web step
    /// begins, and "<see cref="WebStepSurface.CrossDevicePage"/> → <see cref="WebStepSurface.InitiatingPage"/>"
    /// when the link to the other device could not be delivered.
    /// </summary>
    /// <remarks>
    /// The obligations of <see cref="TryMarkHopObservationAsync"/> apply — an atomic conditional write,
    /// never a read followed by a write; no change of <see cref="Transaction.ConcurrencyToken"/>,
    /// <see cref="Transaction.UpdatedAt"/> or <see cref="Transaction.State"/>; <see cref="UpdateAsync"/>
    /// neither writes nor erases the value; every read returns it — with one difference: the condition
    /// includes the state of the transaction. The write goes through only while the transaction is
    /// <see cref="TransactionState.Pending"/> and carries a completion mark, checked in the same atomic
    /// step as the value: a surface written after the transaction left the completion would describe a
    /// step that no longer exists.
    /// <para>
    /// Any other pair of <paramref name="expected"/> and <paramref name="surface"/> is not a move the
    /// surface makes, and the answer is <see langword="false"/> without touching the store.
    /// </para>
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="expected">Value the surface must hold now; null — no surface yet.</param>
    /// <param name="surface">Value to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// true — the surface was written by this call; false — the transaction carries no completion mark,
    /// is not Pending, holds a surface other than <paramref name="expected"/>, the pair is not a move the
    /// surface makes, or the transaction is not stored.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="expected"/> or <paramref name="surface"/> is not a defined value.
    /// </exception>
    Task<bool> TrySetWebStepSurfaceAsync(
        TransactionId id,
        WebStepSurface? expected,
        WebStepSurface surface,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the fingerprint of the form token of the claims completion once, by the first writer
    /// (SPEC-041 N78): the value is written only while none is stored — "none → value".
    /// </summary>
    /// <remarks>
    /// The obligations of <see cref="TrySetWebStepSurfaceAsync"/> apply, the state included: an atomic
    /// conditional write, never a read followed by a write; no change of
    /// <see cref="Transaction.ConcurrencyToken"/>, <see cref="Transaction.UpdatedAt"/> or
    /// <see cref="Transaction.State"/>; <see cref="UpdateAsync"/> neither writes nor erases the value; every
    /// read returns it; and the write goes through only while the transaction is
    /// <see cref="TransactionState.Pending"/> and carries a completion mark, checked in the same atomic step.
    /// Of two concurrent writes exactly one gets <see langword="true"/>, so a token is issued to one holder
    /// only.
    /// <para>
    /// The store keeps the fingerprint only — the token it was taken from never reaches the store.
    /// </para>
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="tokenHash">Fingerprint of the issued form token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// true — the fingerprint was written by this call; false — the transaction carries no completion mark,
    /// is not Pending, already holds a fingerprint, or is not stored.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="tokenHash"/> is null, empty or whitespace.</exception>
    Task<bool> TrySetClaimFormTokenHashAsync(
        TransactionId id,
        string tokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of the claims completion for one missing claim — its collected value or the
    /// mark that it was skipped (SPEC-041 C90) — once per claim.
    /// </summary>
    /// <remarks>
    /// The obligations of <see cref="TrySetWebStepSurfaceAsync"/> apply, the state included. The write
    /// adds the outcome to <see cref="ClaimCompletionState.Results"/> only while the transaction is
    /// <see cref="TransactionState.Pending"/>, carries a completion mark whose
    /// <see cref="ClaimCompletionState.Items"/> name <paramref name="result"/>'s claim, and holds no
    /// outcome for that claim yet — the claim names compared the way claim names are
    /// (<c>VeriqaClaimTypes.NameComparer</c>). All of it is one atomic step: of two concurrent outcomes for
    /// one claim exactly one is recorded, and an outcome never lands after the transaction left
    /// Pending. A store without atomicity over its document runs a read → conditional write → retry loop
    /// whose condition covers everything it read.
    /// <para>
    /// Whether the claim belongs to the current step of the completion is the caller's condition, not
    /// the store's.
    /// </para>
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="result">Outcome to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// true — the outcome was recorded by this call; false — the transaction carries no completion
    /// mark, is not Pending, does not list the claim as missing, already holds an outcome for it, or is
    /// not stored.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="result"/> names no claim, carries a value without a method or a method without a
    /// value, or marks a skip as verified.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="result"/> carries an undefined method.</exception>
    Task<bool> TryRecordClaimResultAsync(
        TransactionId id,
        ClaimCompletionResult result,
        CancellationToken cancellationToken = default);
}
