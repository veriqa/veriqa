// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// The two slots that name the application on the pages a link of a letter opens — the request of a
/// sign-in and of a confirmation, and the question and the warning of the link page of the claims
/// completion: <c>{app}</c>, the name of the application, and <c>{brand_name}</c>, the brand the levels of
/// the transaction state, which the letters name it by.
/// <para>
/// The pages fill both in each of their texts, so a text — built-in or a replacement through
/// <c>AuthPage.Texts</c> — may name the application by either slot, by both or by none. That is why the two
/// are optional slots of these texts (<see cref="Texts"/>): a replacement is not dropped for naming the
/// application with the other slot, or for not naming it at all.
/// </para>
/// </summary>
internal static class EmailLinkPageNameSlots
{
    /// <summary>
    /// Slot of the name of the application.
    /// </summary>
    internal const string App = "{app}";

    /// <summary>
    /// Slot of the brand name — the slot of the same name of the letters.
    /// </summary>
    internal const string BrandName = "{" + SlotNames.BrandName + "}";

    /// <summary>
    /// Both slots, as the texts of these pages declare them optional.
    /// </summary>
    internal static IReadOnlyList<string> Slots { get; } = [App, BrandName];

    /// <summary>
    /// The texts of these pages that name the application — the texts both slots are optional in. The line
    /// of a confirmation request names it in none of its built-in wordings and is here all the same: a
    /// replacement of it may name the application by either slot.
    /// </summary>
    internal static IReadOnlyList<string> Texts { get; } =
    [
        EmailAdapterMailStrings.SignInRequestText,
        EmailAdapterMailStrings.ConfirmationRequestText,
        EmailAdapterMailStrings.ClaimLinkPageQuestion,
        EmailAdapterMailStrings.ClaimLinkPageWarning,
    ];

    /// <summary>
    /// Fills both slots of a text of these pages. The brand is a Natural Key, as in the letters, and goes in
    /// translated into the language of the page; when no level states one, <c>{brand_name}</c> takes the name
    /// of the application, so a text naming the application by the brand never shows an empty slot.
    /// </summary>
    /// <param name="text">Text of the page, already localized or replaced.</param>
    /// <param name="localizer">Localizer of the host's locale files.</param>
    /// <param name="language">Language of the page's content.</param>
    /// <param name="applicationName">Name of the application.</param>
    /// <param name="brandName">Brand name the levels of the transaction state; null — none is stated.</param>
    /// <returns>The text with both slots filled.</returns>
    internal static string Fill(
        string text,
        IConfirmationPromptLocalizer localizer,
        string language,
        string applicationName,
        string? brandName)
    {
        // The brand is translated only for a text that names it.
        if (!text.Contains(BrandName, StringComparison.Ordinal))
        {
            return text.Replace(App, applicationName, StringComparison.Ordinal);
        }

        var brand = string.IsNullOrWhiteSpace(brandName)
            ? applicationName
            : localizer.ResolveOrBaseText(brandName, language);

        return text
            .Replace(App, applicationName, StringComparison.Ordinal)
            .Replace(BrandName, brand, StringComparison.Ordinal);
    }
}
