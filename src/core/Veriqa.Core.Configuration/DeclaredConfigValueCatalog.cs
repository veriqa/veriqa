// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Veriqa.Core.Configuration;

/// <summary>
/// Catalog of one setting at one level BUILT FROM THE DECLARATION of the key (SPEC-012 §10.6,
/// CFG-244). Everything such a catalog needs the declaration already states — the key, its domain, the
/// level and the address inside the record of that level — so a pair "setting + level" costs no class
/// of its own: the pairs are registered from the catalog of declarations, one per level the key names.
/// <para>
/// What the declaration does NOT state is where the records of a level are: that belongs to their
/// owner, and it is asked for through <see cref="IConfigNodeWalker"/>. The core level is the exception
/// the model already has — its record is the application configuration of the host, read here at the
/// absolute address the declaration names (self-hosted ≡ core, SPEC-012 §10.1).
/// </para>
/// <para>
/// The walk asks the level what it STATES, which is not the same question the resolution asks: a
/// resolution follows the fallback chain to ONE value, while every value the level holds — the plain
/// member and each entry of a map beside it — is stated and therefore walked. Where the address of the
/// declaration does not name them all, the key says so itself
/// (<see cref="DeclaredConfigKey{T}.Stated"/>).
/// </para>
/// <para>
/// This enumeration is the ONE seam of the walk where the declared answers of a key are applied, and
/// its counterpart on the resolution path is the single seam there
/// (<see cref="DeclaredConfigKey{T}.StatedAtStep"/>). The reading of one ADDRESS is the same call in both
/// (<see cref="DeclaredConfigKey{T}.StatedAt"/>); what each seam adds is what belongs to its own unit
/// of walking — the answer of a level whose record said nothing
/// (<see cref="DeclaredConfigKey{T}.Default"/>) and the answer carried by the presence of a record
/// (<see cref="DeclaredConfigKey{T}.PresenceMeans"/>).
/// </para>
/// </summary>
/// <typeparam name="T">Type of the setting value.</typeparam>
internal sealed class DeclaredConfigValueCatalog<T> : ConfigValueCatalog<T>, IDimensionBoundCatalog
{
    /// <summary>
    /// Declaration the catalog is built from.
    /// </summary>
    private readonly DeclaredConfigKey<T> _declared;

    /// <summary>
    /// Address of the level inside the record it belongs to (absolute, for the core level).
    /// </summary>
    private readonly ConfigAddressTemplate _template;

    /// <summary>
    /// Path of the step of the chain that narrows NOTHING — the step whose address every level has,
    /// while the narrowed ones exist only where the deployment stated them. Null when the address
    /// carries a substitution OUTSIDE its groups: that address exists for no step but the one that
    /// knows the dimension's value. The core level is then walked by the points of that dimension
    /// (<see cref="_corePoints"/>); a level above it has no address to walk its records at (see
    /// <see cref="CanWalk"/>).
    /// </summary>
    private readonly string? _unnarrowedPath;

    /// <summary>
    /// The dimension the core level is walked BY where its address has no step that narrows nothing:
    /// the section of the host configuration whose child keys are the values of that dimension the
    /// deployment states, and the dimension itself. The record of the core level is the configuration
    /// of the host, whose sections can be enumerated, so such an address is read at every point it
    /// exists at. Null above the core level, where the records of the level are another store's to
    /// enumerate, and for an address the points cannot be read off one section of
    /// (<see cref="ConfigAddressTemplate.EnumerableDimension"/>).
    /// </summary>
    private readonly (string Section, string Dimension)? _corePoints;

    /// <summary>
    /// Walker of the level's records; null at the core level, which has no records to enumerate.
    /// </summary>
    private readonly IConfigNodeWalker? _walker;

    /// <summary>
    /// Application configuration of the host — the record of the core level. It is the instance the
    /// composition root handed in rather than whatever the container resolves, for the same reason the
    /// registrar of the bindings takes it: a host that supplies a subsection is served from a different
    /// root.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// The level being walked is the core one — the level read here instead of through a walker.
    /// </summary>
    private readonly bool _isCore;

