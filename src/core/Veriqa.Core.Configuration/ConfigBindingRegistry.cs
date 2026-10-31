// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Veriqa.Core.Configuration;

/// <summary>
/// The single registry of the deployment's configuration keys and of their extraction bindings
/// (SPEC-012 §10.6). It is filled once at startup by the <see cref="IRegisterConfigKeys"/> registrars
/// — declarations first, bindings second — and afterwards only read, so a resolution never competes
/// for it.
/// <para>
/// The two halves are separate on purpose: the SCHEMA holds what the owners declared, the BINDINGS
/// hold how each level of each key is read. A binding over a key nobody declared is refused, and a key
/// nobody bound stays in the schema and is named at startup.
/// </para>
/// <para>
/// A binding is stored as a typed closure erased to <see cref="Delegate"/>: both type parameters are
/// known statically at the point of registration, and the cast back is performed against the key's
/// own type parameter during resolution. This is the one place in the mechanism where a type is
/// erased, and it is internal — no untyped member appears on a public contract.
/// </para>
/// </summary>
internal sealed class ConfigBindingRegistry : IConfigBindings, IConfigCoreBindings, IConfigKeyDeclarations
{
    /// <summary>
    /// What a refusal calls the side of a collision that was not made by a registrar of the container —
    /// the registry is filled by the registrars alone, so this stands for a caller that reached it by
    /// another road rather than for a registrar whose name was lost.
    /// </summary>
    private const string CallerOutsideTheRegistrars = "a caller outside the registrars";

    /// <summary>
    /// Extraction bindings by the pair "setting name + level".
    /// </summary>
    private readonly Dictionary<(string KeyName, ConfigLevel Level), Delegate> _bindings = [];

    /// <summary>
    /// Core-level getters by setting name, in their SYNCHRONOUS form. The core level is a closure over
    /// global options — pure memory — and this map is what the framework composition steps that run
    /// before the container read (<see cref="ConfigCoreValues"/>). The asynchronous binding of the same
    /// level is built from the very same closure, so there is one declaration and not two.
    /// </summary>
    private readonly Dictionary<string, Delegate> _coreValues = new(StringComparer.Ordinal);

    /// <summary>
    /// Record reader behind each non-core binding. It is kept as the INSTANCE and not merely as its
    /// name, because two questions are asked of it: which class a startup message names when a binding
    /// addresses a level no source serves, and whether what stands behind it is live — the answer that
    /// decides whether the level may be cached at all (SPEC-012 §10.6).
    /// </summary>
    private readonly Dictionary<(string KeyName, ConfigLevel Level), IConfigRecordReader> _readers = [];

    /// <summary>
    /// Declarations of every key of the deployment, by setting name — the schema. A key enters it
    /// through its own DECLARATION and never through a binding, which is what lets the schema hold a
    /// key nobody has bound yet.
    /// </summary>
    private readonly Dictionary<string, ConfigKeyDeclaration> _keys = new(StringComparer.Ordinal);

    /// <summary>
    /// A binding may only be registered for a key that has been declared. It is false for the ONE
    /// registry that has no schema to be consistent with — the throw-away registry behind
    /// <see cref="ConfigCoreValues"/>, built for a single synchronous read of the core level before
    /// the container exists.
    /// </summary>
    private readonly bool _declarationRequired;

    /// <summary>
    /// Registrar behind each declaration, by setting name. A refusal has to name BOTH sides of a
    /// collision: during a migration the two are a handwritten registrar and the one that reads the
    /// catalogs, and "declared twice" without their names says which key is in conflict but not which
    /// of the two to take out.
    /// </summary>
    private readonly Dictionary<string, string> _declaredBy = new(StringComparer.Ordinal);

    /// <summary>
    /// Registrar behind each binding, by the pair "setting name + level" — the other half of the same
    /// answer (see <see cref="_declaredBy"/>).
    /// </summary>
    private readonly Dictionary<(string KeyName, ConfigLevel Level), string> _boundBy = [];

