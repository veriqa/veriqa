// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Instagram;
using Veriqa.Core.ChannelAdapter.Instagram.Configuration;
using Veriqa.Core.ChannelAdapter.Instagram.Constants;
using Veriqa.Core.ChannelAdapter.Meta;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registration of the Instagram channel over <see cref="ChannelAdapterBuilder"/>.
/// The namespace is the builder's own, so <c>adapters.AddInstagram()</c> reads like the registration
/// of every other built-in channel, whatever package it arrives from.
/// </summary>
public static class ChannelAdapterBuilderInstagramExtensions
{
    /// <summary>
    /// Registers the built-in Instagram channel adapter.
    /// </summary>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    public static ChannelAdapterBuilder AddInstagram(this ChannelAdapterBuilder adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Registration skeleton (shared helper)
        adapters.ConfigureChannelOptions<InstagramOptions, InstagramOptionsValidator>(InstagramOptions.SectionName);

        // What the channel contour has to know about Instagram, said by Instagram itself: its keys and
        // the core-level facts read off its options. Both are declared before the enabled check: a
        // disabled channel still belongs to the schema of the deployment. Instagram is webhook-only —
        // it declares no polling transport.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRegisterConfigKeys, InstagramConfigKeyRegistrar>());

        // The catalog of this channel — the declaration half of the same pair. It is registered HERE,
        // at the composition of the channel, so that the key reaches the schema of a deployment that
        // added this channel and no other: the call guards the catalog on its own, so adding the
        // channel twice still leaves one registration.
        adapters.Services.AddVeriqaConfigKeyCatalog(adapters.Configuration, InstagramConfigKeys.Catalog);
        adapters.DeclareCoreChannel<InstagramOptions>(ChannelTypes.Instagram, static options => options.Enabled);

        // If Instagram is disabled, only register the options and validator
        if (!adapters.IsChannelEnabled<InstagramOptions>(InstagramOptions.SectionName, static o => o.Enabled))
        {
            return adapters;
        }

        // Multi-tenancy seam: the default credential provider of the tenant's channel settings
        adapters.Services.AddChannelMultiTenancy();

        // Named IHttpClientFactory client of the Instagram API calls. Explicit timeout instead of the
        // default 100s, so a stuck call on the confirm/decline/expiry path fails fast.
        adapters.Services.AddHttpClient(
            InstagramAdapterConstants.HttpClientName,
            static client => client.Timeout = TimeSpan.FromSeconds(InstagramAdapterConstants.GraphApiTimeoutSeconds));

        // Outbound calls of the channel (the Send API, the user profile read). A singleton, like the
        // adapter that holds it: it keeps no state of a request and resolves the credentials per call.
        adapters.Services.TryAddSingleton<InstagramGraphApiClient>();

        // Register the Instagram adapter through the one registration path every channel takes
        // (singleton per channel type, CA-014). Meta verifies the webhook with a GET handshake, so the
        // channel declares the query parameter whose value the core has to echo back — the
        // verification route is mapped from that declaration, not from the channel's name.
        return adapters.AddChannel<InstagramChannelAdapter>(
            ChannelTypes.Instagram,
            new ChannelRegistrationOptions
            {
                ErrorMessageText = MessageTemplateNaturalKeys.OutcomeErrorInChannel,

                // A sign-in link that cannot be served any more IS answered here: the reply goes to a
                // user who has just written to the account, and the alternative is the silence the
                // user reads as a broken account.
                StaleLinkReplyText = MessageTemplateNaturalKeys.StaleLinkReply,

                // A code the core found no sign-in by is answered with a text of its own: the user
                // who sent it followed no link, and a sentence about an expired link would send them
                // looking for one.
                UnmatchedCodeReplyText = MessageTemplateNaturalKeys.UnmatchedCodeReply,

                // A pressed button that names no transaction IS answered as well: the platform does
                // not always deliver what the link carries, and a user left in front of a silent chat
                // has no way to learn that the code of the sign-in page, or one more scan, takes them
                // on. The adapter hands such a press on only while the deployment keeps the reply
                // switched on, and a typed message that is about no sign-in never gets it.
                UnaddressedReplyText = MessageTemplateNaturalKeys.SignInWithoutLinkReply,
                WebhookVerificationQueryKey = MetaWebhookVerification.HubChallengeQueryKey
            });
    }
}
