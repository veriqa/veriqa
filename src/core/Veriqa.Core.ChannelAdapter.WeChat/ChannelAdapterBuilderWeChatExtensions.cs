// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.WeChat;
using Veriqa.Core.ChannelAdapter.WeChat.Configuration;
using Veriqa.Core.ChannelAdapter.WeChat.Constants;
using Veriqa.Core.ChannelAdapter.WeChat.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registration of the WeChat channel over <see cref="ChannelAdapterBuilder"/>.
/// The namespace is the builder's own, so <c>adapters.AddWeChat()</c> reads like the registration
/// of every other built-in channel, whatever package it arrives from.
/// </summary>
public static class ChannelAdapterBuilderWeChatExtensions
{
    /// <summary>
    /// Registers the built-in WeChat channel adapter.
    /// </summary>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    public static ChannelAdapterBuilder AddWeChat(this ChannelAdapterBuilder adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Registration skeleton (shared helper)
        adapters.ConfigureChannelOptions<WeChatOptions, WeChatOptionsValidator>(WeChatOptions.SectionName);

        // What the channel contour has to know about WeChat, said by WeChat itself: its keys and
        // the core-level facts read off its options. Both are declared before the enabled check: a
        // disabled channel still belongs to the schema of the deployment. WeChat is webhook-only —
        // it declares no polling transport.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRegisterConfigKeys, WeChatConfigKeyRegistrar>());

        // The catalog of this channel — the declaration half of the same pair. It is registered HERE,
        // at the composition of the channel, so that the key reaches the schema of a deployment that
        // added this channel and no other: the call guards the catalog on its own, so adding the
        // channel twice still leaves one registration.
        adapters.Services.AddVeriqaConfigKeyCatalog(adapters.Configuration, WeChatConfigKeys.Catalog);
        adapters.DeclareCoreChannel<WeChatOptions>(ChannelTypes.WeChat, static options => options.Enabled);

        // If WeChat is disabled, only register the options and validator
        if (!adapters.IsChannelEnabled<WeChatOptions>(WeChatOptions.SectionName, static o => o.Enabled))
        {
            return adapters;
        }

        // Multi-tenancy seam: per-tenant client factory + default credential provider
        adapters.Services.AddChannelMultiTenancy();

        // Named IHttpClientFactory client of the Official Account API calls. Explicit timeout
        // instead of the default 100s, so a stuck call fails fast. The request loggers of the
        // factory are removed: the platform takes the access token in the query string of the
        // calls it authorizes, and those loggers write the request address.
        adapters.Services
            .AddHttpClient(
                WeChatAdapterConstants.HttpClientName,
                static client => client.Timeout = TimeSpan.FromSeconds(WeChatAdapterConstants.ApiTimeoutSeconds))
            .RemoveAllLoggers();

        // Builder of the Official Account API client from the tenant's credentials. The client type
        // is the type parameter of the service — the factory key comes from the registration, and
        // the factory keeps one client (with the access token it caches) per tenant.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IChannelClientBuilder<WeChatApiClient>, WeChatClientBuilder>());

        // Register the WeChat adapter through the one registration path every channel takes
        // (singleton per channel type, CA-014). The platform checks the webhook address with a GET
        // request, so the channel declares the query parameter whose value the core has to echo
        // back — the verification route is mapped from that declaration, not from the channel's name.
        return adapters.AddChannel<WeChatChannelAdapter>(
            ChannelTypes.WeChat,
            new ChannelRegistrationOptions
            {
                ErrorMessageText = MessageTemplateNaturalKeys.OutcomeErrorInChannel,

                // A QR code that cannot be served any more IS answered here: the reply goes to a
                // user who has just scanned it, and the alternative is the silence the user reads
                // as a broken account.
                StaleLinkReplyText = MessageTemplateNaturalKeys.StaleLinkReply,
                WebhookVerificationQueryKey = WeChatAdapterConstants.EchoQueryKey
            });
    }
}
