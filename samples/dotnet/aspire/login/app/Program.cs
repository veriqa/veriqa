// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Application orchestrated by the Aspire AppHost of this sample (../Program.cs).
// Veriqa is embedded exactly as in the in-process sample — the same AddVeriqaAuthServer /
// MapVeriqaAuthServer facade; the only difference is where the transaction store address comes
// from: Aspire injects it as the "transactions" connection string, so nothing about the store
// is written into appsettings.json.
//
// Run this project through the AppHost (`dotnet run` in the parent directory), not on its own:
// without Aspire the connection string is absent and startup fails with an explicit error.

using Veriqa.Core.AuthServer.DependencyInjection;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.TransactionEngine.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Name of the store resource in the AppHost — Aspire publishes its address under this key.
const string TransactionStoreConnectionName = "transactions";

// Service discovery, not configuration: the address is provided by the orchestrator. There is no
// literal endpoint fallback on purpose — a missing connection string means the application was
// started outside the AppHost, and that must fail loudly rather than silently use another store.
var transactionStoreConnectionString =
    builder.Configuration.GetConnectionString(TransactionStoreConnectionName)
    ?? throw new InvalidOperationException(
        $"Connection string '{TransactionStoreConnectionName}' is not set: "
        + "run this sample through its Aspire AppHost.");

// Wiring up Veriqa AuthServer: the OpenIddict issuer is embedded in the application process,
// while the transaction store is an orchestrated resource.
builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    // OpenIddict store: the volatile in-memory one, chosen explicitly. Only the transaction store is
    // an orchestrated resource in this sample; there is no default for the OpenIddict one, and a host
    // that says nothing here fails to start outside Development.
    authServer.UseInMemoryOpenIddictStore();

    authServer.ConfigureTransactionEngine(te =>
    {
        te.UseRedisStore(store => store.Configuration = transactionStoreConnectionString);
    });

    // Channel adapters: all 4 trusted Veriqa channels. Each is
    // enabled/disabled via configuration (Veriqa:Channels:<Channel>:Enabled section); with Enabled=false
    // only the options and the validator are registered — tokens/SMTP are not required at startup.
    authServer.AddChannelAdapters(adapters =>
    {
        adapters.AddTelegram();
        adapters.AddWhatsApp();
        adapters.AddMax();
        adapters.AddEmail();
    });
});

var app = builder.Build();

// The Veriqa middleware sequence in one call (security headers → static files → authentication →
// authorization → rate limiting).
app.UseVeriqaAuthServer();

// Veriqa AuthServer OIDC routing (authorize / token / userinfo / callback) — a single call.
app.MapVeriqaAuthServer();

// The issuer has no page of its own to show a signed-in user: a sign-in is started by a relying
// party and ends there, with the claims in its session. This route only tells the host is up.
app.MapGet("/", () => "Hello!");

app.Run();
