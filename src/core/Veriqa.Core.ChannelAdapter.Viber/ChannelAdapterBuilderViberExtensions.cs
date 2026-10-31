// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Viber;
using Veriqa.Core.ChannelAdapter.Viber.Configuration;
using Veriqa.Core.ChannelAdapter.Viber.Constants;
using Veriqa.Core.ChannelAdapter.Viber.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registration of the Viber channel over <see cref="ChannelAdapterBuilder"/>.
/// The namespace is the builder's own, so <c>adapters.AddViber()</c> reads like the registration
/// of every other built-in channel, whatever package it arrives from.
/// </summary>
public static class ChannelAdapterBuilderViberExtensions
{
    /// <summary>
    /// Registers the built-in Viber channel adapter.
    /// </summary>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    public static ChannelAdapterBuilder AddViber(this ChannelAdapterBuilder adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Registration skeleton (shared helper)
        adapters.ConfigureChannelOptions<ViberOptions, ViberOptionsValidator>(ViberOptions.SectionName);

        // What the channel contour has to know about Viber, said by Viber itself: its keys and
        // the core-level facts read off its options. Both are declared before the enabled check: a
        // disabled channel still belongs to the schema of the deployment. Viber is webhook-only —
        // it declares no polling transport.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRegisterConfigKeys, ViberConfigKeyRegistrar>());

        // The catalog of this channel — the declaration half of the same pair. It is registered HERE,
        // at the composition of the channel, so that the key reaches the schema of a deployment that
        // added this channel and no other: the call guards the catalog on its own, so adding the
        // channel twice still leaves one registration.
        adapters.Services.AddVeriqaConfigKeyCatalog(adapters.Configuration, ViberConfigKeys.Catalog);
        adapters.DeclareCoreChannel<ViberOptions>(ChannelTypes.Viber, static options => options.Enabled);

        // If Viber is disabled, only register the options and validator
        if (!adapters.IsChannelEnabled<ViberOptions>(ViberOptions.SectionName, static o => o.Enabled))
        {
            return adapters;
        }

        // Multi-tenancy seam: the default credential provider of the tenant's channel settings
        adapters.Services.AddChannelMultiTenancy();

        // Named IHttpClientFactory client of the Bot API calls. Explicit timeout instead of the
        // default 100s, so a stuck call on the confirm/decline/expiry path fails fast.
        adapters.Services.AddHttpClient(
            ViberAdapterConstants.HttpClientName,
            static client => client.Timeout = TimeSpan.FromSeconds(ViberAdapterConstants.BotApiTimeoutSeconds));

        // Outbound calls of the channel (a message to a user, the webhook of the bot). A singleton,
        // like the adapter that holds it: it keeps no state of a request and resolves the
        // credentials per call.
        adapters.Services.TryAddSingleton<ViberBotApiClient>();

        // Registers the webhook of the default tenant's bot once the host accepts requests.
        adapters.Services.AddHostedService<ViberWebhookInitializerService>();

        // Register the Viber adapter through the one registration path every channel takes
        // (singleton per channel type, CA-014): the webhook route and the error status text come
        // from the declaration.
        return adapters.AddChannel<ViberChannelAdapter>(
            ChannelTypes.Viber,
            new ChannelRegistrationOptions
            {
                ErrorMessageText = MessageTemplateNaturalKeys.OutcomeErrorInChannel

                // No StaleLinkReplyText, deliberately: bots of this platform run on commercial terms,
                // under which an outbound message may be billed to the installation, so a link that
                // cannot be served any more is not answered on the channel's own initiative.
            });
    }
}
