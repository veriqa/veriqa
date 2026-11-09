// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The ASP.NET Core client of the AI agent approval sample: the backend that runs a support agent and
// asks a person to approve its refund tool in Telegram through the Veriqa confirmation API. It talks
// to Veriqa over the public HTTP API only (client_credentials, POST /api/transaction/confirmation,
// GET /api/transaction/{id}/result, the confirmation grant), so any standalone Veriqa works.
//
// Configuration (environment variables, all optional on the local stand):
//   VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
//   VERIQA_CLIENT_ID     - the client registered on the issuer, `support-agent`;
//   VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET of ../../built-in/.env;
//   VERIQA_CA_FILE       - the certificate to trust for the issuer; by default
//                          ../../built-in/certs/localhost.crt when it exists;
//   PORT                 - the port of this client, 7040.

using System.Collections.Concurrent;
using System.Text.Json;
using Veriqa.Sample.Quickstart.AgentApproval;

var builder = WebApplication.CreateBuilder(args);

// `dotnet run` starts the application in its project directory, so paths are relative to it.
var settings = VeriqaSettings.FromEnvironment(builder.Environment.ContentRootPath, "support-agent", out var error);
if (settings is null)
{
    Console.Error.WriteLine(error);
    return 1;
}

var port = Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } configuredPort ? configuredPort : "7040";
builder.WebHost.UseUrls($"http://localhost:{port}");

// The page reads the JSON of this backend in snake_case, like the API of Veriqa itself.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<VeriqaClient>();
builder.Services.AddSingleton<SupportAgent>();

var app = builder.Build();

// The runs live in memory: a restart forgets them, as the stand forgets its transactions.
var runs = new ConcurrentDictionary<string, AgentRun>(StringComparer.Ordinal);
var page = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "page", "index.html"));

app.MapGet("/", () => Results.File(page, "text/html; charset=utf-8"));

app.MapPost("/runs", async (RefundTicket ticket, SupportAgent agent, CancellationToken cancellationToken) =>
{
    var run = await agent.StartAsync(ticket, cancellationToken);
    runs[run.Id] = run;
    return Results.Ok(new { run.Id });
});

// Every read moves the run on: the page polls, and the agent goes on once the person has answered.
app.MapGet("/runs/{id}", async (string id, SupportAgent agent, CancellationToken cancellationToken) =>
{
    if (!runs.TryGetValue(id, out var run))
    {
        return Results.NotFound();
    }

    await agent.AdvanceAsync(run, cancellationToken);
    return Results.Ok(run.Snapshot());
});

await app.RunAsync();
return 0;
