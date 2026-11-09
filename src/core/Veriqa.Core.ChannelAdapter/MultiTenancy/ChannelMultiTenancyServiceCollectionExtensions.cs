// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// Registration of the channel track multi-tenancy seam (SPEC-003 §17.4, CFG-202/234/235).
/// Registers the canonical TASK-040 resolver (the single source of a tenant's channel settings and
/// of the <c>channels_enabled</c> capability), the channel client factory with a per-tenant cache
/// and the Core getters of the channel keys. All without a
/// mandatory external dependency (invariant-1): the default = degenerate N=1. The tenant-level
/// Cloud provider is added opt-in on top (behind the NuGet boundary) — this code does not change (invariant-2).
/// </summary>
public static class ChannelMultiTenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the channel multi-tenancy contract seam and the core default implementations.
    /// Idempotent (<c>TryAdd</c>): a repeated call does not multiply registrations.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddChannelMultiTenancy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Canonical layer resolver (the only one in the core) — the basis of credential/capability
        // resolution — and the Core getter of channels_enabled in the core-level provider.
        services.AddChannelCoreConfigKeys();

        // Default source of active polling tenants (N=1 — degenerate, TASK-051).
        // The Cloud profile supplies the multi-tenant implementation opt-in behind the NuGet boundary (CA-172) — this code does not change.
        services.TryAddSingleton<IPollingTenantSource, ResolverPollingTenantSource>();

        // Standard platform cache (TASK-050): the factory's negative cache (#9). AddMemoryCache is idempotent.
        services.AddMemoryCache();

        // Per-client-type state of the factory: the index of the registered builders and the per-tenant
        // cache of the clients built from them. Open generic — one singleton per client type.
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ChannelClientRegistry<>), typeof(ChannelClientRegistry<>)));

        // Channel client factory with a per-tenant cache + the failure-path negative cache (#9).
        services.TryAddSingleton<IChannelClientFactory, ChannelClientFactory>();

        // The optional dependency of the webhook routes — the resolver the tenant demux authorizes
        // against. Asking the container what it happens to hold belongs HERE, in the composition root —
        // not inside a request path.
        services.TryAddScoped(provider => new ChannelWebhookSupport(
            provider.GetService<IConfigurationResolver>()));

        return services;
    }

    /// <summary>
    /// Registers the canonical layer resolver and the Core getter of the channel contour's
    /// <c>channels_enabled</c> capability (<see cref="ChannelCoreConfigKeys"/>) — the ONE place that
    /// registers that getter. Called by the multi-tenancy seam above and by the configuration half the
    /// channel compositions share. Idempotent: the resolver guards itself, and the getter is added with
    /// <c>TryAddEnumerable</c>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    internal static IServiceCollection AddChannelCoreConfigKeys(this IServiceCollection services)
    {
        services.AddVeriqaConfigurationResolver();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRegisterConfigKeys, ChannelCoreConfigKeys>());

        return services;
    }
}