    /// <summary>
    /// Registrar being applied right now; null outside the two passes of the constructor.
    /// </summary>
    private string? _applying;

    /// <summary>
    /// Creates the registry of the deployment and applies every registrar of the container IN TWO
    /// PASSES: first every declaration, then every binding. The order of the registrars in the
    /// container therefore never decides whether a binding finds its key.
    /// </summary>
    /// <param name="registrars">Registrars of the consumers' keys and bindings.</param>
    public ConfigBindingRegistry(IEnumerable<IRegisterConfigKeys> registrars)
        : this(registrars, declarationRequired: true)
    {
    }

    /// <summary>
    /// Creates the registry, stating whether a binding must address a declared key.
    /// </summary>
    /// <param name="registrars">Registrars of the consumers' keys and bindings.</param>
    /// <param name="declarationRequired">A binding may only be registered for a declared key.</param>
    private ConfigBindingRegistry(IEnumerable<IRegisterConfigKeys> registrars, bool declarationRequired)
    {
        ArgumentNullException.ThrowIfNull(registrars);

        _declarationRequired = declarationRequired;

        var applied = registrars.ToArray();

        // The registrar being applied is remembered around each call rather than passed down through
        // the registration members: those members are the PUBLIC contract every owner writes against
        // (IConfigKeyDeclarations, IConfigBindings), and an argument naming the caller would be one
        // more thing every registrar has to state correctly for a diagnostic message to be true.
        foreach (var registrar in applied)
        {
            _applying = registrar.GetType().Name;
            registrar.DeclareKeys(this);
        }

        foreach (var registrar in applied)
        {
            _applying = registrar.GetType().Name;
            registrar.Register(this, this);
        }

        _applying = null;
    }

    /// <summary>
    /// Builds the throw-away registry behind <see cref="ConfigCoreValues"/>: a single synchronous read
    /// of the core level, performed by a framework composition step that runs before the container
    /// exists. It is not the schema of the deployment — nothing publishes it, nothing walks it and no
    /// startup report is printed from it — so the keys it is asked about are the ones its caller names
    /// on the spot, and requiring a separate declaration of them would be a ceremony over a local
    /// closure.
    /// </summary>
    /// <param name="registrars">Registrars of the core getters to read.</param>
    /// <returns>Registry holding the declared core getters.</returns>
    public static ConfigBindingRegistry ForCoreValues(IEnumerable<IRegisterConfigKeys> registrars) =>
        new(registrars, declarationRequired: false);

