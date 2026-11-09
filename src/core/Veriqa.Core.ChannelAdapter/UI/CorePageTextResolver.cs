// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Resolves the overrides of the built-in texts of a core page (<see cref="CorePageTextConfigKeys.Texts"/>)
/// ONCE per render, over the context of the page's own transaction, for every overridable text — so that
/// the render itself, which is synchronous, applies them without touching a store
/// (<see cref="CorePageTexts.Localize"/>).
/// <para>
/// A page without a transaction resolves against a context that names no client: the levels of the
/// application and of the <c>ui_config</c> record are then empty, and only the tenant speaks.
/// </para>
/// </summary>
public sealed class CorePageTextResolver
{
    /// <summary>
    /// Canonical multi-level configuration resolver.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Texts the owners of the pages declared overridable.
    /// </summary>
    private readonly CorePageOverridableTexts _overridable;

    /// <summary>
    /// Logger of the degradation path — a failure of the mechanism is silent for the visitor, so the
    /// operator has to hear about it here.
    /// </summary>
    private readonly ILogger<CorePageTextResolver> _logger;

    /// <summary>
    /// Creates the resolver of the text overrides. Internal: the resolver is a collaborator of the
    /// contour's own pages rather than a type integrators construct, and the container builds it through
    /// <see cref="CorePageUiServiceCollectionExtensions.AddCorePageUiServices"/>.
    /// </summary>
    /// <param name="resolver">Canonical multi-level configuration resolver.</param>
    /// <param name="overridable">Texts the owners of the pages declared overridable.</param>
    /// <param name="logger">Logger of the degradation path.</param>
    internal CorePageTextResolver(
        IConfigurationResolver resolver,
        CorePageOverridableTexts overridable,
        ILogger<CorePageTextResolver> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _overridable = overridable ?? throw new ArgumentNullException(nameof(overridable));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolves the overrides of the page's texts for the given ownership context.
    /// </summary>
    /// <remarks>
    /// Never throws over the texts: every text has its regular translation to fall back on, so a failure
    /// of the mechanism (an unreachable <c>ui_config</c> store) degrades to the regular texts rather than
    /// into an exception thrown at a page — and is logged, because nothing else on the request reports it.
    /// All texts are resolved in ONE resolution scope, so every record is read once for the whole page.
    /// </remarks>
    /// <param name="context">Resolution context of the page's own transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Overrides of the page; <see cref="CorePageTexts.None"/> when nothing is overridden or the
    /// resolution failed.</returns>
    public async Task<CorePageTexts> ResolveAsync(
        ResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_overridable.Keys.Count == 0)
        {
            return CorePageTexts.None;
        }

        var overrides = new Dictionary<string, CorePageTextOverride>(StringComparer.Ordinal);

        try
        {
            using var scope = new ConfigResolutionScope(context);

            foreach (var naturalKey in _overridable.Keys)
            {
                var resolved = await _resolver.ResolveAsync(
                    CorePageTextConfigKeys.Texts,
                    scope,
                    ConfigDimensionValues.Of((CorePageTextConfigKeys.TextDimensionName, naturalKey)),
                    cancellationToken);

                // The highest level stating an entry decides it whole; a variant of it whose slots differ
                // from the text's, the optional ones aside, is not applied, and the page shows the text the
                // regular way — the snapshot report has already named that variant.
                if (resolved.Value?.ApplicableTo(naturalKey, _overridable.OptionalSlotsOf(naturalKey)) is { } applicable)
                {
                    overrides[naturalKey] = applicable;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal request cancellation — propagate, this is not a failure of the texts.
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The text overrides of this page could not be resolved; the page is rendered with its "
                + "regular texts.");

            return CorePageTexts.None;
        }

        return overrides.Count == 0 ? CorePageTexts.None : new CorePageTexts(overrides);
    }
}
