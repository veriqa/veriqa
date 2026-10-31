// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

using Veriqa.Core.TransactionEngine.Store;
using Veriqa.Sample.PseudoChannels.Adapter;

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// The sign-in at the provider: one standard OpenID Connect handler per enabled provider, named by
/// the channel type. The handler does the whole protocol — the redirect, the state and nonce, the
/// code exchange, the validation of the ID token; the sample adds only what happens once the
/// provider's answer has been validated.
/// </summary>
internal static class ProviderSignIn
{
    /// <summary>
    /// Registers the Google handler.
    /// </summary>
    /// <param name="authentication">Authentication builder of the host.</param>
    /// <param name="settings">Google credentials.</param>
    /// <returns>The builder for chaining.</returns>
    public static AuthenticationBuilder AddGoogleProvider(this AuthenticationBuilder authentication, GoogleSettings settings)
        => authentication.AddOpenIdConnect(PseudoChannelConstants.GoogleChannelType, options =>
        {
            ConfigureCommon(options, PseudoChannelConstants.GoogleChannelType);

            options.Authority = PseudoHostConstants.Google.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;

            // The code comes back in the query of a plain redirect: no intermediate auto-posting page.
            options.ResponseMode = OpenIdConnectResponseMode.Query;

            // openid and profile are requested by the handler already.
            options.Scope.Add(OpenIdConnectScope.Email);

            // A phone that is signed in to one Google account would otherwise pass without a single
            // screen; the chooser at least shows which account is about to be used.
            options.Prompt = PseudoHostConstants.Google.Prompt;
        });

    /// <summary>
    /// Registers the Sign in with Apple handler.
    /// </summary>
    /// <param name="authentication">Authentication builder of the host.</param>
    /// <param name="settings">Apple credentials.</param>
    /// <param name="clientSecret">Maker of the per-request client secret.</param>
    /// <returns>The builder for chaining.</returns>
    public static AuthenticationBuilder AddAppleProvider(
        this AuthenticationBuilder authentication,
        AppleSettings settings,
        AppleClientSecret clientSecret)
        => authentication.AddOpenIdConnect(PseudoChannelConstants.AppleChannelType, options =>
        {
            ConfigureCommon(options, PseudoChannelConstants.AppleChannelType);

            options.Authority = PseudoHostConstants.Apple.Authority;
            options.ClientId = settings.ClientId;

            // Apple names its own scopes — openid, email and name — and has no "profile". The name
            // is not requested: Apple returns it next to the ID token rather than inside it, so it
            // is not covered by the signature the handler validates.
            options.Scope.Clear();
            options.Scope.Add(OpenIdConnectScope.OpenId);
            options.Scope.Add(OpenIdConnectScope.Email);

            // Apple requires form_post whenever a scope is requested.
            options.ResponseMode = OpenIdConnectResponseMode.FormPost;

            // Only the parameters Apple documents are sent.
            options.UsePkce = false;

            // There is no static client secret: a token signed with the developer key is made for
            // each exchange of the code.
            options.Events.OnAuthorizationCodeReceived = context =>
            {
                if (context.TokenEndpointRequest is { } tokenRequest)
                {
                    tokenRequest.ClientSecret = clientSecret.Create();
                }

                return Task.CompletedTask;
            };
        });

    /// <summary>
    /// Settings both providers share.
    /// </summary>
    /// <param name="options">Handler options.</param>
    /// <param name="channelType">Channel type the handler serves; also its scheme name.</param>
    private static void ConfigureCommon(OpenIdConnectOptions options, string channelType)
    {
        // The handler signs in to the short-lived provider cookie, not to the scheme the desktop
        // page reads: a sign-in at the provider is a step of the transaction, not a session.
        options.SignInScheme = PseudoHostConstants.Schemes.ProviderCookie;

        // The return address registered at the provider: {PublicBaseUrl}/pseudo/{type}/callback.
        options.CallbackPath = PseudoHostConstants.Routes.ForChannel(channelType, PseudoHostConstants.Routes.CallbackSegment);

        options.ResponseType = OpenIdConnectResponseType.Code;

        // Keep the claims under their OpenID Connect names: the relay reads them by those names.
        options.MapInboundClaims = false;

        // Everything the sample needs is in the ID token; the tokens themselves are not kept.
        options.GetClaimsFromUserInfoEndpoint = false;
        options.SaveTokens = false;

        options.Events.OnTicketReceived = OnTicketReceivedAsync;
        options.Events.OnRemoteFailure = OnRemoteFailureAsync;
    }

    /// <summary>
    /// The provider's answer has been validated: the sample, playing the platform, tells the core
    /// that the sign-in has started. The core then hands the question to the adapter inside this
    /// very call, so by the time the phone lands on the question page the context is there.
    /// </summary>
    /// <param name="context">Event context.</param>
    /// <returns>A task that completes when the event is handled.</returns>
    private static async Task OnTicketReceivedAsync(TicketReceivedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var cancellationToken = context.HttpContext.RequestAborted;
        var channelType = context.Scheme.Name;

        // The transaction comes out of the state the handler protected on the way to the provider.
        // One that ran out of time while the user was there gets no webhook at all.
        var relayed = context.Principal is { } providerUser
            && PhoneSession.TryReadTransaction(context.Properties, channelType, out var transactionId)
            && await PhoneSession.IsOpenAsync(
                services.GetRequiredService<ITransactionStore>(),
                services.GetRequiredService<TimeProvider>(),
                transactionId,
                cancellationToken)
            && await services.GetRequiredService<PseudoChannelRelay>().SendAsync(
                channelType,
                PseudoChannelConstants.Intents.Start,
                transactionId,
                providerUser,
                cancellationToken);

        if (!relayed)
        {
            // No cookie is issued: without a started sign-in there is nothing to answer.
            context.HandleResponse();
            await SamplePages.WriteNoticeAsync(context.HttpContext, SampleTexts.StatusTitle, SampleTexts.LinkNotValid);
        }
    }

    /// <summary>
    /// The provider answered with an error, the user cancelled there, or the answer did not pass
    /// validation. The phone gets a neutral page — the reason goes to the log — and no webhook is
    /// sent.
    /// </summary>
    /// <param name="context">Event context.</param>
    /// <returns>A task that completes when the event is handled.</returns>
    private static async Task OnRemoteFailureAsync(RemoteFailureContext context)
    {
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ProviderSignIn))
            .LogWarning(context.Failure, "The sign-in at the {ChannelType} provider failed", context.Scheme.Name);

        context.HandleResponse();
        await SamplePages.WriteNoticeAsync(context.HttpContext, SampleTexts.StatusTitle, SampleTexts.ProviderFailed);
    }
}
