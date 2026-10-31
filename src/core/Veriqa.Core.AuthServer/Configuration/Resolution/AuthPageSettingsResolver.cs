// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// Resolves every level-owned setting of the sign-in page in one place, against the resolution
/// context built once at the entry of the request (SPEC-012 §10.6). This is the only consumer-side
/// point where the page's settings are gathered: a level added to any of the page's keys reaches the
/// page without a line of change here, and no consumer merges levels on its own.
/// <para>
/// What the page is MADE OF is declared by the domain group <see cref="AuthPageSettingsGroup"/> and
/// filled by the mechanism; this class is what the endpoint and the renderers call, and it holds the
/// per-channel questions that are not part of the page-wide aggregate.
/// </para>
/// </summary>
internal sealed class AuthPageSettingsResolver
{
    /// <summary>
    /// Canonical multi-level configuration resolver.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger of an inconsistent combination of the page's settings.
    /// </summary>
    private readonly ILogger<AuthPageSettingsResolver> _logger;

    /// <summary>
    /// Creates the resolver of the sign-in page settings.
    /// </summary>
    /// <param name="resolver">Canonical multi-level configuration resolver.</param>
    /// <param name="logger">Logger of an inconsistent combination of the page's settings.</param>
    public AuthPageSettingsResolver(IConfigurationResolver resolver, ILogger<AuthPageSettingsResolver> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Tells whether the <c>ui_config</c> selector carried by the context addresses a record the
    /// application owns (CFG-203). The answer is the presence of the <c>ui_config</c> level itself
    /// (CFG-232): a selector that names no owned record leaves the level unset.
    /// <para>
    /// A source that is unreachable leaves the level unset as well — the resolver deliberately does
    /// not distinguish "no record" from "source unavailable" for a consumer (CFG-233), and both mean
    /// the same thing for the page: it is rendered from the levels below the record.
    /// </para>
    /// </summary>
    /// <param name="context">Resolution context carrying the candidate selector.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the selector addresses an owned record.</returns>
    public async Task<bool> IsSelectorAssignedAsync(
        ResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolved = await _resolver.ResolveAsync(
            AuthServerConfigKeys.UiConfigSelectorAssigned,
            context,
            ConfigDimensionValues.None,
            cancellationToken);

        return resolved.SourceLevel is not null;
    }

    /// <summary>
    /// Resolves the effective settings of the page for the given ownership context — one call of the
    /// mechanism over the page's domain group.
    /// <para>
    /// An inconsistent COMBINATION of the resolved values is reported and does not stop the page: a
    /// sign-in window refused over a branding mismatch would cost the user the login, while a branding
    /// value that does not reach the painted theme costs an entry in the operator's log and a page in
    /// the neutral canon. Which of the two the caller prefers is exactly what the mechanism leaves to
    /// it.
    /// </para>
    /// <para>
    /// The entry is written on EVERY resolution, and NOT once per configuration snapshot the way the
    /// registry of bindings reports a value its key's domain rejects. That is a deliberate trade-off,
    /// not an oversight of the convention: a rejected value is known where the level is bound, while
    /// this violation exists only for a FILLED aggregate — one tenant, one application, one
    /// <c>ui_config</c> selector — and nothing at startup knows which of those combinations a
    /// deployment will be asked for. The cost paid is a repeated Warning for as long as a misconfigured
    /// combination keeps being requested; the cost refused is silence, since a level below Warning
    /// would leave the operator unaware of branding that never reaches the painted page, and this call
    /// is the only place where the combination is ever seen.
    /// </para>
    /// </summary>
    /// <param name="context">Resolution context of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective settings of the page.</returns>
    public async Task<AuthPageSettings> ResolveAsync(
        ResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolved = await _resolver.ResolveGroupAsync(AuthPageSettingsGroup.Group, context, cancellationToken);

        if (!resolved.IsConsistent)
        {
            _logger.LogWarning(
                "The resolved settings of the sign-in page are inconsistent: {Reason}",
                resolved.Violation);
        }

        return resolved.Value;
    }

    /// <summary>
    /// Resolves the effective QR pixel scale FOR ONE CHANNEL (SPEC-012 §9, CFG-237): the channel is a
    /// dimension of the key, so the level that owns the scale of this channel answers from its
    /// per-channel map or from its plain field, and only then does the resolution drop to the level
    /// below (CFG-236).
    /// <para>
    /// A scale no level supplies at all — every step of every level unset — degrades to
    /// <see cref="QrCodeOptions.DefaultPixelsPerModule"/>: a zero scale would fail the PNG encoder
    /// while the sign-in page is being served.
    /// </para>
    /// </summary>
    /// <param name="context">Resolution context of the request.</param>
    /// <param name="channelType">Channel type the QR is rendered for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Pixel scale in px per module.</returns>
    public async Task<int> ResolvePixelsPerModuleAsync(
        ResolutionContext context,
        string channelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scale = await _resolver.ResolveAsync(
            AuthServerConfigKeys.QrCodePixelsPerModule,
            context,
            ConfigDimensionValues.Of((AuthServerConfigKeys.ChannelDimensionName, channelType)),
            cancellationToken);

        return scale.SourceLevel is null ? QrCodeOptions.DefaultPixelsPerModule : scale.Value;
    }

    /// <summary>
    /// Resolves the effective channel display mode (SPEC-012 §9): the mode of the <c>ui_config</c>
    /// record where it states one, and the global mode of the core level otherwise — one chain of the
    /// mechanism rather than a record's value merged with an options field by the consumer.
    /// <para>
    /// A level that states a mode outside the enumeration leaves its own step unset and the mode comes
    /// from the level below it, exactly as CFG-240 prescribes. Where that leaves NO level speaking —
    /// the core level itself states a value it cannot read, and there is no level under it — the chain
    /// ends unanswered and yields the default of the type; the reader degrades to the shipped mode
    /// instead, the same way <see cref="ResolvePixelsPerModuleAsync"/> does, so the answer stays the
    /// one the key declares even after the shipped mode changes.
    /// </para>
    /// </summary>
    /// <param name="context">Resolution context of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective channel display mode.</returns>
    public async Task<ChannelDisplayMode> ResolveChannelDisplayModeAsync(
        ResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var mode = await _resolver.ResolveAsync(
            AuthServerConfigKeys.PageChannelDisplayMode,
            context,
            ConfigDimensionValues.None,
            cancellationToken);

        return mode.SourceLevel is null
            ? AuthServerConfigKeys.ShippedChannelDisplayMode
            : mode.Value;
    }

    /// <summary>
    /// Resolves the hint of ONE channel (SPEC-012 §9, CFG-237/CFG-239). The chain of the key is a
    /// single step, so a level either states the hint of this channel or is skipped — and when no
    /// level states one, the channel's panel is rendered without a hint at all.
    /// </summary>
    /// <param name="context">Resolution context of the request.</param>
    /// <param name="channelType">Channel type the panel belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hint text, or null when no level states one for the channel.</returns>
    public async Task<string?> ResolveChannelHintAsync(
        ResolutionContext context,
        string channelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hint = await _resolver.ResolveAsync(
            AuthServerConfigKeys.ChannelHints,
            context,
            ConfigDimensionValues.Of((AuthServerConfigKeys.ChannelDimensionName, channelType)),
            cancellationToken);

        return hint.Value;
    }

    /// <summary>
    /// Tells whether the spare start code of ONE channel is prepared and shown. The key answers on its
    /// core level when no level states a value, so a deployment that declares nothing keeps the code.
    /// A read that fails is reported and answered with <c>false</c>: the page is rendered and the channel
    /// stays on it, only without the spare code.
    /// </summary>
    /// <param name="context">Resolution context of the transaction the page belongs to.</param>
    /// <param name="channelType">Channel type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the spare start code of the channel is on.</returns>
    public async Task<bool> ResolveSpareStartCodeEnabledAsync(
        ResolutionContext context,
        string channelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var enabled = await _resolver.ResolveAsync(
                ChannelChatConfigKeys.SpareStartCodeEnabled,
                context,
                ConfigDimensionValues.Of((ConfigDimensionNames.Channel, channelType)),
                cancellationToken);

            return enabled.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Failed to read whether the spare start code of channel {ChannelType} is on — the channel is shown without it",
                channelType);

            return false;
        }
    }

    /// <summary>
    /// Resolves the client's own login-initiation address (<c>initiate_login_uri</c>, OIDC Dynamic
    /// Client Registration 1.0 §2) — the address a page offers as the way to start an expired sign-in
    /// over: the expired sign-in window and the callback's "sign-in session expired" page. The key
    /// stands over a single level, so the chain either finds the value on the application's entry or
    /// ends with no level speaking; what a page offers then is that page's own fallback.
    /// <para>
    /// It is asked outside the page's aggregate deliberately: the aggregate is what the sign-in PAGE is
    /// made of and is resolved for both surfaces that render it, while this address is read only where
    /// a sign-in has run out. A confirmation transaction is not such a place: its context names the
    /// application too and would resolve to the same value, so the surface that may meet one — the
    /// callback page — does not ask for it.
    /// </para>
    /// </summary>
    /// <param name="context">Resolution context of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stated address, or null when no level states one.</returns>
    public async Task<string?> ResolveInitiateLoginUriAsync(
        ResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stated = await _resolver.ResolveAsync(
            AuthServerConfigKeys.ClientInitiateLoginUri,
            context,
            ConfigDimensionValues.None,
            cancellationToken);

        return stated.Value;
    }
}
