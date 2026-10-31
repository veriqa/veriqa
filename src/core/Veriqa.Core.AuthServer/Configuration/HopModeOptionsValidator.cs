// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Startup validation of the hop mode (SPEC-025 HOP-123): what the reading of the hop settings cannot
/// repair stops the host, with the address of the value to correct. The values judged are those of the
/// core level; the entries of the OIDC clients are asked one thing — whether one of them turns hop on.
/// <list type="bullet">
/// <item><description>
/// the <c>HopPath</c> is judged always, by the one check of its suitability — the getter of the path is
/// read by every projection of a channel, so an unsuitable path would otherwise pass the start and fail
/// each of them;
/// </description></item>
/// <item><description>
/// every node-wide value the startup members of the hop settings read synchronously — the path, the
/// switch, the acceptance of client signals and, where the base is judged, the issuer — has to be
/// readable: one stated in a shape its setting is not read from is refused with its address rather than
/// escaping the validation as an exception;
/// </description></item>
/// <item><description>
/// the per-channel values of every registered channel are read by the very rule their keys read the core
/// level by, and a value that rule cannot read, or a style outside the enumeration, is refused;
/// </description></item>
/// <item><description>
/// where the core level or an OIDC client entry turns hop on for a channel the core level enables, the
/// issuer has to be an absolute HTTPS URL — the base every hop URL is built on. The values of a client
/// entry are not judged, and a level whose records startup cannot read is not asked about: its base is
/// not computable before a request.
/// </description></item>
/// </list>
/// The minimum lifetime of a hop token is not judged here: it is the domain of its key, which refuses a
/// shorter core-level value at start on every level alike (SPEC-025 HOP-151).
/// </summary>
/// <remarks>
/// The base, the per-channel values and the path are read through <see cref="HopModeSettings"/> and the
/// configuration text rather than through the bound options: the options stand for the node-wide section
/// only, while those values are read by the resolver's rules, which the startup facts of
/// <see cref="HopModeSettings"/> share. The bound options are asked one thing — whether a node-wide
/// enumeration value is a member of its enumeration.
/// </remarks>
internal sealed class HopModeOptionsValidator : IValidateOptions<HopModeOptions>
{
    /// <summary>
    /// Address of the issuer in the host configuration — the core level of the base of hop URLs.
    /// </summary>
    private const string IssuerAddress = OidcServerOptions.SectionName + ":" + nameof(OidcServerOptions.Issuer);

    /// <summary>
    /// The single reading of the hop settings — the source of the core-level facts judged here.
    /// </summary>
    private readonly HopModeSettings _settings;

    /// <summary>
    /// Types of the channels registered in this deployment.
    /// </summary>
    private readonly IReadOnlyList<string> _channelTypes;

    /// <summary>
    /// Node of the core level over the root the per-channel values hang from.
    /// </summary>
    private readonly ConfigNode _coreRoot;

