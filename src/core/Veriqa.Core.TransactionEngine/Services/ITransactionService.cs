// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// Core transaction management service.
/// Defines the contract for creating, retrieving, and managing transaction states.
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Creates and activates a new transaction.
    /// Steps 2–3 of the algorithm: ID generation, State = Created → Pending.
    /// </summary>
    /// <param name="request">Transaction creation parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the created transaction or an error.</returns>
    Task<Result<Transaction>> CreateTransactionAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current state of a transaction.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the transaction or an error.</returns>
    Task<Result<Transaction>> GetTransactionAsync(TransactionId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms a transaction via a Channel Adapter (State → Confirmed).
    /// </summary>
    /// <remarks>
    /// <see cref="TransactionErrorCodes.TransactionExpired"/> is refused to a transaction that running
    /// out of time ENDED: one already in Expired, or one still Created/Pending past the deadline that
    /// the cleanup pass has not promoted yet. The refusal of the second one is not a no-op: it moves
    /// the transaction into Expired and publishes <c>TransactionExpiredEvent</c> on the spot, by the
    /// step <see cref="ExpireOverdueAsync"/> takes. A transaction some decision already ended is refused
    /// with the code of that state instead, and never with expiry.
    /// <para>
    /// Claims completion (SPEC-041 L91, C90). When the requirement set of the transaction names claims
    /// the channel did not give, the confirmation does not move the transaction to Confirmed: it writes
    /// the channel snapshot together with the completion mark and the list of missing claims, keeps the
    /// transaction Pending, begins the first step of the completion and returns the transaction in that
    /// state (<see cref="Transaction.IsCompletingClaims"/>) — or Confirmed/Completed, when the writer of
    /// the last outcome finished the completion while the step began. A transaction another writer ended
    /// otherwise in that moment is refused with <see cref="TransactionErrorCodes.TransactionExpired"/>
    /// (Expired) or <see cref="TransactionErrorCodes.InvalidStateTransition"/>. A required phone number
    /// the channel of the transaction cannot ask for, found before the mark is set, fails the transaction
    /// with <see cref="TransactionErrorCodes.RequiredClaimUnobtainable"/>, and the confirmation returns
    /// that code. Once the mark is set, the channel snapshot is frozen: a confirmation while a missing
    /// claim is still open is refused with <see cref="TransactionErrorCodes.ClaimCompletionInProgress"/>
    /// and leaves the transaction Pending, though the completion may have written it first, taking off it
    /// the stored values that vanished (SPEC-041 E86). The exception is a vanished value whose required
    /// claim the channel cannot ask for: the completion fails the transaction with
    /// <see cref="TransactionErrorCodes.RequiredClaimUnobtainable"/>, and the confirmation is refused with
    /// <see cref="TransactionErrorCodes.ClaimCompletionInProgress"/> all the same. A confirmation after
    /// the last outcome confirms with the snapshot of the transaction — the passed one is not used.
    /// </para>
    /// <para>
    /// Subject binding (SPEC-007 UI-022, SPEC-039 N34). A sender other than the subject the transaction
    /// is bound to (<see cref="Transaction.IsBoundToAnotherSubject"/>) is refused with
    /// <see cref="TransactionErrorCodes.ChannelSubjectMismatch"/> before anything is written. On a
    /// transaction already Confirmed or Completed the repeated confirmation succeeds only for the subject
    /// of its channel snapshot; any other sender gets the same refusal.
    /// </para>
    /// <para>
    /// Superseding of earlier sign-ins. When this write is the one that gives a sign-in its channel
    /// subject — it had none before — the earlier sign-ins of the same tenant, application and channel
    /// subject that are still Pending are closed: each goes to Failed with
    /// <see cref="TransactionErrorCodes.SupersededByNewSignIn"/>, and nothing is sent to the channel
    /// about them. It happens after the write and never changes the result of this call: a failure to
    /// find or to close an earlier sign-in is logged and leaves that sign-in to its deadline.
    /// </para>
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="channelIdentity">User data from the channel.</param>
    /// <param name="concurrencyToken">Optimistic concurrency token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the updated transaction or an error.</returns>
    Task<Result<Transaction>> ConfirmTransactionAsync(
        TransactionId id,
        ChannelIdentitySnapshot channelIdentity,
        string concurrencyToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches the channel identity to a transaction that stays Pending: the core has accepted the
    /// user's action in the channel, but the confirming question is asked elsewhere (the core web
    /// page), so the transaction must NOT advance along the state machine yet.
    /// </summary>
    /// <remarks>
    /// The pair "State == Pending + a populated ChannelIdentitySnapshot" is what tells a later reader
    /// (the status endpoint, the authorize callback) that the core is waiting for the answer on its
    /// own page. Only the snapshot, the subject binding and the concurrency metadata change; the state
    /// machine is not touched. A repeated call of the same channel subject with a current token
    /// overwrites the snapshot (the last delivery of the same inbound event wins), which keeps provider
    /// retries idempotent. A sender other than the subject the transaction is bound to
    /// (<see cref="Transaction.IsBoundToAnotherSubject"/>) is refused with
    /// <see cref="TransactionErrorCodes.ChannelSubjectMismatch"/> after the state and TTL checks and
    /// before anything is written (SPEC-007 UI-022, SPEC-039 N34); the first sender binds the transaction.
    /// The sender is admitted under the same rules the confirmation applies — the transaction's
    /// channel allow-list and the bot policy — because the attached identity is the one the answer on
    /// the web page signs in: a sender that confirmation would reject must not reach that page.
    /// <para>
    /// A transaction past its TTL is refused with <see cref="TransactionErrorCodes.TransactionExpired"/>
    /// even while it is still Pending: the state is promoted by the cleanup pass or by a decision after
    /// the deadline, not by this call, so "Pending" alone does not mean the answer can still be given.
    /// </para>
    /// <para>
    /// A transaction in the claims completion is refused with
    /// <see cref="TransactionErrorCodes.ClaimCompletionInProgress"/>: its channel snapshot is frozen
    /// (SPEC-041 L91).
    /// </para>
    /// <para>
    /// Superseding of earlier sign-ins. When this write is the one that gives a sign-in its channel
    /// subject — it had none before — the earlier sign-ins of the same tenant, application and channel
    /// subject that are still Pending are closed: each goes to Failed with
    /// <see cref="TransactionErrorCodes.SupersededByNewSignIn"/>, and nothing is sent to the channel
    /// about them. It happens after the write and never changes the result of this call: a failure to
    /// find or to close an earlier sign-in is logged and leaves that sign-in to its deadline.
    /// </para>
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="identity">User data from the channel.</param>
    /// <param name="concurrencyToken">Optimistic concurrency token.</param>
    /// <param name="bindAnswerToBrowser">
    /// true — the answer on the core confirmation page is accepted only from the browser marked by the
    /// caller (<see cref="ChannelSubjectBinding.AnswerBrowserBound"/>); a value set by an earlier call is
    /// never cleared by a later one.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the updated transaction or an error.</returns>
    Task<Result<Transaction>> AttachChannelIdentityAsync(
        TransactionId id,
        ChannelIdentitySnapshot identity,
        string concurrencyToken,
        bool bindAnswerToBrowser = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds the transaction to the channel subject of the sender (SPEC-007 UI-018, UI-022; SPEC-039
    /// N34) without changing its state or its channel snapshot.
    /// </summary>
    /// <remarks>
    /// A separate operation because the snapshot cannot serve as the binding at the moment the sender
    /// enters the channel: "Pending + a snapshot" means the question waits on the core page
    /// (<see cref="Transaction.IsAwaitingWebConfirmation"/>).
    /// <list type="bullet">
    /// <item>Bound to another subject — refused with <see cref="TransactionErrorCodes.ChannelSubjectMismatch"/>.</item>
    /// <item>Bound to the sender, or no longer Pending, or in the claims completion — success, nothing written.</item>
    /// <item>Pending, not past its TTL, the sender admitted (<see cref="ValidateChannelAdmissibilityAsync"/>),
    /// nobody bound — the binding is written, keeping the sender's snapshot as the profile of the start
    /// (<see cref="ChannelSubjectBinding.StartIdentity"/>) unless the binding with it is over the size limit of a
    /// stored snapshot, which binds without it; otherwise the refusal of the failed check.</item>
    /// </list>
    /// Of two different senders racing for an unbound transaction exactly one is bound, and the other
    /// is refused with <see cref="TransactionErrorCodes.ChannelSubjectMismatch"/>, not with a concurrency
    /// conflict.
    /// <para>
    /// Superseding of earlier sign-ins. When this write is the one that gives a sign-in its channel
    /// subject — it had none before — the earlier sign-ins of the same tenant, application and channel
    /// subject that are still Pending are closed: each goes to Failed with
    /// <see cref="TransactionErrorCodes.SupersededByNewSignIn"/>, and nothing is sent to the channel
    /// about them. It happens after the write and never changes the result of this call: a failure to
    /// find or to close an earlier sign-in is logged and leaves that sign-in to its deadline.
    /// </para>
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="sender">Channel snapshot of the sender.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The transaction as it stands after the call — carrying the new concurrency token when the binding
    /// was written — or an error.
    /// </returns>
    Task<Result<Transaction>> BindChannelSubjectAsync(
        TransactionId id,
        ChannelIdentitySnapshot sender,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the sender of a channel event may act on this transaction: the channel is in
    /// the transaction's allow-list, the sender is not a bot when bots are rejected, the claims of the
    /// sender pass the restriction policies of the deployment, and no admissibility check the
    /// deployment registered refuses.
    /// </summary>
    /// <remarks>
    /// The same gate <see cref="ConfirmTransactionAsync"/> and <see cref="AttachChannelIdentityAsync"/>
    /// apply before they write anything of the channel onto the transaction. It is stated on the
    /// contract so that a surface which does not write — one that only tells the sender what the
    /// transaction ended in — can apply the very same decision instead of carrying a second copy of
    /// the rule. The transaction is taken as a value rather than by identifier: such a caller has
    /// already read it, and a second read here would be a second answer to the same question.
    /// <para>
    /// A refusal carries <see cref="TransactionErrorCodes.ChannelNotAllowed"/>,
    /// <see cref="TransactionErrorCodes.BotRejected"/>,
    /// <see cref="TransactionErrorCodes.RestrictionPolicyViolation"/> — a restriction policy refused the
    /// sender — or <see cref="TransactionErrorCodes.SecurityPolicyViolation"/> — a registered
    /// <see cref="Veriqa.Core.TransactionEngine.RestrictionPolicies.IChannelAdmissibilityCheck"/> did. An empty allow-list means "no
    /// restriction by channel", not "no channel allowed".
    /// </para>
    /// <para>
    /// The checks form an ordered conjunction that ends at the first refusal. A refusal changes nothing
    /// on the transaction, and the last two codes carry one neutral message: which rule or check
    /// refused is written to the audit trail and the log only.
    /// </para>
    /// </remarks>
    /// <param name="transaction">Transaction the event acts on.</param>
    /// <param name="channelIdentity">Identity of the sender in the channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success when the sender is admissible; otherwise the error to answer with.</returns>
    ValueTask<Result> ValidateChannelAdmissibilityAsync(
        Transaction transaction,
        ChannelIdentitySnapshot channelIdentity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the collected value of a missing claim of the claims completion (SPEC-041 C90), then
    /// checks whether the completion is over.
    /// </summary>
    /// <remarks>
    /// The value is accepted only while the transaction is not expired, carries the completion mark, is
    /// Pending, and the claim is a missing claim of the CURRENT step with no outcome yet. Whether the
    /// writer may write it at all (the chat of the snapshot, the right of the form) is the writer's
    /// check, not the engine's. After the write the completion is checked in its one place: when every
    /// missing claim has an outcome, the transaction is confirmed with its own channel snapshot and
    /// finalized (confirm → resolve → complete, with compensation); when the write moved the completion
    /// to another step, that step begins.
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="claim">Name of the claim.</param>
    /// <param name="value">Collected value, already validated and normalized by the writer.</param>
    /// <param name="verified">Whether the value was verified (the source of the matching <c>*_verified</c> claim).</param>
    /// <param name="method">How the value was obtained.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Success — the transaction after the completion check: still in the completion, Completed, Failed
    /// by the compensation of the finalization, or Confirmed/Completed as re-read by the loser of a race
    /// for the confirmation; a refusal of the finalization does not travel out, the value is recorded
    /// either way. Failure — <see cref="TransactionErrorCodes.TransactionNotFound"/>,
    /// <see cref="TransactionErrorCodes.TransactionExpired"/> or
    /// <see cref="TransactionErrorCodes.ClaimCompletionWriteRefused"/>.
    /// </returns>
    Task<Result<Transaction>> RecordClaimValueAsync(
        TransactionId id,
        string claim,
        string value,
        bool verified,
        ClaimCollectionMethod method,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the user skipped an <c>Optional</c> missing claim of the claims completion
    /// (SPEC-041 C90, L73), then checks whether the completion is over — the conditions and the outcome
    /// of <see cref="RecordClaimValueAsync"/> apply.
    /// </summary>
    /// <remarks>
    /// A <c>Required</c> claim cannot be skipped: in its place the user cancels the sign-in, which is
    /// <see cref="FailTransactionAsync"/> with <see cref="TransactionErrorCodes.DeclinedByUser"/>.
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="claim">Name of the claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>As <see cref="RecordClaimValueAsync"/>; skipping a <c>Required</c> claim is refused with
    /// <see cref="TransactionErrorCodes.ClaimCompletionWriteRefused"/>.</returns>
    Task<Result<Transaction>> SkipClaimAsync(
        TransactionId id,
        string claim,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes a transaction (State → Completed).
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="resolvedIdentity">Final resolved identity.</param>
    /// <param name="concurrencyToken">Optimistic concurrency token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the updated transaction or an error.</returns>
    Task<Result<Transaction>> CompleteTransactionAsync(
        TransactionId id,
        ResolvedIdentitySnapshot resolvedIdentity,
        string concurrencyToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly transitions a transaction to Failed.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="reasonCode">Failure reason code.</param>
    /// <param name="concurrencyToken">Optimistic concurrency token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the updated transaction or an error.</returns>
    Task<Result<Transaction>> FailTransactionAsync(
        TransactionId id,
        string reasonCode,
        string concurrencyToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a transaction on behalf of the client (→ Failed + cancelled_by_client).
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="concurrencyToken">Optimistic concurrency token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with the updated transaction or an error.</returns>
    Task<Result<Transaction>> CancelTransactionAsync(
        TransactionId id,
        string concurrencyToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a transaction whose deadline has passed into Expired now and publishes
    /// <c>TransactionExpiredEvent</c>, by the same step the cleanup pass takes (SPEC-001 §8.3).
    /// </summary>
    /// <remarks>
    /// For a surface that refuses a decision because the deadline has passed: the refusal is the moment
    /// the expiry is known, and the transaction no longer waits for the next cleanup pass to end it.
    /// <see cref="ConfirmTransactionAsync"/> takes the step itself when it refuses by the deadline; a
    /// surface that refuses before calling the engine calls this after it has answered the decision,
    /// so the record of its chat is spent by that answer and the expiry handler finds nothing to send.
    /// Nothing is written for a transaction its deadline does not end — not found, still inside its
    /// TTL, or in a state expiry does not apply to — and a write lost to another writer (the cleanup
    /// pass, a parallel decision) is not an error: the event is published by the writer whose record
    /// stands. Store and publisher failures are logged, never thrown.
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="cancellationToken">Cancellation token of the read; the write and the event are not
    /// abandoned once started.</param>
    /// <returns>The completed operation.</returns>
    Task ExpireOverdueAsync(
        TransactionId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a completed transaction as having handed the identity of the confirming party to its
    /// client (SPEC-039 C51). The mark is written once per transaction.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="true"/> to exactly one call per transaction. <see langword="false"/> —
    /// the transaction is not stored (including one swept by retention), is not Completed, or already
    /// carries the mark; the reasons are deliberately not told apart. The state does not change.
    /// Atomicity rests on the optimistic concurrency of the store: a lost lock is answered inside the
    /// method by re-reading the transaction and checking again. Infrastructure exceptions of the store
    /// propagate.
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when this call wrote the mark.</returns>
    Task<bool> TryMarkIdentityTokenIssuedAsync(
        TransactionId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a completed transaction as having issued its authorization code
    /// (<see cref="Transaction.AuthorizationCodeIssuedAt"/>). The mark is written once per transaction.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="true"/> to exactly one call per transaction. <see langword="false"/> —
    /// the transaction is not stored (including one swept by retention), is not Completed, or already
    /// carries the mark; the reasons are deliberately not told apart. Only the mark and the concurrency
    /// token change: the state stays, and so does <see cref="Transaction.UpdatedAt"/>, which the
    /// retention of the transaction counts from. The transaction type is not checked — which
    /// transactions issue a code is the caller's condition. Atomicity rests on the optimistic
    /// concurrency of the store: a lost lock is answered inside the method by re-reading the
    /// transaction and checking again. Infrastructure exceptions of the store propagate.
    /// </remarks>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when this call wrote the mark.</returns>
    Task<bool> TryMarkAuthorizationCodeIssuedAsync(
        TransactionId id,
        CancellationToken cancellationToken = default);
}
