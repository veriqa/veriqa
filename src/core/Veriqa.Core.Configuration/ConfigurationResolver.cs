// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections;
using System.Collections.Frozen;

using Microsoft.Extensions.Logging;

namespace Veriqa.Core.Configuration;

/// <summary>
/// Implementation of the canonical multi-level resolver (SPEC-012 §10.3, CFG-210/211/212).
/// It collects level values through the registered extraction bindings and combines them according to
/// the key semantics (<see cref="ConfigKeyKind"/>). It contains no branching by profile (CFG-202) and
/// no branching by key: which levels populate a setting is data on the key, and which source serves a
/// level is data of the contour.
/// </summary>
internal sealed class ConfigurationResolver : IConfigurationResolver
{
    /// <summary>
    /// Registry of the extraction bindings ("key + level" → extraction over a source record).
    /// </summary>
    private readonly ConfigBindingRegistry _bindings;

    /// <summary>
    /// Map "level → source" of the contour. It is a dependency of the resolver so that a contour whose
    /// registration is inconsistent fails at startup rather than resolving to a wrong value later.
    /// </summary>
    private readonly ConfigSourceMap _sources;

    /// <summary>
    /// Caching and degradation layer owned by the resolver: it holds the last value successfully read
    /// at each level and answers with it when a fresh read fails.
    /// </summary>
    private readonly IConfigResolutionCache _cache;

    /// <summary>
    /// What the last walk of the configuration snapshot left for the resolution: the records it threw
    /// out of the effective configuration, and how far the walk reaches at all (SPEC-012 §4.1 CFG-246).
    /// </summary>
    private readonly ConfigSnapshotEffect _snapshot;

