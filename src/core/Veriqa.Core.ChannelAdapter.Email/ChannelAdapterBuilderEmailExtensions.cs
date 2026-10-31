// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Email;
using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.Email.Templates;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registration of the Email channel over <see cref="ChannelAdapterBuilder"/>.
/// The namespace is the builder's own, so <c>adapters.AddEmail()</c> stays the same line of code it
/// was while the channel lived in the base assembly — only the package it arrives from changed.
/// </summary>
public static class ChannelAdapterBuilderEmailExtensions
{
    /// <summary>
    /// Registers the built-in Email channel adapter.
    /// Supports the Pull mode (magic link) and the Push mode (the one-tap email).
    /// </summary>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    public static ChannelAdapterBuilder AddEmail(this ChannelAdapterBuilder adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Registration skeleton (shared helper)
        adapters.ConfigureChannelOptions<EmailOptions, EmailOptionsValidator>(EmailOptions.SectionName);

        // What the channel contour has to know about Email, said by Email itself: its configuration
        // keys and its core-level facts. Declared before the enabled check, as they always were.
        // Email pulls its inbound mail in one arrangement only — Push with the mailbox read over
        // IMAP — and declares exactly that; with a webhook provider it declares no polling.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRegisterConfigKeys, EmailConfigKeyRegistrar>());

        // The catalog of this channel — the declaration half of the same pair. It is registered HERE,
        // at the composition of the channel, so that the key reaches the schema of a deployment that
        // added this channel and no other: the call guards the catalog on its own, so adding the
        // channel twice still leaves one registration.
        adapters.Services.AddVeriqaConfigKeyCatalog(adapters.Configuration, EmailConfigKeys.Catalog);
        adapters.DeclareCoreChannel<EmailOptions>(
            ChannelTypes.Email,
            static options => options.Enabled,
            static options => options.ReadsMailboxOverImap());

        // If Email is disabled, only register the options and validator
        if (!adapters.IsChannelEnabled<EmailOptions>(EmailOptions.SectionName, static o => o.Enabled))
        {
            return adapters;
        }

        // The one reading of the sign-in modes — the core ceiling narrowed by the tenant and the
        // application — and the startup check of the records whose set narrows nothing. Both belong to a
        // channel that serves sign-ins, so a disabled Email registers neither.
        adapters.Services.TryAddSingleton<IEmailSignInModes, EmailSignInModes>();
        adapters.Services.AddHostedService<EmailSignInModesRecordCheck>();

        // Register the Pull-mode action-token store. TryAddSingleton keeps a host's own store ahead
        // of this in-process default (same discipline as IEmailPushCorrelationStore below).
        adapters.Services.TryAddSingleton<IEmailActionTokenStore, InMemoryEmailActionTokenStore>();

        // Mail body assembly, shared by any IEmailOutboundSender so that a delivery provider only
        // deals with transport. WHICH wording a mail carries is its message (SPEC-036), so the one
        // seam left here is the composer itself: a host that needs its own slot values or its own
        // render engine replaces it, and TryAddSingleton keeps such a registration ahead of this one.
        adapters.Services.TryAddSingleton<IEmailMessageComposer, EmailMessageComposer>();

        // Push-mode correlation-token store (single-use + dedup by message-id). Registered
        // unconditionally, NOT under PushEnabled: EmailChannelAdapter takes it as a required
        // constructor parameter and is itself registered unconditionally below, so gating the store
        // left the shipped default (Enabled=true, PushEnabled=false — EM-051) with an unresolvable
        // dependency and no way to start the host at all. An idle instance costs three empty
        // dictionaries and does no background work — cleanup is triggered by call count, not by a
        // timer. TryAddSingleton keeps a client's own registration ahead of this default.
        adapters.Services.TryAddSingleton<IEmailPushCorrelationStore, InMemoryEmailPushCorrelationStore>();

        // The mark that charges a letter one attempt of the budget of the confirmation operation however many
        // times it reaches the gate — a link sent twice, a mail redelivered. It keeps its marks in the store
        // above, whichever one is registered last (the Redis satellite replaces it), and the channel
        // processing takes it from the container when it is there.
        adapters.Services.TryAddSingleton<ILetterBudgetChargeMark, EmailLetterBudgetChargeMark>();

