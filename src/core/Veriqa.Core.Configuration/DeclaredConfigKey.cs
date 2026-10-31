// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.Configuration;

/// <summary>
/// Declaration of one key as the catalog holds it: the key itself, the levels it lives at TOGETHER
/// with the address of each of them, and — where the address cannot express the value — the parse
/// hook of the key (SPEC-012 §10.6).
/// <para>
/// It is the answer to "how many places state that this key lives at this level": one. Stated by
/// the key, by a binding per level and by a field per level of every record, the levels would live in
/// three places that could disagree.
/// </para>
/// <para>
/// The type of the value is erased HERE and recovered by <see cref="Accept"/>, so a consumer that
/// needs it — the registrar of the bindings, a table of the documentation — gets it back through the
/// visitor instead of casting. Everything the erased half offers is what a consumer can answer
/// without the type at all.
/// </para>
/// </summary>
public abstract class DeclaredConfigKey
{
    /// <summary>
    /// Creates the erased half of a declaration.
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="valueType">Type of the setting value.</param>
    /// <param name="dimensions">Dimensions of the key; null — the key is not composite.</param>
    /// <param name="levels">Levels of the key together with the address of each.</param>
    /// <param name="declaredDefault">
    /// The answer declared for the core level as a schema states it; null — none was declared.
    /// </param>
    /// <param name="isRequired">A value of the key must be stated by some level.</param>
    /// <param name="notWalked">The walk of a configuration snapshot does not reproduce the value.</param>
    /// <param name="admittedValues">
    /// The values the key admits, as a message naming them prints them; null — the declaration names
    /// none.
    /// </param>
    private protected DeclaredConfigKey(
        string name,
        Type valueType,
        ConfigKeyDimensions? dimensions,
        IReadOnlyList<ConfigLevelAddress> levels,
        ConfigDeclaredDefault? declaredDefault,
        bool isRequired,
        bool notWalked,
        string? admittedValues)
    {
        Name = name;
        ValueType = valueType;
        Dimensions = dimensions;
        Levels = levels;
        DeclaredDefault = declaredDefault;
        IsRequired = isRequired;
        NotWalked = notWalked;
        AdmittedValues = admittedValues;
    }

    /// <summary>
    /// Setting name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Type of the setting value.
    /// </summary>
    public Type ValueType { get; }

    /// <summary>
    /// Dimensions of the key; null — the key is not composite.
    /// </summary>
    public ConfigKeyDimensions? Dimensions { get; }

    /// <summary>
    /// Levels the key lives at, each with the FORM of its address (SPEC-012 §10.6): a path derived
    /// from the name, a path stated explicitly, or no path at all.
    /// </summary>
    public IReadOnlyList<ConfigLevelAddress> Levels { get; }

    /// <summary>
    /// The answer this key declares for its CORE level where the record of that level states nothing
    /// at the address, as a SCHEMA states it — the text of the value, or the bare fact that one was
    /// declared; null when the owner declared none.
    /// <para>
    /// It stands on the ERASED half because that is where every describer of the schema reads it: the
    /// document a host prints for its composition and the slice a consumer of the assembly reads ask
    /// what the owner declared, not what type the value has. The typed half keeps the value itself
    /// (<see cref="DeclaredConfigKey{T}.Default"/>) — the two paths of reading answer WITH it, and a
    /// schema never does.
    /// </para>
    /// </summary>
    public ConfigDeclaredDefault? DeclaredDefault { get; }

    /// <summary>
    /// A value of this setting MUST be stated by some level: a deployment where none states one does
    /// not start (<see cref="ConfigKeyBuilder{T}.Required"/>). WHEN the question is worth asking, and
    /// for which values of a dimension, stays with the owner of the key — the mechanism answers "did
    /// any level state one" and keeps no list of the objects a setting is asked about.
    /// </summary>
    public bool IsRequired { get; }

    /// <summary>
    /// The WALK of a configuration snapshot does not reproduce the value of this key
    /// (<see cref="ConfigKeyBuilder{T}.NotWalked"/>): what a level states is assembled by the parse
    /// hook, and a hook belongs to the path of reading that calls it. The report of the snapshot names
    /// the pairs "this key + each level it is bound at" as standing outside it, instead of saying
    /// nothing about a setting the levels do state.
    /// </summary>
    public bool NotWalked { get; }

