// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Runnable sample: sign-in (login) via embedded Veriqa in In-process mode (inproc).
// Veriqa.Core.AuthServer is plugged in as a library into an ordinary ASP.NET Core application
// through the high-level facade AddVeriqaAuthServer() + MapVeriqaAuthServer().
// This is the only public way of inproc integration; there are no other deployment modes here.
//
// The fragment between the `region:snippet` / `endregion:snippet` markers is the single source
// of truth for the code snippet on the demo site: the demo-stand extractor cuts out
// the body by this marker. Change the Veriqa wiring ONLY here — the snippet updates itself.

using Veriqa.Core.AuthServer.DependencyInjection;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Sample.DotNet.Inproc.Login;

var builder = WebApplication.CreateBuilder(args);

// region:snippet
// Wiring up Veriqa AuthServer (inproc): the OpenIddict issuer is embedded in the application process.
builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    // OpenIddict store (clients, tokens, authorizations): the volatile in-memory one, chosen
    // explicitly. There is no default — a host that says nothing here fails to start outside
    // Development. In production say authServer.UseOpenIddictDatabase(ef => ef.UseNpgsql(...)) instead.
    authServer.UseInMemoryOpenIddictStore();

    // Transaction store: in-memory (for development/demo; in production — EF Core/Redis).
    authServer.ConfigureTransactionEngine(te =>
    {
        te.UseInMemoryStore();
    });

    // Channel adapters: all 4 trusted Veriqa channels. Each is
    // enabled/disabled via configuration (Veriqa:Channels:<Channel>:Enabled section); with Enabled=false
    // only the options and the validator are registered — tokens/SMTP are not required at startup
    // (dotnet run starts without external configuration). Enable the channel you need and set its credentials.
    authServer.AddChannelAdapters(adapters =>
    {
        adapters.AddTelegram();
        adapters.AddWhatsApp();
        adapters.AddMax();
        adapters.AddEmail();
    });
});
// endregion:snippet

// Channel identities (channel user id, phone, email, display name) are PII, and Veriqa ships no
// implementation of the port that stores them: register one and the identities are kept, register
// none and nothing is stored — the sign-in works either way. This sample registers the reference
// in-memory implementation that lives next to this file; replace it with your own store.
builder.Services.AddSingleton<IChannelIdentityRepository, InMemoryChannelIdentityRepository>();

// Other application services can be added here:
// builder.Services.AddScoped<IMyAppService, MyAppService>();

var app = builder.Build();

// Veriqa AuthServer middleware in the order the server requires: security headers (before the
// static files — that is the point of the call), static assets of the sign-in page, authentication,
// authorization, rate limiting. Write the five calls out by hand when something of yours goes
// between them.
app.UseVeriqaAuthServer();

// region:snippet-routing
// Veriqa AuthServer OIDC routing (authorize / token / userinfo / callback) — a single call.
app.MapVeriqaAuthServer();

// The issuer has no page of its own to show a signed-in user: a sign-in is started by a relying
// party and ends there, with the claims in its session. This route only tells the host is up.
app.MapGet("/", () => "Hello!");
// endregion:snippet-routing

app.Run();
