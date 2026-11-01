// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Email.Domain;

/// <summary>
/// Message for sending the sign-in email in Pull mode (SPEC-016 §4.3): the magic link, a code, or both.
/// </summary>
/// <param name="ToAddress">Recipient's email address.</param>
/// <param name="ClientName">Client application name to display in the email.</param>
/// <param name="ActionToken">One-time action token for building the magic link.</param>
/// <param name="ExpiresAt">Token expiration moment (UTC).</param>
/// <param name="PublicBaseUrl">Public base URL for generating the magic link.</param>
/// <param name="Locale">Recipient locale for localizing the email: the IETF language tag of the
/// request that started the Pull flow (the mail is sent before confirmation, so no channel-side
/// locale exists yet). Null — the base language (English).</param>
public sealed record EmailLoginMessage(
    string ToAddress,
    string ClientName,
    string ActionToken,
    DateTimeOffset ExpiresAt,
    string PublicBaseUrl,
    string? Locale = null)
{
    /// <summary>
    /// Ownership levels of the transaction the mail is sent for (SPEC-036 TPL-116): the wording of the
    /// mail is a levelled setting like any other, so a text an application or a <c>ui_config</c> record
    /// declares reaches the recipient only when the send states the levels it was addressed to.
    /// <para>
    /// Required rather than nullable: "no levels" is a value of the type
    /// (<see cref="ResolutionContext.Core"/>, the self-hosted context), so a send cannot quietly leave
    /// the wording to the core level.
    /// </para>
    /// </summary>
    public required ResolutionContext Ownership { get; init; }

    /// <summary>
    /// Initiator context of the transaction (SPEC-017, delta of SPEC-016 §9.3 — informative, it
    /// complements but does not replace sender verification); null — the details were not captured.
    /// <para>
    /// The CONTEXT travels here, not a line rendered from it: the mail is a confirmation text of its
    /// own, so its template declares the initiator slots and words them itself. A context without
    /// details simply leaves those slots unfilled, and the ladder of the mail degrades onto the variant
    /// that names none of them.
    /// </para>
    /// </summary>
    public ConfirmationPromptContext? InitiatorContext { get; init; }

    /// <summary>
    /// Time zone the moments of the mail are shown in (an IANA identifier, for example
    /// <c>Europe/Berlin</c>) — the zone stated for the transaction that started the Pull flow. Null —
    /// the zone is unknown and the expiry is shown in UTC with the marker that says so.
    /// <para>
    /// It travels beside <see cref="Locale"/> and never apart from it: the two are one answer to how
    /// the recipient reads the expiry of the link — the conventions of the date and the zone it is
    /// stated in.
    /// </para>
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// Type of the transaction the mail is sent for (<see cref="Veriqa.Core.TransactionEngine.Domain.TransactionTypes"/>):
    /// the wording of the mail is addressed by it, so a confirmation gets the mail whose words name the
    /// confirmation (SPEC-016 §4.3) and a sign-in the sign-in mail. Null — the type is not stated, and the
    /// wording of the kind itself answers.
    /// </summary>
    public string? TransactionType { get; init; }

    /// <summary>
    /// Action the transaction confirms: the wording of the mail is addressed by it under
    /// <see cref="TransactionType"/>. Null — the transaction confirms no declared action.
    /// </summary>
    public string? ActionType { get; init; }

    /// <summary>
    /// Code of the mail, entered on the page the address was typed into (message kinds
    /// <c>sign-in-code-mail</c> and <c>sign-in-code-link-mail</c>); null — the mail carries no code, and it
    /// is the mail of the magic link (<c>sign-in-mail</c>).
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Whether the mail shows the magic link built of <see cref="ActionToken"/>. False only beside a
    /// <see cref="Code"/>: the mail of the code alone (<c>sign-in-code-mail</c>), whose action token is still
    /// issued — it identifies the letter to the gate of a sign-in — and is never shown.
    /// </summary>
    public bool CarriesLink { get; init; } = true;
}
