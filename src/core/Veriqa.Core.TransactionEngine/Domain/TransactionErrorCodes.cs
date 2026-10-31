// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.Contracts;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// String constants for Transaction Engine error codes.
/// All codes are kept in this class for consistency and to prevent magic strings. Every code the
/// core branches on in behaviour observable outside the free-SPI package aliases
/// <see cref="VeriqaErrorCodes"/>; the rest are internal to the engine.
/// </summary>
public static class TransactionErrorCodes
{
    /// <summary>
    /// The sender is a bot, and the security policy forbids bot authentication.
    /// </summary>
    public const string BotRejected = "bot_rejected";

    /// <summary>
    /// A transaction with the specified ID was not found.
    /// </summary>
    public const string TransactionNotFound = VeriqaErrorCodes.TransactionNotFound;

    /// <summary>
    /// The requested state transition is not allowed.
    /// </summary>
    public const string InvalidStateTransition = "invalid_state_transition";

    /// <summary>
    /// The transaction expired by TTL.
    /// </summary>
    public const string TransactionExpired = VeriqaErrorCodes.TransactionExpired;

    /// <summary>
    /// Duplicate confirmation event (idempotent response).
    /// </summary>
    public const string DuplicateConfirmationEvent = "duplicate_confirmation_event";

    /// <summary>
    /// The channel is not in AllowedChannelTypes.
    /// </summary>
    public const string ChannelNotAllowed = "channel_not_allowed";

    /// <summary>
    /// Identity Resolution could not determine the identity.
    /// </summary>
    public const string IdentityNotResolved = "identity_not_resolved";

    /// <summary>
    /// Error during finalization in an external system.
    /// </summary>
    public const string DownstreamFinalizeFailed = "downstream_finalize_failed";

    /// <summary>
    /// Security policy violation.
    /// </summary>
    public const string SecurityPolicyViolation = "security_policy_violation";

    /// <summary>
    /// The sender of a channel event is not admitted by the restriction policy of the deployment.
    /// The transaction itself is left as it was. The code deliberately says nothing of the rule that
    /// refused.
    /// </summary>
    public const string RestrictionPolicyViolation = "restriction_policy_violation";

    /// <summary>
    /// The transaction was cancelled by the client application.
    /// </summary>
    public const string CancelledByClient = "cancelled_by_client";

    /// <summary>
    /// The sign-in was closed because the same channel subject began a newer sign-in to the same
    /// application: the earlier one no longer waits for an answer. Written by the engine itself.
    /// </summary>
    public const string SupersededByNewSignIn = "superseded_by_new_sign_in";

    /// <summary>
    /// The user declined the action in the channel.
    /// </summary>
    public const string DeclinedByUser = "declined_by_user";

    /// <summary>
    /// Concurrency conflict — ConcurrencyToken does not match.
    /// </summary>
    public const string ConcurrencyConflict = "concurrency_conflict";

    /// <summary>
    /// Finalization timeout (Confirmed for longer than allowed).
    /// </summary>
    public const string FinalizationTimeout = "finalization_timeout";

    /// <summary>
    /// Unknown transaction type.
    /// </summary>
    public const string InvalidTransactionType = "invalid_transaction_type";

    /// <summary>
    /// IdempotencyKey has already been used with different parameters.
    /// </summary>
    public const string IdempotencyKeyConflict = "idempotency_key_conflict";

    /// <summary>
    /// IdempotencyKey fails validation (length exceeded, etc.).
    /// </summary>
    public const string InvalidIdempotencyKey = "invalid_idempotency_key";

    /// <summary>
    /// ClientContext exceeds the limit (4 KB).
    /// </summary>
    public const string ClientContextTooLarge = "client_context_too_large";

    /// <summary>
    /// IdempotencyKey and IdempotencyScope must be provided as a pair (both or neither).
    /// </summary>
    public const string IdempotencyParamsMismatch = "idempotency_params_mismatch";

    /// <summary>
    /// Snapshot data exceeds the allowed size limit (128 KB).
    /// </summary>
    public const string SnapshotTooLarge = "snapshot_too_large";

    /// <summary>
    /// The user exceeded the authentication limit within the given time window.
    /// </summary>
    public const string UserRateLimitExceeded = "user_rate_limit_exceeded";

    /// <summary>
    /// The transaction store has run out of allowed capacity.
    /// </summary>
    public const string StoreCapacityExceeded = "store_capacity_exceeded";

    /// <summary>
    /// The transaction store did not answer: a failure of its provider, reported by category
    /// (<see cref="Common.TransactionErrorCategory.Infrastructure"/>) rather than by a provider type
    /// the engine does not see. Unlike every other code here, repeating the same request later may
    /// succeed.
    /// </summary>
    public const string StoreUnavailable = "store_unavailable";

    /// <summary>
    /// A caller supplied a value for a slot not declared in the effective schema
    /// (SPEC-036 §4.5, TPL-021). Includes the names of server slots — for a caller they do not exist.
    /// </summary>
    public const string SlotUndeclared = "slot_undeclared";

