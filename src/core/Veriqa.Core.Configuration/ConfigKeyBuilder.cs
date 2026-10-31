// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.Configuration;

/// <summary>
/// Declaration of one key as a single chain (SPEC-012 §10.6): its name and value type, its dimensions,
/// the levels it lives at with the address of each, and the semantics it already had — the kind, the
/// domain, the cache policy, secrecy.
/// <para>
/// The builder BUILDS a <see cref="ConfigKey{T}"/> and does not replace it: every property above is
/// handed to the factory a key is created by, so the mechanism sees the same declaration a direct
/// factory call gives it and resolution does not depend on which of the two was used. What the chain
/// removes is the repetition around it — without it the levels of a key would be stated once by the
/// key, once by a binding per level and once by a field per level of every record.
/// </para>
/// <example>
/// A key on three levels with two dimensions, whole:
/// <code>
/// public static readonly ConfigKey&lt;string?&gt; SignText = Declared
///     .Text("AuthPage.SignText")
///     .Narrowing("transaction_type", "outcome")
///     .At(ConfigLevel.Application, ConfigLevel.UiConfig)
///     .At(ConfigLevel.Core, "Veriqa:AuthPageDesign:SignText:[ByType:{transaction_type}]")
///     .Cached(ConfigCachePolicy.ExternalStore)
///     .Declare();
/// </code>
/// A key whose steps read DIFFERENT leaves — the map for the step that addresses the theme, the plain
/// field for the step that addresses nothing — states it with the parse hook, over the helper that
/// reads the map entry by entry (<see cref="DimensionedConfigValue"/>):
/// <code>
/// public static readonly ConfigKey&lt;string?&gt; LogoUrl = Declared
///     .Text("AuthPageDesign.LogoUrl")
///     .Narrowing("theme")
///     .At(ConfigLevel.Application, ConfigLevel.UiConfig)
///     .Parse(static (node, _, point) =&gt;
///         DimensionedConfigValue.Layer(node, point, "theme", "LogoUrlByTheme", "LogoUrl"))
///     .NotWalked()
///     .Declare();
/// </code>
/// </example>
/// </summary>
/// <typeparam name="T">Type of the setting value.</typeparam>
public sealed class ConfigKeyBuilder<T>
{
    /// <summary>
    /// Catalog the finished declaration goes into.
    /// </summary>
    private readonly ConfigKeyCatalog _catalog;

    /// <summary>
    /// Setting name.
    /// </summary>
    private readonly string _name;

    /// <summary>
    /// Narrowing dimensions in the order of their priority.
    /// </summary>
    private readonly List<string> _narrowing = [];

    /// <summary>
    /// Levels of the key with the address of each, in the order they were stated.
    /// </summary>
    private readonly List<LevelDeclaration> _levels = [];

    /// <summary>
    /// Identity dimension of the key; null — the key has none.
    /// </summary>
    private string? _identity;

    /// <summary>
    /// Handwritten fallback chain; null — the chain is generated from the dimensions.
    /// </summary>
    private IReadOnlyList<IReadOnlySet<string>>? _fallback;

    /// <summary>
    /// Level-combining semantics of the key.
    /// </summary>
    private ConfigKeyKind _kind = ConfigKeyKind.Value;

    /// <summary>
    /// Intersection function of a set key.
    /// </summary>
    private Func<T, T, T>? _intersect;

    /// <summary>
    /// Function picking the stricter value of a protective key.
    /// </summary>
    private Func<T, T, T>? _stricter;

    /// <summary>
    /// Levels whose override requires a gate.
    /// </summary>
    private IReadOnlySet<ConfigLevel>? _gatedLevels;

    /// <summary>
    /// Role of the core level in the intersection of a set key.
    /// </summary>
    private bool _coreParticipatesInSetIntersection = true;

    /// <summary>
    /// The value of the key is a secret.
    /// </summary>
    private bool _isSecret;

    /// <summary>
    /// Caching policy of the value; null — not cached.
    /// </summary>
    private ConfigCachePolicy? _cachePolicy;

    /// <summary>
    /// Domain of the value; null — every value of the type is admitted.
    /// </summary>
    private ConfigValueDomain<T>? _domain;

    /// <summary>
    /// Parse hook of the key; null — the value is read by the configuration binder.
    /// </summary>
    private Func<ConfigNode, string, ConfigDimensionValues, LayerValue<T>>? _parse;

    /// <summary>
    /// Stated-addresses hook of the key; null — the address of the level is the only one.
    /// </summary>
    private Func<ConfigNode, string, IEnumerable<string>>? _stated;

    /// <summary>
    /// Answer of the core level over a record stating nothing; unset — none is declared.
    /// </summary>
    private LayerValue<T> _default;

    /// <summary>
    /// A level stating a blank text states nothing at all.
    /// </summary>
    private bool _blankIsUnstated;

    /// <summary>
    /// Answer of a level whose record exists; unset — none is declared.
    /// </summary>
    private LayerValue<T> _presenceMeans;

    /// <summary>
    /// How one address of a record is read into the value; null — by the configuration binder.
    /// </summary>
    private Func<ConfigNode, string, LayerValue<T>>? _reading;

    /// <summary>
    /// Enum whose tokens the value is written as; null — the value is not read as a token.
    /// </summary>
    private Type? _enumTokens;

