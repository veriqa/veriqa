// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Confirmation context handed to the adapter with a prompt (SPEC-003 §6.2, SPEC-017 §7.1).
/// Built by the channel processing pipeline from the transaction (ICC-041); the adapter receives
/// only the fields needed for rendering — never the raw IP or User-Agent (ICC-040).
/// The context is always present: "no details" is a value of it
/// (<see cref="SuppressedConfirmationPromptContext"/>), not a <see langword="null"/>.
/// </summary>
/// <remarks>
/// The hierarchy is closed (a private protected constructor). A future variant is added as another
/// sealed derived record; consumers must treat an unknown variant as "no details" plus a warning
/// rather than throw.
/// <para>
/// The type is marked experimental for exactly that reason: the variant is not hypothetical, it
/// arrives with the roadmap ahead, so this hierarchy may gain one in a minor version instead of
/// waiting for a major one.
/// </para>
/// </remarks>
[Experimental(VeriqaDiagnosticIds.UnsettledChannelSpiShape)]
public abstract record ConfirmationPromptContext
{
    /// <summary>
    /// Closes the hierarchy to this assembly.
    /// </summary>
    private protected ConfirmationPromptContext()
    {
    }

    /// <summary>
    /// Ownership levels of the transaction the prompt is asked about — the tenant, the application
    /// and the <c>ui_config</c> record selector it states (SPEC-036 TPL-116). The template of a
    /// channel message is resolved by the core over the ownership of the transaction itself
    /// (<c>IChannelMessageTemplates</c>), so a text declared by an application or a record actually
    /// reaches the message instead of being answered from the core level; these are the same levels.
    /// </summary>
    /// <remarks>
    /// Every variant of the context carries it, the suppressed one included: what ICC-042 suppresses
    /// is the DISPLAY of the initiator details, not the address the wording is resolved at. A
    /// suppressed context is precisely the case where the adapter falls back on its own declared
    /// text — and that text is the one a deployment is most likely to have overridden.
    /// <para>
    /// Required rather than nullable: "no levels" is a value of the type
    /// (<see cref="ResolutionContext.Core"/>, the self-hosted context), so there is no default here
    /// to forget.
    /// </para>
    /// </remarks>
    public required ResolutionContext Ownership { get; init; }

    /// <summary>
    /// Locale of the confirmation recipient (an IETF tag; null — base language).
    /// The single carrier of the locale: there is no second delivery path and no precedence rule.
    /// </summary>
    public string? RecipientLocale { get; init; }

    /// <summary>
    /// Time zone the moments of the message are shown in (an IANA identifier; null — the zone is not
    /// known and a moment is shown in UTC, said aloud by its marker).
    /// Travels with the locale and never apart from it: the two are one answer to "how does this
    /// recipient read a moment" — the conventions of the date and the zone it is stated in.
    /// </summary>
    public string? RecipientTimeZone { get; init; }

    /// <summary>
    /// Brand name the levels of the transaction stated at its creation (<c>AuthPageDesign.BrandName</c>),
    /// as configured — a Natural Key the text translates into the language of the recipient, as the brand
    /// row of the sign-in page does; null when no level states one. The value of the <c>brand_name</c>
    /// slot of the texts.
    /// </summary>
    /// <remarks>
    /// Every variant carries it, the suppressed one included: the brand is not an initiator detail, and a
    /// text an adapter falls back on may name it all the same.
    /// </remarks>
    public string? BrandName { get; init; }

    /// <summary>
    /// Builds the minimal context — "no details" with the given reason, locale and time zone.
    /// </summary>
    /// <param name="reason">Why the details are absent.</param>
    /// <param name="recipientLocale">Recipient locale (null — base language).</param>
    /// <param name="recipientTimeZone">Recipient time zone (null — UTC with its marker).</param>
    /// <returns>A context carrying no initiator details.</returns>
    /// <remarks>
    /// The ownership of the result is <see cref="ResolutionContext.Core"/>: this shorthand exists for
    /// a caller that has no transaction to take levels from. A caller that HAS one — the channel
    /// pipeline — builds <see cref="SuppressedConfirmationPromptContext"/> with an initializer and
    /// states the real ownership there, which is why this signature does not grow a parameter for it.
    /// </remarks>
    public static ConfirmationPromptContext Suppressed(
        ContextSuppressionReason reason,
        string? recipientLocale,
        string? recipientTimeZone)
        => new SuppressedConfirmationPromptContext
        {
            Reason = reason,
            Ownership = ResolutionContext.Core,
            RecipientLocale = recipientLocale,
            RecipientTimeZone = recipientTimeZone
        };
}

