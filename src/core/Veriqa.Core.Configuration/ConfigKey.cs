// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.Configuration;

/// <summary>
/// Declarative setting key for the canonical resolver (SPEC-012 §10.3).
/// Carries the name, the semantics (<see cref="ConfigKeyKind"/>) and — for composite
/// semantics — level-combining functions (intersection for sets, picking the stricter
/// value for protective ones). Registered once; based on it the resolver applies
/// the precedence, narrowing and protective rules of SPEC-012 §10.3 without hardcoding by setting name.
/// <para>
/// The BOUNDARIES of the setting are data on the key as well: <see cref="Levels"/> states which
/// levels may populate it, <see cref="Dimensions"/> states what it may be asked about,
/// <see cref="Domain"/> states which values it admits at all, and
/// <see cref="CachePolicy"/> states the parameters of its cache entry. Widening the boundaries of a
/// setting therefore costs an edit of this declaration plus an extraction binding at the owner of
/// the new level's source — and no edit of any source or reader class (SPEC-012 §10.4).
/// </para>
/// </summary>
/// <typeparam name="T">Type of the setting value.</typeparam>
public sealed class ConfigKey<T>
{
    /// <summary>
    /// Creates a setting key.
    /// </summary>
    private ConfigKey(
        string name,
        ConfigKeyKind kind,
        IReadOnlySet<ConfigLevel> levels,
        Func<T, T, T>? intersect,
        Func<T, T, T>? stricter,
        IReadOnlySet<ConfigLevel>? gatedLevels,
        bool coreParticipatesInSetIntersection,
        bool isSecret,
        ConfigCachePolicy? cachePolicy,
        ConfigKeyDimensions? dimensions,
        ConfigValueDomain<T>? domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(levels);

        if (levels.Count == 0)
        {
            throw new ArgumentException($"Configuration key '{name}' must declare at least one level.", nameof(levels));
        }

        Name = name;
        Kind = kind;
        Levels = levels;
        Intersect = intersect;
        Stricter = stricter;
        GatedLevels = gatedLevels;
        CoreParticipatesInSetIntersection = coreParticipatesInSetIntersection;
        IsSecret = isSecret;
        CachePolicy = cachePolicy ?? ConfigCachePolicy.None;
        Dimensions = dimensions;
        Domain = domain;

        Dimensions?.Validate(name);
        ValidateGatedLevels();
        ValidateCachePolicy();
    }

    /// <summary>
    /// Setting name (for diagnostics/logs; not used for branching logic).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Level-combining semantics.
    /// </summary>
    public ConfigKeyKind Kind { get; }

    /// <summary>
    /// Levels that MAY populate the key. The order of application is given by
    /// <see cref="ConfigLevel"/> precedence, not by this set. A level outside the set is not queried
    /// for this key at all — neither its source nor its reader is touched.
    /// </summary>
    public IReadOnlySet<ConfigLevel> Levels { get; }

    /// <summary>
    /// Caching policy of the value; the default is <see cref="ConfigCachePolicy.None"/>.
    /// </summary>
    public ConfigCachePolicy CachePolicy { get; }

    /// <summary>
    /// Dimensions of the key; <c>null</c> — the key is not composite (a degenerate chain of one step).
    /// </summary>
    public ConfigKeyDimensions? Dimensions { get; }

    /// <summary>
    /// Domain of the value; <c>null</c> — the setting admits every value of its type. A value outside
    /// the domain leaves its step of the chain unset wherever the key is bound, and is named to the
    /// operator once per configuration snapshot rather than once per resolution
    /// (<see cref="ConfigValueDomain{T}"/>).
    /// </summary>
    public ConfigValueDomain<T>? Domain { get; }

    /// <summary>
    /// Intersection function for two sets (for <see cref="ConfigKeyKind.Set"/>):
    /// (upper, lower) → what the lower level allows within the upper one (SPEC-012 §10.3).
    /// </summary>
    public Func<T, T, T>? Intersect { get; }

    /// <summary>
    /// Function picking the stricter value (for <see cref="ConfigKeyKind.ProtectiveCeiling"/>):
    /// (upper, lower) → the stricter of the two (SPEC-012 §10.3). Applied when the upper level
    /// has NOT issued a gate; when a gate is present, the lower level's value is taken as is.
    /// </summary>
    public Func<T, T, T>? Stricter { get; }

    /// <summary>
    /// Levels whose override requires a gate (for <see cref="ConfigKeyKind.GatedValue"/>):
    /// a level from the set overrides the value only when a lower level's gate is open;
    /// levels outside the set override freely (Value semantics). Null — all levels are free.
    /// </summary>
    public IReadOnlySet<ConfigLevel>? GatedLevels { get; }