    /// <inheritdoc />
    public void Declare<T>(ConfigKey<T> key, ConfigDeclaredDefault? declaredDefault = null, bool notWalked = false)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_keys.TryAdd(key.Name, ConfigKeyDeclaration.Of(key, declaredDefault, notWalked)))
        {
            throw new InvalidOperationException(
                $"Configuration key '{key.Name}' is declared twice — by {DeclaredBy(key.Name)} and by " +
                $"{Applying}: which of the two declarations the schema holds would depend on the order of the " +
                "registrations.");
        }

        _declaredBy[key.Name] = Applying;
    }

    /// <summary>
    /// Declarations of the keys known to the registry — the input of the startup diagnostics.
    /// </summary>
    public IReadOnlyCollection<ConfigKeyDeclaration> Keys => _keys.Values;

    /// <summary>
    /// Builds the read-only slice of the schema of declared keys (<see cref="ConfigKeySchemaView"/>).
    /// The registry itself stays internal: what leaves the assembly is the declarations, not the
    /// bindings behind them.
    /// </summary>
    /// <returns>Slice of the schema as of this moment.</returns>
    public ConfigKeySchemaView CreateSchemaView() =>
        new([.. _keys.Values.OrderBy(key => key.Name, StringComparer.Ordinal).Select(key => key.ToView())]);

    /// <summary>
    /// Builds the read-only slice of the bindings (<see cref="ConfigBindingView"/>): the levels each key
    /// is bound at, in resolution precedence order. The bindings themselves stay here — what leaves the
    /// assembly is the fact that a level is bound, not the closure behind it.
    /// </summary>
    /// <returns>Slice of the bindings as of this moment.</returns>
    public ConfigBindingView CreateBindingView() =>
        new(_bindings.Keys
            .GroupBy(id => id.KeyName, StringComparer.Ordinal)
            .ToFrozenDictionary(
                bound => bound.Key,
                bound => ConfigLevelPrecedence.Order
                    .Intersect(bound.Select(id => id.Level))
                    .ToImmutableArray(),
                StringComparer.Ordinal));

    /// <summary>
    /// Levels bound for a setting.
    /// </summary>
    /// <param name="keyName">Setting name.</param>
    /// <returns>Levels that have a binding.</returns>
    public IEnumerable<ConfigLevel> BoundLevelsOf(string keyName) =>
        _bindings.Keys.Where(id => string.Equals(id.KeyName, keyName, StringComparison.Ordinal))
            .Select(id => id.Level);

    /// <summary>
    /// Non-core bindings of the registry as pairs "key + level" together with the reader behind them.
    /// </summary>
    /// <returns>Bindings above the core level.</returns>
    public IEnumerable<(string KeyName, ConfigLevel Level, string ReaderName)> RecordBindings() =>
        _readers.Select(entry => (entry.Key.KeyName, entry.Key.Level, entry.Value.Name));

    /// <summary>
    /// Returns the record reader behind the binding of the pair "key + level", or null when the level
    /// is bound without one (the core level, whose value is a closure over global options).
    /// </summary>
    /// <param name="keyName">Setting name.</param>
    /// <param name="level">Level.</param>
    /// <returns>Record reader, or null.</returns>
    public IConfigRecordReader? ReaderOf(string keyName, ConfigLevel level) =>
        _readers.GetValueOrDefault((keyName, level));

    /// <inheritdoc />
    public void Register<T, TRecord>(
        ConfigKey<T> key,
        ConfigLevel level,
        IConfigRecordReader<TRecord> reader,
        Func<TRecord, LayerValue<T>> extract)
        where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(extract);

        Register(key, level, reader, (record, _) => extract(record));
    }

    /// <inheritdoc />
    public void Register<T, TRecord>(
        ConfigKey<T> key,
        ConfigLevel level,
        IConfigRecordReader<TRecord> reader,
        Func<TRecord, ConfigDimensionValues, LayerValue<T>> extract)
        where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(extract);

        if (level == ConfigLevel.Core)
        {
            throw new InvalidOperationException(
                $"Configuration key '{key.Name}' binds the core level through a record reader: the core level " +
                "has no record and is bound through the core bindings instead.");
        }

        Add(key, level, async (scope, dimensions, cancellationToken) =>
        {
            var record = await scope.ReadAsync(reader, cancellationToken);

            return ConfigLevelOutcome<T>.Stated(record is null ? LayerValue<T>.None : extract(record, dimensions));
        });

        _readers[(key.Name, level)] = reader;
    }

    /// <inheritdoc />
    public void RegisterCore<T>(ConfigKey<T> key, Func<T> extract)
    {
        ArgumentNullException.ThrowIfNull(extract);

        RegisterCore(key, _ => LayerValue<T>.Set(extract()));
    }

    /// <inheritdoc />
    public void RegisterCore<T>(ConfigKey<T> key, Func<T> extract, Func<bool> gate)
    {
        ArgumentNullException.ThrowIfNull(extract);
        ArgumentNullException.ThrowIfNull(gate);

        RegisterCore(key, _ => LayerValue<T>.Set(extract()), gate);
    }

    /// <inheritdoc />
    public void RegisterCore<T>(
        ConfigKey<T> key,
        Func<ConfigDimensionValues, LayerValue<T>> extract,
        Func<bool>? gate = null) =>
        RegisterCore(key, extract, gate, overSilentRecord: null);

    /// <inheritdoc />
    public void RegisterCore<T>(
        ConfigKey<T> key,
        Func<ConfigDimensionValues, LayerValue<T>> extract,
        Func<ConfigDimensionValues, LayerValue<T>> overSilentRecord)
    {
        ArgumentNullException.ThrowIfNull(overSilentRecord);

        RegisterCore(key, extract, gate: null, overSilentRecord);
    }

    /// <summary>
    /// Registers the core-level binding of a key on both paths of reading — the asynchronous one of the
    /// resolver and the synchronous one of the composition steps.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <param name="extract">Getter of the value at a chain point.</param>
    /// <param name="gate">Getter of the core-level gate flag; null — the level states no gate.</param>
    /// <param name="overSilentRecord">
    /// Getter of what the level states over a record stating nothing; null — the level has no such
    /// answer, and a rejected step is left unset.
    /// </param>
    private void RegisterCore<T>(
        ConfigKey<T> key,
        Func<ConfigDimensionValues, LayerValue<T>> extract,
        Func<bool>? gate,
        Func<ConfigDimensionValues, LayerValue<T>>? overSilentRecord)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(extract);

        // The gate getter (when registered) is evaluated on every resolution — it reflects the live
        // IOptionsMonitor.CurrentValue exactly like the value getter (CFG-212).
        LayerValue<T> Evaluate(ConfigDimensionValues dimensions)
        {
            var value = extract(dimensions);

            return gate is null || !value.HasValue
                ? value
                : LayerValue<T>.Set(value.Value!, gate());
        }

        // The verdict of the domain is taken HERE for the core level rather than left to Add alone: a
        // rejected step of this level carries, beside the fact of the rejection, what the level states
        // over a record stating nothing — there is no level below the core, and whether that answer or
        // the one of CFG-246 is given is the resolver's decision, which it cannot make without both.
        // The answer is read only once a value has been rejected, so an admitted value costs nothing.
        ConfigLevelOutcome<T> CoreOutcome(ConfigDimensionValues dimensions)
        {
            var outcome = ConfigLevelOutcome<T>.Stated(Evaluate(dimensions));

            if (key.Domain is not { } declaredDomain)
            {
                return outcome;
            }

            outcome = WithinDomain(outcome, declaredDomain);

            return outcome.IsRejected && overSilentRecord is not null
                ? outcome with { OverSilentRecord = overSilentRecord(dimensions) }
                : outcome;
        }

        // The core level is pure memory, so the completed ValueTask never allocates a state machine.
        Add(
            key,
            ConfigLevel.Core,
            (_, dimensions, _) => ValueTask.FromResult(CoreOutcome(dimensions)));

        // The SYNCHRONOUS path gets the domain of the key applied to it here, and it has to be applied
        // here separately: the getter below is the very same closure, but it does not go through the
        // binding of Add — the steps of composition that read it (ConfigCoreValues) never touch a
        // binding. A domain applied in Add alone would hold on the asynchronous path and stay silent on
        // this one, which is the same setting answering differently depending on who asks.
        // The REJECTION itself is dropped here rather than answered with the last valid value of the
        // level (CFG-246): this getter serves the composition steps that run before the container, and
        // at that moment no value has ever been read successfully, so there is nothing to fall back to.
        // What the level states over a silent record is the answer instead, where it has one.
        Func<ConfigDimensionValues, LayerValue<T>> coreValue = dimensions =>
        {
            var outcome = CoreOutcome(dimensions);

            return outcome.IsRejected ? outcome.OverSilentRecord : outcome.Value;
        };

        _coreValues[key.Name] = coreValue;
    }

    /// <summary>
    /// Returns the binding of the pair "key + level", or null when the level is not bound for the key.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <param name="level">Level.</param>
    /// <returns>Binding, or null.</returns>
    public ConfigBinding<T>? Find<T>(ConfigKey<T> key, ConfigLevel level) =>
        _bindings.TryGetValue((key.Name, level), out var binding)
            ? (ConfigBinding<T>)binding
            : null;

    /// <summary>
    /// Returns the synchronous core getter of a key, or null when the key declared none.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <returns>Core getter, or null.</returns>
    public Func<ConfigDimensionValues, LayerValue<T>>? FindCoreValue<T>(ConfigKey<T> key) =>
        _coreValues.TryGetValue(key.Name, out var getter)
            ? (Func<ConfigDimensionValues, LayerValue<T>>)getter
            : null;

    /// <summary>
    /// Registrar being applied, as a refusal names it.
    /// </summary>
    private string Applying => _applying ?? CallerOutsideTheRegistrars;

    /// <summary>
    /// Registrar that declared a key, as a refusal names it.
    /// </summary>
    /// <param name="keyName">Setting name.</param>
    /// <returns>Name of the registrar.</returns>
    private string DeclaredBy(string keyName) =>
        _declaredBy.GetValueOrDefault(keyName, CallerOutsideTheRegistrars);

    /// <summary>
    /// Adds a binding, remembering the key's declaration and refusing a duplicate: two bindings of the
    /// same pair would make the effective value depend on the order of the DI registrations. The
    /// refusal names BOTH registrars of the pair — while a key is being migrated from a handwritten
    /// registrar to the one that reads the catalogs, the pair alone does not say which of the two is
    /// the one to take out.
    /// <para>
    /// This is also where the DOMAIN of the key is applied (CFG-240): every binding of every level —
    /// the ones registered through a record reader and the core one alike — passes through here, so a
    /// value the setting does not admit leaves its step of the chain unset without a single level
    /// carrying a check of its own.
    /// </para>
    /// </summary>
    private void Add<T>(ConfigKey<T> key, ConfigLevel level, ConfigBinding<T> binding)
    {
        if (!key.Levels.Contains(level))
        {
            throw new InvalidOperationException(
                $"Configuration key '{key.Name}' is bound at level {level}, which it does not declare among its levels.");
        }

        if (!_keys.ContainsKey(key.Name))
        {
            if (_declarationRequired)
            {
                throw new InvalidOperationException(
                    $"Configuration key '{key.Name}' is bound at level {level} without having been declared: a key " +
                    "enters the schema through the declaration of its owner, and a binding over an undeclared key " +
                    "would leave the setting invisible to every check made over the schema.");
            }

            // The throw-away registry behind ConfigCoreValues is the only caller reaching here, and
            // its keys are named by it on the spot rather than declared: there is no declaration to
            // take an answer of the core level off, and nothing reads a schema it does not publish.
            _keys[key.Name] = ConfigKeyDeclaration.Of(key, declaredDefault: null, notWalked: false);
        }

        if (key.Domain is { } domain)
        {
            var declared = binding;

            binding = async (scope, dimensions, cancellationToken) =>
                WithinDomain(await declared(scope, dimensions, cancellationToken), domain);
        }

        if (!_bindings.TryAdd((key.Name, level), binding))
        {
            throw new InvalidOperationException(
                $"Configuration key '{key.Name}' already has a binding at level {level} — registered by " +
                $"{_boundBy.GetValueOrDefault((key.Name, level), CallerOutsideTheRegistrars)} and now by " +
                $"{Applying}: a second one would make the effective value depend on the order of the " +
                "registrations.");
        }

        _boundBy[(key.Name, level)] = Applying;
    }

    /// <summary>
    /// Keeps a level value only while the domain of the key admits it. A rejected value is turned into
    /// the SAME thing an unstated one yields, so the step of the chain that stated it simply does not
    /// answer: the setting is taken from the next step of the level and, once the level is exhausted,
    /// from the level below (CFG-236). Nothing is written to the log here — a rejection is a property
    /// of the configuration rather than of a resolution, and repeating it on every read would say
    /// nothing new after the first time; the operator is told about it once per configuration snapshot.
    /// <para>
    /// The FACT of the rejection travels on beside the emptied value (<see cref="ConfigLevelOutcome{T}"/>):
    /// an owner that declared <see cref="ConfigValueRejectionPolicy.FailStart"/> is answered with the
    /// last valid value instead of with the level below at the core level the walk of the snapshot
    /// covers (CFG-246), and the resolution cannot tell "rejected" from "never stated" once both have
    /// become None.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="outcome">What the level stated.</param>
    /// <param name="domain">Domain declared by the key.</param>
    /// <returns>The value, or a rejection carrying <see cref="LayerValue{T}.None"/>.</returns>
    private static ConfigLevelOutcome<T> WithinDomain<T>(
        ConfigLevelOutcome<T> outcome,
        ConfigValueDomain<T> domain) =>
        !outcome.Value.HasValue || domain.Admits(outcome.Value.Value!, out _)
            ? outcome
            : ConfigLevelOutcome<T>.Rejected;
}

