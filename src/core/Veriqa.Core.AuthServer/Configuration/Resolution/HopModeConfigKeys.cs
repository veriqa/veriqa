// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// Resolver keys of the hop authentication mode (SPEC-025 §11). Every hop setting is a key of the
/// canonical resolver of levels, with no precedence of its own (SPEC-025 HOP-120, HOP-128): which level
/// wins and how two of them combine is stated by the declarations below.
/// <para>
/// The core level is the section <c>Veriqa:HopMode</c> and, for the two per-channel settings, the
/// section of each channel (SPEC-025 HOP-121). The levels above it are addressed by PATH inside the
/// record of the level, and the record mirrors the core root: the same <c>HopMode</c> section and, for
/// the two per-channel settings, the same <c>Channels:&lt;type&gt;</c> members. The application level is
/// the entry of an OIDC client. The tenant level is read in a contour that states a reader of tenant
/// records; today's delivery ships none, so in a self-hosted deployment the tenant is the core level.
/// The protective settings declare no application level, and the own lifetime of a hop token declares
/// it without a path (SPEC-025 HOP-127, HOP-128). No key declares the <c>ui_config</c>, per-request or
/// transaction level (SPEC-025 HOP-127, HOP-129b).
/// </para>
/// </summary>
public static class HopModeConfigKeys
{
    /// <summary>
    /// Section of the host configuration the core level of a per-channel hop setting is read over — the
    /// root both of its members hang from (<c>Veriqa:HopMode</c> and <c>Veriqa:Channels</c>).
    /// </summary>
    internal const string CoreRootSection = VeriqaOptions.SectionName;

    /// <summary>
    /// Prefix of every core address of a hop setting. What follows it is the address of the same
    /// setting inside the record of a level above the core one, which mirrors the core root.
    /// </summary>
    private const string CoreRootPrefix = CoreRootSection + ":";

    /// <summary>
    /// Catalog of the declarations of the hop settings. It is initialized before the declarations that
    /// fill it, which is the order the initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// The hop settings AS THE PRODUCT SHIPS THEM — a default-constructed options object, read for one
    /// thing only: the value the core level answers with where the section states nothing.
    /// </summary>
    private static readonly HopModeOptions Shipped = new();

    /// <summary>
    /// Members the per-channel hop switch is stated at, on every level of its key.
    /// </summary>
    internal static readonly PerChannelSetting EnabledSetting =
        new(nameof(HopModeOptions.Enabled), HopModeConstants.ChannelHopEnabledMember);

    /// <summary>
    /// Members the per-channel redirect style is stated at, on every level of its key.
    /// </summary>
    internal static readonly PerChannelSetting RedirectStyleSetting =
        new(nameof(HopModeOptions.RedirectStyle), HopModeConstants.ChannelHopRedirectStyleMember);