    /// <summary>
    /// Role of the CORE level in set intersection (only for <see cref="ConfigKeyKind.Set"/>).
    /// <para>
    /// <c>true</c> (default) — core participates in the intersection as a limiting allowlist ceiling:
    /// lower levels may only narrow the set specified by core (the SPEC-017 ICC-081 contract for
    /// <c>InitiatorContext.DisplayFields</c> — the application narrows the tenant/core set but does not
    /// go beyond what core allows).
    /// </para>
    /// <para>
    /// <c>false</c> — core is NOT a ceiling over the TENANT: a set the tenant states replaces the core one.
    /// This is how <c>channels_enabled</c> opts out, where the core's global set is a default, not a
    /// ceiling over the tenant (otherwise an empty global set would wipe out the tenant's one — the
    /// narrowing defect of SPEC-012 §10.3). While the tenant is silent, a core set that allows at least one
    /// member is the base of the narrowing: the levels below the tenant (application and further) narrow
    /// it by intersection and cannot state more than it allows (SPEC-012 CFG-162, CFG-248). An empty core
    /// set, an unstated one and one whose read failed with no last valid value are no base — the levels
    /// below then intersect among themselves; when no level states a set, the core one is the value as it
    /// is, the degenerate N=1 default (self-hosted ≡ core, SPEC-012 §10.1 / SPEC-003 CA-170).
    /// </para>
    /// </summary>
    public bool CoreParticipatesInSetIntersection { get; }

    /// <summary>
    /// Declaration this key was built from; null when the key was built by a factory of its own rather
    /// than by a catalog, and therefore declares nothing beyond the boundaries carried here.
    /// <para>
    /// It is the way a member of the DECLARATION reaches a seam that is handed the key alone — the one
    /// object every consumer of a setting holds. Reading such a member off the container instead (the
    /// catalogs a deployment registered) makes the answer depend on the COMPOSITION: a contour that
    /// reads a key whose catalog another contour registers would find no declaration at all and act as
    /// if the owner had declared nothing, which is precisely the silence a declared guarantee must not
    /// have (<see cref="RequiredConfigValues"/>).
    /// </para>
    /// </summary>
    internal DeclaredConfigKey<T>? Declaration { get; private set; }

    /// <summary>
    /// The value of this setting is a secret (a channel credential and the like). Secrecy is a property
    /// of the KEY — the nature of the value, identical in every contour — and it governs two things:
    /// the resolver never puts the value into diagnostics, and a last valid value kept for degradation
    /// stops being served once it outlives the maximum age allowed for a secret
    /// (<see cref="ConfigResolutionOptions.SecretLastGoodMaxAge"/>): a secret may have been revoked
    /// while its source was unreachable.
    /// </summary>
    public bool IsSecret { get; }

    /// <summary>
    /// Creates a plain-value key (SPEC-012 §10.3): the first value specified by precedence wins.
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="levels">Levels that may populate the key.</param>
    /// <param name="isSecret">The value is a secret (see <see cref="IsSecret"/>).</param>
    /// <param name="cachePolicy">Caching policy; null — not cached.</param>
    /// <param name="dimensions">Dimensions of a composite key; null — not composite.</param>
    /// <param name="domain">Domain of the value; null — every value of the type is admitted.</param>
    /// <returns>Setting key.</returns>
    public static ConfigKey<T> Value(
        string name,
        IReadOnlySet<ConfigLevel> levels,
        bool isSecret = false,
        ConfigCachePolicy? cachePolicy = null,
        ConfigKeyDimensions? dimensions = null,
        ConfigValueDomain<T>? domain = null) =>
        new(name, ConfigKeyKind.Value, levels, intersect: null, stricter: null, gatedLevels: null,
            coreParticipatesInSetIntersection: true, isSecret, cachePolicy, dimensions, domain);

    /// <summary>
    /// Creates a set key (SPEC-012 §10.3): the effective value is the intersection of the levels.
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="levels">Levels that may populate the key.</param>
    /// <param name="intersect">Intersection function (upper, lower) → allowed.</param>
    /// <param name="coreParticipatesInSetIntersection">
    /// Role of the core level in the intersection (see <see cref="CoreParticipatesInSetIntersection"/>).
    /// Default <c>true</c> — core limits the set as an allowlist ceiling (ICC-081 contract).
    /// Pass <c>false</c> for a set where the core's set is no ceiling over the tenant — a tenant set
    /// replaces it — yet stays the base the levels below the tenant narrow while the tenant is silent
    /// (e.g. <c>channels_enabled</c>, CA-170).
    /// </param>
    /// <param name="cachePolicy">Caching policy; null — not cached.</param>
    /// <param name="dimensions">Dimensions of a composite key; null — not composite.</param>
    /// <param name="domain">Domain of the value; null — every value of the type is admitted.</param>
    /// <returns>Setting key.</returns>
    public static ConfigKey<T> Set(
        string name,
        IReadOnlySet<ConfigLevel> levels,
        Func<T, T, T> intersect,
        bool coreParticipatesInSetIntersection = true,
        ConfigCachePolicy? cachePolicy = null,
        ConfigKeyDimensions? dimensions = null,
        ConfigValueDomain<T>? domain = null) =>
        new(name, ConfigKeyKind.Set, levels, intersect, stricter: null, gatedLevels: null,
            coreParticipatesInSetIntersection, isSecret: false, cachePolicy, dimensions, domain);