    /// <summary>
    /// The values this key admits, as a message naming them prints them — the boundary the domain
    /// states in words, or the tokens of a declared dictionary; null when the declaration names
    /// neither, and a message therefore has nothing to list.
    /// <para>
    /// It stands on the ERASED half because the one place that prints it does not know the type of the
    /// value (<see cref="RequiredConfigValues"/>), and because "what may be written here" is a fact of
    /// the declaration rather than of the type.
    /// </para>
    /// </summary>
    public string? AdmittedValues { get; }

    /// <summary>
    /// Hands the declaration to a consumer WITH the type of its value.
    /// </summary>
    /// <param name="visitor">Consumer of the declaration.</param>
    public abstract void Accept(IConfigKeyDeclarationVisitor visitor);
}

/// <summary>
/// Declaration of a key of a known value type — the typed half of <see cref="DeclaredConfigKey"/>.
/// </summary>
/// <typeparam name="T">Type of the setting value.</typeparam>
public sealed class DeclaredConfigKey<T> : DeclaredConfigKey
{
    /// <summary>
    /// Creates the declaration of a key.
    /// </summary>
    /// <param name="key">Setting key built from the declaration.</param>
    /// <param name="levels">Levels of the key together with the address of each.</param>
    /// <param name="parse">Parse hook of the key; null — the value is read by the configuration binder.</param>
    /// <param name="stated">Stated-addresses hook of the key; null — the address of the level is the only one.</param>
    /// <param name="declaredDefault">
    /// Answer of the core level over a record stating nothing; <see cref="LayerValue{T}.None"/> — the
    /// owner declared none.
    /// </param>
    /// <param name="blankIsUnstated">A level stating a blank text states nothing at all.</param>
    /// <param name="presenceMeans">
    /// Answer of a level whose record EXISTS, given without reading the address;
    /// <see cref="LayerValue{T}.None"/> — the owner declared none.
    /// </param>
    /// <param name="reading">
    /// How one address of a record is read into the value of the setting; null — by the configuration
    /// binder.
    /// </param>
    /// <param name="isRequired">A value of the key must be stated by some level.</param>
    /// <param name="notWalked">The walk of a configuration snapshot does not reproduce the value.</param>
    /// <param name="admittedValues">
    /// The values the key admits, as a message naming them prints them; null — the declaration names
    /// none.
    /// </param>
    internal DeclaredConfigKey(
        ConfigKey<T> key,
        IReadOnlyList<ConfigLevelAddress> levels,
        Func<ConfigNode, string, ConfigDimensionValues, LayerValue<T>>? parse,
        Func<ConfigNode, string, IEnumerable<string>>? stated,
        LayerValue<T> declaredDefault,
        bool blankIsUnstated,
        LayerValue<T> presenceMeans,
        Func<ConfigNode, string, LayerValue<T>>? reading,
        bool isRequired,
        bool notWalked,
        string? admittedValues)
        : base(
            key.Name,
            typeof(T),
            key.Dimensions,
            levels,
            // The schema is handed the TEXT of the declared answer and never the value, and nothing of
            // it at all for a secret: this text is printed into the client documentation, and a
            // shipped secret would leave the product through it.
            declaredDefault.HasValue
                ? new ConfigDeclaredDefault(key.IsSecret ? null : ConfigValueText.Of(declaredDefault.Value))
                : null,
            isRequired,
            notWalked,
            admittedValues)
    {
        Key = key;
        Parse = parse;
        Stated = stated;
        Default = declaredDefault;
        BlankIsUnstated = blankIsUnstated;
        PresenceMeans = presenceMeans;
        Reading = reading;

        // The key is told which declaration it belongs to, so that a member declared here reaches a
        // seam handed the key alone (ConfigKey{T}.Declaration).
        key.DeclaredAs(this);
    }

    /// <summary>
    /// Setting key — the very object every consumer of the mechanism already resolves by.
    /// </summary>
    public ConfigKey<T> Key { get; }

