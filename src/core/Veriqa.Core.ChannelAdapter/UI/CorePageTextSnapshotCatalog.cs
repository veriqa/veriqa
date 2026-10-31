// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Snapshot catalog of ONE level of <see cref="CorePageTextConfigKeys.Texts"/>: it reports, once per
/// snapshot of the level's source, the entries a page never applies — an entry keyed by a text no owner
/// of the pages declared overridable, and a replacement whose slots differ from the slots of the text it
/// replaces (SPEC-012 CFG-240). The resolution drops both in silence, per render; this is where the
/// operator hears about them.
/// <para>
/// It is the owner's own catalog rather than a domain of the key: a domain judges a value alone, and
/// both rules here need the text the entry is keyed by — the value of the identity dimension — and the
/// second one needs the registry of overridable texts as well. The records of the level are enumerated
/// by the walker of that level; a level without one in the container is not walked, and the report names
/// it as such.
/// </para>
/// </summary>
internal sealed class CorePageTextSnapshotCatalog : IConfigSnapshotCatalog
{
    /// <summary>
    /// Section of the dictionary inside a record of the level.
    /// </summary>
    private readonly string _section;

    /// <summary>
    /// Walker of the level's records; null when the deployment holds none.
    /// </summary>
    private readonly IConfigNodeWalker? _walker;

    /// <summary>
    /// Texts the owners of the pages declared overridable.
    /// </summary>
    private readonly CorePageOverridableTexts _overridable;

    /// <summary>
    /// Creates the catalog of one level.
    /// </summary>
    /// <param name="level">Level of the key.</param>
    /// <param name="section">Section of the dictionary inside a record of the level.</param>
    /// <param name="walker">Walker of the level's records; null when the deployment holds none.</param>
    /// <param name="overridable">Texts the owners of the pages declared overridable.</param>
    public CorePageTextSnapshotCatalog(
        ConfigLevel level,
        string section,
        IConfigNodeWalker? walker,
        CorePageOverridableTexts overridable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);

        Level = level;
        _section = section;
        _walker = walker;
        _overridable = overridable ?? throw new ArgumentNullException(nameof(overridable));
    }

    /// <inheritdoc />
    public ConfigLevel Level { get; }

    /// <inheritdoc />
    public bool CanWalk => _walker is { CanWalk: true };

    /// <inheritdoc />
    public string SectionKey => _walker?.SectionKey ?? _section;

    /// <inheritdoc />
    public string? ReadThroughSectionKey => null;

    /// <inheritdoc />
    public IReadOnlyCollection<string> SettingKeys { get; } = [CorePageTextConfigKeys.KeyName];

    /// <inheritdoc />
    public IDisposable? Subscribe(Action onSnapshot)
    {
        ArgumentNullException.ThrowIfNull(onSnapshot);

        return _walker?.Subscribe(onSnapshot);
    }

    /// <inheritdoc />
    public IEnumerable<ConfigSnapshotFinding> Walk()
    {
        foreach (var node in _walker?.Walk() ?? [])
        {
            foreach (var text in node.Children(_section))
            {
                foreach (var finding in FindingsOf(node, text))
                {
                    yield return finding;
                }
            }
        }
    }

    /// <summary>
    /// Findings of ONE entry of a record.
    /// </summary>
    /// <param name="node">Node of the record.</param>
    /// <param name="text">Text the entry is keyed by, as the record states it.</param>
    /// <returns>Findings of the entry; none when a page applies it whole.</returns>
    private IEnumerable<ConfigSnapshotFinding> FindingsOf(ConfigNode node, string text)
    {
        var address = _section + ConfigNode.PathSeparator + text;

        if (!_overridable.Contains(text))
        {
            yield return Rejected(
                node,
                address,
                $"an entry for the text '{text}'",
                "entries only for the texts the pages of the core declare overridable");

            yield break;
        }

        CorePageTextOverride? stated;
        string? unreadable = null;

        try
        {
            stated = CorePageTextOverride.Read(node, address).Value;
        }
        // The read of a level fails with the exceptions of whichever dialect the record is read in — the
        // configuration binder or the JSON reader — so the guard does not depend on which one it was.
        // Cancellation is not a configuration failure and stays unhandled.
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            stated = null;
            unreadable = failure.Message;
        }

        if (unreadable is not null)
        {
            yield return new ConfigSnapshotFinding(
                ConfigSnapshotFact.ValueUnreadable,
                Level,
                KeyOf(node, address),
                node.RecordLabel,
                CorePageTextConfigKeys.KeyName,
                Value: null,
                unreadable);

            yield break;
        }

        if (stated is null)
        {
            yield break;
        }

        var slots = CorePageTextSlots.Describe(text);

        foreach (var (language, replacement) in stated.SlotMismatchesWith(text))
        {
            yield return Rejected(
                node,
                language is null ? address : address + ConfigNode.PathSeparator + language,
                $"'{replacement}'",
                $"a replacement carrying the slots of the text it replaces ({slots})");
        }
    }

    /// <summary>
    /// Finding of an entry or a variant a page does not apply.
    /// </summary>
    /// <param name="node">Node of the record.</param>
    /// <param name="address">Address of the entry or variant inside the record.</param>
    /// <param name="value">What the level states there, as the report prints it.</param>
    /// <param name="boundary">What the setting admits instead.</param>
    /// <returns>Finding of the report.</returns>
    private ConfigSnapshotFinding Rejected(ConfigNode node, string address, string value, string boundary) =>
        new(
            ConfigSnapshotFact.ValueRejected,
            Level,
            KeyOf(node, address),
            node.RecordLabel,
            CorePageTextConfigKeys.KeyName,
            value,
            boundary);

    /// <summary>
    /// Configuration key of an address inside a record, as the operator finds it in the configuration.
    /// </summary>
    /// <param name="node">Node of the record.</param>
    /// <param name="address">Address inside the record.</param>
    /// <returns>Configuration key of the address.</returns>
    private static string KeyOf(ConfigNode node, string address) =>
        node.ConfigurationKeyOf(address) ?? address;
}