    /// <summary>
    /// Host configuration — read for the text a refused value is quoted with.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Creates the validator.
    /// </summary>
    /// <param name="settings">Reading of the hop settings.</param>
    /// <param name="channels">Channels registered in this deployment.</param>
    /// <param name="configuration">Host configuration the options are bound from.</param>
    public HopModeOptionsValidator(
        HopModeSettings settings,
        IEnumerable<CoreChannelDeclaration> channels,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(configuration);

        _settings = settings;
        _channelTypes = [.. channels.Select(channel => channel.ChannelType).Distinct(StringComparer.OrdinalIgnoreCase)];
        _coreRoot = ConfigNode.Over(
            configuration.GetSection(HopModeConfigKeys.CoreRootSection), ConfigLevel.Core, recordLabel: null);
        _configuration = configuration;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HopModeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (TryReadNodeWide(
                failures,
                HopModeConfigKeys.CoreAddress(nameof(HopModeOptions.HopPath)),
                () => _settings.ConfiguredHopPath,
                out var hopPath)
            && HopModeSettings.DescribeUnsuitableHopPath(hopPath) is { } refusal)
        {
            failures.Add(refusal);
        }

        TryReadNodeWide(
            failures,
            HopModeConfigKeys.CoreAddress(nameof(HopModeOptions.AcceptClientSignals)),
            () => _settings.CoreAcceptsClientSignals,
            out _);

        var nodeWideSwitchReadable = TryReadNodeWide(
            failures,
            HopModeConfigKeys.EnabledSetting.NodeWideAddress,
            () => HopModeConfigKeys.ReadEnabledStep(_coreRoot, ConfigDimensionValues.None),
            out _);

        // A value outside an enumeration reaches the bound object only as a raw number.
        AddIfUndefined(failures, HopModeConfigKeys.RedirectStyleSetting.NodeWideAddress, options.RedirectStyle);
        AddIfUndefined(failures, HopModeConfigKeys.CoreAddress(nameof(HopModeOptions.QrMode)), options.QrMode);

        var channelFailures = _channelTypes.SelectMany(ChannelValueFailures).ToList();
        failures.AddRange(channelFailures);

        // Whether hop is on for a channel is decided over the per-channel and the node-wide switch; one
        // that cannot be read leaves the question open, and it is refused above already.
        if (channelFailures.Count == 0
            && nodeWideSwitchReadable
            && (_settings.IsEnabledAtCore || _settings.IsEnabledByClientEntry)
            && TryReadNodeWide(failures, IssuerAddress, () => _settings.CoreBaseUrl, out var baseUrl)
            && baseUrl is null)
        {
            failures.Add(
                $"Configuration value '{IssuerAddress}' has to be an absolute https URL while the hop mode is "
                + "on for a channel: every hop URL a QR code carries is built on it. A trailing slash is allowed.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Reads a node-wide value the way a startup member of the hop settings does, and refuses one the core
    /// level cannot read — a section stated where a single value is read — with its address.
    /// </summary>
    /// <typeparam name="T">Type of the value read.</typeparam>
    /// <param name="failures">Failures collected so far.</param>
    /// <param name="address">Address of the value in the host configuration.</param>
    /// <param name="read">Read of the value.</param>
    /// <param name="value">Value read; the default of the type when it cannot be read.</param>
    /// <returns><c>true</c> when the value was read.</returns>
    private static bool TryReadNodeWide<T>(List<string> failures, string address, Func<T> read, out T value)
    {
        try
        {
            value = read();

            return true;
        }
        catch (InvalidOperationException exception)
        {
            failures.Add(
                $"Configuration value '{address}' cannot be read: {exception.Message} State a single value there.");
            value = default!;

            return false;
        }
    }

    /// <summary>
    /// Adds a refusal of an enumeration value that is not a member of its enumeration.
    /// </summary>
    /// <typeparam name="TEnum">Type of the enumeration.</typeparam>
    /// <param name="failures">Failures collected so far.</param>
    /// <param name="address">Address of the value in the host configuration.</param>
    /// <param name="value">Value read.</param>
    private static void AddIfUndefined<TEnum>(List<string> failures, string address, TEnum value)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            failures.Add(NotAMember<TEnum>(address, value.ToString()));
        }
    }

    /// <summary>
    /// Message refusing a value that is not a member of an enumeration.
    /// </summary>
    /// <typeparam name="TEnum">Type of the enumeration.</typeparam>
    /// <param name="address">Address of the value in the host configuration.</param>
    /// <param name="stated">Value as stated.</param>
    /// <returns>Message naming the address and the admitted members.</returns>
    private static string NotAMember<TEnum>(string address, string? stated)
        where TEnum : struct, Enum =>
        $"Configuration value '{address}' ('{stated}') is not one of {string.Join(", ", Enum.GetNames<TEnum>())}.";

    /// <summary>
    /// Refusals of the per-channel hop values of one channel: each is read by the rule its key reads the
    /// core level by, so a value is refused exactly when a resolution could not use it. Nothing stated —
    /// absent or empty — is no value to refuse: the channel inherits the node-wide one.
    /// </summary>
    /// <param name="channelType">Type of the channel.</param>
    /// <returns>One message per refused value, naming its address.</returns>
    private IEnumerable<string> ChannelValueFailures(string channelType)
    {
        var point = ConfigDimensionValues.Of((ConfigDimensionNames.Channel, channelType));

        if (TryReadStep(() => HopModeConfigKeys.ReadEnabledStep(_coreRoot, point)) is null)
        {
            var address = HopModeConfigKeys.EnabledSetting.ChannelAddress(channelType);

            yield return
                $"Configuration value '{address}' ('{_configuration[address]}') is not true or false. State a "
                + $"boolean, or leave it empty for the channel to take '{HopModeConfigKeys.EnabledSetting.NodeWideAddress}'.";
        }

        if (TryReadStep(() => HopModeConfigKeys.ReadRedirectStyleStep(_coreRoot, point)) is not { } style
            || (style.HasValue && !Enum.IsDefined(style.Value)))
        {
            var address = HopModeConfigKeys.RedirectStyleSetting.ChannelAddress(channelType);

            yield return NotAMember<HopRedirectStyle>(address, _configuration[address]);
        }
    }

    /// <summary>
    /// Reads one step of the chain of a per-channel key at the core level, telling a value the rule of the
    /// key cannot read apart from a read one.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="read">Read of the step by the rule of the key.</param>
    /// <returns>The step read; null when the stated value cannot be read.</returns>
    private static LayerValue<T>? TryReadStep<T>(Func<LayerValue<T>> read)
    {
        try
        {
            return read();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