    /// <summary>
    /// Domain of the own lifetime of a hop token (SPEC-025 HOP-151): no value — the token lives as long
    /// as the transaction — or a lifetime of at least <see cref="HopModeConstants.MinimumTokenLifetime"/>.
    /// A shorter lifetime would expire before the code can be scanned and the link opened, so the policy
    /// is the strict one: at the core level such a value stops the start.
    /// </summary>
    private static readonly ConfigValueDomain<TimeSpan?> TokenLifetimeDomain =
        new(
            static lifetime => lifetime is null || lifetime >= HopModeConstants.MinimumTokenLifetime,
            "no value (the lifetime of the transaction) or a duration of at least "
            + HopModeConstants.MinimumTokenLifetime.ToString("c", CultureInfo.InvariantCulture),
            CoreAddress(nameof(HopModeOptions.TokenLifetime)),
            ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Hop switch FOR A CHANNEL (SPEC-025 HOP-120, HOP-127): a plain value over the core, tenant and
    /// application levels, narrowed by the channel. On each level the channel's own value wins over the
    /// node-wide value of the same level, and a channel that states none — absent or empty — inherits
    /// it; between levels the canonical precedence applies.
    /// <para>
    /// Every level reads two different members for the two steps of the chain — the member of the
    /// channel section and the member of the <c>HopMode</c> section — which an address cannot say, so
    /// the key reads the step with a hook (<see cref="ReadEnabledStep"/>); the declared address of a
    /// level names the member of the <c>HopMode</c> section. At the core level the hook states the
    /// shipped value where that member says nothing, so the walk of a snapshot cannot reproduce the
    /// level and the key says so.
    /// </para>
    /// </summary>
    public static ConfigKey<bool> Enabled { get; } = Declared
        .Of<bool>("HopMode.Enabled")
        .Narrowing(ConfigDimensionNames.Channel)
        .At(ConfigLevel.Core, EnabledSetting.NodeWideAddress)
        .At(ConfigLevel.Tenant, EnabledSetting.RecordAddress)
        .At(ConfigLevel.Application, EnabledSetting.RecordAddress)
        .Parse(static (node, _, point) => ReadEnabledStep(node, point))
        .NotWalked()
        .Declare();

    /// <summary>
    /// Redirect style FOR A CHANNEL (SPEC-025 HOP-065, HOP-120, HOP-127): the same levels, narrowing and
    /// reading of a level as <see cref="Enabled"/>. The key gets no domain of its enumeration — a key
    /// read by a hook is given none — so above the core level the hook itself leaves a value outside
    /// the enumeration unset.
    /// </summary>
    public static ConfigKey<HopRedirectStyle> RedirectStyle { get; } = Declared
        .Of<HopRedirectStyle>("HopMode.RedirectStyle")
        .Narrowing(ConfigDimensionNames.Channel)
        .At(ConfigLevel.Core, RedirectStyleSetting.NodeWideAddress)
        .At(ConfigLevel.Tenant, RedirectStyleSetting.RecordAddress)
        .At(ConfigLevel.Application, RedirectStyleSetting.RecordAddress)
        .Parse(static (node, _, point) => ReadRedirectStyleStep(node, point))
        .NotWalked()
        .Declare();

    /// <summary>
    /// Base path of the hop resolve endpoint (SPEC-025 HOP-128): a setting of the node, read at the core
    /// level only — the route is mapped once for the process.
    /// </summary>
    public static ConfigKey<string> HopPath { get; } = Declared
        .Of<string>("HopMode.HopPath")
        .At(ConfigLevel.Core, CoreAddress(nameof(HopModeOptions.HopPath)))
        .Default(Shipped.HopPath)
        .Declare();

    /// <summary>
    /// One hop QR code per channel, one for the transaction, or both — <see cref="HopQrMode.PerChannel"/>,
    /// <see cref="HopQrMode.Unified"/>, <see cref="HopQrMode.UnifiedWithChannels"/> (SPEC-025 HOP-182): a plain
    /// value over the core, tenant and application levels, with no per-channel spelling.
    /// </summary>
    public static ConfigKey<HopQrMode> QrMode { get; } = Declared
        .Of<HopQrMode>("HopMode.QrMode")
        .At(ConfigLevel.Core, CoreAddress(nameof(HopModeOptions.QrMode)))
        .At(ConfigLevel.Tenant, RecordAddress(nameof(HopModeOptions.QrMode)))
        .At(ConfigLevel.Application, RecordAddress(nameof(HopModeOptions.QrMode)))
        .Default(Shipped.QrMode)
        .Declare();

    /// <summary>
    /// Show the buttons of the channels on the "Any channel" tab of a page that keeps the per-channel QR codes
    /// beside the unified one (<see cref="HopQrMode.UnifiedWithChannels"/>, SPEC-025 HOP-187): the same levels
    /// and addresses as <see cref="QrMode"/>, with no per-channel spelling. Under any other QR mode the value is
    /// read and means nothing.
    /// </summary>
    public static ConfigKey<bool> ShowChannelButtonsOnAnyChannelTab { get; } = Declared
        .Of<bool>("HopMode.ShowChannelButtonsOnAnyChannelTab")
        .At(ConfigLevel.Core, CoreAddress(nameof(HopModeOptions.ShowChannelButtonsOnAnyChannelTab)))
        .At(ConfigLevel.Tenant, RecordAddress(nameof(HopModeOptions.ShowChannelButtonsOnAnyChannelTab)))
        .At(ConfigLevel.Application, RecordAddress(nameof(HopModeOptions.ShowChannelButtonsOnAnyChannelTab)))
        .Default(Shipped.ShowChannelButtonsOnAnyChannelTab)
        .Declare();

    /// <summary>
    /// Own lifetime of a hop token (SPEC-025 HOP-151): a protective ceiling over the core, tenant and
    /// application levels — a level below may only shorten the lifetime (see
    /// <see cref="ShorterLifetime"/>). The application level is declared without a path: the entry of
    /// a client states no lifetime of its own.
    /// </summary>
    public static ConfigKey<TimeSpan?> TokenLifetime { get; } = Declared
        .Of<TimeSpan?>("HopMode.TokenLifetime")
        .At(ConfigLevel.Core, CoreAddress(nameof(HopModeOptions.TokenLifetime)))
        .At(ConfigLevel.Tenant, RecordAddress(nameof(HopModeOptions.TokenLifetime)))
        .AtIdentity(ConfigLevel.Application)
        .ProtectiveCeiling(ShorterLifetime)
        .Domain(TokenLifetimeDomain)
        .Default(Shipped.TokenLifetime)
        .Declare();

    /// <summary>
    /// Capture the context of the scanning device: protective — a level below may only turn it off.
    /// </summary>
    public static ConfigKey<bool> CollectScannerContext { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.CollectScannerContext), Shipped.CollectScannerContext);