/// <summary>
/// Confirmation context carrying the initiator details (SPEC-017 §7.1).
/// </summary>
public sealed record DetailedConfirmationPromptContext : ConfirmationPromptContext
{
    /// <summary>
    /// Human-readable name of the client application (for the confirmation text).
    /// </summary>
    public required string ClientApplicationName { get; init; }

    /// <summary>
    /// Normalized browser name of the initiator (null — do not show).
    /// </summary>
    public string? Browser { get; init; }

    /// <summary>
    /// Normalized OS/platform of the initiator (null — do not show).
    /// </summary>
    public string? OsPlatform { get; init; }

    /// <summary>
    /// Device type of the initiator: desktop / mobile / tablet / unknown (null — undetermined).
    /// A SPEC-017 §7.1 contract field; the current message templates (§7.2) do not display it —
    /// available to custom adapter renderers.
    /// </summary>
    public string? DeviceType { get; init; }

    /// <summary>
    /// Initiator's country by GeoIP (approximate, null — do not show).
    /// </summary>
    public string? GeoCountry { get; init; }

    /// <summary>
    /// Initiator's city by GeoIP (approximate, null — do not show).
    /// </summary>
    public string? GeoCity { get; init; }

    /// <summary>
    /// Transaction initiation time (UTC).
    /// </summary>
    public DateTimeOffset InitiatedAt { get; init; }

    /// <summary>
    /// Moment the transaction was created (UTC) — the time of the request the question shows; null when
    /// the producer of the context states none. Every step of the shipped ladder that names the browser,
    /// the OS or the region names the time as well, so without it the shipped question degrades onto the
    /// application alone. Not <see cref="InitiatedAt"/>: that one is a member of the initiator context,
    /// this one is the transaction's own.
    /// </summary>
    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>
    /// Anomaly flag (SPEC-017 §9): an informative warning for the user,
    /// does not block confirmation (ICC-054).
    /// </summary>
    public bool AnomalyFlag { get; init; }

    /// <summary>
    /// Natural Key of the localizable anomaly reason (null — a generic flag).
    /// </summary>
    public string? AnomalyReasonKey { get; init; }

    /// <summary>
    /// Channel snapshot of the account the question is asked of — the source of the masked identity slots
    /// of the confirmation text (SPEC-036 TPL-102); null when no account is known.
    /// </summary>
    /// <remarks>
    /// The snapshot is carried whole and masked by the core where the slot values are assembled: the
    /// rule of a mask is the core's, so a renderer never decides what of the account to show.
    /// </remarks>
    public ChannelIdentitySnapshot? ChannelIdentity { get; init; }

    /// <summary>
    /// Name of the channel the question is asked in, in the language of the text — the value of the
    /// <c>channel_name</c> slot of the confirmation text; null when the channel is not known.
    /// </summary>
    public string? ChannelName { get; init; }

    /// <summary>
    /// Name under which the person sees the chat of the channel the question is asked in, ready to show
    /// (for example <c>@acme_bot</c>) — the value of the <c>bot_name</c> slot of the confirmation text;
    /// null when the channel states no name for its chat, or the name could not be obtained.
    /// </summary>
    public string? BotName { get; init; }
}

/// <summary>
/// Confirmation context of a transaction whose subject IS the confirmation (SPEC-039 R7): it carries
/// the question already worded — the text of the declared message of the action type, rendered from
/// the values the calling party supplied — rather than the fields an adapter would word itself.
/// </summary>
/// <remarks>
/// The variant carries a rendered STRING and not the message it was rendered from: the resolution of a
/// message and its rendering live in the channel-processing assembly, above this package, and the
/// decision "the question cannot be asked at all" has to be taken where the transaction and the
/// container are — never inside an adapter about to send.
/// <para>
/// An adapter built before this variant existed treats it as the hierarchy has always required — an
/// unknown variant is "no details" plus a warning, never a throw — so it falls back on its own default
/// text. Nothing of the subject reaches a user through such an adapter, which is exactly why the core
/// refuses a decision coming back from a channel the subject was not sent to (SPEC-039 E41).
/// </para>
/// </remarks>
public sealed record SubjectConfirmationPromptContext : ConfirmationPromptContext
{
    /// <summary>
    /// The question to show, worded and localized: plain text with every declared slot already
    /// substituted. Never empty — a context that could not be worded is not built at all.
    /// </summary>
    public required string PromptText { get; init; }
}

/// <summary>
/// Confirmation context without initiator details (SPEC-017 §7.1, ICC-042).
/// The adapter renders its own default text; the reason is carried for diagnostics only and never
/// changes the rendering.
/// </summary>
public sealed record SuppressedConfirmationPromptContext : ConfirmationPromptContext
{
    /// <summary>
    /// Why the initiator details are absent.
    /// </summary>
    public required ContextSuppressionReason Reason { get; init; }
}
