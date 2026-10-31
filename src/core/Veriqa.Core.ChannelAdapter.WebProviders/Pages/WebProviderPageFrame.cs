// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Pages;

/// <summary>
/// What every page of a web provider channel is rendered within, whatever it shows: the channel it
/// and the brand it is headed with, where it lives, its language, its texts and its branding.
/// </summary>
/// <param name="ChannelType">Channel type — the routes of the page live under it.</param>
/// <param name="ChannelName">Name of the channel in the head of the page, as on the sign-in button.</param>
/// <param name="BrandName">Brand name the levels of the transaction state, as the brand row of the sign-in
/// window shows it, already localized; <c>null</c> — none is stated, and the page is headed with the
/// channel alone.</param>
/// <param name="PathBase">Path base the application is hosted under (empty — the site root).</param>
/// <param name="Language">Language the texts of the page are rendered in, as its <c>lang</c> states it.</param>
/// <param name="Localizer">Locale-file localizer.</param>
/// <param name="Texts">Overrides of the texts of the page, resolved over its transaction.</param>
/// <param name="Branding">Effective branding of the page, resolved over its transaction.</param>
/// <param name="CspNonce">CSP nonce of the request; without one the script goes out without the
/// attribute, as every inline script of the contour does.</param>
internal sealed record WebProviderPageFrame(
    string ChannelType,
    string ChannelName,
    string? BrandName,
    string PathBase,
    string Language,
    IConfirmationPromptLocalizer Localizer,
    CorePageTexts Texts,
    CorePageBranding? Branding,
    string? CspNonce)
{
    /// <summary>
    /// A text of the page in its language, with the override an owning level states.
    /// </summary>
    /// <param name="naturalKey">Natural Key of the text.</param>
    /// <returns>The text, not encoded.</returns>
    public string Localize(string naturalKey) => Texts.Localize(Localizer, naturalKey, Language);

    /// <summary>
    /// Address of a route of the channel as the page links it, path base included.
    /// </summary>
    /// <param name="segment">Segment of the route.</param>
    /// <returns>The address.</returns>
    public string RouteUrl(string segment) =>
        PathBase + Constants.WebProviderRoutes.RoutePath(ChannelType, segment);
}
