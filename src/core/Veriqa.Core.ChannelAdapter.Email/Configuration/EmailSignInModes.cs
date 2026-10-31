// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Reads the sign-in modes of Email through the canonical resolver: the set of
/// <see cref="EmailConfigKeys.SignInModes"/> — the core ceiling narrowed by the tenant and the
/// application — <see cref="EmailConfigKeys.PreferredSignInMode"/> and <see cref="EmailConfigKeys.PullMethods"/>,
/// all in one resolution scope.
/// A channel switched off offers no mode at all, and so does a channel whose settings could not be read.
/// The switch is read from the same <see cref="IOptions{TOptions}"/> snapshot as the ceiling and the
/// guards of the Email endpoints (SPEC-016 EM-054): a reload does not change the modes a running host
/// offers.
/// </summary>
internal sealed class EmailSignInModes : IEmailSignInModes
{
    /// <summary>
    /// Canonical layer resolver.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Global Email channel settings as bound at the first read — read for the switch of the channel alone.
    /// </summary>
    private readonly IOptions<EmailOptions> _options;

    /// <summary>
    /// Logger of the degradation on unreadable settings.
    /// </summary>
    private readonly ILogger<EmailSignInModes> _logger;

    /// <summary>
    /// Creates the reading of the sign-in modes.
    /// </summary>
    /// <param name="resolver">Canonical layer resolver.</param>
    /// <param name="options">Global Email channel settings as bound at the first read.</param>
    /// <param name="logger">Logger.</param>
    public EmailSignInModes(
        IConfigurationResolver resolver,
        IOptions<EmailOptions> options,
        ILogger<EmailSignInModes> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<EmailSignInModeSet> ResolveAsync(ResolutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var scope = new ConfigResolutionScope(context);

        var modes = (await _resolver.ResolveAsync(
            EmailConfigKeys.SignInModes,
            scope,
            ConfigDimensionValues.None,
            cancellationToken)).Value ?? [];

        var preferred = (await _resolver.ResolveAsync(
            EmailConfigKeys.PreferredSignInMode,
            scope,
            ConfigDimensionValues.None,
            cancellationToken)).Value;

        var pullMethods = (await _resolver.ResolveAsync(
            EmailConfigKeys.PullMethods,
            scope,
            ConfigDimensionValues.None,
            cancellationToken)).Value ?? EmailAdapterConfigDefaults.DefaultPullMethods;

        var enabled = IsChannelEnabled();

        return new EmailSignInModeSet(
            Pull: enabled && modes.Contains(EmailMode.Pull),
            Push: enabled && modes.Contains(EmailMode.Push),
            PreferredMode: preferred,
            PullMethods: CanonicalPullMethods(pullMethods));
    }

    /// <summary>
    /// The set of what the sign-in mail carries as a set: the methods it names, each once, in the order of the
    /// enumeration — so <c>["Code", "MagicLink"]</c> and <c>["MagicLink", "Code"]</c> are one value. A set that
    /// names none of them is the shipped one; the domain of the key does not let such a set through, and this
    /// keeps the reading total all the same.
    /// </summary>
    /// <param name="methods">Set as the levels resolved it.</param>
    /// <returns>The canonical set.</returns>
    private static IReadOnlyList<EmailPullMethod> CanonicalPullMethods(IReadOnlyList<EmailPullMethod> methods)
    {
        IReadOnlyList<EmailPullMethod> canonical = [.. Enum.GetValues<EmailPullMethod>().Where(methods.Contains)];

        return canonical.Count > 0 ? canonical : EmailAdapterConfigDefaults.DefaultPullMethods;
    }

    /// <summary>
    /// The switch of the channel, read so that settings that cannot be bound do not escape as an
    /// exception: the first read of the snapshot binds the section and runs its validator, and the guards
    /// of the sign-in page, the hop pages and the address form ask this reading on requests that have no
    /// exception handler. Unreadable settings are the answer "no mode" — the one the guard of the core
    /// level gives a channel it cannot read. The read is a whole bind-and-validate pass, so the guard is
    /// as wide as the read; cancellation is not a configuration failure and stays unhandled.
    /// </summary>
    /// <returns><c>true</c> when the channel is switched on; <c>false</c> when it is off or unreadable.</returns>
    private bool IsChannelEnabled()
    {
        try
        {
            return _options.Value.Enabled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Email settings could not be read; no Email sign-in mode is offered.");

            return false;
        }
    }
}
