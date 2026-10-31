// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Renderer of the confirmation message text from ConfirmationPromptContext (SPEC-017 §7.2–7.3).
/// Shared across all adapters: assembles the text from the available fields; missing best-effort
/// fields are omitted rather than shown as "unknown". A context that carries no initiator details
/// makes the adapter use its own default text (ICC-042); the default text is also a Natural Key and,
/// when a locale is provided, is resolved into the recipient's language (ICC-050).
/// Localization (SPEC-017 §7.2, ICC-050): texts are Natural Keys from all locale files;
/// when a localizer is provided, templates and the anomaly note are resolved into the recipient's
/// language (ConfirmationPromptContext.RecipientLocale — the single carrier of the locale),
/// otherwise — base-language text (English, the base language after the TASK-057 flip).
/// </summary>
public static class ConfirmationPromptRenderer
{
    /// <summary>
    /// Builds the confirmation message text.
    /// </summary>
    /// <param name="context">Confirmation context; a context without initiator details yields defaultText.</param>
    /// <param name="defaultText">Adapter's default text when the details are absent (Natural Key, ICC-050).</param>
    /// <param name="prompt">
    /// The resolved confirmation-prompt message, obtained by the caller through the canonical resolver
    /// (SPEC-036 TPL-001): a message is two levelled settings, so a variant declared by a deployment
    /// reaches the rendered text.
    /// </param>
    /// <param name="localizer">Localizer of Natural Keys into the recipient's language (null — base language).</param>
    /// <param name="logger">Logger of the render degradations of the prompt (null — none). Stated by
    /// the caller because this is a pure text builder holding no logger of its own, and the caller —
    /// the adapter that sends the prompt — has one.</param>
    /// <returns>Confirmation message text.</returns>
    public static string Render(
        ConfirmationPromptContext context,
        string defaultText,
        ResolvedMessage prompt,
        IConfirmationPromptLocalizer? localizer,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // The subject of a confirmation arrives already worded: it was rendered where the transaction
        // and the container are, by this very mechanism and with its protections, because the decision
        // "the question cannot be asked" belongs before the send and not inside it (SPEC-039 R7/E28).
        // First of the branches: the sign-in message the caller resolved is not read at all here — a
        // sign-in wording may not stand in for a subject, whatever its own state.
        if (context is SubjectConfirmationPromptContext subject)
        {
            return subject.PromptText;
        }

        // The locale always travels with the context — it has no second carrier (SPEC-017 §7.2).
        var locale = context.RecipientLocale;

        // No initiator details — the behavior of the former null context (ICC-042); the default text
        // is the same kind of Natural Key and is resolved into the recipient's language (ICC-050).
        // An unknown future variant of the context is treated the same way, deliberately: a consumer
        // must degrade to "no details", never throw.
        if (context is not DetailedConfirmationPromptContext details)
        {
            return ResolveText(defaultText, locale, localizer);
        }

        // Take the initiator-context slot values from the one place that maps a context onto slots and
        // delegate substitution/variant selection to the message renderer (single source of the matcher,
        // TPL-110/TPL-093). The degradation full → without region → time of the request only →
        // application only is reproduced by variant selection (TPL-031): {app} is always present, while
        // requested_at/browser/os/region are supplied only when present — the "application + region"
        // case is impossible because the without-region variant requires browser and os, so a missing
        // browser/os drops region with them, and a missing time drops every step that names it.
        var values = TransactionSlotValues.Of(details);

        var text = MessageTextRenderer.Render(
            prompt, values, locale, context.RecipientTimeZone, localizer, MessageRenderMode.PlainText,
            logger);

        // No step of the ladder states the plain-text edition this surface delivers — a deployment that
        // rewrote the prompt as an HTML-only ladder. The prompt degrades to the text of the channel, the
        // rung the ladder of the sign-in prompt ends with (SPEC-036 §5.3), and not to an empty message.
        text ??= ResolveText(defaultText, locale, localizer);

        // Anomaly note — on a separate line, visually highlighted (ICC-053)
        if (details.AnomalyFlag)
        {
            var anomalyText = ResolveAnomalyText(details.AnomalyReasonKey, locale, localizer);
            text = text + Environment.NewLine + Environment.NewLine + anomalyText;
        }

        return text;
    }

    /// <summary>
    /// Resolves a Natural Key into the recipient's language; when the localizer,
    /// locale, or translation is unavailable, returns the key's base text.
    /// </summary>
    /// <param name="naturalKey">Natural Key (English base text — the base language after the TASK-057 flip).</param>
    /// <param name="recipientLocale">Recipient locale (null — base language).</param>
    /// <param name="localizer">Localizer (null — base language).</param>
    /// <returns>Text in the recipient's language or the base text.</returns>
    private static string ResolveText(
        string naturalKey,
        string? recipientLocale,
        IConfirmationPromptLocalizer? localizer)
    {
        return localizer?.ResolveOrBaseText(naturalKey, recipientLocale)
            ?? NaturalKeyContext.BaseTextOf(naturalKey);
    }

    /// <summary>
    /// Builds the anomaly note text with validation of the reason key (ICC-053, core-rules §10).
    /// AnomalyReasonKey comes from the host implementation of the anomaly detector — before insertion
    /// the key must be registered in the locale files (ICC-050) and must not contain
    /// control characters; otherwise the generic anomaly note is used.
    /// </summary>
    /// <param name="reasonKey">Natural Key of the anomaly reason (null — generic note).</param>
    /// <param name="recipientLocale">Recipient locale.</param>
    /// <param name="localizer">Localizer (null — validation against the key registry is impossible, the generic note is used).</param>
    /// <returns>Localized anomaly note text.</returns>
    private static string ResolveAnomalyText(
        string? reasonKey,
        string? recipientLocale,
        IConfirmationPromptLocalizer? localizer)
    {
        var isKnownSafeKey = reasonKey is not null
            && !ContainsControlCharacters(reasonKey)
            && localizer is not null
            && localizer.IsKnownKey(reasonKey);

        var key = isKnownSafeKey ? reasonKey! : MessageTemplateNaturalKeys.PromptAnomalyGeneric;

        return ResolveText(key, recipientLocale, localizer);
    }

    /// <summary>
    /// Checks whether the value contains control characters.
    /// </summary>
    /// <param name="value">Value to check.</param>
    /// <returns>true — at least one control character found.</returns>
    private static bool ContainsControlCharacters(string value)
    {
        foreach (var symbol in value)
        {
            if (char.IsControl(symbol))
            {
                return true;
            }
        }

        return false;
    }
}