    /// <summary>
    /// How the value is read out of the record's subtree; null — by the configuration binder at the
    /// resolved address. The hook is handed the node of the level, the address the step resolved to
    /// and the point of the chain, and it exists for the two shapes an address cannot describe: one
    /// value standing over several members of the record (a path together with its SRI hash), and a
    /// step reading a different leaf than its neighbour (the map <c>LogoUrlByTheme</c> against the
    /// plain <c>LogoUrl</c>).
    /// <para>
    /// A GATE (SPEC-012 §10.3) is stated through the hook as well — it returns the layer value, so it
    /// returns <see cref="LayerValue{T}.Set(T, bool)"/> where the level opens one. That is why no
    /// separate member of the declaration carries a gate address.
    /// </para>
    /// </summary>
    public Func<ConfigNode, string, ConfigDimensionValues, LayerValue<T>>? Parse { get; }

    /// <summary>
    /// Which addresses inside ONE record of a level the setting is stated at, given the node of that
    /// record and the address of the level; null — the address of the level is the only one there is.
    /// <para>
    /// It answers the question a WALK of the snapshot asks and a resolution never does. A resolution
    /// follows the fallback chain to one value, so an address per step is all it needs; a walk has to
    /// name everything the level states, and a level that keeps a per-channel map beside a plain member
    /// (SPEC-012 CFG-237) states as many values as the map has entries. Only the key knows where that
    /// map lives — the same knowledge <see cref="Parse"/> carries for the reading of one step — so
    /// leaving the walk without it would silently narrow the report down to the plain member.
    /// </para>
    /// </summary>
    public Func<ConfigNode, string, IEnumerable<string>>? Stated { get; }

    /// <summary>
    /// What the CORE level answers where its record states NOTHING at the address;
    /// <see cref="LayerValue{T}.None"/> — the owner declared no such answer, and the level then cedes
    /// like any other.
    /// <para>
    /// It is DATA and not a hook, and that is the whole point of it: a hook can only be applied by the
    /// path of reading that calls it, so a value stated by one was invisible to the walk of a snapshot
    /// — which then reported that the level states nothing, over a level that always speaks. Stated
    /// here, the same fact is applied by both paths from one declaration.
    /// </para>
    /// <para>
    /// The answer belongs to the core level alone because that is the level whose record ALWAYS
    /// exists — it is the application configuration of the host. Above the core a level that states
    /// nothing cedes to the level below, and an answer supplied there would take that descent away.
    /// </para>
    /// </summary>
    public LayerValue<T> Default { get; }

    /// <summary>
    /// A level stating a BLANK text — empty or whitespace — states nothing at all, and the resolution
    /// moves on to the level below. It is the feature detection a half-finished edit needs: an empty
    /// string left behind in a record is not an override of the value under it.
    /// <para>
    /// Declarable on a key of a text value only, which is checked where the declaration is closed:
    /// "blank" is defined for a text and for nothing else.
    /// </para>
    /// </summary>
    public bool BlankIsUnstated { get; }

    /// <summary>
    /// What a level whose record EXISTS answers, without reading the address at all;
    /// <see cref="LayerValue{T}.None"/> — the owner declared no such answer.
    /// <para>
    /// It is the key whose value is the FACT that the record is there — the presence of a record the
    /// request selected — rather than anything written inside it. The address of such a level names a
    /// member the record is known to hold, so that the declaration points at something real, and
    /// nothing is read out of it.
    /// </para>
    /// </summary>
    public LayerValue<T> PresenceMeans { get; }

    /// <summary>
    /// How ONE address of a record is read into the value of the setting, in place of the
    /// configuration binder; null — the binder reads it.
    /// <para>
    /// It is filled by a declarative member that implies a reading rather than written by the owner as
    /// code (<see cref="ConfigKeyBuilder{T}.EnumTokens{TEnum}"/>), which is what tells it apart from
    /// <see cref="Parse"/>: a parse hook reads a STEP of the chain whole — several members of a record,
    /// a gate beside the value — and only the path that calls it can apply it, while this reads ONE
    /// address and both paths apply it alike.
    /// </para>
    /// </summary>
    public Func<ConfigNode, string, LayerValue<T>>? Reading { get; }

    /// <inheritdoc />
    public override void Accept(IConfigKeyDeclarationVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);

