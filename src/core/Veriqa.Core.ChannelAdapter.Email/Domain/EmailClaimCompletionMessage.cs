// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Email.Domain;

/// <summary>
/// A mail of the claims completion (SPEC-041 L51): the code, the link or both that confirm an email address
/// the user entered on the web step. It is a mail of its own kind, not the sign-in mail — it carries no
/// initiator details and no text the calling party states, only the recipient address itself.
/// </summary>
/// <param name="ToAddress">Recipient address, as the code or the link was issued to it.</param>
/// <param name="ClientName">Name of the application the user is signing in to.</param>
/// <param name="Code">
/// Code of the mail (message kinds <c>claim-code-mail</c> and <c>claim-code-link-mail</c>); null for a mail
/// with a link only.
/// </param>
/// <param name="Link">
/// Absolute URL of the confirmation page (message kinds <c>claim-link-mail</c> and
/// <c>claim-code-link-mail</c>); null for a mail with a code only. At least one of
/// <paramref name="Code"/> and <paramref name="Link"/> is stated; a mail that states both carries both.
/// </param>
/// <param name="ExpiresAt">Moment the code and the link of the mail stop being accepted (UTC).</param>
/// <param name="Locale">Language of the mail (IETF tag); null — the base language.</param>
public sealed record EmailClaimCompletionMessage(
    string ToAddress,
    string ClientName,
    string? Code,
    string? Link,
    DateTimeOffset ExpiresAt,
    string? Locale)
{
    /// <summary>
    /// Ownership levels of the transaction the mail is sent for (SPEC-036 TPL-116): the wording of the
    /// mail is a levelled setting, resolved at the levels this send states — as for
    /// <see cref="EmailLoginMessage.Ownership"/>.
    /// </summary>
    public required ResolutionContext Ownership { get; init; }

    /// <summary>
    /// Type of the transaction the mail is sent for: the wording of the mail is addressed by it, as for
    /// <see cref="EmailLoginMessage.TransactionType"/>. Null — the wording of the kind itself answers.
    /// </summary>
    public string? TransactionType { get; init; }

    /// <summary>
    /// Action the transaction confirms, as for <see cref="EmailLoginMessage.ActionType"/>; null — none.
    /// </summary>
    public string? ActionType { get; init; }

    /// <summary>
    /// Time zone the expiry of the mail is shown in (an IANA identifier); null — UTC with the marker
    /// that says so. It travels beside <see cref="Locale"/>, as for <see cref="EmailLoginMessage.TimeZone"/>.
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// Brand name the levels of the transaction stated at its creation, as configured; null when none —
    /// the <c>brand_name</c> slot of the mail, as for <see cref="ConfirmationPromptContext.BrandName"/>.
    /// </summary>
    public string? BrandName { get; init; }
}
