// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Runnable sample: the CLIENT side (Relying Party) of the login scenario — an ordinary ASP.NET Core
// application signing in against a Veriqa issuer with the standard authentication stack.
// Nothing Veriqa-specific is referenced here: no Veriqa packages, no ProjectReference. Veriqa lives
// entirely on the issuer side, and this application talks to it over plain OpenID Connect
// (Authorization Code + PKCE), exactly as it would to any conformant OIDC provider.
//
// Pairs with the issuer sample `samples/dotnet/inproc/login` out of the box: that host runs on
// https://localhost:7300 and already registers the `my-app` client with the redirect URI
// https://localhost:7020/signin-oidc — which is where this application listens.
// Run the issuer first, then this one.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// Standard OIDC relying party. The local session lives in a cookie; an unauthenticated challenge is
// handed to the OpenID Connect handler, which drives the redirect to the Veriqa issuer.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        // Authority is the issuer's base address: the handler discovers the endpoints and signing
        // keys from {Authority}/.well-known/openid-configuration — no endpoint is spelled out here.
        options.Authority = builder.Configuration["Oidc:Authority"];
        options.ClientId = builder.Configuration["Oidc:ClientId"];

        // `my-app` is registered without a secret, so it is a PUBLIC client and Veriqa requires PKCE
        // from it (S256). Leaving ClientSecret unset is therefore deliberate, not an omission.
        options.UsePkce = true;
        options.ResponseType = OpenIdConnectResponseType.Code;

        // Must match one of Veriqa:OpenIddict:Clients[].AllowedRedirectUris on the issuer exactly —
        // the comparison is literal and wildcards are not supported.
        options.CallbackPath = "/signin-oidc";

        // A user who presses "Cancel sign-in" on the Veriqa page comes
        // back with error=access_denied. That is an answer, not a fault: with the path set the handler
        // redirects to the route mapped below. With no path set the refusal would go to OnRemoteFailure
        // below, like any other failed return.
        options.AccessDeniedPath = "/signin-cancelled";

        // Every other failed return - Veriqa answering error=server_error, say, because the channel did
        // not deliver the confirmation - raises OnRemoteFailure; with no handler for it the return ends
        // in an HTTP 500. The user is taken to a page that offers to start over. The error text is
        // deliberately not shown to the user: it is written to the log instead.
        options.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
                .LogWarning(context.Failure, "The sign-in return carried an error.");
            context.HandleResponse();
            context.Response.Redirect("/signin-failed");
            return Task.CompletedTask;
        };

        // Veriqa issues the user's name as the short OIDC `name` claim, while User.Identity.Name
        // looks up ClaimTypes.Name (the long WS-Federation URI) by default — without this line the
        // name stays empty after a successful sign-in.
        options.TokenValidationParameters.NameClaimType = "name";

        // The handler already requests openid and profile by default — only the extra scope is
        // added here. It must be listed in AllowedScopes of the client on the issuer.
        options.Scope.Add("email");

        // Claims beyond the id_token are fetched from /connect/userinfo; SaveTokens keeps the
        // issued tokens in the cookie so the application can inspect or forward them.
        options.GetClaimsFromUserInfoEndpoint = true;
        options.SaveTokens = true;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// The launch profile also publishes a plain-HTTP endpoint. Without this redirect a flow started
// there would send redirect_uri=http://localhost:5020/signin-oidc — absent from AllowedRedirectUris,
// which is compared literally — and the handler's correlation cookie (Secure, SameSite=None) would
// not survive the round trip, surfacing as "Correlation failed" rather than as a URI mismatch.
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// Landing page: an anonymous visitor is challenged, which starts the OIDC flow against Veriqa. After
// confirming in a trusted channel the user lands back here authenticated and sees their name — the
// name comes from claims the Veriqa issuer minted, not from any local user store.
app.MapGet("/", (ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated is true
        ? Results.Text($"Hello, {user.Identity.Name}!")
        : Results.Challenge());

// Where the handler sends a user who did not go through with the sign-in (AccessDeniedPath above).
// It is a route of its own because "/" challenges an anonymous visitor: sent there, the user would be
// put straight back on the sign-in page they have just left.
app.MapGet("/signin-cancelled", () => Results.Text("Sign-in was cancelled. Open / to sign in again."));

// Where OnRemoteFailure above sends a user whose sign-in ended in an error. Like the page before it,
// it is anonymous and starts nothing by itself: redirected to "/", a failure that repeats would loop.
app.MapGet("/signin-failed", () => Results.Text("Sign-in could not be completed. Open / to sign in again."));

// Local sign-out: drops this application's cookie only. Veriqa exposes no end-session endpoint, so
// there is no federated sign-out to propagate — signing out here does not end the issuer's session.
app.MapGet("/signout", () =>
    Results.SignOut(
        properties: new AuthenticationProperties { RedirectUri = "/" },
        authenticationSchemes: [CookieAuthenticationDefaults.AuthenticationScheme]));

app.Run();