/// <summary>
/// Declaration of a key as the registry knows it: enough to print the startup map, to publish the
/// schema slice and to spot a level that was declared but never bound. The VALUE of a setting is not
/// part of it — a declaration goes into diagnostics, a value never does (SPEC-012 §10.6).
/// </summary>
/// <param name="Name">Setting name.</param>
/// <param name="Kind">Level-combining semantics of the key.</param>
/// <param name="Levels">Levels the key declares.</param>
/// <param name="GatedLevels">Levels whose override requires a gate; null — every level overrides freely.</param>
/// <param name="Dimensions">
/// Dimensions of the key; null — the key is not composite. Only the NAMES of the steps reach the
/// schema: the chain is data of the key, and printing it would say nothing to an operator that the
/// names do not.
/// </param>
/// <param name="CachePolicy">Caching policy of the key.</param>
/// <param name="IsSecret">The value of the key is a secret.</param>
/// <param name="Domain">
/// Description of the boundary the key admits its values within; null — the key declares no domain.
/// The PREDICATE of the domain is not part of a declaration: a declaration is printed, not evaluated.
/// </param>
/// <param name="OnRejected">
/// What the key does about a value outside its domain. A key without a domain rejects nothing, and
/// carries the default here rather than an absence: there is no third answer to give.
/// </param>
/// <param name="DeclaredDefault">
/// What the key declares its CORE level answers where the record of that level states nothing; null —
/// the owner declared no such answer, which is a different fact from an answer whose value is null.
/// It is the one thing here that comes from the DECLARATION rather than from the key: the answer is a
/// member of the declaration, beside the reading of a step (<see cref="DeclaredConfigKey{T}"/>).
/// </param>
/// <param name="NotWalked">
/// The walk of a configuration snapshot does not reproduce the value of this key, so the report names
/// its pairs as standing outside it. It comes from the DECLARATION as well: what a hook assembles out
/// of a record is a fact of the reading, not of the key.
/// </param>
internal sealed record ConfigKeyDeclaration(
    string Name,
    ConfigKeyKind Kind,
    IReadOnlySet<ConfigLevel> Levels,
    IReadOnlySet<ConfigLevel>? GatedLevels,
    ConfigKeyDimensions? Dimensions,
    ConfigCachePolicy CachePolicy,
    bool IsSecret,
    string? Domain,
    ConfigValueRejectionPolicy OnRejected,
    ConfigDeclaredDefault? DeclaredDefault,
    bool NotWalked)
{
    /// <summary>
    /// Takes the declaration of a key off the key itself — the single place the schema is built from,
    /// so a boundary added to a key reaches the schema without a second edit.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <param name="declaredDefault">Answer declared for the core level; null — none was declared.</param>
    /// <param name="notWalked">The walk of a configuration snapshot does not reproduce the value.</param>
    /// <returns>Declaration of the key.</returns>
    public static ConfigKeyDeclaration Of<T>(
        ConfigKey<T> key,
        ConfigDeclaredDefault? declaredDefault,
        bool notWalked) =>
        new(
            key.Name,
            key.Kind,
            key.Levels,
            key.GatedLevels,
            key.Dimensions,
            key.CachePolicy,
            key.IsSecret,
            key.Domain?.Boundary,
            key.Domain?.OnRejected ?? ConfigValueRejectionPolicy.SkipStep,
            declaredDefault,
            notWalked);

    /// <summary>
    /// Builds the public view of this declaration — the slice a consumer outside the assembly reads.
    /// </summary>
    /// <returns>View of the key.</returns>
    public ConfigKeyView ToView() =>
        new(
            Name,
            Kind,
            [.. ConfigLevelPrecedence.Order.Where(Levels.Contains)],
            GatedLevels is null ? [] : [.. ConfigLevelPrecedence.Order.Where(GatedLevels.Contains)],
            Dimensions is null ? [] : [.. Dimensions.Names],
            CachePolicy,
            Domain,
            OnRejected,
            IsSecret,
            DeclaredDefault);
}

