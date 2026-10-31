// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Runnable sample: two PSEUDO-channels — Google and Apple — behind the cross-device sign-in.
// The desktop shows the QR code of the Veriqa sign-in page; the phone scans it, passes the hop page
// of the node and lands on a sign-in at Google or Apple; the sign-in happens ON THE PHONE and its
// result completes the sign-in ON THE DESKTOP.
//
// "Pseudo" because there is no delivery channel here: this is a sign-in at an identity provider
// wrapped into the shape of a channel. Nothing can be sent to the user "in the channel", so the
// question "is it you signing in over there?" is asked by a page of this host on the phone.
//
// One process plays three roles:
//   1. the Veriqa issuer (inproc) with the pseudo-channels registered through the public SPI;
//   2. the "platform" of those channels: it sends the phone to the provider and reports what
//      happened to the channel webhook of this very host, signed (PseudoChannelRelay);
//   3. the relying party the desktop signs in to — so the outcome is visible on the desktop.

using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.DependencyInjection;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Sample.PseudoChannels.Adapter;
using Veriqa.Sample.PseudoChannels.Host;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(PseudoChannelsSettings.SectionName)
    .Get<PseudoChannelsSettings>() ?? new PseudoChannelsSettings();
builder.Services.AddSingleton(settings);

// How long the adapter keeps the question and the outcome of a transaction: as long as a
// transaction lives. The adapter has no number of its own.
var engineOptions = builder.Configuration.GetSection(TransactionEngineOptions.SectionName)
    .Get<TransactionEngineOptions>() ?? new TransactionEngineOptions();
var stateLifetime = TimeSpan.FromSeconds(engineOptions.TransactionTtlSeconds);

// A provider is a channel only when its client identifier is configured. The same list drives
// everything registered per provider below — the adapter, the declaration, the sign-in handler and
// the phone routes — so the four cannot disagree.
var channels = new List<PseudoChannelOptions>();
if (settings.Google.IsEnabled)
{
    channels.Add(PseudoChannelOptions.ForGoogle(settings.PublicBaseUrl, settings.RelaySecret, stateLifetime));
}

if (settings.Apple.IsEnabled)
{
    channels.Add(PseudoChannelOptions.ForApple(settings.PublicBaseUrl, settings.RelaySecret, stateLifetime));
}

// ---------------------------------------------------------------------------------------------
// Role 1 — the Veriqa issuer, embedded in this process, with the pseudo-channels.
// ---------------------------------------------------------------------------------------------

// What the adapters learn from the core and the phone pages read: one instance per process.
builder.Services.AddSingleton<PseudoChannelExchange>();
builder.Services.AddSingleton<IPseudoChannelExchange>(
    serviceProvider => serviceProvider.GetRequiredService<PseudoChannelExchange>());

builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    authServer.UseInMemoryOpenIddictStore();
    authServer.ConfigureTransactionEngine(te => te.UseInMemoryStore());

    authServer.AddChannelAdapters(adapters =>
    {
        foreach (var channel in channels)
        {
            // One adapter class serves both channel types, so each registration brings a factory
            // of its own: the instance is told apart by its options.
            adapters.AddChannel(
                channel.ChannelType,
                serviceProvider => new PseudoChannelAdapter(
                    channel,
                    serviceProvider.GetRequiredService<PseudoChannelExchange>(),
                    serviceProvider.GetRequiredService<TimeProvider>(),
                    serviceProvider.GetRequiredService<ILogger<PseudoChannelAdapter>>()));

            // AddChannel registers the adapter and its webhook route, not the core-level declaration
            // of the channel. Without the declaration the channel is missing from the set available
            // at core level, and hop mode never wraps its link. The channel is registered only when
            // it is configured, so "enabled" is simply true; it has no polling transport.
            adapters.Services.AddSingleton(new CoreChannelDeclaration(channel.ChannelType, static () => true));
        }
    });
});

// Running behind an HTTP tunnel so a phone can reach the sample: the connector runs locally and
// forwards over loopback, so the KnownProxies/KnownIPNetworks restrictions are cleared — the source
// is always localhost. A production deployment behind a real proxy names that proxy's address
// ranges instead. UseHttpsRedirection is deliberately never called: the tunnel terminates TLS, and
// the relay posts to the plain-HTTP loopback endpoint.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
});

// ---------------------------------------------------------------------------------------------
// Role 2 — the "platform" of the pseudo-channels: the sign-in at the provider and the relay.
// ---------------------------------------------------------------------------------------------

