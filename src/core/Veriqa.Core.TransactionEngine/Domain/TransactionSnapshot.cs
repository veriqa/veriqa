// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Complete state of a <see cref="Transaction"/> as one transferable object — the published path a
/// store implementation uses to write a transaction out and to rehydrate it back.
/// <para>
/// It exists because the lifecycle fields of a transaction (<see cref="Transaction.State"/>,
/// <see cref="Transaction.UpdatedAt"/>, the three outcome snapshots and
/// <see cref="Transaction.ConcurrencyToken"/>) carry internal setters: only the state machine may move
/// a transaction, and a public setter would hand that right to anyone. A store, however, does not move
/// a transaction — it restores one that already moved, and without a way to state those fields an
/// implementation outside the Veriqa assemblies would return an object in its default state with a
/// freshly generated concurrency token, silently breaking both the state machine and optimistic
/// concurrency.
/// </para>
/// <para>
/// <b>More fields are required here than on <see cref="Transaction"/> itself.</b> The transaction
/// declares only the fields a creation path must state; <see cref="State"/>, <see cref="UpdatedAt"/>
/// and <see cref="ConcurrencyToken"/> have defaults there because creation supplies them. Restoring is
/// the opposite case: a store that forgets them recreates exactly the defect above, and the compiler
/// would say nothing. So they are <c>required</c> here. The three outcome snapshots stay optional —
/// a transaction that has not been confirmed legitimately has none.
/// </para>
/// </summary>
/// <remarks>
/// The perimeter is the whole transaction, not the mutable part of it: a store writes and reads every
/// field, and a snapshot covering only the mutable ones would force the immutable half back through
/// object initializers. Adding a field to <see cref="Transaction"/> means adding it here too — see the
/// assembly README for that obligation.
/// </remarks>
public sealed class TransactionSnapshot
{
    /// <summary>
    /// Transaction identifier (see <see cref="Transaction.Id"/>).
    /// </summary>
    public required TransactionId Id { get; init; }

    /// <summary>
    /// Transaction type (see <see cref="Transaction.Type"/>).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// State of the state machine as it was stored (see <see cref="Transaction.State"/>).
    /// </summary>
    public required TransactionState State { get; init; }

    /// <summary>
    /// Reason code of a terminal state (see <see cref="Transaction.StateReasonCode"/>).
    /// </summary>
    public string? StateReasonCode { get; init; }

    /// <summary>
    /// Creation time, UTC (see <see cref="Transaction.CreatedAt"/>).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Time of the last state update, UTC (see <see cref="Transaction.UpdatedAt"/>).
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// TTL expiration time, UTC (see <see cref="Transaction.ExpiresAt"/>).
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Client request identifier for tracing (see <see cref="Transaction.CorrelationId"/>).
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Idempotency key (see <see cref="Transaction.IdempotencyKey"/>).
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Idempotency scope (see <see cref="Transaction.IdempotencyScope"/>).
    /// </summary>
    public string? IdempotencyScope { get; init; }

    /// <summary>
    /// Channel type explicitly requested by the client (see <see cref="Transaction.RequestedChannelType"/>).
    /// </summary>
    public string? RequestedChannelType { get; init; }

    /// <summary>
    /// Allowed channel types (see <see cref="Transaction.AllowedChannelTypes"/>). The restored
    /// transaction freezes the set, so a store may hand over any read-only set it has at hand.
    /// </summary>
    public required IReadOnlySet<string> AllowedChannelTypes { get; init; }

    /// <summary>
    /// Arbitrary client data, raw JSON (see <see cref="Transaction.ClientContext"/>).
    /// </summary>
    public string? ClientContext { get; init; }

    /// <summary>
    /// Confirmation action data (see <see cref="Transaction.ConfirmationSnapshot"/>).
    /// </summary>
    public ConfirmationSnapshot? ConfirmationSnapshot { get; init; }

    /// <summary>
    /// Channel data captured on confirmation (see <see cref="Transaction.ChannelIdentitySnapshot"/>).
    /// </summary>
    public ChannelIdentitySnapshot? ChannelIdentitySnapshot { get; init; }