    /// <summary>
    /// Capture the IP address of the scanning device: protective — a level below may only turn it off.
    /// </summary>
    public static ConfigKey<bool> CollectIpAddress { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.CollectIpAddress), Shipped.CollectIpAddress);

    /// <summary>
    /// Capture the User-Agent of the scanning device: protective — a level below may only turn it off.
    /// </summary>
    public static ConfigKey<bool> CollectUserAgent { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.CollectUserAgent), Shipped.CollectUserAgent);

    /// <summary>
    /// Capture the country of the scanning device: protective — a level below may only turn it off.
    /// </summary>
    public static ConfigKey<bool> CollectGeoLocation { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.CollectGeoLocation), Shipped.CollectGeoLocation);

    /// <summary>
    /// Compute a browser fingerprint: protective — a level below may only turn it off.
    /// </summary>
    public static ConfigKey<bool> CollectFingerprint { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.CollectFingerprint), Shipped.CollectFingerprint);

    /// <summary>
    /// Keep the raw User-Agent: protective — a level below may only turn it off.
    /// </summary>
    public static ConfigKey<bool> StoreRawUserAgent { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.StoreRawUserAgent), Shipped.StoreRawUserAgent);

    /// <summary>
    /// Publish the intermediate hop statuses: protective — a level below may only turn it off.
    /// </summary>
    public static ConfigKey<bool> EmitScannedStatus { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.EmitScannedStatus), Shipped.EmitScannedStatus);

    /// <summary>
    /// Mark a reopening of the hop link from another browser: protective — a level below may only turn it
    /// off.
    /// </summary>
    public static ConfigKey<bool> WarnOnRepeatScan { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.WarnOnRepeatScan), Shipped.WarnOnRepeatScan);

    /// <summary>
    /// Accept signals from the launch page: protective — a level below may only turn it off.
    /// </summary>
    public static ConfigKey<bool> AcceptClientSignals { get; } =
        ProtectiveSwitch(nameof(HopModeOptions.AcceptClientSignals), Shipped.AcceptClientSignals);

    /// <summary>
    /// Declarations of the hop settings — what the composition of the auth server registers
    /// (<see cref="VeriqaConfigKeyCatalogs"/>).
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Value of one step of the chain of <see cref="Enabled"/> at one level (see
    /// <see cref="PerChannelSetting"/> for the rule).
    /// </summary>
    /// <param name="root">
    /// Node of the level: over <see cref="CoreRootSection"/> of the host configuration at the core level,
    /// over the record of the level above it.
    /// </param>
    /// <param name="point">Point of the chain.</param>
    /// <returns>Layer value of the step.</returns>
    internal static LayerValue<bool> ReadEnabledStep(ConfigNode root, ConfigDimensionValues point) =>
        EnabledSetting.ReadStep(root, point, Shipped.Enabled);

    /// <summary>
    /// Value of one step of the chain of <see cref="RedirectStyle"/> at one level (see
    /// <see cref="PerChannelSetting"/> for the rule).
    /// </summary>
    /// <param name="root">
    /// Node of the level: over <see cref="CoreRootSection"/> of the host configuration at the core level,
    /// over the record of the level above it.
    /// </param>
    /// <param name="point">Point of the chain.</param>
    /// <returns>Layer value of the step.</returns>
    internal static LayerValue<HopRedirectStyle> ReadRedirectStyleStep(
        ConfigNode root,
        ConfigDimensionValues point) =>
        RedirectStyleSetting.ReadStep(root, point, Shipped.RedirectStyle, static style => Enum.IsDefined(style));

    /// <summary>
    /// Declares one protective hop switch (SPEC-025 HOP-128): core and tenant levels, "stricter" =
    /// both levels allow it, so a level below may turn the switch off and never back on. The
    /// application level is not declared.
    /// </summary>
    /// <param name="member">Member of the <c>HopMode</c> section the switch is stated at.</param>
    /// <param name="shipped">Value the product ships.</param>
    /// <returns>Setting key.</returns>
    private static ConfigKey<bool> ProtectiveSwitch(string member, bool shipped) => Declared
        .Of<bool>("HopMode." + member)
        .At(ConfigLevel.Core, CoreAddress(member))
        .At(ConfigLevel.Tenant, RecordAddress(member))
        .ProtectiveCeiling(static (u, l) => u && l)
        .Default(shipped)
        .Declare();

    /// <summary>
    /// The stricter of two own token lifetimes (SPEC-025 HOP-151): the shorter of two stated lifetimes;
    /// no value — the lifetime of the transaction — is weaker than any stated one.
    /// </summary>
    /// <param name="upper">Lifetime of the level higher in ownership.</param>
    /// <param name="lower">Lifetime of the level lower in ownership.</param>
    /// <returns>The stricter lifetime.</returns>
    private static TimeSpan? ShorterLifetime(TimeSpan? upper, TimeSpan? lower)
    {
        if (upper is not { } upperLifetime)
        {
            return lower;
        }

        if (lower is not { } lowerLifetime)
        {
            return upperLifetime;
        }

        return upperLifetime <= lowerLifetime ? upperLifetime : lowerLifetime;
    }

    /// <summary>
    /// Core address of a member of <c>Veriqa:HopMode</c> — the one spelling of it, both for the
    /// declarations above and for a message naming the value an operator has to repair.
    /// </summary>
    /// <param name="member">Member of the section.</param>
    /// <returns>Address from the root of the host configuration.</returns>
    internal static string CoreAddress(string member) => HopModeOptions.SectionName + ":" + member;

    /// <summary>
    /// Address of a member of the <c>HopMode</c> section inside the record of a level above the core
    /// one — the core address of the member without the core root.
    /// </summary>
    /// <param name="member">Member of the section.</param>
    /// <returns>Address from the root of the record.</returns>
    private static string RecordAddress(string member) => InsideTheLevelRoot(CoreAddress(member));

    /// <summary>
    /// Address inside the node of a level: the core level stands over <see cref="CoreRootSection"/>, and
    /// the record of a level above it mirrors that root, so one relative address serves them all.
    /// </summary>
    /// <param name="address">Address from the root of the host configuration.</param>
    /// <returns>Address inside the node of a level.</returns>
    /// <exception cref="InvalidOperationException">The address is outside the root section.</exception>
    private static string InsideTheLevelRoot(string address) =>
        address.StartsWith(CoreRootPrefix, StringComparison.OrdinalIgnoreCase)
            ? address[CoreRootPrefix.Length..]
            : throw new InvalidOperationException(
                $"The core address '{address}' of a hop setting is outside the section "
                + $"'{CoreRootSection}' the core level of the setting is read over.");

    /// <summary>
    /// A per-channel hop setting (SPEC-025 §11.1, HOP-120, HOP-121, HOP-127): the general member of the
    /// <c>HopMode</c> section and the member of the section of each channel, and the ONE rule that picks
    /// between them for a step of the fallback chain. The parse hook of the key and every synchronous
    /// reader of a level go through it, so the two cannot part.
    /// <list type="bullet">
    /// <item><description>
    /// the step that addresses a channel reads <c>Channels:&lt;type&gt;:&lt;channel member&gt;</c> — for a
    /// built-in and a third-party channel alike, by its type; nothing stated there (absent or empty)
    /// leaves the step unset, so the chain moves on to the general value of the same level;
    /// </description></item>
    /// <item><description>
    /// the step that addresses no channel reads <c>HopMode:&lt;member&gt;</c>. Where that states nothing,
    /// the core level answers with the value the product ships — the core level always speaks — and a
    /// level above it leaves the step unset, ceding to the level below.
    /// </description></item>
    /// </list>
    /// Both addresses are relative to the node of the level: <c>Veriqa</c> of the host configuration at
    /// the core level, the record of the level above it. A level above the core one also leaves unset a
    /// value outside the enumeration of the setting; at the core level such a value is refused at start
    /// by the validation of the hop options.
    /// </summary>
    internal sealed class PerChannelSetting
    {
        /// <summary>
        /// Member of the section of a channel carrying the channel's own value.
        /// </summary>
        private readonly string _channelMember;

        /// <summary>
        /// Creates a per-channel setting.
        /// </summary>
        /// <param name="member">Member of <c>Veriqa:HopMode</c> carrying the node-wide value.</param>
        /// <param name="channelMember">Member of the section of a channel carrying the channel's own value.</param>
        internal PerChannelSetting(string member, string channelMember)
        {
            NodeWideAddress = CoreAddress(member);
            RecordAddress = HopModeConfigKeys.RecordAddress(member);
            _channelMember = channelMember;
        }

        /// <summary>
        /// Address of the node-wide value from the root of the host configuration — the address the key
        /// declares for its core level.
        /// </summary>
        internal string NodeWideAddress { get; }

        /// <summary>
        /// Address of the general value inside the record of a level above the core one — the address
        /// the key declares for those levels.
        /// </summary>
        internal string RecordAddress { get; }

        /// <summary>
        /// Address of a channel's own value from the root of the host configuration.
        /// </summary>
        /// <param name="channelType">Type of the channel, as the dimension of the key carries it.</param>
        /// <returns>Address of the channel member.</returns>
        internal string ChannelAddress(string channelType) =>
            ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName + ":" + channelType + ":" + _channelMember;

        /// <summary>
        /// Address the core level reads a step of the chain at, from the root of the host configuration:
        /// the channel member for the step that addresses a channel, the node-wide member for the step
        /// that addresses none; null for a step addressing a channel with no type, which states nothing.
        /// </summary>
        /// <param name="point">Point of the chain.</param>
        /// <returns>Address of the step, or null.</returns>
        internal string? CoreStepAddress(ConfigDimensionValues point)
        {
            if (!AddressesChannel(point, out var channelType))
            {
                return NodeWideAddress;
            }

            return string.IsNullOrWhiteSpace(channelType) ? null : ChannelAddress(channelType);
        }

        /// <summary>
        /// Value of one step of the chain at one level, by the rule of this type.
        /// </summary>
        /// <typeparam name="TValue">Type of the setting value.</typeparam>
        /// <param name="root">
        /// Node of the level: over <see cref="CoreRootSection"/> of the host configuration at the core
        /// level, over the record of the level above it.
        /// </param>
        /// <param name="point">Point of the chain.</param>
        /// <param name="shipped">Value the product ships for the setting.</param>
        /// <param name="isAdmitted">
        /// Whether a value read is one the setting admits — for an enumeration, a member of it; null when
        /// every value of the type is admitted. Asked above the core level only.
        /// </param>
        /// <returns>Layer value of the step.</returns>
        internal LayerValue<TValue> ReadStep<TValue>(
            ConfigNode root,
            ConfigDimensionValues point,
            TValue shipped,
            Func<TValue, bool>? isAdmitted = null)
            where TValue : struct
        {
            ArgumentNullException.ThrowIfNull(root);

            if (CoreStepAddress(point) is not { } address)
            {
                return LayerValue<TValue>.None;
            }

            var isAboveCore = root.Level is not ConfigLevel.Core;

            // Read as nullable: an empty member — a JSON null or an empty environment variable — is no
            // statement at all and inherits, rather than failing the read.
            if (root.TryRead<TValue?>(InsideTheLevelRoot(address), out var stated) && stated is { } value)
            {
                // A number outside the enumeration is read into its type without a failure. Above the
                // core level it must not become the effective value, so the step is left unset.
                return isAboveCore && isAdmitted is not null && !isAdmitted(value)
                    ? LayerValue<TValue>.None
                    : LayerValue<TValue>.Set(value);
            }

            // The shipped value is the answer of the core level alone: a level above it that states
            // nothing cedes to the level below.
            return isAboveCore || AddressesChannel(point, out _)
                ? LayerValue<TValue>.None
                : LayerValue<TValue>.Set(shipped);
        }

        /// <summary>
        /// Whether a step of the chain addresses a channel, and which one.
        /// </summary>
        /// <param name="point">Point of the chain.</param>
        /// <param name="channelType">Type of the channel; empty when the caller named none.</param>
        /// <returns><c>true</c> when the step addresses the channel dimension.</returns>
        private static bool AddressesChannel(ConfigDimensionValues point, out string channelType)
        {
            if (point.Values is { } values && values.TryGetValue(ConfigDimensionNames.Channel, out var type))
            {
                channelType = type;

                return true;
            }

            channelType = string.Empty;

            return false;
        }
    }
}
