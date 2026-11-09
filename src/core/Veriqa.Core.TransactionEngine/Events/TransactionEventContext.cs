// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.TransactionEngine.Events;

/// <summary>
/// Attribution of a transaction event: the attributes of the transaction a subscriber needs but the
/// event itself does not state. Captured by the publisher at the moment of publication, while the
/// transaction is still there.
/// </summary>
/// <remarks>
/// Delivery to subscribers is asynchronous, so by the time an event is handled its transaction may
/// already be gone: it is swept as terminal by the cleanup service or dropped by a native store
/// TTL. An attribution read at handling time is
/// therefore non-deterministic — the same event yields a different result depending on who won the
/// race. Carried with the event, it is exact.
/// <para>
/// The set of attributes that leaves the process is closed and safe to publish as is: an event
/// travels through the RabbitMQ publisher satellite verbatim, so the channel identifier goes
/// masked, and no security token or unmasked personal datum is carried at all. The one attribute
/// that stays inside the process — the parameters of the confirmed operation — is excluded from
/// serialization for exactly that reason.
/// </para>
/// </remarks>
public sealed record TransactionEventContext
{
    /// <summary>
    /// Transaction type (login, confirmation).
    /// </summary>
    public string? TransactionType { get; init; }

    /// <summary>
    /// Tenant the transaction belongs to; null when the transaction states none — the default
    /// implicit tenant of a self-hosted installation.
    /// </summary>
    /// <remarks>
    /// Carried rather than excluded from serialization: the value deliberately leaves the process
    /// with the event through the RabbitMQ publisher satellite. It is an ownership identifier of the
    /// deployment, not a secret and not a personal datum, and the subscriber that writes the journal
    /// needs it to attribute the record to its owner.
    /// </remarks>
    public string? TenantId { get; init; }

    /// <summary>
    /// Identifier of the application the transaction was created for (OIDC client_id);
    /// null when the transaction has no OIDC context.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Display name of the application the transaction took at its creation; null when it took none.
    /// </summary>
    /// <remarks>
    /// Stays inside the process, as <see cref="ConfirmationParameters"/> does: its consumer is the
    /// in-channel expiry notice, which names the application as the other receipts of the transaction
    /// do, and the closed set an event carries outward is not widened for it.
    /// </remarks>
    [JsonIgnore]
    public string? ApplicationName { get; init; }

    /// <summary>
    /// Brand name the levels of the transaction stated at its creation, as configured; null when none.
    /// </summary>
    /// <remarks>
    /// Stays inside the process on the grounds of <see cref="ApplicationName"/>.
    /// </remarks>
    [JsonIgnore]
    public string? BrandName { get; init; }

    /// <summary>
    /// Client request identifier for end-to-end tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Language the transaction was shown in (IETF tag), exactly as its request context stated it;
    /// null when neither context states one.
    /// </summary>
    /// <remarks>
    /// Carried for the reason the whole attribution is, only sharper: the transaction is swept
    /// minutes after it completes while a record of the journal lives for months, so the one carrier
    /// of the answer "which language was the question put in" is gone long before the record is
    /// read. Like the tenant, it leaves the process with the event: a language tag is neither a
    /// secret nor a personal datum.
    /// </remarks>
    public string? UiLocale { get; init; }

    /// <summary>
    /// Time zone the moments of the transaction were shown in (IANA identifier), exactly as its
    /// request context stated it; null when neither context states one, which is the case of moments
    /// shown in UTC with the marker that says so.
    /// </summary>
    /// <remarks>
    /// Carried for the same reason as <see cref="UiLocale"/>, and it leaves the process on the same
    /// grounds.
    /// </remarks>
    public string? UiTimeZone { get; init; }

    /// <summary>
    /// Type of the channel whose identity is attached to the transaction; null while none is.
    /// </summary>
    public string? ChannelType { get; init; }

    /// <summary>
    /// Masked identifier of the user in the channel: at most the last two characters stay readable.
    /// The unmasked value never leaves the transaction store.
    /// </summary>
    public string? MaskedChannelUserId { get; init; }

    /// <summary>
    /// Parameters of the operation a confirmation transaction is about; null for a sign-in and for
    /// a confirmation that declared none.
    /// </summary>
    /// <remarks>
    /// The one attribute of the attribution that does NOT leave the process: it is subject data of
    /// the relying party, so it is excluded from serialization and an event published outward
    /// carries exactly the closed safe set it carried before. Its consumers are in-process: the audit
    /// trail, which writes it only where the deployment asked for it, and the in-channel expiry
    /// notice, which words the receipt replacing a shown prompt by its action type and caller slot
    /// values (SPEC-036 TPL-123). Carried here rather than read at handling time for the reason the
    /// whole attribution is: by then the transaction may already be gone.
    /// </remarks>
    [JsonIgnore]
    public ConfirmationSnapshot? ConfirmationParameters { get; init; }

    /// <summary>
    /// Captures the attribution of a live transaction.
    /// </summary>
    /// <param name="transaction">Transaction the event is published for.</param>
    /// <returns>Attribution of the event.</returns>
    public static TransactionEventContext FromTransaction(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var identity = transaction.ChannelIdentitySnapshot;

        return new TransactionEventContext
        {
            TransactionType = transaction.Type,

            // The three values below are normalized in this type and nowhere else: a blank string
            // states no tenant, no locale and no time zone, and letting one through would put a
            // phantom owner on the event, on the audit record and in the resolution context built
            // from it.
            TenantId = Stated(transaction.GetTenantId()),
            ClientId = transaction.GetApplicationId(),
            ApplicationName = transaction.GetApplicationName(),
            BrandName = transaction.GetBrandName(),
            CorrelationId = transaction.CorrelationId,
            UiLocale = Stated(transaction.GetUiLocale()),
            UiTimeZone = Stated(transaction.GetUiTimeZone()),
            ChannelType = identity?.ChannelType,
            MaskedChannelUserId = identity is null ? null : IdentifierMask.Mask(identity.ChannelUserId),
            ConfirmationParameters = transaction.ConfirmationSnapshot
        };
    }

    /// <summary>
    /// Captures the tenant alone, for an event whose publisher attaches no other attribution — the
    /// refusals the channels and the core pages write down (SPEC-011 R44).
    /// </summary>
    /// <remarks>
    /// The tenant is not an attribution of who acted: it is the layer the audit mode is resolved at
    /// and the journal the record is addressed to. So such a publisher carries it even where it
    /// withholds the application, the correlation and the identity of the transaction. The value is
    /// normalized as in <see cref="FromTransaction"/>.
    /// </remarks>
    /// <param name="tenantId">Tenant of the transaction the event is about; null when none is known.</param>
    /// <returns>Attribution carrying the tenant alone, or null when no tenant is stated — the event
    /// then carries no attribution at all.</returns>
    public static TransactionEventContext? ForTenant(string? tenantId) =>
        Stated(tenantId) is { } stated ? new TransactionEventContext { TenantId = stated } : null;

    /// <summary>
    /// Normalizes a captured value: an empty or whitespace value states nothing.
    /// </summary>
    /// <param name="value">Captured value.</param>
    /// <returns>The value, or null when it is null, empty or whitespace.</returns>
    private static string? Stated(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
