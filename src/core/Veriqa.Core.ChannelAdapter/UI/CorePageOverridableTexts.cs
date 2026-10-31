// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The texts of the core pages an entry of <see cref="CorePageTextConfigKeys.Texts"/> may override — the
/// union of what the owners of the pages contributed
/// (<see cref="CorePageTextServiceCollectionExtensions.AddCorePageOverridableTexts"/>). A text outside it
/// is never resolved, and an entry for one is reported by the snapshot report.
/// </summary>
internal sealed class CorePageOverridableTexts
{
    /// <summary>
    /// The texts as their owners spell them — the spelling the pages look an override up by.
    /// </summary>
    private readonly IReadOnlyList<string> _keys;

    /// <summary>
    /// The same texts compared the way the levels read the names of their members.
    /// </summary>
    private readonly HashSet<string> _lookup;

    /// <summary>
    /// Creates the union of the contributions.
    /// </summary>
    /// <param name="contributions">Contributions of the owners of the pages.</param>
    public CorePageOverridableTexts(IEnumerable<CorePageOverridableTextContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        _keys = [.. contributions.SelectMany(contribution => contribution.NaturalKeys).Distinct(StringComparer.Ordinal)];
        _lookup = new HashSet<string>(_keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The overridable texts, each once.
    /// </summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>
    /// Tells whether an entry keyed by <paramref name="text"/> addresses an overridable text. The key of an
    /// entry is compared ignoring case, the way the levels read the names of their members.
    /// </summary>
    /// <param name="text">Key of an entry as a level states it.</param>
    /// <returns><c>true</c> when the text is overridable.</returns>
    public bool Contains(string text) => _lookup.Contains(text);
}

/// <summary>
/// The overridable texts ONE owner of core pages contributed.
/// </summary>
/// <param name="NaturalKeys">Built-in Natural Keys of the owner's page elements.</param>
internal sealed record CorePageOverridableTextContribution(IReadOnlyList<string> NaturalKeys);
