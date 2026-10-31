// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Resolves the branding of a service page of the "Core" contour through the canonical resolver — the
/// preset, the color scheme, the integrator's stylesheet and the brand color, over the context of the
/// page's own transaction (SPEC-007 UI-101, SPEC-012 §10.6). It is the single consumer-side point where a
/// service page gathers its branding, so a level added to any of those keys reaches every such page
/// without a line of change here.
/// <para>
/// A page with no transaction to name a context resolves against <see cref="ResolutionContext.Core"/>
/// and gets the global design — the core level inside the very same order, not a second reading path
/// (SPEC-007 UI-090, SPEC-012 CFG-235).
/// </para>
/// <para>
/// The resolved stylesheet is stated into <see cref="CorePageResourceScope"/> as part of the same
/// call, which is why this type is per-request rather than a singleton: the CSP of the response and
/// the <c>&lt;link&gt;</c> in the markup then come from ONE resolution and cannot disagree — the
/// failure mode C-2 exists to remove (SPEC-007 UI-054, UI-040).
/// </para>
/// </summary>
public sealed class CorePageBrandingResolver
{
    /// <summary>
    /// Canonical multi-level configuration resolver.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// External resources of the page of this request (the CSP source of truth).
    /// </summary>
    private readonly CorePageResourceScope _resourceScope;

    /// <summary>
    /// Logger of the degradation path — a failure of the mechanism is silent for the visitor, so the
    /// operator has to hear about it here.
    /// </summary>
    private readonly ILogger<CorePageBrandingResolver> _logger;

    /// <summary>
    /// Point of the fallback chain asking a themed key for the LIGHT theme.
    /// </summary>
    private static readonly ConfigDimensionValues LightTheme =
        ConfigDimensionValues.Of((CorePageBrandingConfigKeys.ThemeDimensionName, CorePageThemes.Light));

    /// <summary>
    /// Point of the fallback chain asking a themed key for the DARK theme.
    /// </summary>
    private static readonly ConfigDimensionValues DarkTheme =
        ConfigDimensionValues.Of((CorePageBrandingConfigKeys.ThemeDimensionName, CorePageThemes.Dark));

