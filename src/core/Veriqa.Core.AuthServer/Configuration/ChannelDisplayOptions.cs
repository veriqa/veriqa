// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Display settings for authentication channels (SPEC-012 §6.2).
/// Configuration section: Veriqa:ChannelDisplay.
/// <para>
/// The MODE of the display is not a member here: it is a setting owned by a level
/// (<c>ChannelDisplay.Mode</c>) and is read by path, so the section is no longer bound into an object
/// for it. Binding it as an input member made the binder of the platform decide the fate of a value
/// the key's own policy is supposed to decide — an unknown spelling stopped the host before the
/// resolution ever saw it (SPEC-012 CFG-240).
/// </para>
/// </summary>
public sealed class ChannelDisplayOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:ChannelDisplay";

    /// <summary>
    /// Order of channels in the UI (by identifiers).
    /// An empty list means the default order.
    /// </summary>
    public IReadOnlyList<string> ChannelOrder { get; set; } = [];

    /// <summary>
    /// Hint shown inside a channel's own panel on the sign-in page, keyed by channel type
    /// (<c>telegram</c>, <c>whatsapp</c>, a third-party SPI channel type). A channel absent from the
    /// map — or one whose text is blank — gets no hint, and the page markup does not change at all.
    /// Keys are matched case-insensitively, like every other channel-keyed map on this surface.
    /// <para>
    /// It exists because some flows need a word from the channel itself rather than from the page:
    /// WhatsApp, for one, drops the user into the messenger with a prefilled message that they still
    /// have to send. The page-wide instruction cannot say that — it is one per page and does not know
    /// which channel the user picked.
    /// </para>
    /// <para>
    /// <localizable/> The value is operator text and supports localization; the mechanism that serves
    /// it per language is delivered by a separate task, so today the configured text is shown as is.
    /// </para>
    /// <para>
    /// The text may carry inline markup: the tags <c>span</c>, <c>strong</c>, <c>em</c>, <c>a</c> and
    /// <c>br</c>, under the rules the sign-in page applies to the title and the lead instruction. A
    /// value of any other shape — a block tag, an attribute or a class outside the admitted ones, an
    /// unclosed tag — is shown whole as plain text and reported in the log.
    /// </para>
    /// </summary>
    public IDictionary<string, string> Hints { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
