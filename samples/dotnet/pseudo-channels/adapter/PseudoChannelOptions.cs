// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Sample.PseudoChannels.Adapter;

/// <summary>
/// Settings of one pseudo-channel. One adapter class serves both providers, so everything that
/// tells them apart lives here; the host builds one instance per enabled provider
/// (<see cref="ForGoogle"/>, <see cref="ForApple"/>) and hands it to the adapter.
/// </summary>
public sealed class PseudoChannelOptions
{
    /// <summary>
    /// Channel type the adapter instance serves.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Channel name shown on the sign-in button.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Public base address of the host, as the phone reaches it (an absolute URL). The channel link
    /// is built from it.
    /// </summary>
    public required string PublicBaseUrl { get; init; }

    /// <summary>
    /// Secret the host signs the relay webhook with. An empty value rejects every webhook.
    /// </summary>
    public required string RelaySecret { get; init; }

    /// <summary>
    /// How long the question context and the outcome of a transaction are kept in memory. The
    /// adapter has no number of its own: the host supplies the lifetime of a transaction.
    /// </summary>
    public required TimeSpan StateLifetime { get; init; }

    /// <summary>
    /// Whether the provider puts the user's locale into the identity it returns. Declared to the
    /// core as the channel fact of the same meaning.
    /// </summary>
    public bool ProvidesLocale { get; init; }

    /// <summary>
    /// Builds the settings of the Google pseudo-channel. Google lists <c>locale</c> among the claims
    /// of its ID token, so the channel declares it supplies the recipient locale.
    /// </summary>
    /// <param name="publicBaseUrl">Public base address of the host.</param>
    /// <param name="relaySecret">Secret of the relay webhook.</param>
    /// <param name="stateLifetime">Lifetime of the per-transaction state.</param>
    /// <returns>Settings of the Google pseudo-channel.</returns>
    public static PseudoChannelOptions ForGoogle(string publicBaseUrl, string relaySecret, TimeSpan stateLifetime)
        => new()
        {
            ChannelType = PseudoChannelConstants.GoogleChannelType,
            DisplayName = PseudoChannelConstants.GoogleDisplayName,
            PublicBaseUrl = publicBaseUrl,
            RelaySecret = relaySecret,
            StateLifetime = stateLifetime,
            ProvidesLocale = true
        };

    /// <summary>
    /// Builds the settings of the Apple pseudo-channel. No locale is declared: the channel states
    /// only what the provider is known to return.
    /// </summary>
    /// <param name="publicBaseUrl">Public base address of the host.</param>
    /// <param name="relaySecret">Secret of the relay webhook.</param>
    /// <param name="stateLifetime">Lifetime of the per-transaction state.</param>
    /// <returns>Settings of the Apple pseudo-channel.</returns>
    public static PseudoChannelOptions ForApple(string publicBaseUrl, string relaySecret, TimeSpan stateLifetime)
        => new()
        {
            ChannelType = PseudoChannelConstants.AppleChannelType,
            DisplayName = PseudoChannelConstants.AppleDisplayName,
            PublicBaseUrl = publicBaseUrl,
            RelaySecret = relaySecret,
            StateLifetime = stateLifetime,
            ProvidesLocale = false
        };
}
