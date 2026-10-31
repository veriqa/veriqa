// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Http.Configuration;

/// <summary>
/// The part of the start-up checks of the HTTP registrations that needs the built container
/// (SPEC-042 R7, E55):
/// <list type="number">
/// <item>an HTTP registration whose name another channel of the host also carries stops the host —
/// a second declaration of the name, or, for a disabled registration, any adapter of the name: the
/// cases the channel registry cannot see, because a disabled registration adds no adapter to it and a
/// channel registered by code need not declare itself;</item>
/// <item>a registration reaching its adapter over plain <c>http</c>, and one whose transport-secret
/// rotation is still in progress, are warned about by name, and the host starts.</item>
/// </list>
/// Everything that can be checked from the configuration alone is checked earlier, at composition, by
/// <see cref="HttpChannelRegistrationReader"/>. Like the other start-up passes of the channel contour
/// this is a hosted lifecycle service: <see cref="StartingAsync"/> runs before the web server binds.
/// </summary>
internal sealed class HttpChannelStartupCheck : IHostedLifecycleService
{
    /// <summary>
    /// What the HTTP channel sections declared at start.
    /// </summary>
    private readonly HttpChannelRegistrations _registrations;

    /// <summary>
    /// Core-level declarations of every channel of the host.
    /// </summary>
    private readonly IEnumerable<CoreChannelDeclaration> _declarations;

    /// <summary>
    /// All channel adapters registered in the container (built-in and third-party).
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Logger of the start-up warnings.
    /// </summary>
    private readonly ILogger<HttpChannelStartupCheck> _logger;

    /// <summary>
    /// Creates the start-up check.
    /// </summary>
    /// <param name="registrations">What the HTTP channel sections declared at start.</param>
    /// <param name="declarations">Core-level declarations of every channel of the host.</param>
    /// <param name="adapters">All channel adapters registered in the container.</param>
    /// <param name="logger">Logger.</param>
    public HttpChannelStartupCheck(
        HttpChannelRegistrations registrations,
        IEnumerable<CoreChannelDeclaration> declarations,
        IEnumerable<IChannelAdapter> adapters,
        ILogger<HttpChannelStartupCheck> logger)
    {
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
        _declarations = declarations ?? throw new ArgumentNullException(nameof(declarations));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Refuses a name another channel of the host also declares, then writes the warnings of the
    /// enabled registrations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="InvalidOperationException">An HTTP registration shares its name with another channel.</exception>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var declaredTypes = _declarations.Select(declaration => declaration.ChannelType).ToList();
        var servedTypes = _adapters.Select(adapter => adapter.ChannelType).ToHashSet(StringComparer.Ordinal);
        var enabledTypes = _registrations.Enabled
            .Select(registration => registration.ChannelType)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var channelType in _registrations.ChannelTypes)
        {
            // Every HTTP registration adds exactly one declaration of its own name, so a second one
            // comes from a channel registered by code. A disabled registration adds no adapter, so an
            // adapter of its name is a channel registered by code that declared nothing; the adapter of
            // an enabled registration is checked against the others by the channel registry.
            var declaredTwice =
                declaredTypes.Count(declared => string.Equals(declared, channelType, StringComparison.Ordinal)) > 1;
            var servedWhileDisabled = !enabledTypes.Contains(channelType) && servedTypes.Contains(channelType);

            if (declaredTwice || servedWhileDisabled)
            {
                throw new InvalidOperationException(
                    $"HTTP channel '{channelType}' (section '{SectionOf(channelType)}') has the name of another "
                    + "channel registered by this host. Every channel type is registered exactly once: rename the "
                    + "section of the HTTP channel.");
            }
        }

        foreach (var registration in _registrations.Enabled)
        {
            if (registration.UsesInsecureHttp)
            {
                _logger.LogWarning(
                    "HTTP channel {ChannelType} reaches its external adapter over plain http, admitted by "
                    + "'{ConfigPath}' = true: the calls are readable and the answers forgeable in the network "
                    + "between the host and the adapter, which the operator declared isolated.",
                    registration.ChannelType,
                    PathOf(registration.ChannelType, HttpChannelConfigKeys.AllowInsecureHttpMember));
            }

            if (registration.IsRotatingTransportSecret)
            {
                _logger.LogWarning(
                    "HTTP channel {ChannelType} still accepts its previous transport secret ('{ConfigPath}' is set): "
                    + "the rotation is not finished until the key is removed.",
                    registration.ChannelType,
                    PathOf(registration.ChannelType, HttpChannelConfigKeys.PreviousTransportSecretMember));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// No-op: the checks run in <see cref="StartingAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the checks run in <see cref="StartingAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Path of the section of a channel, as the operator writes it.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>The path.</returns>
    private static string SectionOf(string channelType) =>
        ConfigurationPath.Combine(ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName, channelType);

    /// <summary>
    /// Path of a key of the <c>Http</c> subsection of a channel.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="member">Member of the subsection.</param>
    /// <returns>The path.</returns>
    private static string PathOf(string channelType, string member) =>
        ConfigurationPath.Combine(SectionOf(channelType), HttpChannelConfigKeys.HttpSectionName, member);
}
