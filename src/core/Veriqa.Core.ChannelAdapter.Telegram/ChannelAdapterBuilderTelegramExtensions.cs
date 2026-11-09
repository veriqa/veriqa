// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Telegram.Bot;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Telegram;
using Veriqa.Core.ChannelAdapter.Telegram.Configuration;
using Veriqa.Core.ChannelAdapter.Telegram.Constants;
using Veriqa.Core.ChannelAdapter.Telegram.Enums;
using Veriqa.Core.ChannelAdapter.Telegram.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Telegram.Services;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registration of the Telegram channel over <see cref="ChannelAdapterBuilder"/>.
/// The namespace is the builder's own, so <c>adapters.AddTelegram()</c> stays the same line of code it
/// was while the channel lived in the base assembly — only the package it arrives from changed.
/// </summary>
public static class ChannelAdapterBuilderTelegramExtensions
{
    /// <summary>
    /// Registers the built-in Telegram channel adapter.
    /// </summary>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    public static ChannelAdapterBuilder AddTelegram(this ChannelAdapterBuilder adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Registration skeleton (shared helper)
        adapters.ConfigureChannelOptions<TelegramOptions, TelegramOptionsValidator>(TelegramOptions.SectionName);

        // What the channel contour has to know about Telegram, said by Telegram itself: its keys and
        // the core-level facts read off its options — both before the enabled check, as always.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRegisterConfigKeys, TelegramConfigKeyRegistrar>());

        // The catalog of this channel — the declaration half of the same pair. It is registered HERE,
        // at the composition of the channel, so that the key reaches the schema of a deployment that
        // added this channel and no other: the call guards the catalog on its own, so adding the
        // channel twice still leaves one registration.
        adapters.Services.AddVeriqaConfigKeyCatalog(adapters.Configuration, TelegramConfigKeys.Catalog);
        adapters.DeclareCoreChannel<TelegramOptions>(
            ChannelTypes.Telegram,
            static options => options.Enabled,
            static options => options.UpdateMode is TelegramUpdateMode.Polling);

        // If Telegram is disabled, only register the options and validator
        if (!adapters.IsChannelEnabled<TelegramOptions>(TelegramOptions.SectionName, static o => o.Enabled))
        {
            return adapters;
        }

        // Named IHttpClientFactory client every Bot API request goes through. Explicit timeout instead of
        // the default 100s, so a stuck request fails instead of holding the bot; it stays above the
        // long-poll timeout of getUpdates (TelegramAdapterConstants.BotApiTimeoutSeconds). The request
        // loggers of the factory are removed: the Bot API takes the bot token in the request path
        // (/bot<token>/..., /file/bot<token>/...), and those loggers write the request address.
        var botApiClient = adapters.Services
            .AddHttpClient(
                TelegramAdapterConstants.HttpClientName,
                static client => client.Timeout = TimeSpan.FromSeconds(TelegramAdapterConstants.BotApiTimeoutSeconds))
            .RemoveAllLoggers();

        // A host may put a resilience handler on every client (the standard host does, through
        // ConfigureHttpClientDefaults). Its attempt and total timeouts are shorter than the long poll of
        // getUpdates, and it re-sends a request — a sendMessage retried is a second message to the user —
        // so any inherited handler is removed: this client keeps only its own timeout. The package offers
        // the removal as an experimental API; its diagnostic is suppressed around this call only.
#pragma warning disable EXTEXP0001
        botApiClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        // Multi-tenancy seam: per-tenant client factory + default credential provider
        adapters.Services.AddChannelMultiTenancy();

        // Builder of the Telegram Bot client from the tenant's token (replaces the single-token singleton).
        // The client type is the type parameter of the service — the factory key comes from the
        // registration, not from a property an implementation could declare wrongly.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IChannelClientBuilder<ITelegramBotClient>, TelegramClientBuilder>());

        // Register the bot information provider. TryAddSingleton, like every shipped default of a
        // one-implementation port here: a host that registered its own provider beforehand keeps it.
        adapters.Services.TryAddSingleton<IBotInfoProvider, BotInfoProvider>();

        // Register the hosted services (each checks the operating mode internally)
        adapters.Services.AddHostedService<TelegramWebhookInitializerService>();
        adapters.Services.AddHostedService<TelegramPollingService>();

        // Register the Telegram adapter through the one registration path every channel takes
        // (singleton per channel type, CA-014).
        return adapters.AddChannel<TelegramChannelAdapter>(
            ChannelTypes.Telegram,
            new ChannelRegistrationOptions
            {
                ErrorMessageText = MessageTemplateNaturalKeys.OutcomeErrorInChannel,

                // A sign-in link that cannot be served any more IS answered here: a message to a user
                // who already opened this chat costs the installation nothing on this platform, and
                // the alternative is the silence the user reads as a broken bot.
                StaleLinkReplyText = MessageTemplateNaturalKeys.StaleLinkReply
            });
    }
}
