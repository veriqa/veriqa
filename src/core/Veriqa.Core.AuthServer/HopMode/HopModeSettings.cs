// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The single reading of the effective hop settings (SPEC-025 HOP-033a, HOP-120): every slice of the
/// hop mode takes its values from here and nowhere else.
/// <para>
/// A value is computed PER ACT (SPEC-025 HOP-129, HOP-129a): each asynchronous member asks the canonical
/// resolver anew with the caller's resolution context as it was handed in — not replaced, not trimmed,
/// and with no branch on a level — so which levels take part is stated by the declarations of the keys
/// (<see cref="HopModeConfigKeys"/>) alone. Nothing a resolution answered is kept between calls.
/// </para>
/// <para>
/// The synchronous members are the exception, and the only one: values of the node read once for the
/// process — the base path of the resolve endpoint, a setting of the node that no level below splits
/// (SPEC-025 HOP-128), and the predicates the host maps the routes of the resolve endpoint by, which
/// are decided at startup over the levels reachable then (SPEC-025 HOP-125, HOP-129a), together with the
/// facts the startup validation judges (SPEC-025 HOP-123). They read the core level synchronously
/// through <see cref="ConfigCoreValues"/> and the entries of the OIDC clients through the one walk of
/// them, by the same rule those levels of the keys are read by in a resolution.
/// </para>
/// </summary>
internal sealed class HopModeSettings
{
    /// <summary>
    /// The hop settings AS THE PRODUCT SHIPS THEM — what the core level answers where the section states
    /// nothing, the same values the keys declare.
    /// </summary>
    private static readonly HopModeOptions Shipped = new();

    /// <summary>
    /// Canonical resolver of levels.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Bindings of the deployment — which levels can answer a key at all.
    /// </summary>
    private readonly ConfigBindingView _bindings;

    /// <summary>
    /// Channels registered in this deployment, with their core-level facts.
    /// </summary>
    private readonly IReadOnlyList<CoreChannelDeclaration> _channels;

    /// <summary>
    /// The walk of the OIDC client entries when the application level is served by the reader the
    /// product ships, so that startup can read what the entries state; null when another reader serves
    /// the level and its records are out of reach of startup.
    /// </summary>
    private readonly EffectiveClientEntries? _clientEntries;

    /// <summary>
    /// Synchronous reader of the core level of the keys the startup members need.
    /// </summary>
    private readonly ConfigCoreValues _core;

    /// <summary>
    /// Normalized base path of the resolve endpoint, read once for the process.
    /// </summary>
    private readonly Lazy<string> _hopPath;

    /// <summary>
    /// Whether the core level turns hop on for a registered channel it enables, decided once for the process.
    /// </summary>
    private readonly Lazy<bool> _isEnabledAtCore;

    /// <summary>
    /// Whether an OIDC client entry turns hop on for a registered channel the core level enables, decided
    /// once for the process.
    /// </summary>
    private readonly Lazy<bool> _isEnabledByClientEntry;

    /// <summary>
    /// Whether the <c>GET</c> of the resolve endpoint is mapped, decided once for the process.
    /// </summary>
    private readonly Lazy<bool> _isResolveEndpointMapped;

    /// <summary>
    /// Whether the <c>POST</c> signal receiver of the resolve endpoint is mapped, decided once for the process.
    /// </summary>
    private readonly Lazy<bool> _isSignalReceiverMapped;

    /// <summary>
    /// Creates the reading of the hop settings.
    /// </summary>
    /// <param name="resolver">Canonical resolver of levels.</param>
    /// <param name="bindings">Bindings of the deployment.</param>
    /// <param name="channels">Channels registered in this deployment.</param>
    /// <param name="configuration">Host configuration the core level of the hop settings is read from.</param>
    /// <param name="clientEntries">
    /// The walk of the OIDC client entries when the application level is served by the reader the product
    /// ships; null when another reader serves the level.
    /// </param>
    public HopModeSettings(
        IConfigurationResolver resolver,
        ConfigBindingView bindings,
        IEnumerable<CoreChannelDeclaration> channels,
        IConfiguration configuration,
        EffectiveClientEntries? clientEntries)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(configuration);

