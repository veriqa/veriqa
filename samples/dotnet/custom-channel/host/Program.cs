// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Runnable sample: plugging a third-party channel adapter into embedded Veriqa through the public
// SPI. The adapter itself (Veriqa.Sample.CustomChannel.Adapter) is built against the MIT contracts
// package only; this host is the reference wiring — registration, webhook route and sign-in button.


using Veriqa.Core.AuthServer.DependencyInjection;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Sample.CustomChannel.Adapter;

var builder = WebApplication.CreateBuilder(args);

// Settings of the third-party channel: the section belongs to the adapter's author,
// Veriqa neither knows nor binds it.
var acmeChatOptions = builder.Configuration
    .GetSection(AcmeChatOptions.SectionName)
    .Get<AcmeChatOptions>() ?? new AcmeChatOptions();

// region:snippet
// Wiring up Veriqa AuthServer (inproc) with a third-party channel.
builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    // OpenIddict store: the volatile in-memory one, chosen explicitly (there is no default —
    // a host that says nothing here fails to start outside Development).
    authServer.UseInMemoryOpenIddictStore();

    authServer.ConfigureTransactionEngine(te =>
    {
        te.UseInMemoryStore();
    });

    authServer.AddChannelAdapters(adapters =>
    {
        // Public SPI: a third-party adapter is registered by its channel type. The type must match
        // \A[a-z][a-z0-9-]{0,63}\z and the value the adapter returns from ChannelType — otherwise the
        // server fails to start. Veriqa maps POST /api/channels/acme-chat/webhook for the channel
        // (plus the tenant-segment variant) with the same rate limit and body limit as the built-in
        // channels, and shows the channel in the sign-in window.
        adapters.AddChannel(
            AcmeChatConstants.ChannelType,
            serviceProvider => new AcmeChatChannelAdapter(
                acmeChatOptions,
                serviceProvider.GetRequiredService<ILogger<AcmeChatChannelAdapter>>()),
            new ChannelRegistrationOptions
            {
                // What the bot answers to a code no sign-in answers to. Without it such a code gets
                // the reply to a link that cannot be served (StaleLinkReplyText), and this channel
                // declares none — the user would be left in front of a silent chat.
                UnmatchedCodeReplyText = AcmeChatConstants.UnmatchedCodeReplyText
            });
    });
});

// The core-level declaration of the channel — a step of its own next to AddChannel, the one the
// built-in channels take as well. AddChannel registers the adapter and its route; the declaration is
// what puts the channel into the set available at core level, and the readers of that set skip a
// channel missing from it: a channel started by a code gets no hop link into its QR code without it.
// The sample channel has no switch of its own, so it is declared always on; a real channel reads its
// Enabled flag here through IOptionsMonitor.
builder.Services.AddSingleton(new CoreChannelDeclaration(
    AcmeChatConstants.ChannelType,
    isEnabled: static () => true));
// endregion:snippet

var app = builder.Build();

// The Veriqa middleware sequence in one call (security headers → static files → authentication →
// authorization → rate limiting).
app.UseVeriqaAuthServer();

// Veriqa OIDC routing plus the channel webhook routes — the generic route of the third-party
// channel is mapped here together with the built-in ones.
app.MapVeriqaAuthServer();

// The issuer has no page of its own to show a signed-in user: a sign-in is started by a relying
// party and ends there, with the claims in its session. This route only tells the host is up.
app.MapGet("/", () => "Hello!");

app.Run();
