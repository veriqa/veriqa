// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;

namespace Veriqa.Core.Configuration;

/// <summary>
/// Reading of a setting that declares DIMENSIONS (SPEC-012 §10.6, CFG-236): the value a level states
/// FOR a value of a dimension lives in a paired map, and — for a chain whose last step addresses no
/// dimension — the plain field next to it is the "for everything else" value of the same level.
/// <para>
/// One place for the two operations any dimension needs — the reading of the map entry and the layer
/// value of a chain step — so the levels of such a key (the global section, the client entry, the
/// <c>ui_config</c> record) cannot answer "what is the value for this theme / for this channel"
/// differently, and no consumer grows a precedence chain of its own (SPEC-012 CFG-231/CFG-235). It
/// lives in the MECHANISM rather than at a consumer because it names no setting key, no options type
/// and no record type: it is told the names of the dimensions and handed the level's maps — or the
/// record of the level and the addresses its map and its plain member stand at — and that is all it
/// knows (SPEC-012 §10.6 — the criterion of what belongs to the module).
/// </para>
/// <para>
/// <b>The helper carries no step order of its own.</b> The order of the fallback steps is declared by
/// the key alone (<see cref="ConfigKeyDimensions.Fallback"/>), and the resolver calls a binding once
/// per step with an ALREADY PROJECTED point. The dimension names the members below are given are the
/// names the level's MAP is cut by — a property of the level's record shape — and never the order of
/// the steps: a second carrier of the step order would be a second authority with no rule for
/// resolving a disagreement between them. Which step is being served is answered by the point alone.
/// </para>
/// <para>
/// The members read the record ENTRY BY ENTRY rather than taking a map read whole: an entry of the map
/// that cannot be read costs the step of its own dimension value alone (SPEC-012 CFG-246), while a map
/// read whole would fail for every dimension value at once. The value type of the setting is the
/// caller's: the same reading serves a text, a value type and a list.
/// </para>
/// <para>
/// Whether a stated value is one the setting ADMITS is not asked here at all: that is the domain of
/// the key (<see cref="ConfigValueDomain{T}"/>), applied by the resolution mechanism to every binding.
/// </para>
/// </summary>
public static class DimensionedConfigValue
{
    /// <summary>
    /// Text value a level states for one value of the dimension, or null when it states none. The key
    /// is matched case-insensitively: the map comes from configuration written by hand, where the
    /// casing of a theme or a channel name is not the deployment's to get exactly right; a blank value
    /// counts as unstated.
    /// </summary>
    /// <param name="valuesByDimension">Values by dimension value stated by the level; null — none at all.</param>
    /// <param name="dimensionValue">Value of the dimension being asked about (a theme, a channel type).</param>
    /// <returns>Value stated for it, or null.</returns>
    public static string? TextFor(
        IEnumerable<KeyValuePair<string, string>>? valuesByDimension,
        string dimensionValue)
    {
        if (valuesByDimension is null || string.IsNullOrWhiteSpace(dimensionValue))
        {
            return null;
        }

        foreach (var (statedFor, value) in valuesByDimension)
        {
            if (string.Equals(statedFor, dimensionValue, StringComparison.OrdinalIgnoreCase) && IsStatedText(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Value a level states at the point of the chain for ONE dimension, read from the record of the
    /// level ENTRY BY ENTRY: the one entry of the level's map the point names is read, and no other
    /// entry of the map is touched. It is the reading every member below is built of, and it serves a
    /// key whose value fits none of them — a list, whose emptiness its owner answers.
    /// <para>
    /// That is what keeps an entry stating a value the type cannot take — a text that names no member
    /// of an enumeration, a subsection where a text belongs, an element of a list with a member the
    /// strict binding refuses — to the cost of that entry alone (SPEC-012 CFG-246): its read fails for
    /// the step of its own dimension value, the mechanism reports it and leaves that step unset
    /// (CFG-240), and the chain moves on. A map read whole fails on any one of its entries, so it would
    /// leave unset the step of every dimension value it was read for. The report of the snapshot is not
    /// made here: it belongs to the walk, which asks for each entry of such a map by its own address, so
    /// an entry it cannot read is named there as a value that cannot be read, without its text.
    /// </para>
    /// <para>
    /// The entry is addressed by the dimension value under the map's address; both sources of a record
    /// match segments case-insensitively (CFG-238). A blank dimension value, or one carrying the path
    /// separator, names no entry of the map. A point that does not address the dimension at all is the
    /// step that answers from the plain member, and nothing of the map is read for it.
    /// </para>
    /// <para>
    /// A map the level states with no entries to address is read WHOLE: a map written as a single value
    /// has no entry for the step to find, and reading it whole is what raises the failure of its shape
    /// for the mechanism to report instead of the step passing it over in silence. An empty map read
    /// whole has no entry whose failure could cost another.
    /// </para>
    /// </summary>
    /// <typeparam name="TValue">Type of what the level states per dimension value.</typeparam>
    /// <param name="node">Record of the level.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <param name="dimensionName">Name of the dimension the level's map is keyed by.</param>
    /// <param name="mapAddress">Address of the level's map inside the record.</param>
    /// <param name="value">What the level states for the dimension value carried by the point.</param>
    /// <returns><c>true</c> when the point addresses the dimension and the map states an entry for it.</returns>
    /// <exception cref="ArgumentException">A name or an address is null, empty or whitespace.</exception>
    /// <exception cref="Exception">
    /// The record states a value the type cannot take at the entry, or states the map in a shape a map
    /// is not read from (<see cref="ConfigNode.TryRead{T}"/>); the mechanism that called the step
    /// handles it.
    /// </exception>
    public static bool TryReadFor<TValue>(
        ConfigNode node,
        ConfigDimensionValues point,
        string dimensionName,
        string mapAddress,
        [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapAddress);

        value = default;

        if (!point.Addresses(dimensionName))
        {
            return false;
        }

        // Read for its failure alone: a map stated as a single value raises the failure of its shape.
        if (node.Has(mapAddress) && !node.Children(mapAddress).Any())
        {
            _ = node.TryRead<Dictionary<string, TValue>>(mapAddress, out _);
        }

        return EntryAddress(mapAddress, point.ValueOf(dimensionName)) is { } entry
            && node.TryRead(entry, out value);
    }

    /// <summary>
    /// Layer value of ONE step of the fallback chain for a TEXT setting cut by one dimension: the step
    /// that addresses the dimension answers from the entry of the level's map that names its value
    /// (<see cref="TryReadFor{TValue}"/>), the step that addresses none from the plain member. A blank
    /// text states nothing, so an emptied field does not win the key over the level below it. A step
    /// that states nothing yields <see cref="LayerValue{T}.None"/>, so the chain moves on — and when no
    /// step of the level states anything, the level is skipped and the resolution drops to the level
    /// below.
    /// <para>
    /// An entry that cannot be read costs the step of its own dimension value alone: that value takes
    /// the plain member of the same level or the level below, and the other entries stand (SPEC-012
    /// CFG-246, CFG-240). The plain member is read on EVERY step, the one addressing the dimension
    /// included, so a plain member that cannot be read leaves both steps of its level unset and the
    /// level cedes to the one below as a whole.
    /// </para>
    /// </summary>
    /// <param name="node">Record of the level.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <param name="dimensionName">Name of the dimension the level's map is keyed by.</param>
    /// <param name="mapAddress">Address of the level's map inside the record.</param>
    /// <param name="commonAddress">Address of the plain member inside the record.</param>
    /// <returns>Layer value of the step.</returns>
    /// <exception cref="ArgumentException">A name or an address is null, empty or whitespace.</exception>
    /// <exception cref="Exception">
    /// The record states a value that cannot be read as text at the entry or at the plain member read
    /// for the step, or states the map in a shape a map is not read from; the mechanism that called the
    /// step handles it.
    /// </exception>
    public static LayerValue<string?> Layer(
        ConfigNode node,
        ConfigDimensionValues point,
        string dimensionName,
        string mapAddress,
        string commonAddress) =>
        TryReadStep<string>(node, point, dimensionName, mapAddress, commonAddress, out var text)
        && IsStatedText(text)
            ? LayerValue<string?>.Set(text)
            : LayerValue<string?>.None;

    /// <summary>
    /// Layer value of ONE step of the fallback chain for a setting whose value is not text, cut by one
    /// dimension: the step that addresses the dimension answers from the entry of the level's map that
    /// names its value (<see cref="TryReadFor{TValue}"/>), the step that addresses none from the plain
    /// member. A value that is present is STATED — for a value type there is no "blank" shape a
    /// half-finished edit leaves behind; whether the setting ADMITS it belongs to the domain of the key
    /// (<see cref="ConfigValueDomain{T}"/>), which the resolution mechanism applies to every binding.
    /// <para>
    /// An entry that cannot be read costs the step of its own dimension value alone, and a plain member
    /// that cannot be read leaves both steps of its level unset, exactly as for a text setting (see the
    /// text overload).
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="node">Record of the level.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <param name="dimensionName">Name of the dimension the level's map is keyed by.</param>
    /// <param name="mapAddress">Address of the level's map inside the record.</param>
    /// <param name="commonAddress">Address of the plain member inside the record.</param>
    /// <returns>Layer value of the step.</returns>
    /// <exception cref="ArgumentException">A name or an address is null, empty or whitespace.</exception>
    /// <exception cref="Exception">
    /// The record states a value the type cannot take at the entry or at the plain member read for the
    /// step, or states the map in a shape a map is not read from; the mechanism that called the step
    /// handles it.
    /// </exception>
    public static LayerValue<T> Layer<T>(
        ConfigNode node,
        ConfigDimensionValues point,
        string dimensionName,
        string mapAddress,
        string commonAddress)
        where T : struct =>
        TryReadStep<T>(node, point, dimensionName, mapAddress, commonAddress, out var value)
            ? LayerValue<T>.Set(value)
            : LayerValue<T>.None;

    /// <summary>
    /// What one step of a chain cut by one dimension reads: the entry of the map for the step that
    /// addresses the dimension, the plain member for the step that addresses none. The plain member is
    /// read on the step of the dimension too, for its failure alone — a plain member that cannot be
    /// read takes both steps of its level with it.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="node">Record of the level.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <param name="dimensionName">Name of the dimension the level's map is keyed by.</param>
    /// <param name="mapAddress">Address of the level's map inside the record.</param>
    /// <param name="commonAddress">Address of the plain member inside the record.</param>
    /// <param name="value">Value the step reads.</param>
    /// <returns><c>true</c> when the record states a value for the step.</returns>
    private static bool TryReadStep<T>(
        ConfigNode node,
        ConfigDimensionValues point,
        string dimensionName,
        string mapAddress,
        string commonAddress,
        [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapAddress);

        if (!point.Addresses(dimensionName))
        {
            return node.TryRead(commonAddress, out value);
        }

        _ = node.TryRead<T>(commonAddress, out _);

        return TryReadFor(node, point, dimensionName, mapAddress, out value);
    }

    /// <summary>
    /// Address of the entry a level's map states for one dimension value. A value that is blank names
    /// no entry, and one carrying the path separator would address a member nested deeper than the
    /// map's own entries, so it names none either.
    /// </summary>
    /// <param name="mapAddress">Address of the level's map inside the record.</param>
    /// <param name="dimensionValue">Value of the dimension being asked about.</param>
    /// <returns>Address of the entry; null when the value names none.</returns>
    private static string? EntryAddress(string mapAddress, string dimensionValue) =>
        string.IsNullOrWhiteSpace(dimensionValue) || dimensionValue.Contains(ConfigNode.PathSeparator)
            ? null
            : mapAddress + ConfigNode.PathSeparator + dimensionValue;

    /// <summary>
    /// Tells whether a text value counts as stated: a blank text is the same as no value at all, so an
    /// emptied configuration field does not win the key over the level below it.
    /// </summary>
    /// <param name="value">Text value read from a level.</param>
    /// <returns><c>true</c> when the text carries something.</returns>
    private static bool IsStatedText(string? value) => !string.IsNullOrWhiteSpace(value);
}