    /// <summary>
    /// Creates a protective ceiling key (SPEC-012 §10.3): downwards only stricter,
    /// relaxation only via an upper-level gate.
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="levels">Levels that may populate the key.</param>
    /// <param name="stricter">Function picking the stricter value (upper, lower) → stricter.</param>
    /// <param name="cachePolicy">Caching policy; null — not cached.</param>
    /// <param name="dimensions">Dimensions of a composite key; null — not composite.</param>
    /// <param name="domain">Domain of the value; null — every value of the type is admitted.</param>
    /// <returns>Setting key.</returns>
    public static ConfigKey<T> ProtectiveCeiling(
        string name,
        IReadOnlySet<ConfigLevel> levels,
        Func<T, T, T> stricter,
        ConfigCachePolicy? cachePolicy = null,
        ConfigKeyDimensions? dimensions = null,
        ConfigValueDomain<T>? domain = null) =>
        new(name, ConfigKeyKind.ProtectiveCeiling, levels, intersect: null, stricter, gatedLevels: null,
            coreParticipatesInSetIntersection: true, isSecret: false, cachePolicy, dimensions, domain);

    /// <summary>
    /// Creates a value key with controlled override (SPEC-012 §10.3, decision #3):
    /// levels from <paramref name="gatedLevels"/> override only via a lower level's gate,
    /// the rest — freely. A level that is not allowed to override at all is simply
    /// not declared in <paramref name="levels"/> — then it does not participate in
    /// resolution (that is how the ui_config record is kept away from
    /// <c>AuthPageDesign.CustomJs</c>: the key does not declare that level at all).
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="levels">Levels that may populate the key.</param>
    /// <param name="gatedLevels">
    /// Levels whose override requires a gate; each of them must be among <paramref name="levels"/>.
    /// </param>
    /// <param name="cachePolicy">Caching policy; null — not cached.</param>
    /// <param name="dimensions">Dimensions of a composite key; null — not composite.</param>
    /// <param name="domain">Domain of the value; null — every value of the type is admitted.</param>
    /// <returns>Setting key.</returns>
    public static ConfigKey<T> GatedValue(
        string name,
        IReadOnlySet<ConfigLevel> levels,
        IReadOnlySet<ConfigLevel> gatedLevels,
        ConfigCachePolicy? cachePolicy = null,
        ConfigKeyDimensions? dimensions = null,
        ConfigValueDomain<T>? domain = null) =>
        new(name, ConfigKeyKind.GatedValue, levels, intersect: null, stricter: null, gatedLevels,
            coreParticipatesInSetIntersection: true, isSecret: false, cachePolicy, dimensions, domain);

    /// <summary>
    /// Ties the key to the declaration it was built from. It is called once, by that declaration, at
    /// the moment the chain of the builder is closed — a key exists for a declaration or for no
    /// declaration at all, and a second one would mean two declarations claiming one key.
    /// </summary>
    /// <param name="declaration">Declaration built around this key.</param>
    /// <exception cref="InvalidOperationException">The key is already declared.</exception>
    internal void DeclaredAs(DeclaredConfigKey<T> declaration)
    {
        if (Declaration is not null)
        {
            throw new InvalidOperationException(
                $"Configuration key '{Name}' is claimed by a second declaration: a key carries the one "
                + "declaration it was built from, and which of the two it carried would depend on the order "
                + "of the code.");
        }

        Declaration = declaration;
    }

    /// <summary>
    /// Checks that every gated level is a level the key actually declares. A gate over a level outside
    /// <see cref="Levels"/> is never consulted — that level is not even queried — so such a declaration
    /// promises a control that does not exist, and it says so at startup instead of looking inert in
    /// production.
    /// </summary>
    /// <exception cref="InvalidOperationException">A gated level is not among the declared levels.</exception>
    private void ValidateGatedLevels()
    {
        if (GatedLevels is null)
        {
            return;
        }

        foreach (var level in GatedLevels)
        {
            if (!Levels.Contains(level))
            {
                throw new InvalidOperationException(
                    $"Configuration key '{Name}' declares level {level} as gated, but does not declare it " +
                    $"among the levels that may populate it: gated levels are {{{string.Join(", ", GatedLevels)}}}, " +
                    $"declared levels are {{{string.Join(", ", Levels)}}}.");
            }
        }
    }

    /// <summary>
    /// Checks that the declared caching policy is expressible for the declared levels: a key
    /// populated per request or per user cannot be cached at all, because a complete cache-entry key
    /// would have to include the content of the request itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">A per-request or per-user key declared a TTL.</exception>
    private void ValidateCachePolicy()
    {
        if (CachePolicy.Ttl is null)
        {
            return;
        }

        if (Levels.Contains(ConfigLevel.PerRequest) || Levels.Contains(ConfigLevel.UserOverride))
        {
            throw new InvalidOperationException(
                $"Configuration key '{Name}' declares a cache lifetime together with the per-request or " +
                "user-override level: such a value cannot be cached, because the cache entry key would " +
                "have to include the content of the request.");
        }
    }
}
