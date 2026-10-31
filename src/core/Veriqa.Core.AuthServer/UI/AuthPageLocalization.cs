// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Shared Natural Key resolution for the auth-contour pages (the sign-in window and the web
/// confirmation page). Single resolution rule for every language, the base one included: the text
/// is resolved data-driven from the host locale files through
/// <see cref="IConfirmationPromptLocalizer"/>, and the key's own English base text is the fallback
/// when no file carries the key. No translations live in code.
/// </summary>
internal static class AuthPageLocalization
{
    /// <summary>
    /// Resolves a Natural Key into the target language, degrading to the English base text of the
    /// key when the locale files carry no translation for it. English goes through the same lookup
    /// as any other language — <c>en.json</c> is an ordinary translation file whose entries merely
    /// happen to equal their keys, so rewording an entry there rewords the page. A key with a
    /// context suffix (<see cref="NaturalKeyContext"/>) degrades to its base text, never to the raw
    /// label, on a miss in any language.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="naturalKey">Natural Key (English base text).</param>
    /// <param name="language">Language code.</param>
    /// <returns>Localized text.</returns>
    public static string Localize(IConfirmationPromptLocalizer localizer, string naturalKey, string language)
    {
        return localizer.ResolveOrBaseText(naturalKey, language);
    }

    /// <summary>
    /// Resolves the language of a page that belongs to a TRANSACTION: the page speaks the language of
    /// the request that created the transaction, and falls back to the language of the current request
    /// when the transaction states none.
    /// </summary>
    /// <remarks>
    /// The single home of that rule for the whole contour — the sign-in callback, the interaction page
    /// and the confirmation pages all ask here. The tag is stated by whoever created the transaction —
    /// the sign-in window or the calling API — and may name a language the deployment does not carry, so
    /// it is re-clamped to the supported-language registry here rather than used as it is: the page can
    /// only be rendered in a language the deployment carries, and the set of those is configuration that
    /// may have changed since the transaction was created.
    /// </remarks>
    /// <param name="transaction">Transaction the page belongs to.</param>
    /// <param name="requestLanguage">Language detected for the current request (the fallback).</param>
    /// <param name="defaultLanguage">Configured default language.</param>
    /// <param name="supportedLanguages">Registry of supported languages.</param>
    /// <returns>The page language code.</returns>
    public static string ResolveTransactionLanguage(
        Transaction transaction,
        string requestLanguage,
        string defaultLanguage,
        IReadOnlySet<string> supportedLanguages)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return ResolveTransactionLanguage(
            transaction.GetUiLocale(),
            requestLanguage,
            defaultLanguage,
            supportedLanguages);
    }

    /// <summary>
    /// The same rule stated over the transaction's locale TAG, for a page that belongs to a transaction
    /// it does not hold — the <c>form_post</c> auto-posting page, whose handler is not handed the
    /// transaction and receives the tag through the HTTP context of its own request (SPEC-007 UI-102). The
    /// overload taking the transaction delegates here, so the rule stays in one place.
    /// </summary>
    /// <param name="uiLocale">Locale tag stated by the transaction, or <see langword="null"/>/empty when none.</param>
    /// <param name="requestLanguage">Language detected for the current request (the fallback).</param>
    /// <param name="defaultLanguage">Configured default language.</param>
    /// <param name="supportedLanguages">Registry of supported languages.</param>
    /// <returns>The page language code.</returns>
    public static string ResolveTransactionLanguage(
        string? uiLocale,
        string requestLanguage,
        string defaultLanguage,
        IReadOnlySet<string> supportedLanguages)
    {
        return string.IsNullOrWhiteSpace(uiLocale)
            ? requestLanguage
            : AuthPageStrings.DetectLanguage(uiLocale, defaultLanguage, supportedLanguages);
    }

    /// <summary>
    /// Resolves the language the page content will actually be rendered in, so that
    /// <c>html lang</c> matches the text (WCAG 3.1.1). English needs no probe — whether its strings
    /// come from <c>en.json</c> or from the keys themselves, the page is English either way; any
    /// other requested language is honoured only when the localizer actually resolves the
    /// page texts for it — probed on a representative key, since a page's keys are added to the
    /// locale files as one set (and the CI parity gate keeps them so). On a miss the page falls
    /// back to the English base text, so "en" is returned.
    /// <para>
    /// The caller MUST resolve this once per render and use the result both for <c>html lang</c>
    /// and for EVERY <see cref="Localize"/> call. Localizing individual strings with the
    /// requested language instead would produce a mixed page — some strings translated, the rest
    /// English base, under a lang attribute that matches neither.
    /// </para>
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="representativeKey">Any Natural Key of the page's key set.</param>
    /// <param name="language">Requested (detected) language code.</param>
    /// <returns>The language code of the content that will actually be rendered.</returns>
    public static string ResolveContentLanguage(
        IConfirmationPromptLocalizer localizer,
        string representativeKey,
        string language)
    {
        if (string.Equals(language, AuthPageStrings.LanguageEn, StringComparison.Ordinal))
        {
            return AuthPageStrings.LanguageEn;
        }

        return localizer.Resolve(representativeKey, language) is not null
            ? language
            : AuthPageStrings.LanguageEn;
    }
}