    /// <summary>
    /// Creates the catalog of one declared pair "setting + level".
    /// </summary>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="address">Address of the level being walked.</param>
    /// <param name="template">Parsed address of that level.</param>
    /// <param name="walker">Walker of the level's records; null at the core level.</param>
    /// <param name="configuration">Application configuration of the host.</param>
    private DeclaredConfigValueCatalog(
        DeclaredConfigKey<T> declared,
        ConfigLevelAddress address,
        ConfigAddressTemplate template,
        IConfigNodeWalker? walker,
        IConfiguration configuration)
        // The section of the level is the owner's answer where there is an owner, and the section of
        // the declared address where there is not — the core level, whose record is the configuration
        // itself and whose address therefore names that section outright.
        : base(declared.Key, address.Level, walker?.SectionKey ?? template.SectionText)
    {
        _declared = declared;
        _template = template;
        _walker = walker;
        _configuration = configuration;
        _isCore = address.Level is ConfigLevel.Core;

        // The address is resolved once, at construction: it is parsed at declaration time and the step
        // that narrows nothing is the same one for every walk.
        _unnarrowedPath = template.Resolve(ConfigDimensionValues.None);
        _corePoints = _unnarrowedPath is null && _isCore ? template.EnumerableDimension : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The core level is always the source of itself — the application configuration of a host is not
    /// substitutable. Every other level answers with its walker: a level whose walker the container
    /// does not hold, or whose source a contour replaced after the walker was registered, is NAMED as
    /// standing outside the report rather than counted as walked and found clean.
    /// <para>
    /// An address that requires a dimension value answers the same way ABOVE the core level, and for
    /// the same reason: the walk reads the records of a level at the address of the step that narrows
    /// nothing, and an address carrying a substitution outside its groups has no such step. At the
    /// core level the record is the configuration of the host, whose sections name the values of the
    /// dimension, so the level is walked point by point instead. Where neither holds there is nothing
    /// to read, so the pair is NAMED as standing outside the report — the alternative, walking no record and
    /// letting the base state <see cref="ConfigSnapshotFact.SettingNotStated"/>, would tell an operator
    /// the level states no value where no value was ever looked for.
    /// </para>
    /// </remarks>
    public override bool CanWalk =>
        (_isCore || _walker is { CanWalk: true }) && (_unnarrowedPath is not null || _corePoints is not null);

    /// <inheritdoc />
    /// <remarks>
    /// The address is answered whenever it is the obstacle, whatever the source of the level says: it
    /// keeps the walk out on its own — there is no address to read the records at — while "the source
    /// does not enumerate its records" would be the report's answer for a source that enumerates them.
    /// </remarks>
    public string? AddressRequiringDimensionValue =>
        _unnarrowedPath is null && _corePoints is null ? _template.Text : null;

    /// <summary>
    /// Builds the catalog of one declared pair, taking the walker of the level out of the container.
    /// The walker is looked up at RESOLUTION time and not at registration time: which walkers a
    /// deployment has is settled when the container is built, and a catalog registered per declaration
    /// is registered before that.
    /// </summary>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="address">Address of the level being walked.</param>
    /// <param name="configuration">Application configuration of the host.</param>
    /// <param name="provider">Container the walkers are taken from.</param>
    /// <returns>Catalog of the pair.</returns>
    public static DeclaredConfigValueCatalog<T> Over(
        DeclaredConfigKey<T> declared,
        ConfigLevelAddress address,
        IConfiguration configuration,
        IServiceProvider provider)
    {
        var walker = address.Level is ConfigLevel.Core
            ? null
            : provider.GetServices<IConfigNodeWalker>()
                .FirstOrDefault(candidate => candidate.Level == address.Level);

        return new DeclaredConfigValueCatalog<T>(declared, address, address.Template!, walker, configuration);
    }

    /// <inheritdoc />
    public override IDisposable? Subscribe(Action onSnapshot)
    {
        ArgumentNullException.ThrowIfNull(onSnapshot);

        // The boundary of the core level is the reload of the host configuration itself: the level is
        // read here from that configuration and not through an options class, so its own reload token
        // is the one thing that says a new snapshot has arrived.
        return _isCore
            ? ChangeToken.OnChange(_configuration.GetReloadToken, onSnapshot)
            : _walker?.Subscribe(onSnapshot);
    }

    /// <inheritdoc />
    protected override IEnumerable<ConfigWalkedValue<T>> Enumerate()
    {
        // The records are read at the address of the step of the chain that narrows NOTHING: that is
        // the step whose address every level has, while the narrowed ones exist only where the
        // deployment stated them. What a level states BESIDE it is enumerated by the key itself (see
        // Addresses below).
        //
        // An address without such a step is answered by CanWalk, which is where this catalog says it
        // walks nothing. Getting here against that answer means the caller walked a catalog it was told
        // not to, and the refusal is loud on purpose: an empty enumeration would make the base state
        // that the level holds no value for the setting — an assertion about a level not one record of
        // which was read. The report guards every walk it makes and names the level unread instead of
        // failing the host over it.
        if (_corePoints is { } points)
        {
            foreach (var walked in EnumerateCorePoints(points.Section, points.Dimension))
            {
                yield return walked;
            }

            yield break;
        }

        if (_unnarrowedPath is not { } path)
        {
            throw new InvalidOperationException(
                $"The address '{_template.Text}' of the walked level requires a value for a dimension it "
                + "substitutes outside its groups, so the level states nothing at an address a walk of the "
                + "snapshot could read; this catalog answers that it cannot be walked.");
        }

        var answered = false;

        foreach (var (node, relativePath) in Records(path))
        {
            // The presence of the record is the whole answer where the owner declared it so — the same
            // reading the resolution path performs (DeclaredConfigKey.StatedAtStep), asked here of
            // every record the level holds instead of of one step of the chain.
            if (_declared.PresenceMeans is { HasValue: true } presence)
            {
                answered = true;

                yield return ConfigWalkedValue<T>.Stated(
                    KeyOf(node, relativePath), node.RecordLabel, presence.Value!);

                continue;
            }

            foreach (var address in Addresses(node, relativePath))
            {
                if (Read(node, address) is not { } walked)
                {
                    continue;
                }

                answered = true;

                yield return walked;
            }
        }

        // The core level answers with what its owner declared where its record stated nothing anywhere
        // — the same answer the resolution path gives, reported here so that the walk stops calling
        // such a level silent. An address that stated a value the read could not take has ALREADY
        // answered above, and the declared answer is not reached: that is a broken value, not a silent
        // level, and the resolution treats it the same way.
        // The record label is absent because the core level has none: its record is the application
        // configuration of the host, which nothing tells apart from a neighbour it does not have.
        if (!answered && _isCore && _declared.Default is { HasValue: true } declared)
        {
            yield return ConfigWalkedValue<T>.Stated(path, recordLabel: null, declared.Value!);
        }
    }

    /// <summary>
    /// Walks the core level of an address that exists only at the points of one dimension: every child
    /// of the section in front of the substitution is a value of that dimension the deployment states,
    /// and the step standing at it is read the way the resolution reads it
    /// (<see cref="DeclaredConfigKey{T}.StatedAtStep"/>) — a parse hook of the key included, which is
    /// why nothing here has to know how the key is read.
    /// </summary>
    /// <param name="section">Section whose child keys are the values of the dimension.</param>
    /// <param name="dimension">Dimension the address substitutes outside its groups.</param>
    /// <returns>What the core level states at each of its points.</returns>
    private IEnumerable<ConfigWalkedValue<T>> EnumerateCorePoints(string section, string dimension)
    {
        var answered = false;

        foreach (var child in _configuration.GetSection(section).GetChildren())
        {
            var point = ConfigDimensionValues.Of((dimension, child.Key));

            if (_template.Resolve(point) is not { } path)
            {
                continue;
            }

            var (node, relativePath) = Records(path).Single();

            if (ReadStep(node, relativePath, point) is not { } walked)
            {
                continue;
            }

            answered = true;

            yield return walked;
        }

        // No point of the level exists: the level answers with what its owner declared for it, exactly
        // as the unnarrowed address does over a record stating nothing.
        if (!answered && _declared.Default is { HasValue: true } declared)
        {
            yield return ConfigWalkedValue<T>.Stated(_template.Text, recordLabel: null, declared.Value!);
        }
    }

    /// <summary>
    /// Reads one step of the chain for the report: the value it states, the fact that it states one no
    /// read could take, or nothing.
    /// </summary>
    /// <param name="node">Record the step is read from.</param>
    /// <param name="address">Address of the step inside the record.</param>
    /// <param name="point">Point of the chain the step stands at.</param>
    /// <returns>What the step states; null when it states nothing.</returns>
    private ConfigWalkedValue<T>? ReadStep(ConfigNode node, string address, ConfigDimensionValues point)
    {
        try
        {
            return _declared.StatedAtStep(node, address, point) is { HasValue: true } stated
                ? ConfigWalkedValue<T>.Stated(KeyOf(node, address), node.RecordLabel, stated.Value!)
                : null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return ConfigWalkedValue<T>.Unreadable(KeyOf(node, address), node.RecordLabel, ReasonOf(failure));
        }
    }

    /// <summary>
    /// Records of the level, each with the address the setting is read at INSIDE it. Above the core
    /// level both come from the walker of the level; at the core level the record is the configuration
    /// of the host, so the node stands over the section the absolute address opens with and the rest of
    /// that address is the path within it — the very split the registrar of the bindings makes when it
    /// reads the same level.
    /// </summary>
    /// <param name="path">Address the step of the chain resolved to.</param>
    /// <returns>Records of the level with the address inside each.</returns>
    private IEnumerable<(ConfigNode Node, string Path)> Records(string path)
    {
        if (!_isCore)
        {
            return (_walker?.Walk() ?? []).Select(node => (node, path));
        }

        var separator = path.IndexOf(ConfigNode.PathSeparator);

        return
        [
            (ConfigNode.Over(_configuration.GetSection(path[..separator]), ConfigLevel.Core, recordLabel: null),
                path[(separator + 1)..])
        ];
    }

    /// <summary>
    /// Addresses inside ONE record the level states the setting at. By default there is one — the
    /// address of the declaration; a key whose level holds the setting in more than one member says so
    /// itself (<see cref="DeclaredConfigKey{T}.Stated"/>).
    /// </summary>
    /// <param name="node">Record of the level.</param>
    /// <param name="path">Address of the declaration inside that record.</param>
    /// <returns>Addresses to read.</returns>
    private IEnumerable<string> Addresses(ConfigNode node, string path) =>
        _declared.Stated is { } stated ? stated(node, path) : [path];

    /// <summary>
    /// Reads one address of a record: the value stated there, the failure of the read that found a
    /// value it could not take, or nothing at all where the record states nothing.
    /// </summary>
    /// <param name="node">Record of the level.</param>
    /// <param name="address">Address inside the record.</param>
    /// <returns>Answer of the address; null when the record states nothing there.</returns>
    /// <remarks>
    /// A value no parser takes is not a value the DOMAIN of the setting can reject — and it is not a
    /// value the level failed to state either, which is what the walk used to say about it by dropping
    /// it. It is therefore ANSWERED rather than dropped, with the reason the read ended and never with
    /// the value: the read failed, so there is nothing of the setting's type to name, and the text the
    /// record holds may be a secret. The reason is reduced to the TYPE of the failure for a key whose
    /// value cannot be read into. The reason is COMPOSED and never taken from the failure of a binder
    /// we did not write (<see cref="ReasonOf"/>), for exactly that reason: such a message quotes the
    /// text it choked on.
    /// <para>
    /// The failure belongs to ONE ADDRESS rather than to the record — a broken value must not take the
    /// report of the values stated beside it down with it, which is the opposite of what the resolution
    /// does with the same record for the opposite reason: a report NAMES what each address holds, while
    /// a resolution hands ONE value to a consumer and must not assemble it half out of what the
    /// deployment stated. Cancellation is not a failure of a value and travels on.
    /// </para>
    /// </remarks>
    private ConfigWalkedValue<T>? Read(ConfigNode node, string address)
    {
        try
        {
            // Read the way the DECLARATION says an address is read — the very call the resolution path
            // makes, so a level that states a blank text under a key declaring blank to be no statement
            // is silent to both alike.
            return _declared.StatedAt(node, address) is { HasValue: true } stated
                ? ConfigWalkedValue<T>.Stated(KeyOf(node, address), node.RecordLabel, stated.Value!)
                : null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return ConfigWalkedValue<T>.Unreadable(KeyOf(node, address), node.RecordLabel, ReasonOf(failure));
        }
    }

    /// <summary>
    /// Why the read of an address ended, in words the report may print. It is COMPOSED here rather
    /// than taken from the failure, and that is the load-bearing part: the message of a binder quotes
    /// the text it choked on — the configuration binder of the platform spells the rejected value into
    /// its own sentence — and a report that repeated it would print the value of a setting the read
    /// never managed to produce. The one message that does travel is the mechanism's own refusal of a
    /// SHAPE, which names the address and the type the value was read into and nothing else.
    /// <para>
    /// A key whose value is a SECRET is reduced further still, to the type of the failure alone, the
    /// way the resolution path reduces it (<see cref="PathConfigKeyRegistrar"/>): such a key never has
    /// a snapshot catalog at all today (<see cref="ConfigValueCatalog{T}"/> refuses one), and the line
    /// is here so that it stays true if it ever does.
    /// </para>
    /// </summary>
    /// <param name="failure">Failure of the read.</param>
    /// <returns>Reason the report prints.</returns>
    private string ReasonOf(Exception failure)
    {
        if (_declared.Key.IsSecret)
        {
            return failure.GetType().Name;
        }

        return failure is ConfigValueShapeException shape
            ? shape.Message
            : $"the value stated there is not a value of type '{typeof(T)}' ({failure.GetType().Name})";
    }

    /// <summary>
    /// Configuration key of one stated value — the key an operator edits to change it. It is the
    /// RECORD that answers it (<see cref="ConfigNode.ConfigurationKeyOf"/>): the address a value is
    /// read at is spelled in the record's own contract, and whether the configuration holds that
    /// member under the same name is known to the record alone. A record whose source has no
    /// configuration address is named by the address inside it alone.
    /// </summary>
    /// <param name="node">Record of the level.</param>
    /// <param name="address">Address inside the record.</param>
    /// <returns>Configuration key of the value.</returns>
    private static string KeyOf(ConfigNode node, string address) =>
        node.ConfigurationKeyOf(address) ?? address;
}
