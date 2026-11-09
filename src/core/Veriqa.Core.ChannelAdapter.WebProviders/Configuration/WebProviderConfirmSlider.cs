// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// Where the page of a web provider channel takes "Yes" to its question by a slider — a handle drawn to
/// the end of its track — instead of a button, so a stray touch cannot answer it. "No" stays a button
/// in every mode. The slider is drawn by the script of the page: without it the page answers by its two
/// buttons whatever the mode.
/// </summary>
internal enum WebProviderConfirmSlider
{
    /// <summary>
    /// Never: the page answers by two buttons.
    /// </summary>
    Off = 0,

    /// <summary>
    /// On a browser whose main pointer is coarse — a touch screen; two buttons elsewhere.
    /// </summary>
    Mobile = 1,

    /// <summary>
    /// On every browser.
    /// </summary>
    Everywhere = 2
}