    /// <summary>
    /// Bound channel subject (see <see cref="Transaction.SubjectBinding"/>). Not <c>required</c>: a record
    /// stored before the field existed has none, and null is exactly what such a transaction states.
    /// </summary>
    public ChannelSubjectBinding? SubjectBinding { get; init; }

    /// <summary>
    /// Resolved identity (see <see cref="Transaction.ResolvedIdentitySnapshot"/>).
    /// </summary>
    public ResolvedIdentitySnapshot? ResolvedIdentitySnapshot { get; init; }

    /// <summary>
    /// Completion data (see <see cref="Transaction.CompletionSnapshot"/>).
    /// </summary>
    public CompletionSnapshot? CompletionSnapshot { get; init; }

    /// <summary>
    /// Optimistic concurrency token as it was stored (see <see cref="Transaction.ConcurrencyToken"/>).
    /// Restored verbatim: checking it is the business of <c>ITransactionStore.UpdateAsync</c>.
    /// </summary>
    public required string ConcurrencyToken { get; init; }

    /// <summary>
    /// OIDC request context (see <see cref="Transaction.OidcContext"/>).
    /// </summary>
    public OidcContext? OidcContext { get; init; }

    /// <summary>
    /// Server-to-server request context (see <see cref="Transaction.RequestContext"/>).
    /// </summary>
    public TransactionRequestContext? RequestContext { get; init; }

    /// <summary>
    /// Identity-match container (see <see cref="Transaction.IdentityMatch"/>).
    /// </summary>
    public IdentityMatchState? IdentityMatch { get; init; }

    /// <summary>
    /// Initiator context (see <see cref="Transaction.InitiatorContextSnapshot"/>).
    /// </summary>
    public InitiatorContextSnapshot? InitiatorContextSnapshot { get; init; }

    /// <summary>
    /// Requirement set fixed at creation (see <see cref="Transaction.ClaimRequirements"/>). Not
    /// <c>required</c>: a record stored before the field existed has none, and the empty set is exactly
    /// what such a transaction states.
    /// </summary>
    public IReadOnlyList<ClaimRequirement> ClaimRequirements { get; init; } = [];

    /// <summary>
    /// Claims completion fields, all parts of them (see <see cref="Transaction.ClaimCompletion"/>). Not
    /// <c>required</c>: a record stored before the field existed has none, and null is exactly what such
    /// a transaction states.
    /// </summary>
    public ClaimCompletionState? ClaimCompletion { get; init; }

    /// <summary>
    /// Values taken from the collected claims store (see <see cref="Transaction.StoredClaimValues"/>). Not
    /// <c>required</c>: a record stored before the field existed has none, and the empty list is exactly
    /// what such a transaction states.
    /// </summary>
    public IReadOnlyList<ClaimCompletionResult> StoredClaimValues { get; init; } = [];

    /// <summary>
    /// Moment the authorization code of the sign-in was issued
    /// (see <see cref="Transaction.AuthorizationCodeIssuedAt"/>). Not <c>required</c>: a record stored
    /// before the field existed has none, and null is exactly what such a transaction states.
    /// </summary>
    public DateTimeOffset? AuthorizationCodeIssuedAt { get; init; }

    /// <summary>
    /// Context of the scanning device, including its fingerprint when one was filled
    /// (see <see cref="Transaction.ScannerContextSnapshot"/>).
    /// </summary>
    public ScannerContextSnapshot? ScannerContextSnapshot { get; init; }

    /// <summary>
    /// First human resolve of the hop link (see <see cref="Transaction.HopFirstResolvedAt"/>).
    /// </summary>
    public DateTimeOffset? HopFirstResolvedAt { get; init; }

    /// <summary>
    /// Interstitial page shown (see <see cref="Transaction.HopInterstitialOpenedAt"/>).
    /// </summary>
    public DateTimeOffset? HopInterstitialOpenedAt { get; init; }

    /// <summary>
    /// Start of the transition into the channel (see <see cref="Transaction.HopChannelLaunchedAt"/>).
    /// </summary>
    public DateTimeOffset? HopChannelLaunchedAt { get; init; }

    /// <summary>
    /// Repeat human resolve from another browser (see <see cref="Transaction.HopRepeatResolvedAt"/>).
    /// </summary>
    public DateTimeOffset? HopRepeatResolvedAt { get; init; }
}
