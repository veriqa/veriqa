// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Optional display metadata of a channel in the sign-in window (public SPI, additive).
/// A third-party adapter implements it <i>in addition</i> to <see cref="IChannelAdapter"/> to give
/// its channel a human-readable button label and a brand glyph. Without it the channel still
/// renders: the button falls back to the raw <see cref="IChannelAdapter.ChannelType"/> and no icon.
/// </summary>
public interface IChannelDisplayMetadata
{
    /// <summary>
    /// Channel name the pages of the core show on its button and its tab and in the instruction. The value
    /// is the base text of a Natural Key, <c>"&lt;DisplayName&gt; | channel"</c>: a locale file of the host
    /// translates the name by that key, and an entry of <c>AuthPage.Texts</c> overrides it, as well as the
    /// phrases of the button that carry it (<c>Sign in with &lt;DisplayName&gt;</c>,
    /// <c>Confirm in &lt;DisplayName&gt;</c>); without either the name is shown as declared. A name carrying
    /// <c>:</c> is translated but not overridable, since no configuration level addresses such an entry.
    /// A blank value — or one longer than
    /// <see cref="Veriqa.Core.ChannelAdapter.Constants.CustomChannelContract.MaxDisplayNameLength"/>,
    /// which would break the sign-in window layout — degrades to the raw
    /// <see cref="IChannelAdapter.ChannelType"/>:
    /// the button is never rendered nameless.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Inner markup of the channel glyph inside a <c>24×24</c> viewBox (for example
    /// <c>&lt;path d="…"/&gt;</c>), or null for a channel without an icon.
    /// The value is embedded into the sign-in page, so it must be static markup owned by the
    /// adapter's author — never user input or content fetched at runtime. It is checked against an
    /// allowlist before rendering — plain shape elements only
    /// (<c>path</c>, <c>circle</c>, <c>ellipse</c>, <c>rect</c>, <c>line</c>, <c>polyline</c>,
    /// <c>polygon</c>, <c>g</c>) with double-quoted attributes, no <c>style</c> and no
    /// <c>on*</c> handlers — and it must not exceed
    /// <see cref="Veriqa.Core.ChannelAdapter.Constants.CustomChannelContract.MaxIconSvgPathLength"/>;
    /// markup outside it is dropped with a warning and the channel renders without an icon.
    /// </summary>
    string? IconSvgPath => null;
}
