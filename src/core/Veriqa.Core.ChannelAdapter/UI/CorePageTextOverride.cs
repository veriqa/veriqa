// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// What ONE level states for ONE text of a core page under <see cref="CorePageTextConfigKeys.Texts"/> —
/// in one of two forms, exactly one of which is set:
/// <list type="bullet">
/// <item>a replacement KEY (<see cref="ReplacementKey"/>): it goes through the locales of the host the way
/// the text it replaces does, so a stated text substitutes the key and never bypasses the localization;</item>
/// <item>ready texts by language (<see cref="ByLanguage"/>): they bypass the locales, and a language the
/// map does not state leaves the page with the regular translation of the original text.</item>
/// </list>
/// </summary>
public sealed class CorePageTextOverride
{
    /// <summary>
    /// Creates a value of one of the two forms.
    /// </summary>
    /// <param name="replacementKey">Replacement key; null for the form by language.</param>
    /// <param name="byLanguage">Texts by normalized language tag; null for the form of a key.</param>
    private CorePageTextOverride(string? replacementKey, IReadOnlyDictionary<string, string>? byLanguage)
    {
        ReplacementKey = replacementKey;
        ByLanguage = byLanguage;
    }

    /// <summary>
    /// Replacement key of the form "text"; null for the form by language.
    /// </summary>
    public string? ReplacementKey { get; }

    /// <summary>
    /// Ready texts of the form by language, keyed by the language tag normalized the way the locales of
    /// the host name theirs (trimmed, <c>_</c> → <c>-</c>, lower case); null for the form "text".
    /// </summary>
    public IReadOnlyDictionary<string, string>? ByLanguage { get; }

    /// <summary>
    /// Reads the value a level states at an address: a text is the form "text", a section is the form by
    /// language. A blank text, a section of blank texts only and an empty section state nothing, so the
    /// level cedes to the one below it; a blank text of one language inside a section means that language
    /// is not stated.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address of the entry inside the record.</param>
    /// <returns>Layer value of the level.</returns>
    internal static LayerValue<CorePageTextOverride?> Read(ConfigNode node, string path)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.Has(path))
        {
            return LayerValue<CorePageTextOverride?>.None;
        }

        if (node.Children(path).Any())
        {
            return node.TryRead<Dictionary<string, string>>(path, out var stated)
                ? LayerOf(ByLanguageOf(stated))
                : LayerValue<CorePageTextOverride?>.None;
        }

        string? text;

        try
        {
            text = node.TryRead<string>(path, out var read) ? read : null;
        }
        // A member with no named children that is not a text either — the empty object, or a list, of a
        // JSON record — states no entry. The read of a TEXT fails on nothing else: every scalar converts
        // to one.
        catch (InvalidOperationException)
        {
            return LayerValue<CorePageTextOverride?>.None;
        }

        return string.IsNullOrWhiteSpace(text)
            ? LayerValue<CorePageTextOverride?>.None
            : LayerOf(new CorePageTextOverride(text, byLanguage: null));
    }

    /// <summary>
    /// The part of this value a page may apply in place of <paramref name="naturalKey"/>: the variants
    /// whose slots match the slots of the text they replace, its optional slots aside. A variant that does
    /// not match is dropped — the page then shows the original text the regular way.
    /// </summary>
    /// <param name="naturalKey">Text the value stands for.</param>
    /// <param name="optionalSlots">Optional slots of the text (<see cref="CorePageTextSlots"/>).</param>
    /// <returns>Applicable part of the value; null when no variant of it applies.</returns>
    internal CorePageTextOverride? ApplicableTo(string naturalKey, IReadOnlySet<string> optionalSlots)
    {
        if (ReplacementKey is { } replacement)
        {
            return CorePageTextSlots.HaveSameSlots(naturalKey, replacement, optionalSlots) ? this : null;
        }

        // Exactly one of the two forms is set, so a value without a replacement key is the map.
        var byLanguage = ByLanguage!;
        var applicable = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (language, text) in byLanguage)
        {
            if (CorePageTextSlots.HaveSameSlots(naturalKey, text, optionalSlots))
            {
                applicable[language] = text;
            }
        }

        if (applicable.Count == byLanguage.Count)
        {
            return this;
        }

        return applicable.Count == 0 ? null : new CorePageTextOverride(replacementKey: null, applicable);
    }

    /// <summary>
    /// The texts of this value whose slots do not match the slots of <paramref name="naturalKey"/>, its
    /// optional slots aside, each with the address it is stated at relative to the entry — the entry itself
    /// for the form "text", its language member for the form by language.
    /// </summary>
    /// <param name="naturalKey">Text the value stands for.</param>
    /// <param name="optionalSlots">Optional slots of the text (<see cref="CorePageTextSlots"/>).</param>
    /// <returns>Mismatching texts: language (null for the form "text") and text.</returns>
    internal IEnumerable<(string? Language, string Text)> SlotMismatchesWith(
        string naturalKey,
        IReadOnlySet<string> optionalSlots)
    {
        if (ReplacementKey is { } replacement)
        {
            if (!CorePageTextSlots.HaveSameSlots(naturalKey, replacement, optionalSlots))
            {
                yield return (null, replacement);
            }

            yield break;
        }

        foreach (var (language, text) in ByLanguage!)
        {
            if (!CorePageTextSlots.HaveSameSlots(naturalKey, text, optionalSlots))
            {
                yield return (language, text);
            }
        }
    }

    /// <summary>
    /// The texts of the form by language, keyed by the normalized tag; blank texts are left out, and of
    /// two tags normalizing to the same one the first stated is kept.
    /// </summary>
    /// <param name="stated">Map as the level states it.</param>
    /// <returns>Value of the form by language; null when no language states a text.</returns>
    private static CorePageTextOverride? ByLanguageOf(Dictionary<string, string> stated)
    {
        var byLanguage = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (language, text) in stated)
        {
            if (ChannelLocaleTag.Normalize(language) is { } tag && !string.IsNullOrWhiteSpace(text))
            {
                byLanguage.TryAdd(tag, text);
            }
        }

        return byLanguage.Count == 0 ? null : new CorePageTextOverride(replacementKey: null, byLanguage);
    }

    /// <summary>
    /// Layer value of a read value: set when the level states one, unset when it states nothing.
    /// </summary>
    /// <param name="value">Value read; null — nothing stated.</param>
    /// <returns>Layer value of the level.</returns>
    private static LayerValue<CorePageTextOverride?> LayerOf(CorePageTextOverride? value) =>
        value is null ? LayerValue<CorePageTextOverride?>.None : LayerValue<CorePageTextOverride?>.Set(value);
}
