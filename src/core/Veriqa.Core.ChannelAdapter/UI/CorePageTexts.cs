// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Overrides of the texts of a core page, resolved ONCE for one request
/// (<see cref="CorePageTextResolver"/>) and applied synchronously at every point where the page takes a
/// built-in text of its own. Every entry already carries only the variants that apply: a variant whose
/// slots differ from the slots of the text it replaces was dropped at resolution.
/// </summary>
public sealed class CorePageTexts
{
    /// <summary>
    /// Applicable overrides by the text they replace, spelled as the page's own constant spells it.
    /// </summary>
    private readonly IReadOnlyDictionary<string, CorePageTextOverride> _overrides;

    /// <summary>
    /// Creates the overrides of one request.
    /// </summary>
    /// <param name="overrides">Applicable overrides by the text they replace.</param>
    internal CorePageTexts(IReadOnlyDictionary<string, CorePageTextOverride> overrides)
    {
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
    }

    /// <summary>
    /// No override at all: every text is localized exactly as it would be without this mechanism.
    /// </summary>
    public static CorePageTexts None { get; } = new(new Dictionary<string, CorePageTextOverride>(StringComparer.Ordinal));

    /// <summary>
    /// Text of a page element in the language of the page's content.
    /// <list type="bullet">
    /// <item>No override of the text — the text localized the regular way.</item>
    /// <item>A replacement key — the replacement localized the regular way, in place of the text.</item>
    /// <item>Texts by language stating the language of the content — that text, as it is.</item>
    /// <item>Texts by language not stating it — the text localized the regular way.</item>
    /// </list>
    /// </summary>
    /// <param name="localizer">Localizer of the host's locale files.</param>
    /// <param name="naturalKey">Built-in Natural Key of the element.</param>
    /// <param name="language">
    /// Language of the page's content — the one the page states in <c>&lt;html lang&gt;</c>, not the raw
    /// tag of the request: a map by language is matched against it exactly.
    /// </param>
    /// <returns>Text to render.</returns>
    public string Localize(IConfirmationPromptLocalizer localizer, string naturalKey, string language)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(naturalKey);

        return TryOverride(localizer, naturalKey, language, out var stated)
            ? stated
            : localizer.ResolveOrBaseText(naturalKey, language);
    }

    /// <summary>
    /// Tells whether an override of <paramref name="naturalKey"/> applies in the language of the page's
    /// content, and gives the text it puts in place of the key: the replacement key localized the regular
    /// way, or the text the map by language states for that language. A key without an override, and a map
    /// by language not stating the language, give no text — the caller then takes the next source of the
    /// text it builds, which is what lets a page put a text of its own between an override and a
    /// translation.
    /// </summary>
    /// <param name="localizer">Localizer of the host's locale files.</param>
    /// <param name="naturalKey">Built-in Natural Key of the element.</param>
    /// <param name="language">Language of the page's content, as for <see cref="Localize"/>.</param>
    /// <param name="text">Text of the override; null when none applies.</param>
    /// <returns><c>true</c> when an override applies.</returns>
    internal bool TryOverride(
        IConfirmationPromptLocalizer localizer,
        string naturalKey,
        string language,
        [NotNullWhen(true)] out string? text)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(naturalKey);

        text = null;

        if (!_overrides.TryGetValue(naturalKey, out var stated))
        {
            return false;
        }

        if (stated.ReplacementKey is { } replacement)
        {
            text = localizer.ResolveOrBaseText(replacement, language);

            return true;
        }

        if (ChannelLocaleTag.Normalize(language) is { } tag
            && stated.ByLanguage!.TryGetValue(tag, out var byLanguage))
        {
            text = byLanguage;

            return true;
        }

        return false;
    }
}
