// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

using Microsoft.Extensions.Configuration;

using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;

/// <summary>
/// The three compositions of the wrapper a run needs, each of the SAME channel type: two registrations
/// under different names in one composition would not do — the adapter picks its secret by the
/// <c>channel_type</c> of the envelope, and two equal secrets are refused at start (SPEC-042 E55).
/// All three are started before the first scenario.
/// <list type="bullet">
/// <item><see cref="Working"/> — the configuration as the operator gives it; the receiver and every
/// scenario but the refusals of the wrapper use it;</item>
/// <item><see cref="ForeignSecret"/> — the same configuration with a random current transport secret
/// and no previous one;</item>
/// <item><see cref="ShiftedClock"/> — the same configuration with a clock of its own, which
/// <see cref="Clock"/> moves.</item>
/// </list>
/// </summary>
internal sealed class WrapperCompositions : IAsyncDisposable
{
    /// <summary>
    /// Prefix of the Standard Webhooks serialization of a secret.
    /// </summary>
    private const string SecretPrefix = "whsec_";

    /// <summary>
    /// Length of the random foreign secret, in bytes — within the 24–64 bytes a transport secret has.
    /// </summary>
    private const int ForeignSecretLength = 32;

    /// <summary>
    /// Creates the set.
    /// </summary>
    /// <param name="working">The working composition.</param>
    /// <param name="foreignSecret">The composition with a foreign secret.</param>
    /// <param name="shiftedClock">The composition with a shifted clock.</param>
    /// <param name="clock">The clock of <paramref name="shiftedClock"/>.</param>
    private WrapperCompositions(
        WrapperComposition working,
        WrapperComposition foreignSecret,
        WrapperComposition shiftedClock,
        ShiftedTimeProvider clock)
    {
        Working = working;
        ForeignSecret = foreignSecret;
        ShiftedClock = shiftedClock;
        Clock = clock;
    }

    /// <summary>
    /// The configuration as the operator gives it.
    /// </summary>
    public WrapperComposition Working { get; }

    /// <summary>
    /// The configuration with a random transport secret the adapter does not know.
    /// </summary>
    public WrapperComposition ForeignSecret { get; }

    /// <summary>
    /// The configuration with the clock <see cref="Clock"/>.
    /// </summary>
    public WrapperComposition ShiftedClock { get; }

    /// <summary>
    /// The clock of <see cref="ShiftedClock"/>; the system clock until a scenario shifts it.
    /// </summary>
    public ShiftedTimeProvider Clock { get; }

    /// <summary>
    /// Starts the three compositions of the channel under test.
    /// </summary>
    /// <param name="configuration">Configuration of the suite.</param>
    /// <param name="channelType">The channel under test.</param>
    /// <param name="seams">What every composition carries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The started compositions.</returns>
    /// <exception cref="ConformanceSetupException">The channel is not an HTTP channel, or a composition does not start.</exception>
    public static async Task<WrapperCompositions> StartAsync(
        IConfiguration configuration,
        string channelType,
        WrapperSeams seams,
        CancellationToken cancellationToken)
    {
        var httpSection = ConfigurationPath.Combine(
            ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName,
            channelType,
            ConformanceConfigKeys.ChannelHttpSectionName);

        if (!configuration.GetSection(httpSection).Exists())
        {
            throw new ConformanceSetupException(
                "'" + httpSection + "' is not in the configuration: the channel named by '"
                + ConformanceConfigKeys.PathOf(ConformanceConfigKeys.ChannelType) + "' is not an HTTP channel.");
        }

        var clock = new ShiftedTimeProvider();
        var started = new List<WrapperComposition>();

        try
        {
            var working = await StartAndKeepAsync(new WrapperVariant("working", null, null));
            var foreignSecret = await StartAndKeepAsync(new WrapperVariant("foreign secret", ForeignSecretOverlay(httpSection), null));
            var shiftedClock = await StartAndKeepAsync(new WrapperVariant("shifted clock", null, clock));

            return new WrapperCompositions(working, foreignSecret, shiftedClock, clock);
        }
        catch
        {
            foreach (var composition in started)
            {
                await composition.DisposeAsync();
            }

            throw;
        }

        async Task<WrapperComposition> StartAndKeepAsync(WrapperVariant variant)
        {
            var composition = await WrapperComposition.StartAsync(configuration, channelType, seams, variant, cancellationToken);
            started.Add(composition);
            return composition;
        }
    }

    /// <summary>
    /// Stops the compositions.
    /// </summary>
    /// <returns>A task that completes when all three are stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await ShiftedClock.DisposeAsync();
        await ForeignSecret.DisposeAsync();
        await Working.DisposeAsync();
    }

    /// <summary>
    /// A random transport secret in the Standard Webhooks serialization — one the adapter and the
    /// registration under test do not know.
    /// </summary>
    /// <returns>The secret; kept in memory, never printed.</returns>
    public static string NewForeignSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(ForeignSecretLength));

    /// <summary>
    /// The keys that make the configuration sign with a secret the adapter does not know: a random
    /// current secret, and the previous one taken away (an empty value is an unset key of the channel).
    /// </summary>
    /// <param name="httpSection">Path of the <c>Http</c> subsection of the channel.</param>
    /// <returns>The overlay.</returns>
    private static Dictionary<string, string?> ForeignSecretOverlay(string httpSection) => new(StringComparer.OrdinalIgnoreCase)
    {
        [ConfigurationPath.Combine(httpSection, ConformanceConfigKeys.TransportSecretMember)] = NewForeignSecret(),
        [ConfigurationPath.Combine(httpSection, ConformanceConfigKeys.PreviousTransportSecretMember)] = string.Empty
    };
}
