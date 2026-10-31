// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The one rule the chat of a channel is named by to the user. The pages of the core name the chat by it
/// in the texts telling the person where to send the code; the text of a confirmation question names the
/// chat of the channel it is asked in by the same rule, so a chat is called the same in the chat and on a
/// page.
/// </summary>
internal static class ChannelBotNames
{
    // What a caller does when the name read cannot be shown: the tail of the reported line.
    private const string AdapterFallback = "the name is asked of the adapter";
    private const string NoNameFallback = "its texts go without the name";

    /// <summary>
    /// Names the chat of a channel by the rule of the overload that takes the adapter, for the registered
    /// adapter of <paramref name="channelType"/>; a channel no registered adapter serves is named by the
    /// configuration alone.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="resolver">Resolver the configured name of the channel is read by.</param>
    /// <param name="context">Resolution context of the transaction; its tenant is the tenant the adapter
    /// is asked under.</param>
    /// <param name="logger">Logger a rejected or failed reading is reported to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name; null — none to show.</returns>
    public static Task<string?> ReadAsync(
        string channelType,
        IEnumerable<IChannelAdapter> adapters,
        IConfigurationResolver resolver,
        ResolutionContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var adapter = adapters.FirstOrDefault(
            candidate => string.Equals(candidate.ChannelType, channelType, StringComparison.Ordinal));

        return ReadCoreAsync(channelType, adapter, resolver, context, logger, cancellationToken);
    }

    /// <summary>
    /// Names the chat of a channel and degrades the name to a safe value. A name a level of the
    /// configuration states for the channel (<see cref="ChannelChatConfigKeys.BotName"/>) comes first and
    /// is shown as written: the integrator sees what the chat is called on the side of the user. Without
    /// one the name is asked of the adapter's optional <see cref="IChannelBotName"/> (public SPI). A blank
    /// name, a name over the limit of a display label, a failed reading of the configuration and a failure
    /// of the adapter all give null, and the caller words its text without a name.
    /// </summary>
    /// <remarks>
    /// The adapter is asked under the tenant scope of <paramref name="context"/>, opened here: the chat
    /// belongs to the bot of the tenant of the transaction, whoever runs the request and whether or not it
    /// runs inside a scope of its own. The call is third-party code and may reach the platform of the
    /// channel, so nothing it throws reaches the caller.
    /// </remarks>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="resolver">Resolver the configured name of the channel is read by.</param>
    /// <param name="context">Resolution context of the transaction; its tenant is the tenant the adapter
    /// is asked under.</param>
    /// <param name="logger">Logger a rejected or failed reading is reported to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name; null — none to show.</returns>
    public static Task<string?> ReadAsync(
        IChannelAdapter adapter,
        IConfigurationResolver resolver,
        ResolutionContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        return ReadCoreAsync(adapter.ChannelType, adapter, resolver, context, logger, cancellationToken);
    }

    /// <summary>
    /// The rule of both overloads of <c>ReadAsync</c>, for a channel whose adapter may be absent.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="adapter">Adapter of the channel; null — none is registered.</param>
    /// <param name="resolver">Resolver the configured name of the channel is read by.</param>
    /// <param name="context">Resolution context of the transaction.</param>
    /// <param name="logger">Logger a rejected or failed reading is reported to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name; null — none to show.</returns>
    private static async Task<string?> ReadCoreAsync(
        string channelType,
        IChannelAdapter? adapter,
        IConfigurationResolver resolver,
        ResolutionContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        var configured = await ReadConfiguredAsync(channelType, resolver, context, logger, cancellationToken);
        if (configured is not null)
        {
            return configured;
        }

        if (adapter is not IChannelBotName source)
        {
            return null;
        }

        string? botName;

        try
        {
            using (ChannelTenantContext.BeginScope(context.TenantId))
            {
                botName = await source.GetBotNameAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Channel {ChannelType} failed to provide the name of its bot — its texts go without the name",
                channelType);
            return null;
        }

        return Displayable(botName, channelType, NoNameFallback, logger);
    }

    /// <summary>
    /// Reads the name the chat of ONE channel is shown under, as a level states it. A name over the limit
    /// of a display label and a read that fails are reported and answered with null, and the caller then
    /// asks the adapter of the channel for the name.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="resolver">Configuration resolver.</param>
    /// <param name="context">Resolution context of the transaction.</param>
    /// <param name="logger">Logger a rejected or failed reading is reported to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stated name; null — no level states one, or what is stated cannot be shown.</returns>
    private static async Task<string?> ReadConfiguredAsync(
        string channelType,
        IConfigurationResolver resolver,
        ResolutionContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? botName;

        try
        {
            var stated = await resolver.ResolveAsync(
                ChannelChatConfigKeys.BotName,
                context,
                ConfigDimensionValues.Of((ConfigDimensionNames.Channel, channelType)),
                cancellationToken);

            botName = stated.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Failed to read the configured bot name of channel {ChannelType} — the name is asked of the adapter",
                channelType);

            return null;
        }

        return Displayable(botName, channelType, AdapterFallback, logger);
    }

    /// <summary>
    /// The name as it may be shown: a blank name gives null, and a name over the limit of a display label
    /// is reported and gives null.
    /// </summary>
    /// <param name="botName">The name read.</param>
    /// <param name="channelType">Channel type.</param>
    /// <param name="fallback">What the caller does without the name — the tail of the reported line.</param>
    /// <param name="logger">Logger a rejected name is reported to.</param>
    /// <returns>The name; null — none to show.</returns>
    private static string? Displayable(string? botName, string channelType, string fallback, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(botName))
        {
            return null;
        }

        if (botName.Length > CustomChannelConstants.MaxDisplayNameLength)
        {
            logger.LogWarning(
                "The bot name of channel {ChannelType} is {Length} characters long, over the " +
                "{Limit}-character limit — {Fallback}",
                channelType,
                botName.Length,
                CustomChannelConstants.MaxDisplayNameLength,
                fallback);

            return null;
        }

        return botName;
    }
}
