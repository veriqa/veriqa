// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// Parameter Object for creating a transaction.
/// Groups all parameters required to create a new transaction.
/// </summary>
public sealed class CreateTransactionRequest
{
    /// <summary>
    /// Transaction type (login, confirmation). Required.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Preferred channel type. Null — no preference.
    /// </summary>
    public string? RequestedChannelType { get; init; }

    /// <summary>
    /// Allowed channel types. Null — all registered.
    /// </summary>
    public IReadOnlyList<string>? AllowedChannelTypes { get; init; }

    /// <summary>
    /// TTL override in seconds. Null — value from configuration.
    /// </summary>
    public int? TtlSeconds { get; init; }

    /// <summary>
    /// Idempotency key preventing duplication. Set it together with
    /// <see cref="IdempotencyScope"/> — one without the other is refused.
    /// </summary>
    /// <remarks>
    /// An extension point, and a known limitation of this release: nothing in the shipped product
    /// fills it in. The only caller of the creation path is the browser authorization endpoint, and
    /// there a repeated request is a new sign-in by meaning, not a retry of the previous one — so
    /// deriving a key from the correlation identifier would invent an idempotency that does not
    /// exist. The mechanism behind the key is complete and exercised end to end (the unique index of
    /// the stores, the lookup, the race handling), and it is waiting for the entry point where a
    /// repeat IS a repeat: the server-to-server creation of a confirmation transaction over client
    /// credentials (SPEC-039). A host driving the engine directly can set the pair today and get the
    /// behaviour described here.
    /// </remarks>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Idempotency scope — a stable identifier of the calling client. Set it together with
    /// <see cref="IdempotencyKey"/>; the same key in two scopes denotes two different requests.
    /// </summary>
    public string? IdempotencyScope { get; init; }

    /// <summary>
    /// Client request identifier for end-to-end tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Arbitrary client data as raw JSON. Maximum 4 KB.
    /// Passed as a string — the calling code is responsible for JSON validity.
    /// </summary>
    public string? ClientContext { get; init; }

    /// <summary>
    /// Confirmation data (confirmation type).
    /// </summary>
    public ConfirmationSnapshot? ConfirmationSnapshot { get; init; }

    /// <summary>
    /// OIDC request context (for transactions initiated by the OIDC flow).
    /// </summary>
    public OidcContext? OidcContext { get; init; }

    /// <summary>
    /// Request context of a transaction created outside the OIDC flow (SPEC-039 C19): the
    /// tenant/application attribution, the request language and the resolved <c>ui_config</c> code.
    /// Null — the request states no such context (every OIDC-initiated transaction).
    /// </summary>
    public TransactionRequestContext? RequestContext { get; init; }

    /// <summary>
    /// Expectations of the relying party about who is going to confirm (SPEC-039 C23), already
    /// protected by the caller. Null — no expectations were stated.
    /// </summary>
    public IdentityMatchState? IdentityMatch { get; init; }

    /// <summary>
    /// Initiator context collected on the request side (SPEC-017, SPEC-001 §10.4).
    /// Stored as the transaction's InitiatorContextSnapshot. Null — the context was not collected.
    /// </summary>
    public InitiatorContextSnapshot? InitiatorContext { get; init; }

    /// <summary>
    /// Requirement set of the transaction (SPEC-041 C35), built by the caller where the
    /// scopes-and-claims catalog and the request are known; stored as
    /// <see cref="Transaction.ClaimRequirements"/> and never changed after creation. At most one
    /// element per claim name. Empty — the request states no requirements.
    /// </summary>
    /// <remarks>
    /// Not a creation parameter compared on an idempotent repeat: the set is built only by the
    /// authorization endpoint, and that entry point carries no idempotency key.
    /// </remarks>
    public IReadOnlyList<ClaimRequirement> ClaimRequirements { get; init; } = [];
}
