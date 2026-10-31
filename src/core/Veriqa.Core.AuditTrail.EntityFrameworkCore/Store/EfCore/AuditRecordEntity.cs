// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuditTrail.Store.EfCore;

/// <summary>
/// Flat EF Core entity of an audit record. Mirrors the fixed record schema one column per field:
/// the metadata attributes get their own columns rather than a JSON blob, so the closed set of
/// attributes stays closed in the database too.
/// Not sealed: EF Core requires the ability to create proxy-derived classes.
/// </summary>
/// <remarks>
/// The one attribute that cannot get a column per value is the slot values of the confirmed
/// operation: their names are declared by the relying party, so the map is open by contract while
/// the set of metadata attributes carrying it stays closed. It is stored as JSON in a single
/// column, the way the transaction store already stores its snapshots.
/// </remarks>
internal class AuditRecordEntity
{
    /// <summary>
    /// Surrogate storage key of the record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Moment the audited event occurred (UTC).
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Who acted.
    /// </summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>
    /// Stable code of the audited event.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the audited object.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// Outcome of the event (string name of the outcome enum).
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Reason code of a failure. Null for a success.
    /// </summary>
    public string? ReasonCode { get; set; }

    /// <summary>
    /// Metadata attribute: type of the transaction.
    /// </summary>
    public string? TransactionType { get; set; }

    /// <summary>
    /// Metadata attribute: type of the channel.
    /// </summary>
    public string? ChannelType { get; set; }

    /// <summary>
    /// Metadata attribute: correlation identifier of the transaction.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Metadata attribute: tenant the audited event belongs to. Null when the installation states
    /// none (the default implicit tenant of a self-hosted deployment); the rows written before the
    /// column existed carry NULL for the same reason and are never filled in afterwards (the journal
    /// is append-only).
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Metadata attribute: OIDC client_id of the application.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Metadata attribute: language the transaction was shown in (IETF tag as stated). Null when the
    /// transaction states none.
    /// </summary>
    public string? UiLocale { get; set; }

    /// <summary>
    /// Metadata attribute: time zone the moments of the transaction were shown in (IANA identifier
    /// as stated). Null when the transaction states none — the case of moments shown in UTC.
    /// </summary>
    public string? UiTimeZone { get; set; }

    /// <summary>
    /// Metadata attribute: safe details declared by the channel.
    /// </summary>
    public string? ChannelDetails { get; set; }

    /// <summary>
    /// Metadata attribute: verification level the configuration declares for the channel of the
    /// record — the token of the closed dictionary. Null when the record belongs to no channel or no
    /// level declared a value; the rows written before the column existed carry NULL for the same
    /// reason and are never filled in afterwards (the journal is append-only).
    /// </summary>
    public string? ChannelInboundVerification { get; set; }

    /// <summary>
    /// Metadata attribute: basis of a deletion of collected claims — a token of the closed dictionary.
    /// Null on every record that is not such a deletion, and on the rows written before the column existed.
    /// </summary>
    public string? DeletionBasis { get; set; }

    /// <summary>
    /// Metadata attribute: declared kind of the confirmed operation. Null unless the deployment
    /// turned the parameters of the confirmed operation on.
    /// </summary>
    public string? ConfirmationActionType { get; set; }

    /// <summary>
    /// Metadata attribute: slot values of the confirmed operation, as JSON (slot name → value in
    /// the canonical textual form of its declared type). Null unless the deployment turned the
    /// parameters of the confirmed operation on and the operation declared values.
    /// </summary>
    public string? ConfirmationSlotValuesJson { get; set; }

    /// <summary>
    /// Position of the record in the journal. Reserved space for a possible future tamper-evident
    /// chain together with <see cref="PreviousRecordHash"/>, and always null: nothing computes it,
    /// and no implementation is scheduled. The column exists so that shipping the chain — if it is
    /// ever shipped — does not cost every deployment a second migration of a table that only grows.
    /// </summary>
    public long? Sequence { get; set; }

    /// <summary>
    /// Hash of the preceding record. Reserved together with <see cref="Sequence"/> as space for a
    /// possible future implementation, and always null — a non-null value here would prove nothing
    /// on its own.
    /// </summary>
    public string? PreviousRecordHash { get; set; }
}
