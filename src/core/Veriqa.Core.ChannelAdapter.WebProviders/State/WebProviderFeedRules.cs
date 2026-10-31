// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.State;

/// <summary>
/// What of a message the page of the channel can draw — one rule for the adapter, which keeps only that
/// in the feed, and for the page, which draws it. A link is an absolute <c>http</c> or <c>https</c>
/// address: the page puts it into a link, where another scheme (<c>javascript:</c>) has no place. A
/// button carries exactly one of a link and the payload of an item of the menu, as the contract of
/// <see cref="ChannelActionButton"/> states, and a caption.
/// </summary>
internal static class WebProviderFeedRules
{
    /// <summary>
    /// Tells whether the page can draw a button.
    /// </summary>
    /// <param name="button">The button.</param>
    /// <returns><c>true</c> — the page draws it.</returns>
    public static bool IsDrawable(ChannelActionButton button)
    {
        ArgumentNullException.ThrowIfNull(button);

        if (string.IsNullOrWhiteSpace(button.Label) || IsLink(button) == IsMenuItem(button))
        {
            return false;
        }

        return !IsLink(button) || IsWebAddress(button.Url);
    }

    /// <summary>
    /// Tells whether a button is a link rather than an item of the menu.
    /// </summary>
    /// <param name="button">The button.</param>
    /// <returns><c>true</c> — the button carries an address.</returns>
    public static bool IsLink(ChannelActionButton button)
    {
        ArgumentNullException.ThrowIfNull(button);

        return !string.IsNullOrEmpty(button.Url);
    }

    /// <summary>
    /// Tells whether the page can draw the link to the claim form.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <returns><c>true</c> — the page draws it.</returns>
    public static bool IsDrawable(ClaimFormLinkContent link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return IsWebAddress(link.Url);
    }

    /// <summary>
    /// Tells whether a button is an item of the menu.
    /// </summary>
    /// <param name="button">The button.</param>
    /// <returns><c>true</c> — the button carries a payload.</returns>
    private static bool IsMenuItem(ChannelActionButton button) => !string.IsNullOrEmpty(button.CallbackData);

    /// <summary>
    /// Tells whether an address is an absolute <c>http</c> or <c>https</c> one.
    /// </summary>
    /// <param name="address">The address; <c>null</c> — none.</param>
    /// <returns><c>true</c> — the page can put it into a link.</returns>
    private static bool IsWebAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);
}
