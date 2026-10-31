// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net;

using Microsoft.Extensions.Logging;

using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Renderer of a resolved message (SPEC-036 §5.2). The single substitution engine for the confirmation
/// prompt and the sign-in mail strings: it builds the matcher from the CONTRACT of the message (not
/// from the input), picks the template variant, degrades a broken translation onto the base text,
/// renders typed values per the recipient's locale, sanitizes them, and substitutes in a single pass.
/// Deterministic and free of I/O (TPL-035): the message, the values, the locale and the localizer are
/// all inputs.
/// </summary>
public static class MessageTextRenderer
{
    /// <summary>
    /// Marker appended to a rendered datetime so a security-relevant expiry is never ambiguous (D9).
    /// The moment is converted to UTC before it is formatted, whichever shape it is given.
    /// </summary>
    private const string UtcMarker = " UTC";

    /// <summary>
    /// Format of a WHOLE moment — the locale's short date beside its short time (the general "g"
    /// specifier). SPEC-036 TPL-016 asks a datetime to be rendered "in the format of the recipient's
    /// locale", and a moment is a date and a time: this is the shape a moment has unless the product
    /// itself owns what the value means (see <see cref="RenderDatetime"/>).
    /// </summary>
    private const string WholeMomentFormat = "g";

    /// <summary>
    /// Format of a WHOLE moment for a recipient no locale reached — the invariant culture
    /// <see cref="ResolveCulture"/> degrades an absent or unknown tag onto (TPL-043). Its neutral
    /// defaults order the short date month-first, so 06/05 is read as 5 June by one reader and as 6 May
    /// by another — and unlike the zone, which the UTC marker states aloud, the order of a date carries
    /// nothing that would tell them apart. ISO 8601 order names each part by its position (year, month,
    /// day) and needs no knowledge of the reader. It is printed WITH the invariant culture and no other:
    /// ISO 8601 is defined over the Gregorian calendar, so its order may not be filled from another
    /// culture's calendar (a Persian year under ISO separators would read as a wrong Gregorian one).
    /// </summary>
    private const string UnknownLocaleMomentFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// Length cap handed to the sanitizer for a value that is not truncated at all — the value of a
    /// slot the core computes itself (see <see cref="RenderParts"/>).
    /// </summary>
    private const int UncappedValueLength = int.MaxValue;

    /// <summary>
    /// Renders <paramref name="message"/> using <paramref name="values"/>.
    /// </summary>
    /// <param name="message">The resolved message: its contract and its ordered template variants.</param>
    /// <param name="values">Slot name → typed value (string / DateTimeOffset / number). A missing key or a
    /// value that sanitizes to empty means the slot has no value (not a null placeholder). A
    /// localized-string slot is not passed here: its value is the translation of the key the contract
    /// names.</param>
    /// <param name="recipientLocale">Recipient locale (null/unknown — base language + invariant culture).</param>
    /// <param name="recipientTimeZone">Time zone the moments are shown in (IANA identifier; null or
    /// unresolvable — UTC with the marker that says so).</param>
    /// <param name="localizer">Natural Key localizer (null — base language).</param>
    /// <param name="mode">Plain text (messenger / text mail) or HTML (HTML mail body).</param>
    /// <param name="logger">Logger for the translation-degradation and fallback warnings (null — none).</param>
    /// <returns>The rendered message text, or null when no step of the ladder states the edition this
    /// sink asks for — see <see cref="RenderParts"/>.</returns>
    public static string? Render(
        ResolvedMessage message,
        IReadOnlyDictionary<string, object?> values,
        string? recipientLocale,
        string? recipientTimeZone,
        IConfirmationPromptLocalizer? localizer,
        MessageRenderMode mode,
        ILogger? logger) =>
        RenderParts(message, values, recipientLocale, recipientTimeZone, localizer, mode, logger)?.Body;

