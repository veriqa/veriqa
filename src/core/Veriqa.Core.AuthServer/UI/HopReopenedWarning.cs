// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The warning of a hop link opened by a person once more, from another browser (SPEC-041 L93), as both of
/// its surfaces show it: a label of the StatusBadge primitive (SPEC-015 §4.4) carrying the text of
/// <see cref="AuthPageStrings.HopReopened"/>. The form of the claims completion receives the label ready
/// from <see cref="Markup"/>; the sign-in page builds the same label in its script, on the channel status
/// that reports the reopening, out of <see cref="BadgeClass"/> and the localized text.
/// </summary>
internal static class HopReopenedWarning
{
    /// <summary>
    /// Class of the label — the StatusBadge primitive on the warning tokens; the stylesheet of each surface
    /// carries the rule of it.
    /// </summary>
    public const string BadgeClass = "veriqa-badge";

    /// <summary>
    /// Markup of the warning for a page that shows it from the first paint: the label with its text
    /// encoded. Not a live region — a text present when the page loads is not announced as a change
    /// (SPEC-015 §6.3).
    /// </summary>
    /// <param name="text">Localized text of the warning.</param>
    /// <returns>Markup of the label.</returns>
    public static string Markup(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return $"<div class=\"{BadgeClass}\">{WebUtility.HtmlEncode(text)}</div>";
    }
}
