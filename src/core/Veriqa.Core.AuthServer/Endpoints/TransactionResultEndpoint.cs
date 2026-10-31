// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Claims;
using System.Text.Json.Serialization;

using OpenIddict.Validation.AspNetCore;

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Authenticated result surface of a transaction (GET /api/transaction/{id}/result,
/// SPEC-039 C24): the outcome, and the verdict of the check between the confirming party and the
/// expectations the relying party stated. The surface does not tell transaction types apart: it
/// answers the creating client about a sign-in transaction as well as about a confirmation one; a
/// sign-in carries no expectations, so its verdict is null.
/// <para>
/// It sits next to the status endpoint and is deliberately NOT it: the status surface is anonymous
/// and is addressed by an identifier that travels in the QR, the deep link and the URL of the entry
/// page, so anyone holding the way in holds it. The verdict lives only here, behind the same bearer
/// token the creation entry takes and behind the check that the caller is the client the transaction
/// is attributed to (N35).
/// </para>
/// </summary>
public static class TransactionResultEndpoint
{
    /// <summary>
    /// Full endpoint path.
    /// </summary>
    private const string RoutePath = OidcEndpoints.TransactionStatusBase + "/{id}/result";

    /// <summary>
    /// Outcome of a transaction that has not reached a terminal state yet. It is not one of the
    /// terminal outcomes and therefore not a member of their vocabulary — "the answer is not in yet"
    /// rather than an answer.
    /// </summary>
    private const string PendingOutcome = "pending";

    /// <summary>
    /// What the caller is told when the transaction is not theirs to read. Deliberately the same
    /// answer for "no such transaction", "already swept by retention" and "belongs to another client":
    /// separate answers would let a holder of someone else's identifier learn it exists (E44).
    /// </summary>
    private const string NotFoundMessage = "Transaction not found.";

    /// <summary>
    /// Registers the transaction result endpoint.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name (null — no limiting).</param>
    /// <returns>The route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapTransactionResultEndpoint(
        this IEndpointRouteBuilder endpoints,
        string? rateLimitPolicyName = null)
    {
        var builder = endpoints.MapGet(RoutePath, HandleResultAsync)
            .RequireAuthorization(policy =>
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser());

        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            builder.RequireRateLimiting(rateLimitPolicyName);
        }

        return endpoints;
    }

    /// <summary>
    /// Answers with the outcome of the transaction and the verdict of the identity match.
    /// </summary>
    private static async Task<IResult> HandleResultAsync(
        string id,
        HttpContext httpContext,
        ClaimsPrincipal user,
        ITransactionService transactionService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(TransactionResultEndpoint));

        // The token must represent the CLIENT. Authentication itself was enforced by the route policy
        // (401); a valid USER token reaching here is the other refusal (403), for the same reason as on
        // the creation entry: a person who signed in must not act on behalf of the application.
        if (!ClientCredentialsToken.TryGetClientId(user, out var clientId))
        {
            return Results.Forbid(
                authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme]);
        }

        // The answer carries the verdict of a check about a person, so it is never cached on the way.
        httpContext.Response.Headers.CacheControl = "no-store";

        var transactionId = SessionIdMapper.ToTransactionId(id);
        if (transactionId is null)
        {
            return NotFound();
        }

        var found = await transactionService.GetTransactionAsync(
            transactionId.Value,
            httpContext.RequestAborted);

        if (found.IsFailure)
        {
            // Every refusal of the read is the same answer, including "expired by TTL and not swept
            // yet": telling that one apart would require answering ABOUT a transaction before knowing
            // whose it is, which is the oracle E44 forbids. The sweep turns such a transaction into a
            // terminal Expired one within its cycle, and it is then read here as that outcome — the
            // same window the anonymous status surface already has.
            return NotFound();
        }

        var transaction = found.Value;

        // Access belongs to the client the transaction is attributed to and to nobody else (C24). A
        // foreign client gets the answer of a transaction that does not exist — the difference between
        // the two would be an oracle over other people's identifiers — and the fact goes to the log,
        // where an operator sees it and a caller does not.
        if (!string.Equals(transaction.GetApplicationId(), clientId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Result of a transaction attributed to another application was requested. "
                + "ClientId: {ClientId}, RequestId: {RequestId}",
                clientId,
                httpContext.TraceIdentifier);

            return NotFound();
        }

        return Results.Ok(new TransactionResultResponse
        {
            Outcome = OutcomeOf(transaction),

            // The name of the matched type and nothing else: not the value the relying party sent (an
            // echo would turn this into a channel of personal data back out), not the subject, not a
            // claim of the resolved identity (R40, N35, N36).
            MatchedType = transaction.IdentityMatch?.MatchedType
        });
    }

    /// <summary>
    /// The outcome of the transaction as the relying party reads it.
    /// </summary>
    /// <remarks>
    /// The terminal half of the answer is read by the engine's <see cref="TerminalTransactionOutcome"/>
    /// and not by a table of this surface: the channel pipeline answers about the same outcomes, and a
    /// second table here would be a second vocabulary. What stays this surface's own is the completion
    /// of that reading — a transaction that has not answered yet still owes the relying party a value,
    /// and that value is <see cref="PendingOutcome"/>.
    /// </remarks>
    /// <param name="transaction">Transaction being read.</param>
    /// <returns>The outcome value of the answer.</returns>
    private static string OutcomeOf(Transaction transaction) =>
        TerminalTransactionOutcome.Of(transaction)?.Value ?? PendingOutcome;

    /// <summary>
    /// The single "not found" answer of this surface.
    /// </summary>
    /// <returns>The 404 answer.</returns>
    private static IResult NotFound() =>
        Results.Problem(
            detail: NotFoundMessage,
            statusCode: StatusCodes.Status404NotFound,
            title: OidcErrorCodes.TransactionNotFound);
}

/// <summary>
/// Body of the answer of the result surface (SPEC-039 C24).
/// </summary>
internal sealed class TransactionResultResponse
{
    /// <summary>
    /// Outcome of the transaction.
    /// </summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>
    /// Name of the declared comparable type whose value matched; null — nothing matched, no
    /// expectations were stated, or the transaction did not end in a confirmation.
    /// </summary>
    [JsonPropertyName("matched_type")]
    public string? MatchedType { get; init; }
}
