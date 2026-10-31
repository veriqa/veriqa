// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.Email.Templates;

/// <summary>
/// The language a mail of this adapter declares on its HTML document: the language its body is
/// <b>actually</b> rendered in, which is not necessarily the requested one. Every mail composer of the
/// adapter asks here, so two mails of one recipient cannot declare their language by two rules.
/// </summary>
/// <remarks>
/// The requested locale comes from Accept-Language and can be any tag; when no locale file
/// covers it (say "de"), every Natural Key degrades to the base language and the body is
/// English. Declaring <c>lang="de"</c> over English text makes screen readers and mail clients
/// mispronounce and mistranslate it (WCAG 3.1.1), so the declared tag is the one whose locale
/// file actually supplied the body: the requested locale is normalized and matched against the
/// set of loaded locales (<see cref="IConfirmationPromptLocalizer.AvailableLocales"/>) unioned
/// with the base locale, minus the pseudo-locale, following the same resolution order as
/// <c>LocaleFileConfirmationPromptLocalizer.Resolve</c>. A region tag such as "zh-TW" served by
/// "zh.json" is declared as "zh"; anything with no loaded file degrades to the base language and
/// is declared as such.
/// </remarks>
internal static class MailDocumentLanguage
{
    /// <summary>
    /// Fallback language tag of the HTML <c>lang</c> attribute when no recipient locale was detected
    /// (the base language of the Natural Keys, TASK-057).
    /// </summary>
    private const string BaseLanguageTag = ChannelLocalizationOptions.DefaultBaseLocale;

    /// <summary>
    /// Pseudo-locale tag (frontend/localization.md §6): a layout test artifact, not a valid BCP 47
    /// production language. It can appear in <see cref="IConfirmationPromptLocalizer.AvailableLocales"/>,
    /// but must never be declared as the document language (mirrors AuthPageLanguageRegistry.PseudoLocale).
    /// </summary>
    private const string PseudoLanguageTag = "pseudo";

    /// <summary>
    /// Resolves the language tag to declare on the HTML document of a mail.
    /// </summary>
    /// <param name="locale">Requested recipient locale (null — base language).</param>
    /// <param name="localizer">Localizer the body is rendered through.</param>
    /// <returns>The language tag of the rendered text.</returns>
    public static string Resolve(string? locale, IConfirmationPromptLocalizer localizer)
    {
        var normalizedLocale = ChannelLocaleTag.Normalize(locale);
        if (normalizedLocale is null)
        {
            return BaseLanguageTag;
        }

        var declarableTags = BuildDeclarableLanguageTags(localizer);

        // Repeat the resolution order of LocaleFileConfirmationPromptLocalizer.Resolve so the
        // declared lang tracks the file that actually rendered the body: full normalized tag first,
        // then the primary subtag ("zh-tw" → "zh"). Declare the first tag whose file is loaded.
        // fallback order MUST match LocaleFileConfirmationPromptLocalizer.Resolve; keep in sync.
        if (declarableTags.Contains(normalizedLocale))
        {
            return normalizedLocale;
        }

        var separatorIndex = normalizedLocale.IndexOf('-');
        if (separatorIndex > 0)
        {
            var primarySubtag = normalizedLocale[..separatorIndex];
            if (declarableTags.Contains(primarySubtag))
            {
                return primarySubtag;
            }
        }

        return BaseLanguageTag;
    }

    /// <summary>
    /// Builds the set of tags that may be declared as the document language:
    /// <c>(AvailableLocales ∪ { base }) \ { pseudo }</c>. By the interface contract the base locale
    /// may be absent from <see cref="IConfirmationPromptLocalizer.AvailableLocales"/>, so it is unioned
    /// in defensively; this stays harmless when a base-locale file (en.json) is loaded and the tag is
    /// already present, because the union deduplicates. The pseudo-locale is a layout test artifact and
    /// must never be declared. Mirrors AuthPageLanguageRegistry (a different assembly — the logic is
    /// reused, not the type).
    /// </summary>
    /// <param name="localizer">Localizer the body is rendered through.</param>
    /// <returns>Normalized language tags allowed as the declared document language.</returns>
    private static IReadOnlySet<string> BuildDeclarableLanguageTags(IConfirmationPromptLocalizer localizer)
    {
        var tags = new HashSet<string>(localizer.AvailableLocales, StringComparer.Ordinal)
        {
            BaseLanguageTag,
        };

        tags.Remove(PseudoLanguageTag);

        return tags;
    }
}