/// <summary>
/// Extraction of one level's value inside a resolution: the record is taken from the scope (so a
/// source is read once per resolution), and the chain point says what is being asked.
/// </summary>
/// <typeparam name="T">Type of the setting value.</typeparam>
/// <param name="scope">Scope of the current resolution.</param>
/// <param name="dimensions">Point of the fallback chain.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>What the level stated.</returns>
internal delegate ValueTask<ConfigLevelOutcome<T>> ConfigBinding<T>(
    ConfigResolutionScope scope,
    ConfigDimensionValues dimensions,
    CancellationToken cancellationToken);

/// <summary>
/// What one level stated for one key at one point of the fallback chain: the value, and whether the
/// level stated a value the domain of the key does NOT admit.
/// <para>
/// The two are carried together because a rejected value is emptied on the spot
/// (<see cref="ConfigBindingRegistry"/>) and becomes indistinguishable from a value nobody stated,
/// while the two can be answered differently for a key that declared
/// <see cref="ConfigValueRejectionPolicy.FailStart"/>: a level that stated nothing ALWAYS cedes to the
/// level below, whereas a level that stated something inadmissible keeps an answer of its own where
/// CFG-246 gives it one — at the core level the walk of the snapshot reaches, which is answered with
/// the last value the level was read with. Where CFG-246 does not reach — a level whose source does
/// not enumerate its records — the rejection is dropped by the default of CFG-240 and the level cedes
/// exactly as an unstated one does. The flag is therefore what lets the resolution ASK that question,
/// not the answer to it (SPEC-012 §4.1 CFG-246, CFG-240).
/// </para>
/// <para>
/// It is INTERNAL and never appears on the extraction contract an owner of a key implements: what a
/// binding returns is still a plain <see cref="LayerValue{T}"/>, and the verdict of the domain is
/// added by the single seam every binding passes through.
/// </para>
/// </summary>
/// <typeparam name="T">Type of the setting value.</typeparam>
/// <param name="Value">Value the level stated; None when it stated nothing, or when it was rejected.</param>
/// <param name="IsRejected">The level stated a value the domain of the key does not admit.</param>
internal readonly record struct ConfigLevelOutcome<T>(LayerValue<T> Value, bool IsRejected)
{
    /// <summary>
    /// The level stated this value (or stated nothing, when it is None).
    /// </summary>
    /// <param name="value">Value of the level.</param>
    /// <returns>Outcome carrying the value.</returns>
    public static ConfigLevelOutcome<T> Stated(LayerValue<T> value) => new(value, IsRejected: false);

    /// <summary>
    /// The level stated a value its setting does not admit.
    /// </summary>
    public static ConfigLevelOutcome<T> Rejected { get; } = new(LayerValue<T>.None, IsRejected: true);

    /// <summary>
    /// What the level states over a record stating nothing, carried beside a rejection of the CORE
    /// level: nothing lies below the core, so a rejected step the default of CFG-240 drops is answered
    /// with this rather than left to the default of the type. None — the level has no such answer, and
    /// always None above the core, where a dropped step cedes to the level below.
    /// </summary>
    public LayerValue<T> OverSilentRecord { get; init; }
}