    /// <summary>
    /// Logger of degradation facts.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the resolver.
    /// </summary>
    /// <param name="bindings">Registry of the extraction bindings.</param>
    /// <param name="sources">Map "level → source" of the contour.</param>
    /// <param name="cache">Caching and degradation layer.</param>
    /// <param name="snapshot">Effect of the last configuration snapshot walk on the resolution.</param>
    /// <param name="logger">Logger of degradation facts.</param>
    public ConfigurationResolver(
        ConfigBindingRegistry bindings,
        ConfigSourceMap sources,
        IConfigResolutionCache cache,
        ConfigSnapshotEffect snapshot,
        ILogger<ConfigurationResolver> logger)
    {
        _bindings = bindings;
        _sources = sources;
        _cache = cache;
        _snapshot = snapshot;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedValue<T>> ResolveAsync<T>(
        ConfigKey<T> key,
        ResolutionContext context,
        ConfigDimensionValues dimensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // No scope was handed in, so this resolution owns one: every level served by the same source,
        // and every step of a composite key's fallback chain, works over a record read exactly once —
        // and the reading stops at the end of this call.
        using var scope = new ConfigResolutionScope(context);

        return await ResolveAsync(key, scope, dimensions, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<ResolvedValue<T>> ResolveAsync<T>(
        ConfigKey<T> key,
        ConfigResolutionScope scope,
        ConfigDimensionValues dimensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(scope);

        dimensions.ValidateAgainst(key.Name, key.Dimensions);

        return key.Kind switch
        {
            ConfigKeyKind.Set => ResolveSetAsync(key, scope, dimensions, cancellationToken),
            ConfigKeyKind.ProtectiveCeiling => ResolveProtectiveAsync(key, scope, dimensions, cancellationToken),
            ConfigKeyKind.GatedValue => ResolveGatedValueAsync(key, scope, dimensions, cancellationToken),
            _ => ResolveValueAsync(key, scope, dimensions, cancellationToken)
        };
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedGroup<TResult>> ResolveGroupAsync<TResult>(
        ConfigKeyGroup<TResult> group,
        ResolutionContext context,
        CancellationToken cancellationToken = default)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(context);

        // The group owns the scope of its own assembly: every record serving a key of the group is
        // read once for the whole aggregate, and the reading stops when the aggregate is done.
        using var scope = new ConfigResolutionScope(context);

        return await ResolveGroupAsync(group, scope, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedGroup<TResult>> ResolveGroupAsync<TResult>(
        ConfigKeyGroup<TResult> group,
        ConfigResolutionScope scope,
        CancellationToken cancellationToken = default)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(scope);

        // A cancellation in the middle of the filling leaves the assembler through the key it was
        // resolving, so a half-filled aggregate is never returned and never checked for consistency.
        var assembled = await group.Assemble(new ConfigGroupResolution(this, scope, cancellationToken));

        if (assembled is null)
        {
            throw new InvalidOperationException(
                $"The assembler of configuration group '{group.Name}' returned no aggregate: a group states the "
                + "type of its result, and an absent result is not one of its values.");
        }

        return new ResolvedGroup<TResult>(assembled, group.Consistency?.Invoke(assembled));
    }

    /// <summary>
    /// Simple value (CFG-210): the first defined level, top to bottom by precedence, wins — and it is
    /// the level reported as the source of the effective value (CFG-232).
    /// </summary>
    private async ValueTask<ResolvedValue<T>> ResolveValueAsync<T>(
        ConfigKey<T> key,
        ConfigResolutionScope scope,
        ConfigDimensionValues dimensions,
        CancellationToken cancellationToken)
    {
        foreach (var level in ConfigLevelPrecedence.Order)
        {
            var layer = await GetLayerValueAsync(key, level, scope, dimensions, cancellationToken);
            if (layer.HasValue)
            {
                return new ResolvedValue<T>(layer.Value!, level)
                {
                    AddressedDimensions = layer.AddressedDimensions
                };
            }
        }

        // No level defined a value — edge case; the value is default(T) and no level is reported.
        return new ResolvedValue<T>(default!, null);
    }

    /// <summary>
    /// Set (CFG-211, narrowing): the tenant is the operational owner of the set; the application and lower
    /// levels narrow it by intersection; widening from below is forbidden.
    /// <para>
    /// The role of the CORE level in the intersection is defined PER KEY (<see cref="ConfigKey{T}.CoreParticipatesInSetIntersection"/>),
    /// because the core semantics differ across Set keys:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Core participates</b> (default) — core takes part in the intersection as a limiting allowlist ceiling
    /// over EVERY level, the tenant included: lower levels only narrow what core has allowed (SPEC-017 ICC-081
    /// contract for <c>InitiatorContext.DisplayFields</c> — the application narrows the tenant/core set).
    /// </description></item>
    /// <item><description>
    /// <b>Core does not participate</b> — core is no ceiling over the TENANT: a set the tenant states replaces
    /// the core one, which is what keeps an empty global core set (the normal case for a multi-tenant
    /// deployment: channels are enabled per tenant rather than globally) from zeroing out the tenant set — the
    /// CFG-211 defect for <c>channels_enabled</c>. While the tenant is silent, a core set that allows something
    /// is the BASE of the narrowing: the levels below the tenant narrow it exactly as they narrow a tenant set,
    /// so an application never states more than the core allows (SPEC-012 CFG-162, CFG-248). An empty core
    /// set, an unstated one and one whose read failed with no last valid value (CFG-240) are no base: the
    /// levels below then intersect among themselves, and when no level states a set at all the core one is
    /// the value as it is — the degenerate N=1 default (self-hosted ≡ core, CFG-202 / SPEC-003 CA-170).
    /// </description></item>
    /// </list>
    /// <para>
    /// The reported source level is the MOST SPECIFIC level that took part in the intersection: every
    /// level that declared a set influenced the outcome, and the most specific one is what an operator
    /// looks for first (CFG-232). A core base that no level below narrowed is reported as the core level.
    /// </para>
    /// </summary>
    private async ValueTask<ResolvedValue<T>> ResolveSetAsync<T>(
        ConfigKey<T> key,
        ConfigResolutionScope scope,
        ConfigDimensionValues dimensions,
        CancellationToken cancellationToken)
    {
        var intersect = key.Intersect!;
        var coreParticipates = key.CoreParticipatesInSetIntersection;

        // Intersect levels top to bottom by ownership (the reverse of precedence: core → … → user).
        // The core level of a key it does not cap is not intersected in its own step: it enters at the
        // tenant step instead, as the base of the narrowing when the tenant is silent.
        T? accumulated = default;
        var hasAccumulated = false;
        ConfigLevel? source = null;

        // Kept beside the source level and moved with it: the two are one answer about the same
        // contributor, and a set left behind by an earlier level would describe a value that is no
        // longer the one being reported.
        IReadOnlySet<string> addressed = FrozenSet<string>.Empty;

        // The core level of a key it does not cap, read at most once: at the tenant step, and only when
        // the tenant is silent — a stated tenant set replaces it without the core being asked.
        var coreLayer = LayerValue<T>.None;

        for (var i = ConfigLevelPrecedence.Order.Length - 1; i >= 0; i--)
        {
            var level = ConfigLevelPrecedence.Order[i];

            if (level == ConfigLevel.Core && !coreParticipates)
            {
                continue;
            }

            var layer = await GetLayerValueAsync(key, level, scope, dimensions, cancellationToken);
            var contributor = level;

            if (level == ConfigLevel.Tenant && !coreParticipates && !layer.HasValue)
            {
                coreLayer = await GetLayerValueAsync(key, ConfigLevel.Core, scope, dimensions, cancellationToken);

                // An empty core set is no base: as the upper side of the intersection it would zero out
                // every set stated below it.
                if (coreLayer.HasValue && AllowsSomething(coreLayer.Value))
                {
                    layer = coreLayer;
                    contributor = ConfigLevel.Core;
                }
            }

            if (!layer.HasValue)
            {
                continue;
            }

            if (!hasAccumulated)
            {
                accumulated = layer.Value;
                hasAccumulated = true;
            }
            else
            {
                // Intersection: only what is allowed by both the upper and the lower level remains allowed.
                accumulated = intersect(accumulated!, layer.Value!);
            }

            // The loop walks from the least specific level to the most specific one, so the last
            // contributor seen is the most specific one.
            source = contributor;
            addressed = layer.AddressedDimensions;
        }

        if (hasAccumulated)
        {
            return new ResolvedValue<T>(accumulated!, source) { AddressedDimensions = addressed };
        }

        // We reach here only if no level stated a set that took part. For a key whose core does not cap
        // the set, the tenant step has already read the core level (the tenant was silent, or something
        // would have accumulated) — its set, an empty one included, is the degenerate default (self-hosted
        // N=1). When coreParticipates, core is already accounted for in the loop above.
        if (!coreParticipates && coreLayer.HasValue)
        {
            return new ResolvedValue<T>(coreLayer.Value!, ConfigLevel.Core)
            {
                AddressedDimensions = coreLayer.AddressedDimensions
            };
        }

        return new ResolvedValue<T>(default!, null);
    }

    /// <summary>
    /// Whether a core set may be the base of the narrowing of a key whose core does not cap the set: it
    /// allows at least one member. A value that is not enumerable carries no notion of emptiness and is
    /// a base whenever it is stated; an absent (null) value allows nothing.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="value">Set stated by the core level.</param>
    /// <returns><c>true</c> when the set may be the base of the narrowing.</returns>
    private static bool AllowsSomething<T>(T value) => value switch
    {
        null => false,
        IEnumerable members => members.Cast<object?>().Any(),
        _ => true
    };

    /// <summary>
    /// Protective setting (CFG-212): the top owning level defines the ceiling;
    /// going down it may only get stricter, and relaxation is allowed only when the upper level opens a gate.
    /// The reported source level is the most specific level that influenced the ceiling.
    /// </summary>
    private async ValueTask<ResolvedValue<T>> ResolveProtectiveAsync<T>(
        ConfigKey<T> key,
        ConfigResolutionScope scope,
        ConfigDimensionValues dimensions,
        CancellationToken cancellationToken)
    {
        var stricter = key.Stricter!;

        T? accumulated = default;
        var hasAccumulated = false;
        ConfigLevel? source = null;
        IReadOnlySet<string> addressed = FrozenSet<string>.Empty;

        // Top to bottom by ownership (core → … → user).
        // gateOpen — the upper level allowed the lower one to relax the value (CFG-212).
        var gateOpen = false;

        for (var i = ConfigLevelPrecedence.Order.Length - 1; i >= 0; i--)
        {
            var level = ConfigLevelPrecedence.Order[i];
            var layer = await GetLayerValueAsync(key, level, scope, dimensions, cancellationToken);
            if (!layer.HasValue)
            {
                continue;
            }

            if (!hasAccumulated)
            {
                accumulated = layer.Value;
                hasAccumulated = true;
            }
            else if (gateOpen)
            {
                // The upper level opened the gate — the lower level may set any value
                // (including a more lenient one), and it is applied as is.
                accumulated = layer.Value;
            }
            else
            {
                // Gate closed — the lower level may only tighten the upper level's value.
                accumulated = stricter(accumulated!, layer.Value!);
            }

            source = level;
            addressed = layer.AddressedDimensions;

            // This level's gate applies to the next (higher-precedence) level.
            gateOpen = layer.AllowLowerOverride;
        }

        return hasAccumulated
            ? new ResolvedValue<T>(accumulated!, source) { AddressedDimensions = addressed }
            : new ResolvedValue<T>(default!, null);
    }

    /// <summary>
    /// Value with controlled override (CFG-212, decision #3): we go top to bottom by ownership;
    /// a level in the gated set overrides the accumulated value only when the lower level's gate is open;
    /// levels outside the set override freely (Value semantics). The reported source level is the level
    /// whose value actually stands at the end — a level whose override was refused did not influence it.
    /// </summary>
    private async ValueTask<ResolvedValue<T>> ResolveGatedValueAsync<T>(
        ConfigKey<T> key,
        ConfigResolutionScope scope,
        ConfigDimensionValues dimensions,
        CancellationToken cancellationToken)
    {
        var gatedLevels = key.GatedLevels;

        T? accumulated = default;
        var hasAccumulated = false;
        ConfigLevel? source = null;
        IReadOnlySet<string> addressed = FrozenSet<string>.Empty;

        // gateOpen — the lower owning level allowed the current level to override.
        var gateOpen = false;

        for (var i = ConfigLevelPrecedence.Order.Length - 1; i >= 0; i--)
        {
            var level = ConfigLevelPrecedence.Order[i];
            var layer = await GetLayerValueAsync(key, level, scope, dimensions, cancellationToken);
            if (!layer.HasValue)
            {
                continue;
            }

            if (!hasAccumulated)
            {
                accumulated = layer.Value;
                hasAccumulated = true;
                source = level;
                addressed = layer.AddressedDimensions;
            }
            else
            {
                // The level requires a gate if it is in the gated set; otherwise it overrides freely.
                var requiresGate = gatedLevels is not null && gatedLevels.Contains(level);
                if (!requiresGate || gateOpen)
                {
                    accumulated = layer.Value;
                    source = level;
                    addressed = layer.AddressedDimensions;
                }
                // Gate closed and the override requires a gate — keep the upper level's value.
            }

            // Only a level that stated a gate of its own moves the flag. A level that merely
            // supplies a value (Set without the gate argument) has no opinion about who may
            // override it further and must not revoke a gate opened below: here the gate is
            // consulted at the gated levels alone (for AuthPageDesign.CustomJs — the application),
            // so an intermediate level closing it would silently cancel the permission the owner of
            // the global section has granted.
            // ResolveProtectiveAsync deliberately keeps the unconditional assignment: there the gate is
            // consulted at EVERY level, and being consumed by the next one down is the protective
            // semantics of CFG-212 rather than the same defect.
            if (layer.DeclaresGate)
            {
                gateOpen = layer.AllowLowerOverride;
            }
        }

        return hasAccumulated
            ? new ResolvedValue<T>(accumulated!, source) { AddressedDimensions = addressed }
            : new ResolvedValue<T>(default!, null);
    }

    /// <summary>
    /// Returns the value of one level: a level the key does not declare, a level the contour does not
    /// serve and a level without a binding for this key are all "unset" and are skipped in precedence.
    /// A level the key does not declare is not even queried — neither its source nor its reader is
    /// touched.
    /// <para>
    /// A level whose read fails does not propagate the failure to the caller (CFG-233): the resolution
    /// completes with the last value successfully read at that level, and only when there is none does
    /// the level fall back to None so the resolution drops through to the levels below. A cancellation
    /// is NOT a failure of a source and is propagated as it is.
    /// </para>
    /// <para>
    /// A level ABOVE the core one answers nothing at all when the record it serves for this context was
    /// thrown out of the effective configuration by the walk of the snapshot (CFG-246): the record is
    /// gone as a whole, so every setting it stated — not only the one that was rejected — is resolved
    /// without it, exactly as for a record that did not bind.
    /// </para>
    /// </summary>
    private async ValueTask<LayerValue<T>> GetLayerValueAsync<T>(
        ConfigKey<T> key,
        ConfigLevel level,
        ConfigResolutionScope scope,
        ConfigDimensionValues dimensions,
        CancellationToken cancellationToken)
    {
        if (!key.Levels.Contains(level))
        {
            return LayerValue<T>.None;
        }

        if (level != ConfigLevel.Core && _sources.SourceOf(level) is null)
        {
            return LayerValue<T>.None;
        }

        var binding = _bindings.Find(key, level);
        if (binding is null)
        {
            return LayerValue<T>.None;
        }

        // Asked before the level is read at all: a discarded record is absent from the effective
        // configuration, and a value read out of it would be a value of a record that no longer exists.
        if (level != ConfigLevel.Core && _snapshot.IsRecordDiscarded(level, scope.Context))
        {
            return LayerValue<T>.None;
        }

        var entryKey = new ConfigCacheKey(key.Name, level, scope.Context, dimensions);

        // Decided ONCE for the level and shared with the chain below it: the chain stops at a rejected
        // step only where the level has an answer of CFG-246 to give, and both readings of that
        // question must be the same one.
        var refusesSubstitution = RefusesSubstitution(key, level);

        // The verdict of the domain is taken out of the read rather than out of the cached value: only
        // a value the setting ADMITS is ever cached, so a hit is by construction not a rejection, and a
        // miss runs the read below before this call returns.
        var rejected = false;
        var overSilentRecord = LayerValue<T>.None;

        async ValueTask<LayerValue<T>> ReadLevelAsync(CancellationToken token)
        {
            var outcome = await ApplyFallbackChainAsync(
                key,
                binding,
                scope,
                dimensions,
                refusesSubstitution,
                token);
            rejected = outcome.IsRejected;
            overSilentRecord = outcome.OverSilentRecord;

            return outcome.Value;
        }

        LayerValue<T> layer;

        try
        {
            layer = await _cache.GetOrCreateAsync(
                entryKey,
                CachePolicyFor(key, level),
                ReadLevelAsync,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLevelFailure(key, level, ex);

            return await _cache.GetLastGoodOrNoneAsync<T>(entryKey, key.IsSecret, cancellationToken);
        }

        if (rejected && refusesSubstitution)
        {
            // CFG-246 at the CORE level: there is nothing below the core to cede to, so a setting whose
            // owner refused a silent substitution is answered with the last value the level was read
            // with. At start there is none — the host does not start on such a value at all, which is
            // the snapshot report's decision, not this one — so this branch is the RELOAD that brought
            // an inadmissible value where a valid one used to stand. A reload that brought one where
            // the level stated nothing before — or where nothing ever READ the level, so no last value
            // was kept — finds none and drops the step by the default of CFG-240, which is the branch
            // below.
            var lastGood = await _cache.GetLastGoodOrNoneAsync<T>(entryKey, key.IsSecret, cancellationToken);

            if (lastGood.HasValue)
            {
                return lastGood;
            }
        }

        if (rejected && !layer.HasValue && overSilentRecord.HasValue)
        {
            // The default of CFG-240 at the CORE level: the rejected step is dropped, and with no level
            // below the core the level answers as over a record stating nothing — the value its owner
            // declared — instead of leaving the consumer with the default of the type. Only the core
            // level carries such an answer, and it is not the last value the level was READ with, so it
            // is not stored as one.
            return overSilentRecord;
        }

        if (layer.HasValue)
        {
            await _cache.StoreLastGoodAsync(entryKey, key.IsSecret, layer, cancellationToken);
        }

        return layer;
    }

    /// <summary>
    /// Whether a value rejected at this level must be answered with the last valid value of the level
    /// rather than with the level below (CFG-246). Three things have to hold at once: the owner of the
    /// key declared <see cref="ConfigValueRejectionPolicy.FailStart"/>, the value lies at the CORE
    /// level — every level above it throws the RECORD out instead, which the walk of the snapshot
    /// decides — and the snapshot report actually reaches this pair "setting + level": a level whose
    /// source does not enumerate its records is outside the report, and the alternative of CFG-240 does
    /// not cover it.
    /// <para>
    /// It is also what decides whether the fallback chain of the level stops at a rejected step
    /// (<see cref="ApplyFallbackChainAsync{T}"/>): stopping the chain is half of that answer, and a
    /// level that has no such answer must not lose the coarser steps of the chain over it — that would
    /// leave the resolution below the default of CFG-240 rather than above it.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <param name="level">Level that stated the rejected value.</param>
    /// <returns><c>true</c> when the level answers with its last valid value.</returns>
    private bool RefusesSubstitution<T>(ConfigKey<T> key, ConfigLevel level) =>
        level == ConfigLevel.Core
        && key.Domain?.OnRejected == ConfigValueRejectionPolicy.FailStart
        && _snapshot.Covers(key.Name, level);

    /// <summary>
    /// Decides the caching policy of one level of one key. Whether the level is cached AT ALL is
    /// answered by WHOEVER HOLDS THE RECORD: the READER of the level answers first
    /// (<see cref="IConfigRecordReader.IsLive"/>), and only a reader with no answer of its own leaves
    /// the question to the level's source (<see cref="IConfigSource.IsLive"/>). Live means "freshness
    /// is somebody else's job" — an application configuration reloads itself, and caching it would
    /// only hide a hot reload; a store that is not live is cached by the lifetime the key declares.
    /// <para>
    /// The reader is asked first because the source cannot answer for it: ONE reader class stands over
    /// stores that differ by contour — an options monitor in the ordinary deployment, a database in
    /// the cloud — while the level's source is the same Options source in both. Asking the source
    /// alone would make a declared lifetime silently inapplicable everywhere.
    /// </para>
    /// <para>
    /// The resolver still never inspects the type of a store and never branches by key name: it asks a
    /// contract, and the answer is data supplied by the deployment.
    /// </para>
    /// <para>
    /// The core level is pure memory and has no reader at all, so its source answers and it is never
    /// cached: there is nothing to save.
    /// </para>
    /// </summary>
    private ConfigCachePolicy CachePolicyFor<T>(ConfigKey<T> key, ConfigLevel level)
    {
        if (key.CachePolicy.Ttl is null)
        {
            return ConfigCachePolicy.None;
        }

        var reader = _bindings.ReaderOf(key.Name, level);
        var source = _sources.SourceOf(level);

        // A reader without an answer of its own defers to the source — the behaviour every reader had
        // before the question could be asked of one.
        var isLive = reader?.IsLive ?? source?.IsLive;

        if (isLive is not true)
        {
            return key.CachePolicy;
        }

        // One and the same key is served by different stores in different contours, so a declared
        // lifetime meeting a live one is not a deployment error — it is simply not applicable here.
        // Refusing to start on it would break the ordinary deployment for the sake of the cloud one.
        _logger.LogDebug(
            "Configuration key {ConfigKey} declares a cache lifetime, but level {ConfigLevel} is served live by "
            + "{ConfigSource}: the lifetime does not apply and the level is read fresh.",
            key.Name,
            level,
            reader?.IsLive is not null ? reader.Name : source?.Name);

        return ConfigCachePolicy.None;
    }

    /// <summary>
    /// Applies the fallback chain of a composite key at one level: the steps are tried top to bottom
    /// against the record already read into the scope, and the first step that declares a value wins.
    /// Specificity is exhausted INSIDE the level — when no step declares anything the level is unset
    /// and the resolution drops to the level below. A key without dimensions is the degenerate chain of
    /// one step and needs no separate code path.
    /// <para>
    /// A step stating a value the setting does not admit is skipped like an unstated one — that is the
    /// default of CFG-240 and it holds for the whole chain. The chain STOPS at such a step only where
    /// this level answers the rejection with something other than that default
    /// (<see cref="RefusesSubstitution{T}"/>): answering from a coarser step would then be exactly the
    /// quiet substitution the owner of the key declared against, and what the level answers is decided
    /// by CFG-246 instead of by the rest of the chain. Where the alternative does not reach — a level
    /// the snapshot report does not walk — the chain runs to its end, because the level has no other
    /// answer to give and cutting it short would push the resolution BELOW the default of CFG-240.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <param name="binding">Extraction binding of the level.</param>
    /// <param name="scope">Resolution scope.</param>
    /// <param name="dimensions">Dimension values of the resolution.</param>
    /// <param name="refusesSubstitution">
    /// Whether a rejection at this level is answered by CFG-246 rather than by the default of CFG-240 —
    /// the verdict of <see cref="RefusesSubstitution{T}"/>, passed in so that the chain and the level
    /// agree on one answer instead of deciding it twice.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the level states, and whether a step of it was rejected.</returns>
    private static async ValueTask<ConfigLevelOutcome<T>> ApplyFallbackChainAsync<T>(
        ConfigKey<T> key,
        ConfigBinding<T> binding,
        ConfigResolutionScope scope,
        ConfigDimensionValues dimensions,
        bool refusesSubstitution,
        CancellationToken cancellationToken)
    {
        if (key.Dimensions is not { } declared)
        {
            return await binding(scope, ConfigDimensionValues.None, cancellationToken);
        }

        var rejected = false;
        var overSilentRecord = LayerValue<T>.None;

        foreach (var step in declared.Fallback)
        {
            // The point of the step is what the step ACTUALLY addresses: a dimension the caller did
            // not ask about is projected away, so the step behaves — and is reported — as the coarser
            // one it became.
            var point = dimensions.Project(step);
            var outcome = await binding(scope, point, cancellationToken);

            if (outcome.Value.HasValue)
            {
                return outcome with { Value = outcome.Value.AtStep(AddressedBy(point)) };
            }

            if (!outcome.IsRejected)
            {
                continue;
            }

            if (refusesSubstitution)
            {
                return outcome;
            }

            rejected = true;

            // The answer of the FIRST rejected step that has one stands for the level: it is the most
            // specific point the level was asked at, and the coarser steps are still tried before it
            // is given — an admitted value of the chain wins over it.
            if (!overSilentRecord.HasValue)
            {
                overSilentRecord = outcome.OverSilentRecord;
            }
        }

        return new ConfigLevelOutcome<T>(LayerValue<T>.None, rejected) { OverSilentRecord = overSilentRecord };
    }

    /// <summary>
    /// Names of the dimensions a point of the chain addresses — the step's own subset of the key's
    /// dimensions, as it stands after the projection.
    /// </summary>
    /// <param name="point">Point of the chain.</param>
    /// <returns>Names of the addressed dimensions.</returns>
    private static IReadOnlySet<string> AddressedBy(ConfigDimensionValues point) =>
        point.Count == 0
            ? FrozenSet<string>.Empty
            : point.Values.Keys.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Reports a failed read of a level. For a key declared secret the reason is reduced to the
    /// exception type: the resolver never lets the value of such a key — or any text that may quote it
    /// — reach diagnostics; the key name, the level and the reason are what an operator needs.
    /// </summary>
    private void LogLevelFailure<T>(ConfigKey<T> key, ConfigLevel level, Exception ex)
    {
        if (key.IsSecret)
        {
            _logger.LogError(
                "Configuration key {ConfigKey} could not be read at level {ConfigLevel}: {Reason}.",
                key.Name,
                level,
                ex.GetType().Name);

            return;
        }

        _logger.LogError(
            ex,
            "Configuration key {ConfigKey} could not be read at level {ConfigLevel}.",
            key.Name,
            level);
    }
}