    /// <summary>
    /// Creates the branding resolver of the generated service pages. Internal: the resolver is a
    /// collaborator of the contour's own pages rather than a type integrators construct, and the container
    /// builds it through <see cref="CorePageUiServiceCollectionExtensions.AddCorePageUiServices"/>.
    /// </summary>
    /// <param name="resolver">Canonical multi-level configuration resolver.</param>
    /// <param name="resourceScope">External resources of the page of this request.</param>
    /// <param name="logger">Logger of the degradation path.</param>
    internal CorePageBrandingResolver(
        IConfigurationResolver resolver,
        CorePageResourceScope resourceScope,
        ILogger<CorePageBrandingResolver> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _resourceScope = resourceScope ?? throw new ArgumentNullException(nameof(resourceScope));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolves the branding of the page for the given ownership context and states the resulting
    /// stylesheet for this request's CSP.
    /// </summary>
    /// <remarks>
    /// Never throws over branding: branding is decoration, and every page already has a value from
    /// below for it — the neutral canon of the token layer. A failure of the mechanism (a global
    /// section a deployment-side edit broke, and which is bound on its first read) therefore degrades
    /// to unbranded rather than into an exception thrown at a pipeline with no handler. The caller
    /// still gets a page — and the failure is logged, because nothing else on the request will report
    /// it.
    /// </remarks>
    /// <param name="context">Resolution context of the page's own transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective branding; all design values unset when nothing is branded or the resolution
    /// failed.</returns>
    public Task<CorePageBranding> ResolveAsync(
        ResolutionContext context,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(context, [], cancellationToken);

    /// <summary>
    /// Resolves the branding of a page that leads into the given channels: the branding of
    /// <see cref="ResolveAsync(ResolutionContext, CancellationToken)"/> together with the colors the levels
    /// state for the buttons of those channels.
    /// </summary>
    /// <param name="context">Resolution context of the page's own transaction.</param>
    /// <param name="buttonChannels">Types of the channels whose buttons the page shows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective branding.</returns>
    internal async Task<CorePageBranding> ResolveAsync(
        ResolutionContext context,
        IReadOnlyCollection<string> buttonChannels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(buttonChannels);

        CorePageCustomResource? customCss;
        DesignPreset declaredPreset;
        AuthPageTheme declaredTheme;
        string? primaryLight;
        string? primaryDark;
        string? brandName;
        IReadOnlyDictionary<string, string>? buttonColors;

        try
        {
            customCss = await ValueOfAsync(CorePageBrandingConfigKeys.CustomCss, context, ConfigDimensionValues.None, cancellationToken);
            declaredPreset = await ValueOfAsync(CorePageBrandingConfigKeys.Preset, context, ConfigDimensionValues.None, cancellationToken);
            declaredTheme = await ValueOfAsync(CorePageBrandingConfigKeys.Theme, context, ConfigDimensionValues.None, cancellationToken);
            primaryLight = await ValueOfAsync(CorePageBrandingConfigKeys.PrimaryColor, context, LightTheme, cancellationToken);
            primaryDark = await ValueOfAsync(CorePageBrandingConfigKeys.PrimaryColor, context, DarkTheme, cancellationToken);
            brandName = await ValueOfAsync(CorePageBrandingConfigKeys.BrandName, context, ConfigDimensionValues.None, cancellationToken);
            buttonColors = await ChannelButtonColorsAsync(context, buttonChannels, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal request cancellation — propagate, this is not a branding failure.
            throw;
        }
        catch (Exception exception)
        {
            // The visitor sees an unbranded but working page, and the declaration below also silences
            // the middleware's own fallback for this request — the operator hears about the failure
            // only from here.
            _logger.LogWarning(
                exception,
                "The branding of this page could not be resolved; the page is rendered unbranded and "
                + "its content security policy without an external origin.");

            _resourceScope.Declare(stylesheetPath: null);

            return new CorePageBranding();
        }

        // The preset rule, the theme rule and the brand-color rule are the contour's, shared with the
        // sign-in window: a custom stylesheet forces the base preset, the Dark preset darkens a page
        // whose color scheme is Auto, and only a Branded page honours the client color.
        var preset = CorePageBrandingRules.EffectivePreset(declaredPreset, customCss?.Path);

        _resourceScope.Declare(customCss?.Path);

        return new CorePageBranding
        {
            BrandName = brandName,
            CustomCssPath = customCss?.Path,
            CssSriHash = customCss?.SriHash,
            PrimaryColor = CorePageBrandingRules.BrandPrimary(preset, primaryLight),
            PrimaryColorDark = CorePageBrandingRules.BrandPrimary(preset, primaryDark),
            Theme = CorePageBrandingRules.EffectiveTheme(declaredTheme, preset),
            ChannelButtonColors = buttonColors
        };
    }

    /// <summary>
    /// Resolves the colors the levels state for the buttons of the given channels — for a page that
    /// assembles its branding by itself (the sign-in window) rather than through
    /// <see cref="ResolveAsync(ResolutionContext, IReadOnlyCollection{string}, CancellationToken)"/>. Never
    /// throws over the colors, for the reason the branding does not: a failure leaves every button the color
    /// of its channel and is logged.
    /// </summary>
    /// <param name="context">Resolution context of the page's own transaction.</param>
    /// <param name="channelTypes">Types of the channels whose buttons the page shows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Colors by channel type; null — no level states one.</returns>
    internal async Task<IReadOnlyDictionary<string, string>?> ResolveChannelButtonColorsAsync(
        ResolutionContext context,
        IReadOnlyCollection<string> channelTypes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(channelTypes);

        try
        {
            return await ChannelButtonColorsAsync(context, channelTypes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The colors of the channel buttons of this page could not be resolved; every button keeps "
                + "the color of its channel.");

            return null;
        }
    }

    /// <summary>
    /// Resolves the color of the button of every given channel, leaving out the channels no level states one
    /// for.
    /// </summary>
    /// <param name="context">Resolution context.</param>
    /// <param name="channelTypes">Channel types.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Colors by channel type; null — none stated.</returns>
    private async Task<IReadOnlyDictionary<string, string>?> ChannelButtonColorsAsync(
        ResolutionContext context,
        IReadOnlyCollection<string> channelTypes,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string>? colors = null;
        foreach (var channelType in channelTypes.Distinct(StringComparer.Ordinal))
        {
            var color = await ValueOfAsync(
                CorePageBrandingConfigKeys.ChannelButtonColor,
                context,
                ConfigDimensionValues.Of((ConfigDimensionNames.Channel, channelType)),
                cancellationToken);

            if (color is not null)
            {
                (colors ??= new Dictionary<string, string>(StringComparer.Ordinal))[channelType] = color;
            }
        }

        return colors;
    }

    /// <summary>
    /// Resolves the effective value of one key at a point of its fallback chain.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <param name="context">Resolution context.</param>
    /// <param name="dimensions">Point of the key's fallback chain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective value.</returns>
    private async ValueTask<T> ValueOfAsync<T>(
        ConfigKey<T> key,
        ResolutionContext context,
        ConfigDimensionValues dimensions,
        CancellationToken cancellationToken) =>
        (await _resolver.ResolveAsync(key, context, dimensions, cancellationToken)).Value;
}
