// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The ASP.NET Core client of the confirmation and step-up sample: a backend that asks a person in
// Telegram through the Veriqa confirmation API, with no sign-in and no redirect.
//   1. Approve a payment: a server-to-server confirmation; once confirmed, the backend learns who
//      confirmed from an id_token.
//   2. Step-up before deleting a project: the confirmation names the owner in expected_identities, and
//      the project is deleted only when matched_type says the owner confirmed.
// It talks to Veriqa over the public HTTP API only, so any standalone Veriqa works.
//
// Configuration (environment variables, all optional on the local stand):
//   VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
//   VERIQA_CLIENT_ID     - the client registered on the issuer, `sample-app`;
//   VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET of ../../built-in/.env;
//   VERIQA_CA_FILE       - the certificate to trust for the issuer; by default
//                          ../../built-in/certs/localhost.crt when it exists;
//   PORT                 - the port of this client, 7030.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Veriqa.Sample.Quickstart.StepUpConfirmation;

var builder = WebApplication.CreateBuilder(args);

// `dotnet run` starts the application in its project directory, so paths are relative to it.
var settings = VeriqaSettings.FromEnvironment(builder.Environment.ContentRootPath, "sample-app", out var error);
if (settings is null)
{
    Console.Error.WriteLine(error);
    return 1;
}

var port = Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } configuredPort ? configuredPort : "7030";
builder.WebHost.UseUrls($"http://localhost:{port}");

// The page reads the JSON of this backend in snake_case, like the API of Veriqa itself.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<VeriqaClient>();
builder.Services.AddSingleton<Workspace>();

var app = builder.Build();

// A transaction is exchanged for an id_token once, so the claims are kept for the page to read again.
// The gate keeps two reads at once from exchanging (or concluding) one transaction twice.
var paymentClaims = new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal);
var resultGate = new SemaphoreSlim(1, 1);
var page = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "page", "index.html"));

app.MapGet("/", () => Results.File(page, "text/html; charset=utf-8"));

// --- 1. a server-to-server confirmation of a payment ---

app.MapPost("/payments", (PaymentForm form, VeriqaClient veriqa, CancellationToken cancellationToken) =>
    VeriqaCallAsync(async () =>
    {
        var created = await veriqa.CreateAsync(
            "approve-payment",
            new Dictionary<string, string> { ["amount"] = form.Amount, ["payee"] = form.Payee },
            null,
            cancellationToken);
        return Results.Ok(new
        {
            created.TransactionId,
            created.ChannelEntry.Url,
            created.ChannelEntry.Qr,
            created.ChannelEntry.ValidUntil
        });
    }));

app.MapGet("/payments/{transactionId}", (string transactionId, VeriqaClient veriqa, CancellationToken cancellationToken) =>
    VeriqaCallAsync(async () =>
    {
        var result = await veriqa.GetResultAsync(transactionId, cancellationToken);
        if (result.Outcome != ConfirmationResult.Confirmed)
        {
            return Results.Ok(new { result.Outcome });
        }

        await resultGate.WaitAsync(cancellationToken);
        try
        {
            if (!paymentClaims.TryGetValue(transactionId, out var claims))
            {
                claims = await veriqa.ExchangeForClaimsAsync(transactionId, cancellationToken);
                paymentClaims[transactionId] = claims;
            }

            return Results.Ok(new { result.Outcome, Claims = claims });
        }
        finally
        {
            resultGate.Release();
        }
    }));

// --- 2. step-up before deleting a project ---

app.MapGet("/workspace", (Workspace workspace) => Results.Ok(workspace.Snapshot()));

app.MapPost("/workspace/owner/reset", (Workspace workspace) =>
{
    workspace.ResetOwner();
    return Results.Ok(workspace.Snapshot());
});

app.MapPost("/projects/{projectId}/deletion", (string projectId, Workspace workspace, VeriqaClient veriqa, CancellationToken cancellationToken) =>
    VeriqaCallAsync(async () =>
    {
        if (workspace.Find(projectId) is not { } project)
        {
            return Results.NotFound();
        }

        // The step-up itself: once an owner is known, the confirmation is addressed to that person.
        var owner = workspace.Owner;
        var expected = owner is null
            ? null
            : new Dictionary<string, string> { [Workspace.OwnerIdentityType] = owner };

        var created = await veriqa.CreateAsync(
            "delete-project",
            new Dictionary<string, string>
            {
                ["project"] = project.Name,
                ["documents"] = project.Documents.ToString(CultureInfo.InvariantCulture)
            },
            expected,
            cancellationToken);
        workspace.StartDeletion(created.TransactionId, project.Id);

        return Results.Ok(new
        {
            created.TransactionId,
            created.ChannelEntry.Url,
            created.ChannelEntry.Qr,
            ExpectsOwner = owner is not null
        });
    }));

app.MapGet("/deletions/{transactionId}", (string transactionId, Workspace workspace, VeriqaClient veriqa, CancellationToken cancellationToken) =>
    VeriqaCallAsync(async () =>
    {
        await resultGate.WaitAsync(cancellationToken);
        try
        {
            if (workspace.FindDeletion(transactionId) is not { } deletion)
            {
                return Results.NotFound();
            }

            if (deletion.Verdict is not null)
            {
                return Results.Ok(deletion);
            }

            var result = await veriqa.GetResultAsync(transactionId, cancellationToken);
            if (result.Outcome == ConfirmationResult.Pending)
            {
                return Results.Ok(deletion);
            }

            if (result.Outcome != ConfirmationResult.Confirmed)
            {
                return Results.Ok(workspace.Conclude(transactionId, result.Outcome, Verdicts.NotDeleted));
            }

            if (workspace.Owner is null)
            {
                // No owner yet: the first person to confirm becomes the owner of the account.
                var claims = await veriqa.ExchangeForClaimsAsync(transactionId, cancellationToken);
                return Results.Ok(workspace.Conclude(
                    transactionId,
                    result.Outcome,
                    Verdicts.DeletedOwnerBound,
                    claims.GetProperty("channel_user_id").GetString()));
            }

            // The decision a step-up exists for. Veriqa does not refuse a confirmation by somebody else:
            // the outcome is still confirmed, and only matched_type says whether the owner answered.
            var verdict = result.MatchedType == Workspace.OwnerIdentityType
                ? Verdicts.Deleted
                : Verdicts.ConfirmedByAnotherPerson;
            return Results.Ok(workspace.Conclude(transactionId, result.Outcome, verdict));
        }
        finally
        {
            resultGate.Release();
        }
    }));

await app.RunAsync();
return 0;

// A refusal of Veriqa reaches the page as Veriqa sent it — status and body — so the page shows what
// went wrong; an unreachable Veriqa answers 502.
static async Task<IResult> VeriqaCallAsync(Func<Task<IResult>> call)
{
    try
    {
        return await call();
    }
    catch (VeriqaCallException refused)
    {
        return Results.Content(refused.Body, "application/json", statusCode: refused.StatusCode);
    }
    catch (HttpRequestException unreachable)
    {
        return Results.Problem(unreachable.Message, statusCode: StatusCodes.Status502BadGateway);
    }
}

/// <summary>The payment the page asks to approve.</summary>
internal sealed record PaymentForm(string Amount, string Payee);
