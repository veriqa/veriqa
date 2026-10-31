// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Display-only demo-stand preview form: sign-in (login) via an EXTERNAL Veriqa issuer.
// One form for two modes — self-hosted (a separate Veriqa instance) and Veriqa Cloud:
// the client application code is identical, ONLY the configuration differs (Authority + channels).
// Illustrative configuration sample; not a runnable host.
//
// The client is a standard OIDC Relying Party on Microsoft.AspNetCore.Authentication.OpenIdConnect
// (Authorization Code + PKCE). No Veriqa-specific packages: Veriqa lives on the issuer side.
// Fragments between `region:snippet` / `region:snippet-routing` are the source of truth for the code snippet.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

// region:snippet
// Standard OIDC client pointed at the external Veriqa issuer (self-hosted or Cloud). Authority and
// ClientId are read from the application configuration (Oidc section) — see the config tab.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        options.Authority = builder.Configuration["Oidc:Authority"];
        options.ClientId = builder.Configuration["Oidc:ClientId"];
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"];
        options.ResponseType = "code";
        options.UsePkce = true;
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.GetClaimsFromUserInfoEndpoint = true;
        options.SaveTokens = true;

        // Veriqa issues the user's name as the short OIDC `name` claim, while User.Identity.Name
        // looks up ClaimTypes.Name (the long WS-Federation URI) by default — without this line the
        // name stays empty after a successful sign-in.
        options.TokenValidationParameters.NameClaimType = "name";

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
    });
// endregion:snippet

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// region:snippet-routing
// Login scenario: after signing in via a trusted channel, "Hello, {name}!" is displayed.
// The name is taken from claims issued by the external Veriqa issuer.
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
// endregion:snippet-routing

app.Run();