    /// <summary>
    /// Renders <paramref name="message"/> into the PARTS a channel that has more than one of them needs
    /// — a mail, whose step states a subject beside the editions of its body (SPEC-036 §4.3).
    /// </summary>
    /// <param name="message">The resolved message: its contract and its ordered template variants.</param>
    /// <param name="values">Slot name → typed value; see <see cref="Render"/>.</param>
    /// <param name="recipientLocale">Recipient locale (null/unknown — base language + invariant culture).</param>
    /// <param name="recipientTimeZone">Time zone the moments are shown in (IANA identifier; null or
    /// unresolvable — UTC with the marker that says so).</param>
    /// <param name="localizer">Natural Key localizer (null — base language).</param>
    /// <param name="mode">Mode the render point asks for — the mode of the SINK. It names the EDITION
    /// the ladder is asked for and, with it, how the values of the chosen text are escaped.</param>
    /// <param name="logger">Logger for the translation-degradation and fallback warnings (null — none).</param>
    /// <returns>The rendered parts of the message, or <c>null</c> when NO step of the ladder states the
    /// edition this sink asks for. That is a legitimate declaration and not an error — "this mail is
    /// HTML only" is said exactly that way (SPEC-016 §4.3) — so what an absent edition means is the
    /// sink's to decide: a mail goes out without that part, a sink that has only one part has no
    /// message at all.</returns>
    public static RenderedMessageText? RenderParts(
        ResolvedMessage message,
        IReadOnlyDictionary<string, object?> values,
        string? recipientLocale,
        string? recipientTimeZone,
        IConfirmationPromptLocalizer? localizer,
        MessageRenderMode mode,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(values);

        var culture = ResolveCulture(recipientLocale, localizer);

        // The zone is resolved ONCE per render, next to the culture: the two are the one answer to
        // "how does this recipient read a moment", and both are wanted by every typed value below.
        var zone = ResolveZone(recipientTimeZone);

        // A slot is "available" for variant selection when the caller supplied its value (a non-null key).
        // Presence is the caller's decision (a best-effort server slot is simply omitted when absent, and
        // a caller value is non-empty by validation); the value is then typed-rendered and sanitized (the
        // second echelon), and an empty result substitutes as an empty string rather than dropping the slot.
        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in message.Contract.Slots)
        {
            // A localized string comes with no value of its own: the CONTRACT names the Natural Key, and
            // the slot stands for its translation into the recipient's language, degrading onto the base
            // text exactly as the text of the variant does. The values are not consulted for it at all —
            // otherwise a render point (and through it the calling party) would get to say what a phrase
            // of the product says. What lands here is text and is escaped like any other value in HTML
            // mode: markup lives in the text of the variant, never in a translation.
            if (slot is { Source: SlotSource.LocalizedText, NaturalKey: { } naturalKey })
            {
                var localized = localizer?.ResolveOrBaseText(naturalKey, recipientLocale)
                    ?? NaturalKeyContext.BaseTextOf(naturalKey);

                rendered[slot.Name] = MessageValueSanitizer.Sanitize(localized) ?? string.Empty;

                continue;
            }

            if (!values.TryGetValue(slot.Name, out var raw) || raw is null)
            {
                continue;
            }

            // A configured text is translated as the page that shows it translates it — the brand name
            // reads the same in the brand row of the sign-in page and in the text.
            var typed = raw is NaturalKeySlotValue configured
                ? localizer?.ResolveOrBaseText(configured.NaturalKey, recipientLocale)
                    ?? NaturalKeyContext.BaseTextOf(configured.NaturalKey)
                : RenderTypedValue(slot, raw, culture, zone);

            // The length ceiling protects a message from a value somebody ELSE wrote — the calling party,
            // the user agent of the initiator, the claims of an identity, a translation file. A
            // ServerSystem value is one the core computes itself, and cutting it breaks what it is: a
            // sign-in link shortened to the ceiling opens nothing. Control characters are neutralized
            // and the value is trimmed for every source alike.
            rendered[slot.Name] = (slot.Source is SlotSource.ServerSystem
                ? MessageValueSanitizer.Sanitize(typed, UncappedValueLength)
                : MessageValueSanitizer.Sanitize(typed)) ?? string.Empty;
        }

