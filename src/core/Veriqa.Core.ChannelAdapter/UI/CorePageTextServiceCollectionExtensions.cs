// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Registration of the overridable texts of the core pages (<see cref="CorePageTextConfigKeys.Texts"/>).
/// </summary>
public static class CorePageTextServiceCollectionExtensions
{
    /// <summary>
    /// Contributes texts of an owner's core pages to the set an entry of
    /// <see cref="CorePageTextConfigKeys.Texts"/> may override. Every owner of core pages calls it for its
    /// own texts, and the contributions are united; a text contributed twice is overridable once.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="naturalKeys">Built-in Natural Keys of the owner's page elements.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// A text is blank or carries the path separator of the configuration: no level could address an
    /// entry for it.
    /// </exception>
    public static IServiceCollection AddCorePageOverridableTexts(
        this IServiceCollection services,
        IEnumerable<string> naturalKeys) =>
        services.AddCorePageOverridableTexts(naturalKeys, optionalSlots: []);

    /// <summary>
    /// Contributes texts of an owner's core pages to the set an entry of
    /// <see cref="CorePageTextConfigKeys.Texts"/> may override, together with the slots that are optional in
    /// them: the owner's page fills each of those slots in each of these texts whenever a text carries it,
    /// so a replacement may carry any of them, all of them or none — and may name with one slot what the
    /// built-in text names with another. The other slots of the texts have to match exactly, as for every
    /// overridable text.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="naturalKeys">Built-in Natural Keys of the owner's page elements.</param>
    /// <param name="optionalSlots">Slots in their written form (<c>{app}</c>) the page fills in every one of
    /// these texts.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// A text is blank or carries the path separator of the configuration: no level could address an
    /// entry for it; or an optional slot is not one slot in its written form.
    /// </exception>
    public static IServiceCollection AddCorePageOverridableTexts(
        this IServiceCollection services,
        IEnumerable<string> naturalKeys,
        IEnumerable<string> optionalSlots)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(naturalKeys);
        ArgumentNullException.ThrowIfNull(optionalSlots);

        IReadOnlyList<string> contributed = [.. naturalKeys];
        IReadOnlyList<string> optional = [.. optionalSlots];

        foreach (var slot in optional)
        {
            if (slot is null || !CorePageTextSlots.IsSlot(slot))
            {
                throw new ArgumentException(
                    $"'{slot}' cannot be an optional slot: it has to be one slot in its written form, such as "
                    + "'{app}'.",
                    nameof(optionalSlots));
            }
        }

        foreach (var naturalKey in contributed)
        {
            if (string.IsNullOrWhiteSpace(naturalKey) || naturalKey.Contains(ConfigNode.PathSeparator))
            {
                throw new ArgumentException(
                    $"Text '{naturalKey}' cannot be made overridable: an entry for it is addressed by the text "
                    + $"itself, and no level addresses a blank one or one carrying '{ConfigNode.PathSeparator}'.",
                    nameof(naturalKeys));
            }
        }

        services.AddSingleton(new CorePageOverridableTextContribution(contributed) { OptionalSlots = optional });
        services.TryAddSingleton<CorePageOverridableTexts>();

        return services;
    }

    /// <summary>
    /// Declares <see cref="CorePageTextConfigKeys.Texts"/> and registers its snapshot catalogs, one per
    /// level of the key.
    /// <para>
    /// The catalogs are ADDED rather than TryAdded: an enumerable registration is deduplicated by
    /// implementation type, and every level is the same type — so a TryAdd would keep the first level and
    /// drop the others. What must not repeat is the pair "setting + level", which the snapshot report
    /// refuses to start with twice; so the catalogs are registered under the same guard as the catalog of
    /// the key, and a composition that runs twice registers them once.
    /// </para>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration of the host.</param>
    /// <returns>The service collection for chaining.</returns>
    internal static IServiceCollection AddCorePageTextKey(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services.Any(descriptor => ReferenceEquals(descriptor.ImplementationInstance, CorePageTextConfigKeys.Catalog)))
        {
            return services;
        }

        services.AddVeriqaConfigKeyCatalog(configuration, CorePageTextConfigKeys.Catalog);
        services.TryAddSingleton<CorePageOverridableTexts>();

        foreach (var (level, section) in CorePageTextConfigKeys.Levels)
        {
            services.Add(ServiceDescriptor.Singleton<IConfigSnapshotCatalog>(provider => new CorePageTextSnapshotCatalog(
                level,
                section,
                provider.GetServices<IConfigNodeWalker>().FirstOrDefault(walker => walker.Level == level),
                provider.GetRequiredService<CorePageOverridableTexts>())));
        }

        return services;
    }
}