    /// <summary>
    /// A required caller slot has no value (SPEC-036 §4.5, TPL-022).
    /// </summary>
    public const string SlotRequiredMissing = "slot_required_missing";

    /// <summary>
    /// A caller slot value fails its type/constraint validation (SPEC-036 §4.5, TPL-023).
    /// The details list every invalid slot at once as "slot_name: rule".
    /// </summary>
    public const string SlotValueInvalid = "slot_value_invalid";

    /// <summary>
    /// The channel declared it cannot carry the confirmation out (SPEC-003 CA-191) — the reason
    /// code the core records on the transaction it then fails.
    /// </summary>
    public const string ChannelCannotContinue = VeriqaErrorCodes.ChannelCannotContinue;

    /// <summary>
    /// No message declares the subject of a confirmation transaction, so the question was never asked
    /// and the transaction ends without a decision of the user (SPEC-039 E28). A reason code of the
    /// core alone: the relying party reads it off the status, no adapter produces it.
    /// </summary>
    /// <remarks>
    /// Aliases the code the suppressed prompt context carries as its reason — one fact with two
    /// halves, the diagnostic and the terminal state, and the value is stated once in the package
    /// both of them can see, so an operator correlating the log with the status greps one string.
    /// </remarks>
    public const string ConfirmationTemplateUnavailable =
        ContextSuppressionReasons.ConfirmationTemplateUnavailable;

    /// <summary>
    /// The aggregate caller-values set exceeds the size limit aligned with the snapshot limit
    /// (SPEC-036 TPL-025, D4). The public wire code is named by SPEC-039 C17.
    /// </summary>
    public const string SlotValuesTooLarge = "slot_values_too_large";

    /// <summary>
    /// The confirmation creation request states no action type (SPEC-039 C17, R4).
    /// </summary>
    public const string ActionTypeMissing = "action_type_missing";

    /// <summary>
    /// No declaration is visible for the stated action type (SPEC-039 C17, E24). One code for both
    /// outcomes of the resolution — nothing declared it, and the configuration source did not
    /// answer — because the entry cannot tell them apart and is not asked to: the relying party
    /// learns neither the reason nor which of the two it was.
    /// </summary>
    public const string ActionTypeUnknown = "action_type_unknown";

    /// <summary>
    /// The stated locale is not a syntactically well-formed language tag (SPEC-039 C17). A well-formed
    /// tag naming a language the deployment does not carry is NOT this code — it degrades through the
    /// ordinary fallback chain at render time.
    /// </summary>
    public const string LocaleInvalid = "locale_invalid";

    /// <summary>
    /// The stated time zone is one the deployment cannot resolve into a zone. Unlike a locale a
    /// deployment does not carry, an unresolvable zone has no fallback that keeps the caller's
    /// meaning: a moment shown in the wrong zone reads as another moment, so the request is refused
    /// rather than degraded.
    /// </summary>
    public const string TimeZoneInvalid = "time_zone_invalid";

    /// <summary>
    /// An identity type stated among the relying party's expectations is not declared for the
    /// tenant/application, or the set of comparable types is empty (SPEC-039 C17, E42). The two
    /// causes deliberately share a code.
    /// </summary>
    public const string CandidateTypeUndeclared = "candidate_type_undeclared";

    /// <summary>
    /// An expected identity value does not normalize to the canonical form of its declared type
    /// (SPEC-039 C17, C23).
    /// </summary>
    public const string CandidateValueInvalid = "candidate_value_invalid";

    /// <summary>
    /// The transaction waits for missing claims (SPEC-041 L91): the channel snapshot is frozen, so a
    /// confirmation or an attachment of channel data is refused and changes nothing. A refusal of the
    /// engine, never the reason of a terminal state.
    /// </summary>
    public const string ClaimCompletionInProgress = "claim_completion_in_progress";

    /// <summary>
    /// A value or a skip of a missing claim is refused (SPEC-041 C90): the transaction carries no
    /// completion mark, is not Pending, the claim is not one of the current step, an outcome for it is
    /// already recorded, or a <c>Required</c> claim is skipped.
    /// </summary>
    public const string ClaimCompletionWriteRefused = "claim_completion_write_refused";

    /// <summary>
    /// A <c>Required</c> claim has no way to be obtained in this transaction (SPEC-041 L48): the phone
    /// number the channel did not give, while the channel of the transaction cannot ask for it. The
    /// reason code the transaction is failed with when the user confirms.
    /// </summary>
    public const string RequiredClaimUnobtainable = "required_claim_unobtainable";

    /// <summary>
    /// The transaction is bound to another channel subject (SPEC-007 UI-022, SPEC-039 N34): the action
    /// of this sender is refused and changes nothing. The refusal names no subject. A refusal of the
    /// engine, never the reason of a terminal state.
    /// </summary>
    public const string ChannelSubjectMismatch = "channel_subject_mismatch";
}