    /// <summary>
    /// Tokens the declared dictionary admits; null — the key declares no dictionary.
    /// </summary>
    private IReadOnlyList<string>? _admittedTokens;

    /// <summary>
    /// A value of the key must be stated by some level.
    /// </summary>
    private bool _isRequired;

    /// <summary>
    /// The walk of a configuration snapshot does not reproduce the value of this key.
    /// </summary>
    private bool _notWalked;

    /// <summary>
    /// Opens the declaration of a key.
    /// </summary>
    /// <param name="catalog">Catalog the finished declaration goes into.</param>
    /// <param name="name">Setting name.</param>
    internal ConfigKeyBuilder(ConfigKeyCatalog catalog, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _catalog = catalog;
        _name = name;
    }

    /// <summary>
    /// States the IDENTITY dimension — the one without which the question loses its meaning ("the hint
    /// of WHICH channel"). It is addressed by every step of the fallback chain and never drops out of
    /// it (SPEC-036 §4.6), which is what tells it apart from a narrowing attribute; a handwritten chain
    /// breaking that is refused at declaration.
    /// </summary>
    /// <param name="dimension">Name of the dimension.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states an identity dimension.</exception>
    public ConfigKeyBuilder<T> Identity(string dimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);

        if (_identity is not null)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states two identity dimensions ('{_identity}' and '{dimension}'): " +
                "what the setting is asked ABOUT is one thing, and the second of them is a narrowing attribute.");
        }

        _identity = dimension;

        return this;
    }

    /// <summary>
    /// States the NARROWING attributes in the order of their priority, most significant first. The
    /// fallback chain follows from them: they drop out of it one by one from the least significant,
    /// and the last step keeps the identity dimension alone (or nothing at all, for a key without
    /// one).
    /// </summary>
    /// <param name="dimensions">Names of the dimensions, most significant first.</param>
    /// <returns>Builder for chaining.</returns>
    public ConfigKeyBuilder<T> Narrowing(params string[] dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);

        foreach (var dimension in dimensions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dimension);

            _narrowing.Add(dimension);
        }

        return this;
    }

    /// <summary>
    /// States a HANDWRITTEN fallback chain in place of the generated one — for a key that tries a
    /// subset of the combinations rather than all of them. Each step is a subset of the key's
    /// dimensions, applied as a whole; the steps are tried in the order they are given.
    /// </summary>
    /// <param name="steps">Steps of the chain, most specific first.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states a fallback chain.</exception>
    public ConfigKeyBuilder<T> Fallback(params string[][] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        IReadOnlyList<IReadOnlySet<string>> chain =
            [.. steps.Select(step => (IReadOnlySet<string>)new HashSet<string>(step, StringComparer.Ordinal))];

        StateOnce(ref _fallback, chain, "a fallback chain");

        return this;
    }

    /// <summary>
    /// States levels whose address FOLLOWS FROM THE NAME of the key: the dots of the name become the
    /// separators of the path, so <c>AuthPage.Title</c> is read at <c>AuthPage:Title</c> inside the
    /// record of each of these levels.
    /// <para>
    /// A derived address carries no dimension substitution, so for a composite key every step of the
    /// chain reads the SAME leaf and the first of them decides. That is exactly what a level which
    /// does not cut its value by the dimension means; a level that does cut it states the cut in an
    /// explicit address (<see cref="At(ConfigLevel, string)"/>) or in the parse hook.
    /// </para>
    /// </summary>
    /// <param name="levels">Levels the key lives at.</param>
    /// <returns>Builder for chaining.</returns>
    public ConfigKeyBuilder<T> At(params ConfigLevel[] levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        foreach (var level in levels)
        {
            Add(level, ConfigLevelAddressKind.DerivedFromName, ConfigAddressTemplate.DerivedFrom(_name));
        }

        return this;
    }

    /// <summary>
    /// States a level whose address is written EXPLICITLY — where the deployed form of the
    /// configuration does not match the name of the key, and an installation has to keep reading what
    /// it read before. The address is relative to the record of the level; only the core level, whose
    /// record is the application configuration of the host, is addressed from the root
    /// (SPEC-012 §10.1).
    /// </summary>
    /// <param name="level">Level the key lives at.</param>
    /// <param name="path">Address inside the record of the level.</param>
    /// <returns>Builder for chaining.</returns>
    public ConfigKeyBuilder<T> At(ConfigLevel level, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Add(level, ConfigLevelAddressKind.Path, path);

        return this;
    }

    /// <summary>
    /// States a level that has NO path: it is addressed by the identity of the key and the point of
    /// the chain alone. That is what a store keeping values as rows "level × owner × key × dimensions"
    /// needs — there is no document to walk into, so neither a derived nor an explicit path means
    /// anything there.
    /// </summary>
    /// <param name="level">Level the key lives at.</param>
    /// <returns>Builder for chaining.</returns>
    public ConfigKeyBuilder<T> AtIdentity(ConfigLevel level)
    {
        Add(level, ConfigLevelAddressKind.Identity, path: null);

        return this;
    }

    /// <summary>
    /// States how the value is read out of the record's subtree, in place of the configuration binder.
    /// The hook is handed the node of the level, the address the step resolved to and the point of the
    /// chain — the same point the resolver hands a handwritten binding today.
    /// <para>
    /// It exists for the two shapes an address cannot describe: one value standing over several
    /// members of the record (a path together with its SRI hash), and a step reading a different leaf
    /// than its neighbour (the map <c>LogoUrlByTheme</c> against the plain <c>LogoUrl</c> — see the
    /// example on this type). A gate (SPEC-012 §10.3) is stated here as well, by returning
    /// <see cref="LayerValue{T}.Set(T, bool)"/>.
    /// </para>
    /// </summary>
    /// <param name="parse">Reading of the value: node, resolved address, point of the chain.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states a parse hook.</exception>
    public ConfigKeyBuilder<T> Parse(Func<ConfigNode, string, ConfigDimensionValues, LayerValue<T>> parse)
    {
        ArgumentNullException.ThrowIfNull(parse);

        StateOnce(ref _parse, parse, "a parse hook");

        return this;
    }

    /// <summary>
    /// States which addresses inside a record of a level the setting is STATED at, when the address of
    /// the level does not name them all. The hook is handed the node of the record and the address of
    /// the level, and it answers with every address the record may hold a value of this setting at.
    /// <para>
    /// It exists for the WALK of a configuration snapshot and changes no resolution: the reading of one
    /// step of the chain stays <see cref="Parse"/>. The two are the same knowledge asked in two
    /// directions — where a step READS its value, and everything the level STATES — and a key that
    /// keeps a per-channel map beside a plain member (SPEC-012 CFG-237) needs both: without this hook
    /// the report would name a rejected value of the plain member and stay silent about every entry of
    /// the map next to it.
    /// </para>
    /// </summary>
    /// <param name="stated">Addresses the setting is stated at: node of the record, address of the level.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states such a hook.</exception>
    public ConfigKeyBuilder<T> Stated(Func<ConfigNode, string, IEnumerable<string>> stated)
    {
        ArgumentNullException.ThrowIfNull(stated);

        StateOnce(ref _stated, stated, "the addresses its value is stated at");

        return this;
    }

    /// <summary>
    /// States what the CORE level answers where its record states NOTHING at the address — the value
    /// the product ships the setting with.
    /// <para>
    /// It is DECLARED rather than written as a hook because a hook can only be applied by the path of
    /// reading that calls it: a shipped default hidden in <see cref="Parse"/> is invisible to the walk
    /// of a snapshot, which then reports that the level states no value over a level that always
    /// speaks. Stated here it is data, and both paths — the resolution of a step and the walk — answer
    /// with it from one declaration. It is also what the SCHEMA of declared keys carries, so everything
    /// that asks the product what it declares gets the same answer.
    /// </para>
    /// <para>
    /// The answer is the CORE level's alone: that is the level whose record always exists. Above the
    /// core a level that states nothing cedes to the level below, and answering there would take that
    /// descent away.
    /// </para>
    /// </summary>
    /// <param name="value">Value the core level answers with; null is a value like any other.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states such an answer.</exception>
    public ConfigKeyBuilder<T> Default(T value)
    {
        if (_default.HasValue)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states the answer of its core level twice: the second statement " +
                "would silently replace the first.");
        }

        _default = LayerValue<T>.Set(value);

        return this;
    }

    /// <summary>
    /// States that a level stating a BLANK text — empty or made of whitespace — states nothing at all,
    /// so the resolution moves on to the level below. A half-finished edit that left an empty string
    /// behind is not an override.
    /// <para>
    /// Declarable on a key of a TEXT value only, and refused on any other where the chain is closed:
    /// "blank" is defined for a text and for nothing else.
    /// </para>
    /// </summary>
    /// <returns>Builder for chaining.</returns>
    public ConfigKeyBuilder<T> BlankIsUnstated()
    {
        _blankIsUnstated = true;

        return this;
    }

    /// <summary>
    /// States what a level whose record EXISTS answers, without reading the address at all — for the
    /// key whose value is the FACT that the record is there rather than anything written inside it.
    /// The address of such a level still names a member the record is known to hold, so that the
    /// declaration points at something real.
    /// </summary>
    /// <param name="value">Value a level with a record answers with.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states such an answer.</exception>
    public ConfigKeyBuilder<T> PresenceMeans(T value)
    {
        if (_presenceMeans.HasValue)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states what the presence of a record means twice: the second " +
                "statement would silently replace the first.");
        }

        _presenceMeans = LayerValue<T>.Set(value);

        return this;
    }

    /// <summary>
    /// States that the value is written as the TOKEN of a member of <typeparamref name="TEnum"/>,
    /// derived from the names of the members by the one rule of the mechanism
    /// (<see cref="ConfigEnumTokens"/>).
    /// <para>
    /// It is for the key whose tokens are written in a convention the standard binder of the platform
    /// does not read — that binder knows a member by its own name and by nothing else. Declaring the
    /// tokens spares the owner of such a key a class of constants, a map in each direction, a parse of
    /// its own and a list of the admitted tokens for messages. Every one of those
    /// is derived from the members, and a member added to the enum brings its token with it.
    /// </para>
    /// <para>
    /// The member is opt-in and changes nothing for a key that does not state it: an enum key without
    /// it is read by the binder, by the names of its members.
    /// </para>
    /// <para>
    /// A level states the value in either spelling that names a member — the canonical token or the
    /// name of the member (<see cref="ConfigEnumTokens.TryParse{TEnum}"/>) — and a level that states
    /// NEITHER has stated a value the read cannot take, which the reading says out loud instead of
    /// passing off as silence: the level did write something there, and only a read that says so lets
    /// the diagnostics of the mechanism name the address rather than tell an operator that nobody
    /// stated a value.
    /// </para>
    /// </summary>
    /// <typeparam name="TEnum">Enum whose members the value names.</typeparam>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states how its value is read.</exception>
    public ConfigKeyBuilder<T> EnumTokens<TEnum>()
        where TEnum : struct, Enum
    {
        if (_reading is not null)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states how its value is read twice: the second statement would " +
                "silently replace the first.");
        }

        // The token names a member of TEnum, and the value of the key is that member — either as it is
        // or lifted into a nullable of it, which is how a key says "no level declared a value". Both are
        // the same member once boxed, so one reading serves the two and the declaration needs no second
        // member for the nullable case.
        _reading = static (node, path) =>
        {
            if (!node.TryRead<string>(path, out var stated))
            {
                return LayerValue<T>.None;
            }

            if (ConfigEnumTokens.TryParse<TEnum>(stated, out var member))
            {
                return LayerValue<T>.Set((T)(object)member);
            }

            // The level STATES something at the address and it names no member. Answering "the level
            // states nothing" here would make the mechanism assert the one thing that is false about
            // such a level, and it is the assertion an operator is then shown: the refusal of a start
            // tells them to write a value at an address that already holds one. It is raised instead —
            // the boundary of the reading path (PathConfigKeyRegistrar) catches it, names the address
            // and answers it as any value it cannot read (CFG-240), and the walk of a snapshot reports
            // the address as holding a value it cannot read. The message names what may be written and
            // never what was: a stated value never enters diagnostics.
            throw new InvalidOperationException(
                $"The value stated at '{path}' names no member of '{typeof(TEnum)}'. Admitted values: "
                + string.Join(", ", ConfigEnumTokens.All<TEnum>()) + ".");
        };

        _enumTokens = typeof(TEnum);

        // Derived here rather than at the first message: a dictionary two members of which collide is a
        // defect of the enum, and it is worth failing where the key is declared — at the start of the
        // host — instead of inside the refusal that was about to print it.
        _admittedTokens = ConfigEnumTokens.All<TEnum>();

        return this;
    }

    /// <summary>
    /// States that a value of this setting MUST be stated by some level: a deployment where no level
    /// states one does not start.
    /// <para>
    /// Nothing else in a declaration says this. <see cref="Domain"/> judges a value that was already
    /// read, and the startup report of the mechanism compares declarations with bindings — neither of
    /// them looks at whether a value exists. An owner who needed it was left writing the pass itself:
    /// a nullable type declared for the sake of the absence, a loop over the objects the setting is
    /// asked about, and a refusal message of their own.
    /// </para>
    /// <para>
    /// The mechanism answers "did ANY level state one" and nothing beyond it. WHEN the question is
    /// worth asking stays with the owner (a setting is only owed by an object that is switched on, on
    /// this profile, of this kind) — otherwise a shipped configuration that deliberately says nothing
    /// about the objects it does not use would stop starting. And a key with a DIMENSION is asked per
    /// value of that dimension, which the owner supplies: the mechanism keeps no list of channels and
    /// no list of tenants, and it must not start keeping one.
    /// </para>
    /// </summary>
    /// <returns>Builder for chaining.</returns>
    public ConfigKeyBuilder<T> Required()
    {
        _isRequired = true;

        return this;
    }

    /// <summary>
    /// States that the WALK of a configuration snapshot does not reproduce the value of this key: what
    /// a level states here is assembled by the parse hook, and a hook belongs to the path of reading
    /// that calls it. The report of the snapshot then NAMES the pairs "this key + each level it is
    /// bound at" as standing outside it.
    /// <para>
    /// It is declared and not derived because only the owner can tell the two cases apart: a hook may
    /// read the very member the address names, in which case the walk reproduces the value perfectly
    /// well (<see cref="Stated"/> beside such a hook), and calling that pair uncovered would drop a
    /// check the report could have made. What the mechanism does enforce is that the question is
    /// ANSWERED: a key that states a hook and declares no domain — and therefore gets no snapshot
    /// catalog of its own — must say which of the two it is, so that the third state, silence over a
    /// setting a level does state, cannot be reached by forgetting.
    /// </para>
    /// </summary>
    /// <returns>Builder for chaining.</returns>
    public ConfigKeyBuilder<T> NotWalked()
    {
        _notWalked = true;

        return this;
    }

    /// <summary>
    /// Declares SET semantics (SPEC-012 §10.3): the effective value is the intersection of the levels,
    /// so a lower level does not widen what the upper one allows.
    /// </summary>
    /// <param name="intersect">Intersection function (upper, lower) → allowed.</param>
    /// <param name="coreParticipatesInSetIntersection">
    /// Role of the core level in the intersection (see
    /// <see cref="ConfigKey{T}.CoreParticipatesInSetIntersection"/>).
    /// </param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states level-combining semantics.</exception>
    public ConfigKeyBuilder<T> Set(Func<T, T, T> intersect, bool coreParticipatesInSetIntersection = true)
    {
        ArgumentNullException.ThrowIfNull(intersect);

        StateKind(ConfigKeyKind.Set);

        _intersect = intersect;
        _coreParticipatesInSetIntersection = coreParticipatesInSetIntersection;

        return this;
    }

    /// <summary>
    /// Declares PROTECTIVE CEILING semantics (SPEC-012 §10.3): downwards only stricter, relaxation
    /// only through the gate of an upper level.
    /// </summary>
    /// <param name="stricter">Function picking the stricter value (upper, lower) → stricter.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states level-combining semantics.</exception>
    public ConfigKeyBuilder<T> ProtectiveCeiling(Func<T, T, T> stricter)
    {
        ArgumentNullException.ThrowIfNull(stricter);

        StateKind(ConfigKeyKind.ProtectiveCeiling);

        _stricter = stricter;

        return this;
    }

    /// <summary>
    /// Declares CONTROLLED OVERRIDE semantics (SPEC-012 §10.3): the listed levels override the value
    /// only where the level below has opened a gate; the rest override freely.
    /// </summary>
    /// <param name="gatedLevels">Levels whose override requires a gate.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states level-combining semantics.</exception>
    public ConfigKeyBuilder<T> GatedValue(params ConfigLevel[] gatedLevels)
    {
        ArgumentNullException.ThrowIfNull(gatedLevels);

        StateKind(ConfigKeyKind.GatedValue);

        _gatedLevels = new HashSet<ConfigLevel>(gatedLevels);

        return this;
    }

    /// <summary>
    /// States the DOMAIN of the value — which values the setting admits at all, and what a value
    /// outside it costs (SPEC-012 §10.6, CFG-240).
    /// <para>
    /// A key of an ENUM gets a domain of the members even without this member
    /// (<see cref="DefaultDomainOfAnEnum"/>); a domain stated here wins over that default, so an owner
    /// who needs a narrower boundary or the strict policy states it and nothing of the default remains.
    /// </para>
    /// </summary>
    /// <param name="domain">Domain of the value.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states a domain.</exception>
    public ConfigKeyBuilder<T> Domain(ConfigValueDomain<T> domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        StateOnce(ref _domain, domain, "a domain of the value");

        return this;
    }

    /// <summary>
    /// States the caching policy of the value.
    /// </summary>
    /// <param name="policy">Caching policy.</param>
    /// <returns>Builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">The key already states a caching policy.</exception>
    public ConfigKeyBuilder<T> Cached(ConfigCachePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        StateOnce(ref _cachePolicy, policy, "a caching policy");

        return this;
    }

    /// <summary>
    /// States that the value is a SECRET (see <see cref="ConfigKey{T}.IsSecret"/>).
    /// </summary>
    /// <returns>Builder for chaining.</returns>
    public ConfigKeyBuilder<T> Secret()
    {
        _isSecret = true;

        return this;
    }

    /// <summary>
    /// Closes the chain: builds the key, puts the declaration into the catalog of its owner and hands
    /// the key back. A chain that is never closed declares nothing — the key it would have built does
    /// not exist, and no level of it is bound.
    /// </summary>
    /// <returns>Setting key.</returns>
    /// <exception cref="InvalidOperationException">The declaration contradicts itself.</exception>
    public ConfigKey<T> Declare()
    {
        RefuseContradictoryAnswers();

        var dimensions = BuildDimensions();
        var levels = ConfigLevels.Of([.. _levels.Select(level => level.Level)]);

        // The default domain reaches the key as an ARGUMENT and is deliberately not written back into
        // _domain: the state of the builder stays what the OWNER declared, so AdmittedValuesText()
        // below keeps listing the token dictionary of a key that declared one instead of the CLR names
        // of the members — a spelling that key does not read. Making the default visible through the
        // state would take that apart, and RefuseContradictoryAnswers with it.
        var key = BuildKey(levels, dimensions, _domain ?? DefaultDomainOfAnEnum());
        var declared = new DeclaredConfigKey<T>(
            key,
            BuildAddresses(dimensions),
            _parse,
            _stated,
            _default,
            _blankIsUnstated,
            _presenceMeans,
            _reading,
            _isRequired,
            _notWalked,
            AdmittedValuesText());

        _catalog.Add(declared);

        return key;
    }

    /// <summary>
    /// Refuses a chain that states, in two ways at once, what a level answers. Every check below is one
    /// rule seen from a different side: a declaration answers a question ONCE, and a second answer to
    /// the same question does not fail — it is silently picked by the order of the code, and the
    /// declaration then reads as if it said something it does not do.
    /// </summary>
    /// <exception cref="InvalidOperationException">The declaration contradicts itself.</exception>
    private void RefuseContradictoryAnswers()
    {
        // The parse hook IS the reading of a step, whole: it decides what a record states, what a
        // silent record states and what the level answers. A declared answer beside it would be
        // applied by neither path — the hook has already answered — while the declaration would show
        // both.
        // The token names a member of the enum, so the value of the key has to BE that member — as it
        // is, or lifted into a nullable of it. On any other type the reading could not hand its answer
        // back, and the declaration would promise a convention nothing applies.
        if (_enumTokens is { } tokens
            && typeof(T) != tokens
            && typeof(T) != typeof(Nullable<>).MakeGenericType(tokens))
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states the tokens of '{tokens}' while declaring a value of type " +
                $"'{typeof(T)}': a token names a member of that enum, so the value of the key is the member or a " +
                "nullable of it.");
        }

        if (_parse is not null
            && (_default.HasValue || _blankIsUnstated || _presenceMeans.HasValue || _reading is not null))
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states a parse hook together with a declared member saying what a " +
                "level answers or how its value is read: the hook reads a step of the chain whole, so the " +
                "declared member would never be reached — and a declaration is read as what a level does.");
        }

        // An answer of the core level is given whenever the record says nothing, so the setting is
        // never unstated — there would be no deployment for the requirement to refuse, and the
        // declaration would state a guarantee that can never fire.
        if (_isRequired && (_default.HasValue || _presenceMeans.HasValue))
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' requires a value to be stated while declaring an answer of its " +
                "own: the declared answer is always given, so no deployment could ever fail the requirement.");
        }

        // "The walk does not reproduce this value" is a statement about the HOOK: without one the value
        // is read at the address the declaration names, which is exactly what a walk reads.
        if (_notWalked && _parse is null)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states that the walk of a snapshot does not reproduce its value " +
                "while declaring no parse hook: without a hook the value is read at the address of the level, " +
                "and that is the reading a walk performs.");
        }

        // A DOMAIN is what puts a key into the walk — a snapshot catalog is registered for the pairs of
        // a key that declares one — so "the pair is outside the report" would be a second answer to the
        // question the domain has already answered.
        if (_notWalked && _domain is not null)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states that the walk of a snapshot does not reproduce its value " +
                "while declaring a domain: a key with a domain is walked, and the report names the pairs of it " +
                "the walk does not reach on its own.");
        }

        // The third state the report must not have: a key whose value a hook assembles and whose pairs
        // no snapshot catalog covers — the report would be silent over a setting a level does state.
        // The mechanism cannot tell whether such a hook is reproducible at the address, so it refuses
        // the silence instead of guessing, and the owner states the answer.
        if (_parse is not null && _domain is null && !_notWalked)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states a parse hook and no domain, and says nothing about the walk " +
                "of a snapshot: no catalog covers its pairs, so the report would be silent over a level that " +
                $"does state a value. State {nameof(NotWalked)}() where the walk cannot reproduce it.");
        }

        if (_default.HasValue && _presenceMeans.HasValue)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states both what the presence of a record means and what its core " +
                "level answers over a record stating nothing: the record of the core level always exists, so the " +
                "second answer could never be given.");
        }

        // "Blank" is a property of a text. On a key of any other value type the statement has nothing
        // to hold of, and a declaration nothing applies is a declaration that misleads its reader.
        if (_blankIsUnstated && typeof(T) != typeof(string))
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states that a blank text is no statement while declaring a value of " +
                $"type '{typeof(T)}': blank is a property of a text and of nothing else.");
        }

        // The answer belongs to the core level, so a key that has no core level has nowhere to give it:
        // the chain would carry an answer no level of it can ever reach.
        if (_default.HasValue && !_levels.Exists(level => level.Level is ConfigLevel.Core))
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states what its core level answers while declaring no core level: " +
                "there is no level for the answer to be given at.");
        }
    }

    /// <summary>
    /// Remembers a level of the key together with the form of its address.
    /// </summary>
    /// <param name="level">Level the key lives at.</param>
    /// <param name="kind">Form of the address.</param>
    /// <param name="path">Address as it is written; null for a level without one.</param>
    /// <exception cref="InvalidOperationException">The level is stated twice.</exception>
    private void Add(ConfigLevel level, ConfigLevelAddressKind kind, string? path)
    {
        if (_levels.Exists(declared => declared.Level == level))
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states level {level} twice: which of the two addresses the level " +
                "is read at would depend on the order of the calls.");
        }

        _levels.Add(new LevelDeclaration(level, kind, path));
    }

    /// <summary>
    /// Remembers the LEVEL-COMBINING semantics of the key, refusing a second statement of them: how the
    /// levels of a key combine is one property, and a chain stating two of them says two different
    /// things about the same key. Kept alongside the key would be whichever call came last — and a
    /// <see cref="Set"/> silently turned into a <see cref="GatedValue"/> stops intersecting the levels,
    /// so a lower level widens what an upper one allows and the declaration reads as if it did not.
    /// </summary>
    /// <param name="kind">Semantics the chain states.</param>
    /// <exception cref="InvalidOperationException">The key already states level-combining semantics.</exception>
    private void StateKind(ConfigKeyKind kind)
    {
        if (_kind is not ConfigKeyKind.Value)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states {_kind} semantics together with {kind}: how the levels of a " +
                "key combine is one thing, and the second of them would silently replace the first.");
        }

        _kind = kind;
    }

    /// <summary>
    /// Remembers a property the key states AT MOST ONCE, refusing a second statement of it: the second
    /// would replace the first without a word, and which of the two the key ends up with would depend
    /// on the order of the calls.
    /// </summary>
    /// <typeparam name="TProperty">Type of the property.</typeparam>
    /// <param name="property">Field the property is kept in.</param>
    /// <param name="value">Value the chain states.</param>
    /// <param name="what">The property, as the message of a refusal names it.</param>
    /// <exception cref="InvalidOperationException">The property is already stated.</exception>
    private void StateOnce<TProperty>(ref TProperty? property, TProperty value, string what)
        where TProperty : class
    {
        if (property is not null)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' states {what} twice: the second statement would silently replace " +
                "the first.");
        }

        property = value;
    }

    /// <summary>
    /// The values the key ADMITS, as a message naming them prints them; null when the declaration
    /// names none and a message therefore has nothing to list. Two members of a declaration can answer
    /// it — the domain of the value states its boundary in words, and a declared token dictionary IS
    /// the list of what may be written — and where both are stated the domain wins: it is the wider
    /// statement, the one a value is actually judged against.
    /// </summary>
    /// <returns>Text listing the admitted values; null when the declaration names none.</returns>
    private string? AdmittedValuesText()
    {
        const string TokenSeparator = " / ";

        if (_domain is { } domain)
        {
            return domain.Boundary;
        }

        return _admittedTokens is { } tokens ? string.Join(TokenSeparator, tokens) : null;
    }

    /// <summary>
    /// Builds the dimensions of the key: the identity dimension first (it addresses every step), the
    /// narrowing attributes after it in the order of their priority. The chain is generated from them
    /// unless the owner wrote one by hand.
    /// </summary>
    /// <returns>Dimensions of the key, or null when it declares none.</returns>
    /// <exception cref="InvalidOperationException">A chain was written for a key without dimensions, or one of its steps drops the identity dimension.</exception>
    private ConfigKeyDimensions? BuildDimensions()
    {
        List<string> names = _identity is null ? [] : [_identity];
        names.AddRange(_narrowing);

        if (names.Count == 0)
        {
            if (_fallback is not null)
            {
                throw new InvalidOperationException(
                    $"Configuration key '{_name}' states a fallback chain while declaring no dimension: the steps " +
                    "of a chain are subsets of the dimensions, and there are none to take a subset of.");
            }

            return null;
        }

        var chain = _fallback ?? GenerateFallback();

        RefuseAStepDroppingTheIdentity(chain);

        return new ConfigKeyDimensions { Names = names, Fallback = chain };
    }

    /// <summary>
    /// Refuses a chain whose step does not address the IDENTITY dimension (SPEC-036 §4.6: it "stands on
    /// every step and is not written out"). The identity IS the question the setting answers — "the text
    /// of WHICH message", "the policy of WHICH channel" — so a step dropping it asks about something
    /// else, and the value it matches belongs to another subject. Left unchecked, that is silent: the
    /// step matches, the resolution answers, and nothing tells the caller the answer is not about what
    /// was asked.
    /// <para>
    /// The GENERATED chain holds this by construction; the check exists for the HANDWRITTEN one, which
    /// is where the rule can be broken — and handwritten is exactly what a key whose ladder the
    /// generator cannot express has to be.
    /// </para>
    /// </summary>
    /// <param name="chain">Steps of the chain about to become the dimensions of the key.</param>
    /// <exception cref="InvalidOperationException">A step does not address the identity dimension.</exception>
    private void RefuseAStepDroppingTheIdentity(IReadOnlyList<IReadOnlySet<string>> chain)
    {
        if (_identity is not { } identity)
        {
            return;
        }

        for (var step = 0; step < chain.Count; step++)
        {
            if (!chain[step].Contains(identity))
            {
                throw new InvalidOperationException(
                    $"Configuration key '{_name}' states a fallback step (#{step + 1}) that does not address its " +
                    $"identity dimension '{identity}': the identity is the question the setting answers, so such a " +
                    "step asks about something else and would answer with another subject's value. Every step of a " +
                    "handwritten chain names the identity dimension.");
            }
        }
    }

    /// <summary>
    /// Generates the FULL ladder of the fallback chain: the narrowing attributes drop out of it one by
    /// one from the least significant, and the identity dimension stays on every step. A key that
    /// needs a subset of these steps states the chain itself.
    /// </summary>
    /// <returns>Steps of the chain, most specific first.</returns>
    private IReadOnlyList<IReadOnlySet<string>> GenerateFallback()
    {
        var steps = new List<IReadOnlySet<string>>(_narrowing.Count + 1);

        for (var kept = _narrowing.Count; kept >= 0; kept--)
        {
            var step = new HashSet<string>(StringComparer.Ordinal);

            if (_identity is not null)
            {
                step.Add(_identity);
            }

            for (var i = 0; i < kept; i++)
            {
                step.Add(_narrowing[i]);
            }

            // A key with an identity dimension has no "for everything else" step: the last step keeps
            // the identity alone, and for a key without one it is the empty step.
            steps.Add(step);
        }

        return steps;
    }

    /// <summary>
    /// Builds the key itself through the very factory it has always been created by, so the mechanism
    /// receives the declaration it has always received.
    /// </summary>
    /// <param name="levels">Levels that may populate the key.</param>
    /// <param name="dimensions">Dimensions of the key.</param>
    /// <param name="domain">Domain of the value — the declared one, or the default of the type.</param>
    /// <returns>Setting key.</returns>
    /// <exception cref="InvalidOperationException">The declaration contradicts itself.</exception>
    private ConfigKey<T> BuildKey(
        IReadOnlySet<ConfigLevel> levels,
        ConfigKeyDimensions? dimensions,
        ConfigValueDomain<T>? domain)
    {
        // Secrecy is a property only a plain-value key carries: the other three kinds combine the
        // values of several levels, and there is no factory to hand it to. Dropping it silently would
        // turn a declared secret into a value the diagnostics may print.
        if (_isSecret && _kind is not ConfigKeyKind.Value)
        {
            throw new InvalidOperationException(
                $"Configuration key '{_name}' declares its value a secret together with {_kind} semantics, which " +
                "carries no secrecy: a secret is a plain value of one level.");
        }

        return _kind switch
        {
            ConfigKeyKind.Set => ConfigKey<T>.Set(
                _name, levels, _intersect!, _coreParticipatesInSetIntersection, _cachePolicy, dimensions, domain),
            ConfigKeyKind.ProtectiveCeiling => ConfigKey<T>.ProtectiveCeiling(
                _name, levels, _stricter!, _cachePolicy, dimensions, domain),
            ConfigKeyKind.GatedValue => ConfigKey<T>.GatedValue(
                _name, levels, _gatedLevels!, _cachePolicy, dimensions, domain),
            _ => ConfigKey<T>.Value(_name, levels, _isSecret, _cachePolicy, dimensions, domain)
        };
    }

    /// <summary>
    /// The domain a key of an ENUM has where its owner declares none: the members of the enumeration
    /// and nothing else. It exists because neither of the two readings of such a value answers "is this
    /// a member" — the configuration binder and <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>
    /// both take an arbitrary number, so <c>Mode: 7</c> becomes a member that does not exist and
    /// travels to the consumer unnamed by any level of diagnostics. Declared here, at the one seam
    /// every key passes through, the boundary reaches every enum axis at once instead of being copied
    /// into the catalog of each owner.
    /// <para>
    /// It is also what puts such a key into the WALK of a configuration snapshot at all
    /// (a catalog is registered for the pairs of a key that declares a domain), so a value outside the
    /// enumeration is reported with its key, level, record and the rule it broke.
    /// </para>
    /// <para>
    /// The policy is the default one (SPEC-012 CFG-240): the step of the level that stated such a value
    /// is left unset and the resolution goes on. A key whose owner wants the strict answer declares its
    /// own domain, and the declared one always wins.
    /// </para>
    /// <para>
    /// Three shapes stay outside it. A FLAG enum, because <see cref="Enum.IsDefined(Type, object)"/>
    /// answers <c>false</c> for a lawful combination of flags and the default would then reject correct
    /// values. A key that reads its value through a PARSE HOOK, because the hook is the reading of a
    /// step whole and such a key states about the walk of a snapshot itself — a domain appearing under
    /// it would contradict <see cref="NotWalked"/>. And a key whose value type is not an enum at all,
    /// a collection of enums included: the boundary here is "a member", and a set has none.
    /// </para>
    /// </summary>
    /// <returns>Domain of the members of the enum; null when the type is not one this applies to.</returns>
    private ConfigValueDomain<T>? DefaultDomainOfAnEnum()
    {
        var enumType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (_parse is not null || !enumType.IsEnum || enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            return null;
        }

        // A nullable key admits the absence of a value: the domain is applied to the outcome of EVERY
        // level, and "this level stated nothing" is not a rejection — refusing it would turn the
        // silence of a level into a rejected value.
        return new ConfigValueDomain<T>(
            value => value is null || Enum.IsDefined(enumType, value),
            "one of: " + string.Join(", ", Enum.GetNames(enumType)));
    }

    /// <summary>
    /// Parses the address of every level against the dimensions of the key. A malformed address is
    /// refused here — while the declaration is being built — rather than at a resolution that quietly
    /// found nothing.
    /// </summary>
    /// <param name="dimensions">Dimensions of the key.</param>
    /// <returns>Levels with their parsed addresses.</returns>
    /// <exception cref="InvalidOperationException">An address is malformed, or a core address names no section.</exception>
    private IReadOnlyList<ConfigLevelAddress> BuildAddresses(ConfigKeyDimensions? dimensions)
    {
        var names = new HashSet<string>(dimensions?.Names ?? [], StringComparer.Ordinal);
        var addresses = new List<ConfigLevelAddress>(_levels.Count);

        foreach (var (level, kind, path) in _levels)
        {
            if (kind is ConfigLevelAddressKind.Identity)
            {
                addresses.Add(new ConfigLevelAddress(level, kind, template: null));

                continue;
            }

            var template = ConfigAddressTemplate.Parse(_name, path!, names);

            // The core level has no record: its node stands over a SECTION of the host configuration,
            // and the address therefore names that section and the path inside it. An address of one
            // segment would name a section and nothing within it — there would be nothing to read.
            if (level is ConfigLevel.Core && template.MinimumSegments < 2)
            {
                throw new InvalidOperationException(
                    $"Configuration key '{_name}' addresses the core level at '{template.Text}': the core level is " +
                    "the application configuration of the host, so its address names a section and the path of the " +
                    "value inside it.");
            }

            addresses.Add(new ConfigLevelAddress(level, kind, template));
        }

        return addresses;
    }

    /// <summary>
    /// One level of the key as the chain stated it, before the address is parsed.
    /// </summary>
    /// <param name="Level">Level the key lives at.</param>
    /// <param name="Kind">Form of the address.</param>
    /// <param name="Path">Address as it is written; null for a level without one.</param>
    private readonly record struct LevelDeclaration(ConfigLevel Level, ConfigLevelAddressKind Kind, string? Path);
}
