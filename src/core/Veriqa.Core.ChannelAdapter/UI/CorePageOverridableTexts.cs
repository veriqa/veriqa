// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The texts of the core pages an entry of <see cref="CorePageTextConfigKeys.Texts"/> may override — the
/// union of what the owners of the pages contributed
/// (<see cref="CorePageTextServiceCollectionExtensions.AddCorePageOverridableTexts(Microsoft.Extensions.DependencyInjection.IServiceCollection, IEnumerable{string})"/>).
/// A text outside it is never resolved, and an entry for one is reported by the snapshot report. With the
/// texts it holds the optional slots their owners declared
/// (<see cref="CorePageTextServiceCollectionExtensions.AddCorePageOverridableTexts(Microsoft.Extensions.DependencyInjection.IServiceCollection, IEnumerable{string}, IEnumerable{string})"/>).
/// </summary>
internal sealed class CorePageOverridableTexts
{
    /// <summary>
    /// No optional slot: what a text gets when no owner declared one for it.
    /// </summary>
    private static readonly IReadOnlySet<string> NoOptionalSlots = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The texts as their owners spell them — the spelling the pages look an override up by.
    /// </summary>
    private readonly IReadOnlyList<string> _keys;

    /// <summary>
    /// The same texts compared the way the levels read the names of their members.
    /// </summary>
    private readonly HashSet<string> _lookup;

    /// <summary>
    /// Optional slots by the text they were declared for, compared as <see cref="_lookup"/> compares; a
    /// text no owner declared one for is absent.
    /// </summary>
    private readonly Dictionary<string, IReadOnlySet<string>> _optionalSlots;

    /// <summary>
    /// Creates the union of the contributions. The optional slots of a text are united as well: an owner
    /// declares a slot optional for the texts of its own page, and a Natural Key carrying the slot is that
    /// page's own text.
    /// </summary>
    /// <param name="contributions">Contributions of the owners of the pages.</param>
    public CorePageOverridableTexts(IEnumerable<CorePageOverridableTextContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        IReadOnlyList<CorePageOverridableTextContribution> contributed = [.. contributions];

        _keys = [.. contributed.SelectMany(contribution => contribution.NaturalKeys).Distinct(StringComparer.Ordinal)];
        _lookup = new HashSet<string>(_keys, StringComparer.OrdinalIgnoreCase);
        _optionalSlots = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var contribution in contributed.Where(contribution => contribution.OptionalSlots.Count > 0))
        {
            foreach (var naturalKey in contribution.NaturalKeys)
            {
                var slots = new HashSet<string>(contribution.OptionalSlots, StringComparer.Ordinal);

                if (_optionalSlots.TryGetValue(naturalKey, out var declared))
                {
                    slots.UnionWith(declared);
                }

                _optionalSlots[naturalKey] = slots;
            }
        }
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

    /// <summary>
    /// The optional slots of a text — those a replacement of it may carry or drop freely. The text is
    /// compared as <see cref="Contains"/> compares it.
    /// </summary>
    /// <param name="text">Text as a page or a level spells it.</param>
    /// <returns>Optional slots in their written form; empty when no owner declared one.</returns>
    public IReadOnlySet<string> OptionalSlotsOf(string text) =>
        _optionalSlots.GetValueOrDefault(text, NoOptionalSlots);
}

/// <summary>
/// The overridable texts ONE owner of core pages contributed.
/// </summary>
/// <param name="NaturalKeys">Built-in Natural Keys of the owner's page elements.</param>
internal sealed record CorePageOverridableTextContribution(IReadOnlyList<string> NaturalKeys)
{
    /// <summary>
    /// Slots, in their written form, the owner's page fills in every one of <see cref="NaturalKeys"/>
    /// whenever a text carries them — so a replacement of those texts may carry any of them, all or none.
    /// Empty: every slot of a text has to match.
    /// </summary>
    public IReadOnlyCollection<string> OptionalSlots { get; init; } = [];
}