// The relay address is checked here rather than on the first sign-in: a value that is not an
// absolute HTTP address would otherwise surface as a failed page on a phone.
if (!Uri.TryCreate(settings.RelayBaseUrl, UriKind.Absolute, out var relayBaseAddress)
    || (relayBaseAddress.Scheme != Uri.UriSchemeHttp && relayBaseAddress.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException(
        $"{PseudoChannelsSettings.SectionName}:{nameof(PseudoChannelsSettings.RelayBaseUrl)} must be an absolute " +
        "http or https URL: the host posts the relay webhook of its pseudo-channels to it.");
}

builder.Services.AddHttpClient<PseudoChannelRelay>(http => http.BaseAddress = relayBaseAddress);

var authentication = builder.Services
    .AddAuthentication(options =>
    {
        // The default schemes belong to role 3. The provider handlers are named by their channel
        // type and are challenged explicitly.
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddCookie(PseudoHostConstants.Schemes.ProviderCookie, options =>
    {
        // Lives as long as a transaction does and is never renewed: it backs one answer.
        options.Cookie.Name = PseudoHostConstants.Schemes.ProviderCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = stateLifetime;
        options.SlidingExpiration = false;
    });

if (settings.Google.IsEnabled)
{
    authentication.AddGoogleProvider(settings.Google);
}

if (settings.Apple.IsEnabled)
{
    authentication.AddAppleProvider(settings.Apple, new AppleClientSecret(settings.Apple, TimeProvider.System));
}

// ---------------------------------------------------------------------------------------------
// Role 3 — the relying party the desktop signs in to. Plain OpenID Connect, pointed at the issuer
// this very process hosts.
// ---------------------------------------------------------------------------------------------
authentication.AddOpenIdConnect(options =>
{
    // The issuer's base address — the very value the issuer is configured with, so the two cannot
    // drift apart: behind a tunnel one variable (Veriqa__OpenIddict__Server__Issuer) moves both.
    options.Authority = builder.Configuration[$"{OidcServerOptions.SectionName}:{nameof(OidcServerOptions.Issuer)}"];
    options.ClientId = settings.DesktopClientId;

    // The client is registered without a secret — a PUBLIC client, from which Veriqa requires PKCE.
    options.UsePkce = true;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.ResponseMode = OpenIdConnectResponseMode.Query;

    // Must match one of the client's AllowedRedirectUris on the issuer exactly.
    options.CallbackPath = PseudoHostConstants.Routes.DesktopCallback;

    // "No" on the phone and "Cancel sign-in" on the Veriqa page come back as error=access_denied;
    // every other failed return raises OnRemoteFailure. Both end on the same page of the sample —
    // an answer, not an unhandled exception.
    options.AccessDeniedPath = PseudoHostConstants.Routes.SignInFailed;
    options.Events.OnRemoteFailure = context =>
    {
        context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogWarning(context.Failure, "The sign-in return carried an error.");
        context.HandleResponse();
        context.Response.Redirect(PseudoHostConstants.Routes.SignInFailed);
        return Task.CompletedTask;
    };

    // Keep the claims under the names Veriqa issued them under.
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = VeriqaClaimTypes.Name;

    // Exactly the scopes the client is allowed: the handler's default "profile" is not among them.
    options.Scope.Clear();
    options.Scope.Add(OpenIdConnectScope.OpenId);
    options.Scope.Add(OpenIdConnectScope.Email);
    options.Scope.Add(VeriqaScopes.Channel);

    // Claims that travel outside the ID token are read from /connect/userinfo, and the handler
    // copies from there only what its ClaimActions name.
    options.GetClaimsFromUserInfoEndpoint = true;
    options.ClaimActions.MapUniqueJsonKey(VeriqaClaimTypes.EmailVerified, VeriqaClaimTypes.EmailVerified);
    options.ClaimActions.MapUniqueJsonKey(VeriqaClaimTypes.ChannelType, VeriqaClaimTypes.ChannelType);
    options.ClaimActions.MapUniqueJsonKey(VeriqaClaimTypes.ChannelUserId, VeriqaClaimTypes.ChannelUserId);
});

var app = builder.Build();

// Before UseVeriqaAuthServer: the scheme and host of the original request have to be restored
// before anything builds an absolute address from them — the hop URL, the provider return address.
app.UseForwardedHeaders();

app.UseVeriqaAuthServer();
app.MapVeriqaAuthServer();

// The phone side: one set of addresses per enabled pseudo-channel.
foreach (var channel in channels)
{
    app.MapPseudoChannel(channel.ChannelType);
}

// The desktop side. The page reads the cookie of the relying party only, so a sign-in at the
// provider alone — before the answer on the phone — does not make anyone signed in here.
app.MapGet(PseudoHostConstants.Routes.Home, (ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated is true ? SamplePages.SignedIn(user) : SamplePages.Anonymous());

app.MapGet(PseudoHostConstants.Routes.SignIn, (ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated is true
        ? Results.Redirect(PseudoHostConstants.Routes.Home)
        : Results.Challenge(new AuthenticationProperties { RedirectUri = PseudoHostConstants.Routes.Home }));

app.MapGet(PseudoHostConstants.Routes.SignInFailed, SamplePages.SignInFailed);

// Local sign-out: drops this application's cookie. Veriqa exposes no end-session endpoint, so the
// issuer's own session is not ended by this.
app.MapGet(PseudoHostConstants.Routes.SignOut, () =>
    Results.SignOut(
        properties: new AuthenticationProperties { RedirectUri = PseudoHostConstants.Routes.Home },
        authenticationSchemes: [CookieAuthenticationDefaults.AuthenticationScheme]));

app.Run();
