// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.Contracts.Audit;

/// <summary>
/// Closed set of safe attributes attached to an audit record.
/// </summary>
/// <remarks>
/// Deliberately a typed shape and not a free-form dictionary: the set of attributes an audit
/// record may carry is a contract, so an attribute outside this list cannot be attached at all
/// (SPEC-011 C43). Widening the set is a revision of that contract, not a call-site decision.
/// Every attribute is optional — an event that carries none of them yields an empty instance.
/// </remarks>
public sealed record AuditMetadata
{
    /// <summary>
    /// Type of the transaction the event belongs to (login, confirmation).
    /// </summary>
    public string? TransactionType { get; init; }

    /// <summary>
    /// Type of the channel involved in the event.
    /// </summary>
    public string? ChannelType { get; init; }

    /// <summary>
    /// Client-supplied correlation identifier of the transaction (end-to-end tracing).
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Tenant the audited event belongs to, exactly as the transaction stated it. Null when the
    /// installation states no tenant — the default implicit tenant of a self-hosted deployment —
    /// and never "unknown".
    /// </summary>
    /// <remarks>
    /// An opaque string of the core: the core neither narrows the form nor validates it, so a
    /// contour whose journal container is keyed by something stricter converts the value on its own
    /// side. The attribute is an INPUT of addressing and not the carrier of a record's membership:
    /// which journal a record belongs to is stated by the container it is written into.
    /// </remarks>
    public string? TenantId { get; init; }

    /// <summary>
    /// OIDC client_id of the application the event was raised in the context of.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Language the transaction was shown in — the IETF tag exactly as the request stated it. Null
    /// when the transaction states none.
    /// </summary>
    /// <remarks>
    /// Written as it was named and never as it resolved: the record answers "what was stated", not
    /// "what the platform managed to apply", so a tag no culture resolves is stored verbatim. The
    /// regional formatting conventions the renderer derived from it are deliberately NOT part of the
    /// set — they are a function of the deployment configuration at the moment of rendering, not a
    /// fact of the transaction.
    /// </remarks>
    public string? UiLocale { get; init; }

    /// <summary>
    /// Time zone the moments of the transaction were shown in — the IANA identifier exactly as the
    /// request stated it. Null when the transaction states none, which is the case of moments shown
    /// in UTC with the marker that says so.
    /// </summary>
    /// <remarks>
    /// Stored verbatim on the same grounds as <see cref="UiLocale"/>, and no guess is put in place
    /// of a missing value: the default of the deployment is already applied on the way in, so a
    /// transaction that names none had none to name.
    /// </remarks>
    public string? UiTimeZone { get; init; }

    /// <summary>
    /// Safe details of a channel audit event, exactly as the channel declared them. The core does
    /// not parse or extend the value; their composition is defined by the channel's own audit rules.
    /// </summary>
    public string? ChannelDetails { get; init; }

    /// <summary>
    /// Verification level the configuration DECLARES for the channel of this record — the token of the
    /// closed dictionary (<c>signature</c> / <c>shared_secret</c> / <c>outbound_fetch</c> /
    /// <c>none</c>). Null when the record belongs to no channel, or when no level declared a value.
    /// </summary>
    /// <remarks>
    /// It is a declared FACT of the configuration and never a statement the adapter made about itself:
    /// the core does not verify WHICH check an adapter ran, only that one ran and passed, so the
    /// journal records what the integrator declared and carries the risk for.
    /// <para>
    /// The value is the one declared AT THE MOMENT THE RECORD IS BUILT, not at the moment of the
    /// event: a record written after the configuration was edited carries the new value. The journal
    /// is a log of what was declared when it wrote, and the fact does not belong to the transaction.
    /// </para>
    /// <para>
    /// A token and not a .NET enum: what makes this set typed is that an attribute is NAMED and listed
    /// by the contract, not that it carries a platform type — and the contracts assembly is MIT and
    /// references no assembly of the product, so the dictionary that owns the token stays where it is.
    /// </para>
    /// </remarks>
    public string? ChannelInboundVerification { get; init; }

    /// <summary>
    /// Basis of a deletion of collected claims — a token of <see cref="AuditDeletionBases"/>
    /// (SPEC-041 L85). Null on every record that is not a deletion.
    /// </summary>
    public string? DeletionBasis { get; init; }

    /// <summary>
    /// Parameters of the confirmed operation. Null unless the deployment turned them on explicitly
    /// and the event belongs to a confirmation transaction that carries them.
    /// </summary>
    /// <remarks>
    /// The one attribute of this set that is not safe unconditionally: it carries subject data of
    /// the relying party (SPEC-011 C43, N39), so it stays off by default and is written only where an
    /// operator asked for it — <c>AuditTrailBuilder.ConfigureRecord</c>.
    /// </remarks>
    public AuditConfirmationParameters? ConfirmationParameters { get; init; }
}