        visitor.Visit(this);
    }

    /// <summary>
    /// What ONE address of a record states, read the way the DECLARATION of the key says it is read.
    /// It is the single place that reading lives, and both paths ask it — the resolution of one step
    /// (<see cref="PathConfigKeyRegistrar"/>) and the walk of a snapshot
    /// (<see cref="ConfigValueCatalog{T}"/>) — so a declared member cannot mean one thing to one of
    /// them and another to the other.
    /// <para>
    /// What is NOT here is what belongs to the level rather than to the address:
    /// <see cref="Default"/> is the answer of a level whose record said nothing anywhere, and
    /// <see cref="PresenceMeans"/> is an answer about the record itself. Each path applies those two
    /// where its own unit of walking is — a step of the chain, a record of the level.
    /// </para>
    /// </summary>
    /// <param name="node">Record of the level.</param>
    /// <param name="path">Address inside the record.</param>
    /// <returns>Value stated at the address, or <see cref="LayerValue{T}.None"/>.</returns>
    /// <exception cref="Exception">The record states a value the read cannot take; see the callers.</exception>
    internal LayerValue<T> StatedAt(ConfigNode node, string path)
    {
        var stated = Reading is { } read
            ? read(node, path)
            : node.TryRead<T>(path, out var value) ? LayerValue<T>.Set(value) : LayerValue<T>.None;

        return stated.HasValue && StatesNothing(stated.Value!) ? LayerValue<T>.None : stated;
    }

    /// <summary>
    /// What ONE step of the chain states over a record: the parse hook of the key where it states one,
    /// the answer carried by the presence of the record, and otherwise the address read the way the
    /// declaration says (<see cref="StatedAt"/>), with the declared answer of the core level where the
    /// record said nothing there.
    /// <para>
    /// It is the single place the reading of a STEP lives, as <see cref="StatedAt"/> is for an address.
    /// The resolution asks it of the step it was handed (<see cref="PathConfigKeyRegistrar"/>), and the
    /// walk of a snapshot asks it of every point of the core level it enumerates
    /// (<see cref="DeclaredConfigValueCatalog{T}"/>) — so HOW a key is read is the key's own business
    /// and neither path has to know it, a parse hook included.
    /// </para>
    /// </summary>
    /// <param name="node">Record of the level.</param>
    /// <param name="path">Address the step resolved to, inside the record.</param>
    /// <param name="point">Point of the chain the step stands at.</param>
    /// <returns>Value the step states, before the domain of the key is asked about it.</returns>
    /// <exception cref="Exception">The record states a value the read cannot take; see the callers.</exception>
    internal LayerValue<T> StatedAtStep(ConfigNode node, string path, ConfigDimensionValues point)
    {
        if (Parse is { } parse)
        {
            return parse(node, path, point);
        }

        // The presence of the record is the whole answer where the owner declared it so: the address
        // is not what such a key is about, and nothing is read at it.
        if (PresenceMeans.HasValue)
        {
            return PresenceMeans;
        }

        var stated = StatedAt(node, path);

        // The declared answer of the core level is reached exactly where the record said nothing —
        // which includes a record silenced whole after a value of a shape the setting is not read
        // from. Above the core there is no such answer, and the level cedes to the one below.
        return stated.HasValue || node.Level is not ConfigLevel.Core ? stated : Default;
    }

    /// <summary>
    /// Whether a value the record states is, BY THE DECLARATION of the key, no statement at all.
    /// </summary>
    /// <param name="value">Value the record states.</param>
    /// <returns>true when the declaration says this is not a statement.</returns>
    private bool StatesNothing(T value) =>
        BlankIsUnstated && value is string text && string.IsNullOrWhiteSpace(text);
}

/// <summary>
/// Consumer of a key declaration that needs the TYPE of the value back: the registrar that binds the
/// key's levels, a report that prints its domain. It is the seam where the erasure of the catalog is
/// undone, and it is the only one — a consumer casting a declaration on its own would be a second
/// place deciding what the type of a key is.
/// </summary>
public interface IConfigKeyDeclarationVisitor
{
    /// <summary>
    /// Receives one declaration with the type of its value.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    void Visit<T>(DeclaredConfigKey<T> declared);
}
