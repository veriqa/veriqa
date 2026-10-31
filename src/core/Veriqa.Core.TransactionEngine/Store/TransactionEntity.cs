// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Store;

/// <summary>
/// Flat EF Core entity for storing a transaction in the database.
/// Complex types (snapshots, collections) are stored as JSON strings.
/// Not sealed: EF Core requires the ability to create proxy-derived classes.
/// </summary>
internal class TransactionEntity
{
    /// <summary>
    /// Transaction identifier (Base62, 43 characters).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Transaction type (login, confirmation, etc.).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Current transaction state (string name of the TransactionState enum).
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Reason code of the terminal state. Null for non-terminal states.
    /// </summary>
    public string? StateReasonCode { get; set; }

    /// <summary>
    /// Moment the transaction was created (UTC).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Moment of the last state update (UTC).
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// TTL expiration moment (UTC).
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Client request identifier for tracing.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Idempotency key.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Idempotency scope.
    /// </summary>
    public string? IdempotencyScope { get; set; }

    /// <summary>
    /// Channel type explicitly requested by the client.
    /// </summary>
    public string? RequestedChannelType { get; set; }

    /// <summary>
    /// Arbitrary client data (raw JSON string).
    /// </summary>
    public string? ClientContext { get; set; }

    /// <summary>
    /// Allowed channel types (JSON string array).
    /// </summary>
    public string AllowedChannelTypesJson { get; set; } = string.Empty;

    /// <summary>
    /// Confirmation data snapshot (JSON).
    /// </summary>
    public string? ConfirmationSnapshotJson { get; set; }

    /// <summary>
    /// Channel identity snapshot (JSON).
    /// </summary>
    public string? ChannelIdentitySnapshotJson { get; set; }

    /// <summary>
    /// Bound channel subject (JSON, SPEC-039 N34); null — no binding was written, which is also what a
    /// row stored before the column existed reads as.
    /// </summary>
    public string? SubjectBindingJson { get; set; }

    /// <summary>
    /// Fingerprint of the sign-in subject scope of the transaction (tenant, application, channel type,
    /// channel user): 64 lowercase hex characters. A lookup column only — written on every write of the
    /// transaction and never read back into the domain. Null — the transaction has no such scope (not a
    /// sign-in, no application named, no subject bound yet), which is also what a row stored before the
    /// column existed reads as.
    /// </summary>
    public string? SignInSubjectFingerprint { get; set; }

    /// <summary>
    /// Final resolved identity snapshot (JSON).
    /// </summary>
    public string? ResolvedIdentitySnapshotJson { get; set; }

    /// <summary>
    /// Completion data snapshot (JSON).
    /// </summary>
    public string? CompletionSnapshotJson { get; set; }

    /// <summary>
    /// Optimistic concurrency token.
    /// </summary>
    public string ConcurrencyToken { get; set; } = string.Empty;

    /// <summary>
    /// OIDC request context (JSON).
    /// </summary>
    public string? OidcContextJson { get; set; }

    /// <summary>
    /// Request context of a non-OIDC transaction (JSON, SPEC-039 C19). Immutable after creation.
    /// </summary>
    public string? RequestContextJson { get; set; }

    /// <summary>
    /// Confirming-party check container (JSON, SPEC-039 C23). Holds protected, opaque values.
    /// </summary>
    public string? IdentityMatchJson { get; set; }

    /// <summary>
    /// Initiator context snapshot (JSON, SPEC-017). Immutable after creation.
    /// </summary>
    public string? InitiatorContextSnapshotJson { get; set; }

    /// <summary>
    /// Requirement set of the transaction (JSON array, SPEC-041 C35). Immutable after creation;
    /// null — the empty set, including every record stored before the column existed.
    /// </summary>
    public string? ClaimRequirementsJson { get; set; }

    /// <summary>
    /// Mark and list of missing claims of the claims completion (JSON, SPEC-041 C90): the moment the
    /// user confirmed and the claims missing at that moment. Written by an ordinary update; null — no
    /// completion mark, including every record stored before the column existed.
    /// </summary>
    public string? ClaimCompletionJson { get; set; }

    /// <summary>
    /// Surface of the web step of the claims completion — the member name of <c>WebStepSurface</c>;
    /// null — the web step has not begun. Written only by its conditional write, never by an update.
    /// </summary>
    public string? ClaimCompletionSurface { get; set; }

    /// <summary>
    /// Outcomes of the claims completion recorded so far (JSON array). Written only by its conditional
    /// write, never by an update; null — none.
    /// </summary>
    public string? ClaimCompletionResultsJson { get; set; }

    /// <summary>
    /// Fingerprint of the form token of the claims completion (SPEC-041 N78) — never the token itself;
    /// null — no token was issued. Written only by its conditional write, never by an update.
    /// </summary>
    public string? ClaimCompletionFormTokenHash { get; set; }

    /// <summary>
    /// Values taken from the collected claims store this transaction hands out (JSON array, SPEC-041 C37).
    /// Written by an ordinary update; null — none, including every record stored before the column existed.
    /// </summary>
    public string? StoredClaimValuesJson { get; set; }

    /// <summary>
    /// Moment the authorization code of the sign-in was issued (UTC). Written by an ordinary update;
    /// null — no code was issued, including every record stored before the column existed.
    /// </summary>
    public DateTimeOffset? AuthorizationCodeIssuedAt { get; set; }

    /// <summary>
    /// Scanner context snapshot (JSON, SPEC-025 §8.1) WITHOUT its fingerprint — that one is kept in
    /// <see cref="ScannerFingerprint"/>, so its one-time fill is a conditional write on a column of
    /// its own rather than an edit inside a JSON document.
    /// </summary>
    public string? ScannerContextSnapshotJson { get; set; }

    /// <summary>
    /// Fingerprint of the scanner context snapshot, filled once after the snapshot (HOP-080).
    /// </summary>
    public string? ScannerFingerprint { get; set; }

    /// <summary>
    /// First human resolve of the hop link (UTC), written once (HOP-071a).
    /// </summary>
    public DateTimeOffset? HopFirstResolvedAt { get; set; }

    /// <summary>
    /// Interstitial page shown (UTC), written once (HOP-190).
    /// </summary>
    public DateTimeOffset? HopInterstitialOpenedAt { get; set; }

    /// <summary>
    /// Start of the transition into the channel (UTC), written once (HOP-190).
    /// </summary>
    public DateTimeOffset? HopChannelLaunchedAt { get; set; }

    /// <summary>
    /// Repeat human resolve from another browser (UTC), written once (SPEC-041 L93).
    /// </summary>
    public DateTimeOffset? HopRepeatResolvedAt { get; set; }
}
