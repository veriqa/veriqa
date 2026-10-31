// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.RegularExpressions;

using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Slots of a text of a core page — the places a page substitutes a value into. The texts of the core
/// pages carry them in two forms: the named slot of the message templates (<c>{address}</c>) and the
/// positional item of a composite format (<c>{0}</c>), which the page fills with
/// <c>string.Format</c>. A replacement of a text has to carry exactly the slots
/// of the text it replaces: a slot it drops loses a value the page supplies, and a slot it adds either
/// stays on the page as a literal or — a positional one — throws out of the formatting.
/// </summary>
internal static partial class CorePageTextSlots
{
    /// <summary>
    /// Text naming a set without slots in a message.
    /// </summary>
    private const string NoSlots = "no slots";

    /// <summary>
    /// Separator of the slots a message lists.
    /// </summary>
    private const string ListSeparator = ", ";

    /// <summary>
    /// Positional item of a composite format: the index, optionally followed by an alignment or a format
    /// string, which are part of the item and not of its identity.
    /// </summary>
    /// <returns>Pattern of a positional item.</returns>
    [GeneratedRegex("\\{([0-9]+)(?:[,:][^{}]*)?\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PositionalItemPattern();

    /// <summary>
    /// Tells whether a replacement carries exactly the slots of the text it replaces.
    /// </summary>
    /// <param name="naturalKey">Text the replacement stands for.</param>
    /// <param name="replacement">Replacement text.</param>
    /// <returns><c>true</c> when both carry the same set of slots.</returns>
    public static bool HaveSameSlots(string naturalKey, string replacement) =>
        Of(naturalKey).SetEquals(Of(replacement));

    /// <summary>
    /// The slots of a text as a message names them — what a replacement of it has to carry.
    /// </summary>
    /// <param name="naturalKey">Text of a core page.</param>
    /// <returns>Slots of the text, or a phrase saying it has none.</returns>
    public static string Describe(string naturalKey)
    {
        var slots = Of(naturalKey);

        return slots.Count == 0
            ? NoSlots
            : string.Join(ListSeparator, slots.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Slots of a text, each in its written form (<c>{address}</c>, <c>{0}</c>), so the named and the
    /// positional forms never collide.
    /// </summary>
    /// <param name="text">Text to scan.</param>
    /// <returns>Slots of the text.</returns>
    private static HashSet<string> Of(string text)
    {
        var slots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in SlotTokenScanner.ExtractSlotNames(text))
        {
            slots.Add("{" + name + "}");
        }

        foreach (Match match in PositionalItemPattern().Matches(text))
        {
            slots.Add("{" + match.Groups[1].Value + "}");
        }

        return slots;
    }
}
