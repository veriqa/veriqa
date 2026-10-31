// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.Configuration;

/// <summary>
/// Synchronous reader of CORE-level bindings, for the composition steps a framework performs
/// synchronously — the OpenIddict server options and the rate-limiter policies BEFORE the DI container
/// exists, and the mapping of routes whose need is decided once at startup. They read the core level
/// and nothing else, and the core level is pure memory (a closure over the host configuration): there
/// is no input/output there and, by the way the level is built, there cannot be.
/// <para>
/// This is NOT a resolver and not a second entry of resolution (SPEC-012 §10.6 — one
/// resolution, no fork). It has no precedence, no
/// level other than <see cref="ConfigLevel.Core"/>, none of the key semantics
/// (<see cref="ConfigKeyKind.Set"/> / <see cref="ConfigKeyKind.ProtectiveCeiling"/> /
/// <see cref="ConfigKeyKind.GatedValue"/>), no cache and no degradation. It evaluates one core
/// binding — for a composite key, at the steps of the key's own fallback chain — which is the same
/// closure the resolver itself would evaluate.
/// </para>
/// <para>
/// Everywhere an asynchronous seam exists, the single asynchronous
/// <see cref="IConfigurationResolver.ResolveAsync{T}(ConfigKey{T}, ResolutionContext, ConfigDimensionValues, CancellationToken)"/> is used instead — that is the rule, and this
/// reader is the exception the framework's synchronous composition leaves no way around.
/// </para>
/// </summary>
public sealed class ConfigCoreValues
{
    /// <summary>
    /// Registry holding the declared core bindings.
    /// </summary>
    private readonly ConfigBindingRegistry _bindings;

    /// <summary>
    /// Creates the reader over an already filled registry.
    /// </summary>
    private ConfigCoreValues(ConfigBindingRegistry bindings) => _bindings = bindings;

    /// <summary>
    /// Declares the core getters of the keys to be read and returns the reader over them.
    /// </summary>
    /// <param name="declare">Declaration of the core getters.</param>
    /// <returns>Reader of the declared core values.</returns>
    public static ConfigCoreValues Declare(Action<IConfigCoreBindings> declare)
    {
        ArgumentNullException.ThrowIfNull(declare);

        return new ConfigCoreValues(ConfigBindingRegistry.ForCoreValues([new DelegateCoreKeys(declare)]));
    }

    /// <summary>
    /// Reads the core-level value of a key.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <returns>Core-level value; <c>default</c> when the key declared no core getter.</returns>
    public T Read<T>(ConfigKey<T> key) => Read(key, ConfigDimensionValues.None);

    /// <summary>
    /// Reads the core-level value of a key at the dimension values asked — the answer the core level
    /// alone gives to them. For a composite key the steps of its fallback chain are tried in order over
    /// the one core getter, each with the point it actually addresses, and the first step that states a
    /// value answers: the same walk the resolver makes inside a level, restricted to the core one. A step
    /// whose value the domain of the key rejects states nothing here, as an unstated one does.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <param name="dimensions">
    /// Dimension values of a composite key — what is being asked; <see cref="ConfigDimensionValues.None"/>
    /// for a key without dimensions.
    /// </param>
    /// <returns>Core-level value; <c>default</c> when the key declared no core getter or no step states one.</returns>
    /// <exception cref="ArgumentException">A dimension value was passed that the key does not declare.</exception>
    public T Read<T>(ConfigKey<T> key, ConfigDimensionValues dimensions)
    {
        ArgumentNullException.ThrowIfNull(key);

        dimensions.ValidateAgainst(key.Name, key.Dimensions);

        // The core getter is a closure over global options — nothing is awaited here, because there is
        // nothing to await: the asynchronous binding of this very level is built from the same closure.
        var getter = _bindings.FindCoreValue(key);
        if (getter is null)
        {
            return default!;
        }

        // A key without dimensions is the degenerate chain of one step, asked at the point that
        // addresses nothing.
        IEnumerable<ConfigDimensionValues> points = key.Dimensions is { } declared
            ? declared.Fallback.Select(dimensions.Project)
            : [ConfigDimensionValues.None];

        foreach (var point in points)
        {
            var value = getter(point);

            if (value.HasValue)
            {
                return value.Value!;
            }
        }

        return default!;
    }

    /// <summary>
    /// Registrar built from a delegate — the reader declares its core getters inline, through the same
    /// hook a consumer assembly uses.
    /// </summary>
    /// <param name="declare">Declaration of the core getters.</param>
    private sealed class DelegateCoreKeys(Action<IConfigCoreBindings> declare) : IRegisterConfigKeys
    {
        /// <inheritdoc />
        /// <remarks>
        /// Nothing is declared into a schema here: this registrar serves the throw-away registry of a
        /// single synchronous read, which is not the schema of the deployment (see
        /// <c>ConfigBindingRegistry.ForCoreValues</c>). The keys read are the ones the caller names in
        /// the delegate above.
        /// </remarks>
        public void DeclareKeys(IConfigKeyDeclarations keys)
        {
        }

        /// <inheritdoc />
        public void Register(IConfigBindings bindings, IConfigCoreBindings coreBindings) => declare(coreBindings);
    }
}