        // The mail side of the email completion of the web step (SPEC-041 L51): the auth server asks it
        // through IClaimCompletionEmail, and finds it only when this adapter is added and enabled — which
        // is what makes "no Email adapter" mean "only the Unverified method". Its store follows the
        // discipline of the two above: TryAddSingleton keeps a host's (or the Redis satellite's) own
        // store ahead of the in-process default. The composer of its two mails is registered whatever
        // the provider, like the sign-in mail composer, because the SMTP provider below takes it.
        adapters.Services.TryAddSingleton<IEmailClaimCompletionStore, InMemoryEmailClaimCompletionStore>();
        adapters.Services.TryAddSingleton<ClaimCompletionMailComposer>();
        adapters.Services.TryAddSingleton<IClaimCompletionEmail, EmailClaimCompletion>();

        // Register the email sending provider conditionally by Outbound.Provider
        var emailOptions = adapters.Configuration
            .GetSection(EmailOptions.SectionName)
            .Get<EmailOptions>();

        if (emailOptions?.Outbound?.Provider is EmailOutboundProvider.Smtp)
        {
            // The condition is unchanged — only the form of the registration is. TryAddSingleton
            // keeps a sender the host registered before AddVeriqaChannelAdapters.
            adapters.Services.TryAddSingleton<IEmailOutboundSender, SmtpEmailOutboundSender>();
        }

        // Every other value registers nothing here, and that is the whole of the branch. With
        // Provider=Custom the host registers IEmailOutboundSender itself; anything else — a value
        // outside the enumeration, or a reserved member with no sender behind it — is refused by
        // EmailOptionsValidator at start, by a message that names the configuration address. This
        // registration used to refuse the second case itself, while the container was being built,
        // by a message that named the axis in words only, so the axis was judged in two places and
        // by two kinds of message. It is judged in one now (SPEC-012 §8.2), as are the three other
        // enum axes of this section.

        // Push mode (the one-tap email): register the inbound processor only when Push is enabled — Push
        // is off by default (EM-051). Unlike the correlation store above, this one is safe to gate:
        // it is never a constructor dependency, only resolved from the service provider on the
        // inbound request path, and that path already returns early on !PushEnabled.
        if (emailOptions?.PushEnabled is true)
        {
            // With Provider=Custom the client registers its own IEmailInboundProcessor.
            // TryAddSingleton guarantees priority of the client's registration (as for Outbound Custom).
            if (emailOptions.Inbound?.Provider is not EmailInboundProvider.Custom)
            {
                adapters.Services.TryAddSingleton<IEmailInboundProcessor, DefaultEmailInboundProcessor>();
            }

            // The other way a Push mail arrives: Veriqa reads the mailbox itself over IMAP. The
            // service is registered with Push rather than with the provider, like the polling of
            // the other channels: which tenants pull their mail is answered by the polling tenant
            // source from what the channel declared above, so with a webhook provider the service
            // runs no loop and opens no connection.
            adapters.Services.AddHostedService<EmailImapPollingService>();

            // The client the access token of a mailbox is requested with when its sign-in is by
            // OAuth 2.0. An explicit deadline instead of the default 100 s, and a bounded answer:
            // the token endpoint is configured, but what answers from it is still an external party.
            adapters.Services.AddHttpClient(
                EmailAdapterConstants.ImapOAuthHttpClientName,
                static client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(EmailAdapterConstants.ImapOAuthRequestTimeoutSeconds);
                    client.MaxResponseContentBufferSize = EmailAdapterConstants.ImapOAuthMaxResponseBytes;
                });
        }

        // The channel's own endpoints (its Pull/Push pages and the inbound webhook outside the
        // generic path convention) are mapped by the channel itself, wherever the host maps the
        // Veriqa channel endpoints — registered here, so a disabled Email exposes no route at all.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IChannelEndpointRegistrar, EmailChannelEndpointRegistrar>());

        // The texts of those pages an application may reword for itself (AuthPage.Texts) — the channel's
        // part of the set, contributed beside the pages it maps: a disabled Email has no page to reword.
        adapters.Services.AddCorePageOverridableTexts(EmailAdapterMailStrings.OverridablePageTexts);

        adapters.Services.TryAddSingleton<EmailChannelAdapter>();

        // Register the Email adapter through the one registration path every channel takes. The
        // generic webhook route is NOT mapped: the channel's inbound route is /api/channels/email/inbound,
        // which it maps itself above.
        return adapters.AddChannel(
            ChannelTypes.Email,
            static services => services.GetRequiredService<EmailChannelAdapter>(),
            new ChannelRegistrationOptions
            {
                MapWebhook = false,
                ErrorMessageText = MessageTemplateNaturalKeys.OutcomeErrorInChannel
            });
    }
}
