// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Diagnostics;

/// <summary>
/// Keys of the channel health probe (SPEC-012 §10.6): the outer limit a single probe is waited for
/// (<see cref="ChannelProbeLimit"/>) and the interval a channel is probed at
/// (<see cref="HealthProbeInterval"/>). Both are core-level values resolved through the shared
/// resolver; no precedence of their own is built here (anti-fork CFG-202).
/// <para>
/// The keys are declared in ChannelAdapter because their consumer lives here — the channel health
/// check. The section <c>Veriqa:HealthChecks</c> is named by the auth server as well, but this contour
/// does not reference it, so the address of the limit is written here.
/// </para>
/// </summary>
internal static class ChannelHealthConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this registry — what the one registrar of the deployment declares
    /// and binds. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Canonical name of the key of the probe limit.
    /// </summary>
    public const string ChannelProbeLimitKeyName = "HealthChecks.ChannelProbeLimit";

    /// <summary>
    /// Canonical name of the key of the probe interval.
    /// </summary>
    public const string HealthProbeIntervalKeyName = "Channels.HealthProbeInterval";

    /// <summary>
    /// Core-level address of the probe limit.
    /// </summary>
    private const string CoreChannelProbeLimitAddress = "Veriqa:HealthChecks:ChannelProbeLimit";

    /// <summary>
    /// Member of a channel section carrying the probe interval of that channel.
    /// </summary>
    private const string HealthProbeIntervalMember = "HealthProbeInterval";

    /// <summary>
    /// Core-level address of the probe interval, with the channel dimension in it.
    /// </summary>
    private const string CoreHealthProbeIntervalAddress =
        ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName
        + ":{" + ConfigDimensionNames.Channel + "}:"
        + HealthProbeIntervalMember;

    /// <summary>
    /// Limit of a single probe the product ships with.
    /// </summary>
    private static readonly TimeSpan ShippedChannelProbeLimit = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Interval of the probes of one channel the product ships with.
    /// </summary>
    private static readonly TimeSpan ShippedHealthProbeInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The longest limit the platform timer accepts: a wait bounded by a longer duration is refused by
    /// the runtime at the moment it is started, which is a probe rather than the start of the host.
    /// </summary>
    private static readonly TimeSpan PlatformTimerCeiling = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Domain of the probe limit: a positive duration the platform timer accepts. The policy is the
    /// strict one — a limit silently replaced by the shipped one would leave an operator believing a
    /// channel is given a time it is not given.
    /// </summary>
    private static readonly ConfigValueDomain<TimeSpan> ChannelProbeLimitDomain = new(
        static limit => limit > TimeSpan.Zero && limit <= PlatformTimerCeiling,
        "a positive duration of at most " + PlatformTimerCeiling.ToString("c", CultureInfo.InvariantCulture),
        CoreChannelProbeLimitAddress,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Domain of the probe interval: a positive duration. A zero interval would probe the channel on
    /// every read of the report, which is the very call rate the snapshot exists to prevent.
    /// </summary>
    private static readonly ConfigValueDomain<TimeSpan> HealthProbeIntervalDomain = new(
        static interval => interval > TimeSpan.Zero,
        "a positive duration",
        CoreHealthProbeIntervalAddress,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Outer limit of a single channel probe, on the system timer. A probe that has not answered within
    /// it is reported as timed out, whether or not the adapter observes the cancellation it is handed.
    /// How long the channel is given to answer within that limit is the adapter's own business
    /// (SPEC-042 L44 for an HTTP channel), so the limit is set well above the timeouts adapters ship
    /// with. Core level only.
    /// </summary>
    public static ConfigKey<TimeSpan> ChannelProbeLimit { get; } = Declared
        .Of<TimeSpan>(ChannelProbeLimitKeyName)
        .At(ConfigLevel.Core, CoreChannelProbeLimitAddress)
        .Domain(ChannelProbeLimitDomain)
        .Default(ShippedChannelProbeLimit)
        .Declare();

    /// <summary>
    /// Interval of the probes of one channel: a read of the report finding the last probe of the channel
    /// older than this starts the next one in the background. It bounds the rate of live calls to the
    /// channel platform from above; it does not bound staleness on its own, because only a read moves a
    /// probe. Core level only, with the channel as its identity dimension.
    /// </summary>
    public static ConfigKey<TimeSpan> HealthProbeInterval { get; } = Declared
        .Of<TimeSpan>(HealthProbeIntervalKeyName)
        .Identity(ConfigDimensionNames.Channel)
        .At(ConfigLevel.Core, CoreHealthProbeIntervalAddress)
        .Domain(HealthProbeIntervalDomain)
        .Default(ShippedHealthProbeInterval)
        .Declare();

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;
}