        _resolver = resolver;
        _bindings = bindings;
        _channels = [.. channels];
        _clientEntries = clientEntries;
        _core = DeclareCoreValues(configuration);
        _hopPath = new Lazy<string>(ReadHopPath);
        _isEnabledAtCore = new Lazy<bool>(DecideEnabledAtCore);
        _isEnabledByClientEntry = new Lazy<bool>(DecideEnabledByClientEntry);
        _isResolveEndpointMapped = new Lazy<bool>(DecideResolveEndpointMapped);
        _isSignalReceiverMapped = new Lazy<bool>(DecideSignalReceiverMapped);
    }

    /// <summary>
    /// Normalized base path of the resolve endpoint (SPEC-025 HOP-033a, HOP-128): one leading slash, no
    /// trailing one. It is the only reading of the key, computed once for the process, and the source of
    /// both the route template of the resolve endpoint and <see cref="HopChannelMode.HopPath"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The configured path cannot serve as the base path of the endpoint; the message names its address.
    /// </exception>
    public string HopPath => _hopPath.Value;

    /// <summary>
    /// Whether the host maps the <c>GET</c> of the resolve endpoint (SPEC-025 HOP-110, HOP-125,
    /// HOP-129a). It is mapped when at least one of three holds: the core level turns hop on for a
    /// registered channel it enables (<see cref="IsEnabledAtCore"/>); an OIDC client entry does
    /// (<see cref="IsEnabledByClientEntry"/>); or the hop switch is bound at a level whose records
    /// startup cannot read — it may turn hop on per request, where startup cannot see it.
    /// </summary>
    /// <remarks>
    /// The value is decided once for the process, so a client entry added by a reload of the
    /// configuration after the start does not map the endpoint (SPEC-025 HOP-125).
    /// </remarks>
    public bool IsResolveEndpointMapped => _isResolveEndpointMapped.Value;

    /// <summary>
    /// Whether the host maps the <c>POST</c> signal receiver of the resolve endpoint (SPEC-025 HOP-060,
    /// HOP-066b): the resolve endpoint is mapped, and client signals are either accepted at the core
    /// level or may be accepted by a level above it that is bound for the key.
    /// </summary>
    public bool IsSignalReceiverMapped => _isSignalReceiverMapped.Value;

    /// <summary>
    /// Whether the core level turns hop on for at least one registered channel that the core level
    /// enables — the startup half of <see cref="IsResolveEndpointMapped"/>, and the condition under which
    /// the startup validation requires a base of hop URLs (SPEC-025 HOP-123).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A per-channel or the node-wide switch at the core level cannot be read.
    /// </exception>
    public bool IsEnabledAtCore => _isEnabledAtCore.Value;

    /// <summary>
    /// Whether at least one effective OIDC client entry turns hop on for a registered channel that the
    /// core level enables — by the channel's own value in the entry, otherwise by the general value of the
    /// entry. Always <c>false</c> when the application level is not served by the reader the product
    /// ships: the entries are then not what that level reads. A value of an entry that cannot be read
    /// states nothing here, as it does in a resolution. The entries are asked as stated: in the product
    /// as shipped today an entry the configuration snapshot drops as a whole is still counted here, so
    /// such an entry that turns hop on maps the endpoint although no resolution reads it.
    /// </summary>
    public bool IsEnabledByClientEntry => _isEnabledByClientEntry.Value;

    /// <summary>
    /// The base of hop URLs the core level states — its issuer in the form of
    /// <see cref="HopUrlForm.NormalizeBase"/>; null when the issuer is not stated or is no absolute HTTPS
    /// URL (SPEC-025 HOP-123).
    /// </summary>
    /// <exception cref="InvalidOperationException">The issuer at the core level cannot be read.</exception>
    public string? CoreBaseUrl => HopUrlForm.NormalizeBase(_core.Read(AuthServerConfigKeys.Issuer));

    /// <summary>
    /// The core-level <c>HopPath</c> as the deployment states it, before normalization — the one reading
    /// of the key, which both <see cref="HopPath"/> and the startup validation judge.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path at the core level cannot be read.</exception>
    public string ConfiguredHopPath => _core.Read(HopModeConfigKeys.HopPath);

    /// <summary>
    /// Whether the core level accepts client signals — the one synchronous reading of the key, which both
    /// <see cref="IsSignalReceiverMapped"/> and the startup validation take.
    /// </summary>
    /// <exception cref="InvalidOperationException">The switch at the core level cannot be read.</exception>
    public bool CoreAcceptsClientSignals => _core.Read(HopModeConfigKeys.AcceptClientSignals);

    /// <summary>
    /// Whether client signals can be accepted at all in this deployment: the core level accepts them, or a
    /// level above it is bound for the key and may accept them per request. The half of
    /// <see cref="IsSignalReceiverMapped"/> that does not depend on the hop switch — what the mapping of
    /// the receiver asks when the resolve endpoint is mapped for another reason than the switch.
    /// </summary>
    /// <exception cref="InvalidOperationException">The switch at the core level cannot be read.</exception>
    public bool MayAcceptClientSignals =>
        IsBoundAboveCore(HopModeConfigKeys.AcceptClientSignals) || CoreAcceptsClientSignals;

    /// <summary>
    /// Whether at least one of the given channels can be available to a layer of this deployment, as far
    /// as startup can tell: the core level enables it — the same reading of a channel
    /// <see cref="IsEnabledAtCore"/> takes — or the set of available channels is bound at a level above
    /// the core one, whose records startup cannot read and which may make the channel available per
    /// request. The startup half of <see cref="ResolveCodeChannelAsync"/>: a channel that answers
    /// <c>false</c> here is never issued a hop URL by it.
    /// </summary>
    /// <param name="channelTypes">Types of the channels asked about, compared ordinally.</param>
    /// <returns><c>true</c> when one of the channels can be available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="channelTypes"/> is null.</exception>
    public bool CanBeAvailable(IReadOnlySet<string> channelTypes)
    {
        ArgumentNullException.ThrowIfNull(channelTypes);

        return channelTypes.Count > 0
            && (IsBoundAboveCore(ChannelConfigKeys.ChannelsEnabled)
                || _channels.Any(channel => channel.IsEnabled && channelTypes.Contains(channel.ChannelType)));
    }

    /// <summary>
    /// The hop mode of one channel for one act (SPEC-025 HOP-120, HOP-124, HOP-129a).
    /// </summary>
    /// <param name="context">Resolution context of the act, passed to the resolver as is.</param>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hop mode of the channel.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="channelType"/> is empty.</exception>
    public async ValueTask<HopChannelMode> ResolveChannelAsync(
        ResolutionContext context,
        string channelType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelType);

        var channel = ChannelPoint(channelType);

        // One scope for the act: a record serving several of the keys below is read once.
        using var scope = new ConfigResolutionScope(context);

        var enabled = await _resolver.ResolveAsync(HopModeConfigKeys.Enabled, scope, channel, cancellationToken);
        var redirectStyle = await _resolver.ResolveAsync(HopModeConfigKeys.RedirectStyle, scope, channel, cancellationToken);
        var issuer = await _resolver.ResolveAsync(AuthServerConfigKeys.Issuer, scope, ConfigDimensionValues.None, cancellationToken);

        // Hop for a channel the layer does not make available is a no-op (SPEC-025 HOP-124): the one
        // reading of that availability is the channels_enabled capability, and it is asked only when the
        // switch itself is on.
        var isEnabled = enabled.Value
            && await _resolver.IsChannelEnabledAsync(context.TenantId, channelType, cancellationToken);

        return new HopChannelMode(isEnabled, redirectStyle.Value, HopUrlForm.NormalizeBase(issuer.Value), HopPath);
    }

    /// <summary>
    /// The hop mode of a channel whose link carries no transaction and whose only start is a message with
    /// a code, for one act. The phone that scanned the QR code of such a channel has to be shown that
    /// message, and the page of the resolve endpoint is the one place that can show it — so hop is on for
    /// the channel whatever its switch states, and the answer is a page whatever the redirect style states.
    /// Neither key is read here. The channel still has to be available to the layer
    /// (<c>channels_enabled</c>), as for any channel (SPEC-025 HOP-124), and the base of hop URLs is the
    /// effective one.
    /// </summary>
    /// <param name="context">Resolution context of the act, passed to the resolver as is.</param>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hop mode of the channel.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="channelType"/> is empty.</exception>
    public async ValueTask<HopChannelMode> ResolveCodeChannelAsync(
        ResolutionContext context,
        string channelType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelType);

        using var scope = new ConfigResolutionScope(context);

        var issuer = await _resolver.ResolveAsync(AuthServerConfigKeys.Issuer, scope, ConfigDimensionValues.None, cancellationToken);
        var isAvailable = await _resolver.IsChannelEnabledAsync(context.TenantId, channelType, cancellationToken);

        return new HopChannelMode(isAvailable, HopRedirectStyle.Interstitial, HopUrlForm.NormalizeBase(issuer.Value), HopPath);
    }

    /// <summary>
    /// What the hop mode captures and publishes, for one act (SPEC-025 HOP-128, HOP-129a).
    /// </summary>
    /// <param name="context">Resolution context of the act, passed to the resolver as is.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Observation settings of the act.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public async ValueTask<HopObservationSettings> ResolveObservationAsync(
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // One scope for the act: a record serving several of the keys below is read once.
        using var scope = new ConfigResolutionScope(context);

        var collectScannerContext = await ResolveAsync(HopModeConfigKeys.CollectScannerContext, scope, cancellationToken);
        var collectIpAddress = await ResolveAsync(HopModeConfigKeys.CollectIpAddress, scope, cancellationToken);
        var collectUserAgent = await ResolveAsync(HopModeConfigKeys.CollectUserAgent, scope, cancellationToken);
        var collectGeoLocation = await ResolveAsync(HopModeConfigKeys.CollectGeoLocation, scope, cancellationToken);
        var collectFingerprint = await ResolveAsync(HopModeConfigKeys.CollectFingerprint, scope, cancellationToken);
        var storeRawUserAgent = await ResolveAsync(HopModeConfigKeys.StoreRawUserAgent, scope, cancellationToken);
        var emitScannedStatus = await ResolveAsync(HopModeConfigKeys.EmitScannedStatus, scope, cancellationToken);
        var acceptClientSignals = await ResolveAsync(HopModeConfigKeys.AcceptClientSignals, scope, cancellationToken);
        var warnOnRepeatScan = await ResolveAsync(HopModeConfigKeys.WarnOnRepeatScan, scope, cancellationToken);

        return new HopObservationSettings(
            collectScannerContext,
            collectIpAddress,
            collectUserAgent,
            collectGeoLocation,
            collectFingerprint,
            storeRawUserAgent,
            emitScannedStatus,
            acceptClientSignals,
            warnOnRepeatScan);
    }

    /// <summary>
    /// How hop tokens are issued and the page of the unified QR code is laid out, for one act (SPEC-025
    /// HOP-182, HOP-187, HOP-151).
    /// </summary>
    /// <param name="context">Resolution context of the act, passed to the resolver as is.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Issuance settings of the act.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public async ValueTask<HopIssuanceSettings> ResolveIssuanceAsync(
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // One scope for the act: a record serving several of the keys below is read once.
        using var scope = new ConfigResolutionScope(context);

        var qrMode = await ResolveAsync(HopModeConfigKeys.QrMode, scope, cancellationToken);
        var showChannelButtonsOnAnyChannelTab = await ResolveAsync(
            HopModeConfigKeys.ShowChannelButtonsOnAnyChannelTab, scope, cancellationToken);
        var tokenLifetime = await ResolveAsync(HopModeConfigKeys.TokenLifetime, scope, cancellationToken);

        return new HopIssuanceSettings(qrMode, showChannelButtonsOnAnyChannelTab, tokenLifetime);
    }

    /// <summary>
    /// Declares the core getters of the keys the startup members read. The per-channel switch is read by
    /// the very rule the parse hook of its key reads the core level by (<see cref="HopModeConfigKeys.ReadEnabledStep"/>);
    /// a node-wide member that states nothing — absent or empty — answers with the shipped value, as the
    /// declared default of its key does.
    /// </summary>
    /// <param name="configuration">Host configuration.</param>
    /// <returns>Reader of the core values.</returns>
    private static ConfigCoreValues DeclareCoreValues(IConfiguration configuration) =>
        ConfigCoreValues.Declare(core =>
        {
            core.RegisterCore(
                HopModeConfigKeys.Enabled,
                point => HopModeConfigKeys.ReadEnabledStep(Node(configuration, HopModeConfigKeys.CoreRootSection), point));

            core.RegisterCore(
                HopModeConfigKeys.AcceptClientSignals,
                () => Node(configuration, HopModeOptions.SectionName)
                    .TryRead<bool?>(nameof(HopModeOptions.AcceptClientSignals), out var accepts) && accepts is { } stated
                    ? stated
                    : Shipped.AcceptClientSignals);

            core.RegisterCore(
                HopModeConfigKeys.HopPath,
                () => Node(configuration, HopModeOptions.SectionName)
                    .TryRead<string>(nameof(HopModeOptions.HopPath), out var path)
                    ? path
                    : Shipped.HopPath);

            // An issuer that is absent states the shipped value of the key: none.
            core.RegisterCore(
                AuthServerConfigKeys.Issuer,
                () => Node(configuration, OidcServerOptions.SectionName)
                    .TryRead<string>(nameof(OidcServerOptions.Issuer), out var issuer)
                    ? issuer
                    : null);
        });

    /// <summary>
    /// Node of the core level over a section of the host configuration. It is built at each read rather
    /// than captured, as the core level of a resolution is.
    /// </summary>
    /// <param name="configuration">Host configuration.</param>
    /// <param name="section">Section the node stands over.</param>
    /// <returns>Node over the section.</returns>
    private static ConfigNode Node(IConfiguration configuration, string section) =>
        ConfigNode.Over(configuration.GetSection(section), ConfigLevel.Core, recordLabel: null);

    /// <summary>
    /// Point of the chain that addresses a channel.
    /// </summary>
    /// <param name="channelType">Type of the channel.</param>
    /// <returns>Point of the chain.</returns>
    private static ConfigDimensionValues ChannelPoint(string channelType) =>
        ConfigDimensionValues.Of((ConfigDimensionNames.Channel, channelType));

    /// <summary>
    /// Whether a level above the core one is bound for a key in this deployment — a level that may
    /// answer the key per request, where startup cannot see its value.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <returns><c>true</c> when such a level is bound.</returns>
    private bool IsBoundAboveCore<T>(ConfigKey<T> key) =>
        _bindings.BoundLevels.TryGetValue(key.Name, out var levels)
        && levels.Any(static level => level is not ConfigLevel.Core);

    /// <summary>
    /// Resolves a key without dimensions within the scope of an act.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="key">Setting key.</param>
    /// <param name="scope">Scope of the act, carrying its resolution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective value.</returns>
    private async ValueTask<T> ResolveAsync<T>(
        ConfigKey<T> key,
        ConfigResolutionScope scope,
        CancellationToken cancellationToken) =>
        (await _resolver.ResolveAsync(key, scope, ConfigDimensionValues.None, cancellationToken)).Value;

    /// <summary>
    /// Reads the core-level <c>HopPath</c>, refuses a value that cannot serve as the base path of the
    /// endpoint and normalizes the rest.
    /// </summary>
    /// <returns>Normalized path.</returns>
    /// <exception cref="InvalidOperationException">The configured path is unsuitable.</exception>
    private string ReadHopPath()
    {
        var configured = ConfiguredHopPath;

        if (DescribeUnsuitableHopPath(configured) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        return HopUrlForm.NormalizeHopPath(configured);
    }

    /// <summary>
    /// Refusal of a configured <c>HopPath</c> by the one check of its suitability
    /// (<see cref="HopUrlForm.FindHopPathDefect"/>) — the one wording of it, for the getter of
    /// <see cref="HopPath"/> and for the startup validation alike.
    /// </summary>
    /// <param name="configured">Configured path.</param>
    /// <returns>Null when the path is suitable; otherwise a message naming its address and what is wrong.</returns>
    internal static string? DescribeUnsuitableHopPath(string? configured) =>
        HopUrlForm.FindHopPathDefect(configured) is { } defect
            ? $"Configuration value '{HopModeConfigKeys.CoreAddress(nameof(HopModeOptions.HopPath))}' "
              + $"('{configured}') cannot serve as the base path of the hop resolve endpoint: {defect}."
            : null;

    /// <summary>
    /// Decides whether the <c>GET</c> of the resolve endpoint is mapped (see <see cref="IsResolveEndpointMapped"/>).
    /// </summary>
    /// <returns><c>true</c> when the endpoint is mapped.</returns>
    private bool DecideResolveEndpointMapped() =>
        IsEnabledAtCore || IsEnabledByClientEntry || IsSwitchBoundOutOfStartupReach();

    /// <summary>
    /// Whether the hop switch is bound at a level whose records startup cannot read: any level above the
    /// core one except the application level served by the reader the product ships, whose records are
    /// the OIDC client entries read by <see cref="IsEnabledByClientEntry"/>.
    /// </summary>
    /// <returns><c>true</c> when such a level is bound.</returns>
    private bool IsSwitchBoundOutOfStartupReach() =>
        _bindings.BoundLevels.TryGetValue(HopModeConfigKeys.Enabled.Name, out var levels)
        && levels.Any(level =>
            level is not ConfigLevel.Core
            && !(level is ConfigLevel.Application && _clientEntries is not null));

    /// <summary>
    /// Decides whether an OIDC client entry turns hop on (see <see cref="IsEnabledByClientEntry"/>).
    /// </summary>
    /// <returns><c>true</c> when such an entry exists.</returns>
    private bool DecideEnabledByClientEntry()
    {
        if (_clientEntries is null)
        {
            return false;
        }

        var points = _channels
            .Where(static channel => channel.IsEnabled)
            .Select(static channel => ChannelPoint(channel.ChannelType))
            .ToList();

        return points.Count > 0
            && _clientEntries.Walk().Any(entry => points.Exists(point => IsEnabledIn(entry, point)));
    }

    /// <summary>
    /// Whether one OIDC client entry turns hop on for a channel: the channel's own value in the entry,
    /// otherwise the general value of the entry (the chain of the key within one level).
    /// </summary>
    /// <param name="entry">Node over the client entry.</param>
    /// <param name="channel">Point of the chain that addresses the channel.</param>
    /// <returns><c>true</c> when the entry states the switch on for the channel.</returns>
    private static bool IsEnabledIn(ConfigNode entry, ConfigDimensionValues channel)
    {
        var ofChannel = ReadEntryStep(entry, channel);
        var stated = ofChannel.HasValue ? ofChannel : ReadEntryStep(entry, ConfigDimensionValues.None);

        return stated.HasValue && stated.Value;
    }

    /// <summary>
    /// Reads one step of the hop switch out of a client entry by the rule of its key. A value that
    /// cannot be read into the type of the setting states nothing: a resolution leaves such a step unset
    /// with a warning, and the start is not stopped over it.
    /// </summary>
    /// <param name="entry">Node over the client entry.</param>
    /// <param name="point">Point of the chain.</param>
    /// <returns>Layer value of the step; unset when the stated value cannot be read.</returns>
    private static LayerValue<bool> ReadEntryStep(ConfigNode entry, ConfigDimensionValues point)
    {
        try
        {
            return HopModeConfigKeys.ReadEnabledStep(entry, point);
        }
        catch (InvalidOperationException)
        {
            return LayerValue<bool>.None;
        }
    }

    /// <summary>
    /// Decides whether the core level turns hop on for a registered channel it enables (see <see cref="IsEnabledAtCore"/>).
    /// </summary>
    /// <returns><c>true</c> when such a channel exists.</returns>
    private bool DecideEnabledAtCore() =>
        _channels.Any(channel =>
            channel.IsEnabled && _core.Read(HopModeConfigKeys.Enabled, ChannelPoint(channel.ChannelType)));

    /// <summary>
    /// Decides whether the <c>POST</c> signal receiver is mapped (see <see cref="IsSignalReceiverMapped"/>).
    /// </summary>
    /// <returns><c>true</c> when the receiver is mapped.</returns>
    private bool DecideSignalReceiverMapped() => IsResolveEndpointMapped && MayAcceptClientSignals;
}