        var edition = EditionOf(mode);
        var variant = SelectVariant(message, rendered, edition, logger);

        // No step of the ladder states the edition this sink is about to deliver. Nothing is rendered
        // and nothing is fallen back onto: taking a step of the OTHER edition would put plain text into
        // an HTML part (or markup into a plain one) — the very seam the edition closes.
        if (variant?.TextOf(edition) is not { } stated)
        {
            return null;
        }

        var declaredSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in message.Contract.Slots)
        {
            declaredSlots.Add(slot.Name);
        }

        // A subject is never markup — the mail client shows it as text — so its values are substituted
        // unescaped whatever the body's mode is.
        var subject = variant.Subject is null
            ? null
            : Substitute(variant.Subject, rendered, declaredSlots, MessageRenderMode.PlainText, recipientLocale, localizer, logger);

        // The body is rendered in the mode of the SINK — the one the render point asked for, which is
        // where the result is about to go. The two can no longer disagree: the sink asks the step for the
        // edition it is about to deliver, and a step that does not state it is not a candidate at all. An
        // HTML sink can therefore never end up delivering a text written as plain, which is what would
        // take the escaping off the part it lands in and let a slot value supplied by the calling RP
        // arrive as live markup (core-rules §10).
        var body = Substitute(
            stated, rendered, declaredSlots, mode, recipientLocale, localizer, logger);

        return new RenderedMessageText(subject, body, ReferencedSlots(variant, edition));
    }

    /// <summary>
    /// Slot names the RENDERED texts reference — the subject and the body of the delivered edition, not
    /// the other edition of the same step: a channel deciding on an attachment by this set must be told
    /// what the part it is shipping actually points at. Read off the DECLARATION and not off the
    /// rendered string: after substitution a token is gone, and a channel that has to know which form of
    /// a message was chosen would be left parsing the result back.
    /// </summary>
    /// <param name="variant">The chosen template variant.</param>
    /// <param name="edition">Edition the sink asked for.</param>
    /// <returns>The referenced slot names.</returns>
    private static IReadOnlySet<string> ReferencedSlots(
        MessageTemplateVariant variant,
        MessageTemplateEdition edition)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var text in variant.TextsOf(edition))
        {
            referenced.UnionWith(SlotTokenScanner.ExtractSlotNames(text));
        }

        return referenced;
    }

    /// <summary>
    /// Localizes one text of a variant and substitutes the values into it.
    /// <para>
    /// <b>Only the VALUES are escaped in HTML mode, never the text itself.</b> The text is written by
    /// whoever owns the configuration — a body may legitimately BE markup, and escaping it would deliver
    /// the tags to the recipient as visible characters. The values are the other party's: they arrive
    /// from the calling RP over the API, and escaping them is what keeps a value from closing a tag the
    /// text opened (core-rules §10).
    /// </para>
    /// </summary>
    /// <param name="text">Base Natural Key of the text.</param>
    /// <param name="rendered">Slot name → the typed value, rendered and sanitized, still unescaped.</param>
    /// <param name="declaredSlots">Names the contract declares.</param>
    /// <param name="mode">Mode this text is rendered in.</param>
    /// <param name="recipientLocale">Recipient locale.</param>
    /// <param name="localizer">Natural Key localizer.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The rendered text.</returns>
    private static string Substitute(
        string text,
        IReadOnlyDictionary<string, string> rendered,
        IReadOnlySet<string> declaredSlots,
        MessageRenderMode mode,
        string? recipientLocale,
        IConfirmationPromptLocalizer? localizer,
        ILogger? logger)
    {
        var values = mode is MessageRenderMode.Html ? Escaped(rendered) : rendered;

        // Resolve the base Natural Key into the recipient's language, degrading a broken translation
        // (declared-slot set changed) back onto the base text with a WARNING (TPL-042).
        var resolved = ResolveTemplate(text, declaredSlots, recipientLocale, localizer, logger);

        return SlotTokenScanner.Substitute(resolved, values);
    }

    /// <summary>
    /// The same values, HTML-escaped — the one place the escaping of a slot value happens.
    /// </summary>
    /// <param name="rendered">Slot name → the rendered value.</param>
    /// <returns>Slot name → the escaped value.</returns>
    private static Dictionary<string, string> Escaped(IReadOnlyDictionary<string, string> rendered)
    {
        var escaped = new Dictionary<string, string>(rendered.Count, StringComparer.Ordinal);

        foreach (var (name, value) in rendered)
        {
            escaped[name] = WebUtility.HtmlEncode(value);
        }

        return escaped;
    }

    /// <summary>
    /// The edition a render mode asks a step for. The two spellings of one thing: the mode is what a
    /// render point of this contour says, the edition is what a step of a ladder states (SPEC-036 §4.3).
    /// </summary>
    /// <param name="mode">Mode the render point asks for.</param>
    /// <returns>The edition that serves it.</returns>
    private static MessageTemplateEdition EditionOf(MessageRenderMode mode) => mode is MessageRenderMode.Html
        ? MessageTemplateEdition.Html
        : MessageTemplateEdition.Plain;

    /// <summary>
    /// Selects the step to render: among the steps STATING the edition the sink asks for, the first one
    /// all of whose slots have values (TPL-031). When none is fully covered, falls back to the minimal
    /// (last) of them with a WARNING (D5) — a "raw {slot}" never appears in any branch (TPL-032).
    /// <para>
    /// A step stating another edition only is skipped in silence: it is not a defect but the shape of a
    /// ladder whose two editions have different depths (the HTML wording of a mail may rest on an
    /// attachment the plain one has no place for). When NO step states the edition, the whole render has
    /// no answer and says so by returning null — there is no ladder to fall back onto.
    /// </para>
    /// </summary>
    /// <param name="message">The resolved message.</param>
    /// <param name="available">Slot name → the rendered value.</param>
    /// <param name="edition">Edition the sink asks for.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The step to render, or null when no step states the edition.</returns>
    private static MessageTemplateVariant? SelectVariant(
        ResolvedMessage message,
        IReadOnlyDictionary<string, string> available,
        MessageTemplateEdition edition,
        ILogger? logger)
    {
        var candidates = OfEdition(message, edition);

        if (candidates.Count is 0)
        {
            return null;
        }

        foreach (var variant in candidates)
        {
            var covered = true;

            foreach (var text in variant.TextsOf(edition))
            {
                foreach (var referenced in SlotTokenScanner.ExtractSlotNames(text))
                {
                    if (!available.ContainsKey(referenced))
                    {
                        covered = false;

                        break;
                    }
                }

                if (!covered)
                {
                    break;
                }
            }

            if (covered)
            {
                return variant;
            }
        }

        logger?.LogWarning(
            "No template variant of the {Edition} edition of kind {Kind} is fully covered by values; "
            + "falling back to the minimal variant of that edition.",
            edition,
            message.Kind);

        return candidates[^1];
    }

    /// <summary>
    /// The steps a sink asking one edition may use: the ones stating that edition (a step spelled as a
    /// string states both, so it serves any sink). The rule is the one the startup check and the runtime
    /// admission read the floor of a ladder by (<see cref="MessageTemplateVariant.Floors"/>) — one rule,
    /// three readers.
    /// </summary>
    /// <param name="message">The resolved message.</param>
    /// <param name="edition">Edition the sink asks for.</param>
    /// <returns>The steps, in the order the ladder states them.</returns>
    private static IReadOnlyList<MessageTemplateVariant> OfEdition(
        ResolvedMessage message,
        MessageTemplateEdition edition)
    {
        var candidates = new List<MessageTemplateVariant>(message.Templates.Count);

        foreach (var variant in message.Templates)
        {
            if (variant.TextOf(edition) is not null)
            {
                candidates.Add(variant);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Resolves a base Natural Key into the recipient's language and enforces slot-set preservation
    /// (TPL-041/TPL-042): if the translation's slot set differs from the base, the base text is used and
    /// a WARNING is logged (naming only the key and locale, never a value — core-rules §10).
    /// Degradation goes to the key's BASE TEXT (<see cref="NaturalKeyContext"/>), so a template key with
    /// a context suffix never renders its label into the message.
    /// </summary>
    private static string ResolveTemplate(
        string baseTemplate,
        IReadOnlySet<string> declaredSlots,
        string? recipientLocale,
        IConfirmationPromptLocalizer? localizer,
        ILogger? logger)
    {
        var resolved = localizer?.Resolve(baseTemplate, recipientLocale);
        if (resolved is null || string.Equals(resolved, baseTemplate, StringComparison.Ordinal))
        {
            return NaturalKeyContext.BaseTextOf(baseTemplate);
        }

        // Compare only DECLARED slot tokens: a non-declared token such as {typo} is not a slot and must
        // stay literal (TPL-033), so it must not by itself trigger degradation. A lost or corrupted
        // DECLARED slot does trigger it (TPL-042).
        var baseSlots = Intersect(SlotTokenScanner.ExtractSlotNames(baseTemplate), declaredSlots);
        var translatedSlots = Intersect(SlotTokenScanner.ExtractSlotNames(resolved), declaredSlots);
        if (!baseSlots.SetEquals(translatedSlots))
        {
            logger?.LogWarning(
                "A translation changed the slot set — falling back to the base-language text. Key: {Key}, locale: {Locale}.",
                baseTemplate,
                recipientLocale);

            return NaturalKeyContext.BaseTextOf(baseTemplate);
        }

        return resolved;
    }

    /// <summary>
    /// Returns the subset of <paramref name="tokens"/> that are declared slots.
    /// </summary>
    private static IReadOnlySet<string> Intersect(IReadOnlySet<string> tokens, IReadOnlySet<string> declaredSlots)
    {
        var intersection = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (declaredSlots.Contains(token))
            {
                intersection.Add(token);
            }
        }

        return intersection;
    }

    /// <summary>
    /// Renders a typed value to text: number by locale, datetime in the recipient's zone by the locale
    /// (D9), string/entity_ref verbatim (entity_ref strictly text — no markup/link).
    /// </summary>
    private static string RenderTypedValue(
        SlotDeclaration slot,
        object value,
        CultureInfo culture,
        TimeZoneInfo? zone) => slot.Type switch
        {
            SlotType.Number => value switch
            {
                IFormattable formattable => formattable.ToString(null, culture),
                _ => value.ToString() ?? string.Empty,
            },
            SlotType.Datetime => RenderDatetime(slot, value, culture, zone),
            // string / entity_ref (and the forward-declared money never reaches render in v1) — as text.
            _ => value.ToString() ?? string.Empty,
        };

    /// <summary>
    /// Renders a datetime value in the recipient's locale, in the recipient's zone. HOW MUCH of the
    /// moment is shown is decided by the declaration: a system moment is shortened to the time of day,
    /// every other one is shown whole. The two axes are independent — the zone applies to a moment of
    /// either source alike.
    /// </summary>
    private static string RenderDatetime(
        SlotDeclaration slot,
        object value,
        CultureInfo culture,
        TimeZoneInfo? zone)
    {
        var moment = value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt),
            _ => (DateTimeOffset?)null,
        };

        if (moment is null)
        {
            return value.ToString() ?? string.Empty;
        }

        // The zone the reader keeps their own calendar in needs no marker — a wall clock is read in the
        // zone the reader lives in, and naming it would be noise on every message. Where no zone is
        // known the moment is normalized to UTC and SAYS SO: an expiry is security-relevant, and an
        // unmarked one in an unstated zone is worse than an explicit UTC.
        var shown = zone is null ? moment.Value.ToUniversalTime() : TimeZoneInfo.ConvertTime(moment.Value, zone);
        var marker = zone is null ? UtcMarker : string.Empty;

        // Dropping a part of a value is allowed only where the product itself owns what the value means.
        // A ServerSystem moment is one the core states for a text the core ships — today the expiry of a
        // sign-in link and the time of the request a question asks about, both moments inside the TTL of
        // the very request the reader is answering: their calendar date says nothing, and the time of day
        // is the shape such a value has always had (D9). A moment of any other source is worded by someone
        // whose meaning the product does not know — an RP subject saying "the meeting", "the payment",
        // "the deadline"; shortening it to the time of day makes
        // "today" and "in four days" read exactly alike, so it is shown whole (TPL-016).
        if (slot.Source is SlotSource.ServerSystem)
        {
            return shown.ToString(culture.DateTimeFormat.ShortTimePattern, culture) + marker;
        }

        // A whole moment is shown the way the recipient's locale reads it — its order, its calendar, its
        // conventions, all of which the platform owns and none of which this renderer second-guesses: a
        // Persian year for fa-AF and a Buddhist one for th-TH are that reader's date, not a defect. Only
        // where no locale reached the render at all — the culture is then the invariant one — does the
        // fallback shape apply, and it is formatted with that same invariant culture.
        return culture.Equals(CultureInfo.InvariantCulture)
            ? shown.ToString(UnknownLocaleMomentFormat, CultureInfo.InvariantCulture) + marker
            : shown.ToString(WholeMomentFormat, culture) + marker;
    }

    /// <summary>
    /// Resolves a stated zone identifier into a zone, degrading an absent or unresolvable one to
    /// "the zone is not known" — the same shape an unknown locale degrades in.
    /// </summary>
    /// <remarks>
    /// The entries validate what a caller states before it ever reaches a transaction, so an
    /// unresolvable value arriving here means the deployment moved between installations with
    /// different zone data. UTC with its marker is then the honest answer: converting to a zone that
    /// is not the reader's would print another moment with no sign that it is one.
    /// </remarks>
    /// <param name="recipientTimeZone">Stated IANA time zone identifier (null — none).</param>
    /// <returns>The zone, or null when none is known.</returns>
    private static TimeZoneInfo? ResolveZone(string? recipientTimeZone)
    {
        if (string.IsNullOrWhiteSpace(recipientTimeZone))
        {
            return null;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(recipientTimeZone, out var zone) ? zone : null;
    }

    /// <summary>
    /// Resolves a locale tag to a culture, degrading an unknown/unavailable locale to the invariant
    /// culture (TPL-043). <c>predefinedOnly</c> is what draws that line, and it is the platform's to
    /// draw: a tag the platform has locale data for is accepted with the conventions it carries, while a
    /// merely well-formed one — <c>zz</c>, <c>en-XX</c> — is rejected rather than materialized as a
    /// custom culture over neutral defaults. A culture returned from here therefore always states the
    /// conventions of a real locale, and the invariant culture means exactly "no locale".
    /// </summary>
    private static CultureInfo ResolveCulture(string? recipientLocale, IConfirmationPromptLocalizer? localizer)
    {
        if (string.IsNullOrWhiteSpace(recipientLocale))
        {
            return CultureInfo.InvariantCulture;
        }

        // A language stated WITHOUT a region has the conventions the platform ships for it, and for
        // the base language of the product those are American ones — bare "en" orders a short date
        // month-first, which every reader outside that region reads as another day. The deployment is
        // the party that knows which region its readers are in, and states it through the
        // localization port; a language that already carries a region is never touched here.
        if (!recipientLocale.Contains('-', StringComparison.Ordinal)
            && localizer?.RegionalCultureFor(recipientLocale) is { } regional
            && Predefined(regional) is { } statedCulture)
        {
            return statedCulture;
        }

        // A stated region that this installation has no locale data for falls through to the language
        // itself rather than to the invariant culture: the deployment asked for a better region, not
        // for the loss of the language it named.
        return Predefined(recipientLocale) ?? CultureInfo.InvariantCulture;
    }

    /// <summary>
    /// The culture of a tag the platform has locale data for, or null. <c>predefinedOnly</c> is what
    /// draws that line, and it is the platform's to draw — see <see cref="ResolveCulture"/>.
    /// </summary>
    /// <param name="tag">Language tag.</param>
    /// <returns>The culture, or null when the platform carries no locale by that name.</returns>
    private static CultureInfo? Predefined(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
