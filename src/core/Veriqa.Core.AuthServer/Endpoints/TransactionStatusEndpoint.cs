// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Transaction status polling endpoint (GET /api/transaction/{id}/status).
/// Provides a fallback for clients without WebSocket support (SPEC-007 §5.4).
/// Returns a safe public subset of the transaction state:
/// state, channel_type, ttl_seconds, reason_code, awaiting_web_confirmation, answer_browser_bound, awaiting_answer_in_channel,
/// authorization_code_issued, claim_completion.
/// </summary>
public static class TransactionStatusEndpoint
{
    /// <summary>
    /// Full endpoint path.
    /// </summary>
    private const string RoutePath = OidcEndpoints.TransactionStatusBase + "/{id}/status";

    /// <summary>
    /// Builds the root-relative address of the status of one transaction — the address every page of the
    /// contour polls, built in one place.
    /// </summary>
    /// <param name="sessionId">External session identifier of the transaction.</param>
    /// <returns>Root-relative URL of the status, without the path base.</returns>
    internal static string BuildStatusPath(string sessionId) =>
        $"{OidcEndpoints.TransactionStatusBase}/{Uri.EscapeDataString(sessionId)}/status";

    /// <summary>
    /// Registers the transaction status polling endpoint with rate limiting.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name (null — no limiting).</param>
    /// <returns>The route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapTransactionStatusEndpoint(
        this IEndpointRouteBuilder endpoints,
        string? rateLimitPolicyName = null)
    {
        var builder = endpoints.MapGet(RoutePath, HandleStatusAsync);

        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            builder.RequireRateLimiting(rateLimitPolicyName);
        }

        return endpoints;
    }

    /// <summary>
    /// Handles the transaction status request by its external identifier (session_id).
    /// </summary>
    private static async Task<IResult> HandleStatusAsync(
        string id,
        ITransactionService transactionService,
        TimeProvider timeProvider,
        HttpContext httpContext)
    {
        // The method parses the id as a session_id, fetches the transaction, and returns its public status.

        // No answer is cached: the status moves with every event, and a copy kept by the browser or by a
        // cache on the way would show a page a state that has passed. Not every page that polls it asks the
        // browser for a fresh answer, so the directive is what keeps every copy out.
        CorePageCaching.Forbid(httpContext.Response);

        // 1. Map session_id → TransactionId
        var transactionId = SessionIdMapper.ToTransactionId(id);

        if (transactionId is null)
        {
            return Results.Problem(
                detail: "Invalid transaction identifier.",
                statusCode: StatusCodes.Status400BadRequest,
                title: OidcErrorCodes.OidcRequestInvalid);
        }

        // 2. Fetch the transaction from the store
        var txResult = await transactionService.GetTransactionAsync(
            transactionId.Value,
            httpContext.RequestAborted);

        if (txResult.IsFailure)
        {
            return Results.Problem(
                detail: "Transaction not found or expired.",
                statusCode: StatusCodes.Status404NotFound,
                title: OidcErrorCodes.TransactionNotFound);
        }

        var transaction = txResult.Value;

        // 3. Build the public response
        var response = BuildStatusResponse(transaction, timeProvider.GetUtcNow());

        return Results.Ok(response);
    }

    /// <summary>
    /// Builds the public representation of the transaction status.
    /// Does not expose sensitive data (claims, snapshot contents).
    /// </summary>
    /// <param name="transaction">Transaction.</param>
    /// <param name="now">Current time (UTC), supplied by the caller's time provider.</param>
    /// <returns>Anonymous object with public status fields.</returns>
    private static object BuildStatusResponse(Transaction transaction, DateTimeOffset now)
    {
        // The method builds a safe public status without exposing internal data.

        var ttlSeconds = transaction.IsTerminal()
            ? 0
            : Math.Max(0, (transaction.ExpiresAt - now).TotalSeconds);

        // The channel type appears as soon as the channel data reaches the transaction — that is at
        // the confirmation for the in-channel surfaces, and already before it when the core takes the
        // question onto its own page.
        var channelType = transaction.ChannelIdentitySnapshot?.ChannelType;

        return new
        {
            state = transaction.State.ToString(),
            channel_type = channelType,
            ttl_seconds = Math.Round(ttlSeconds, 1),
            reason_code = transaction.StateReasonCode,

            // The polling fallback needs the same signal SignalR delivers as a status: the question
            // is now on the core page, so the browser must go to the callback. It is a separate
            // field rather than a synthetic value of "state", which stays the name of a state-machine
            // state.
            awaiting_web_confirmation = transaction.IsAwaitingWebConfirmation(),

            // Whether that question takes its answer from one browser only — the one the channel identity
            // was attached from, where the question is already open. A page following the transaction
            // then stays where it is: the question would refuse it. The same fact the hub sends with the
            // status, so polling and the hub cannot disagree.
            answer_browser_bound = transaction.IsAwaitingWebConfirmation()
                && transaction.SubjectBinding is { AnswerBrowserBound: true },

            // Whether the answer is awaited in the channel: a sender has entered the sign-in, and neither the
            // decision nor a hand-over of the question to the core page is written yet. The same predicate
            // under which the hub sends and replays the channel status channel_answered, so polling and the
            // hub cannot disagree; a page that does not subscribe to the hub learns from it that its spare
            // ways to start lead nowhere now. A separate field for the same reason as the one above; it
            // carries nothing about the subject the sign-in is bound to (SPEC-007 UI-095).
            awaiting_answer_in_channel = transaction.IsAwaitingAnswerInChannel(),

            // Whether the authorization code of the sign-in has been issued — the one mark the callback
            // answers with the "session expired" page. A page shown for a sign-in already Completed learns
            // from it that "Yes" was answered: the state alone does not change then (SPEC-007 UI-123). A
            // separate field for the same reason as the one above; it carries nothing about the user
            // (SPEC-007 UI-095).
            authorization_code_issued = AuthorizeCallbackEndpoint.HasIssuedAuthorizationCode(transaction),

            // The same signal the broadcast carries as the ClaimCompletion status (SPEC-041 L91): the
            // current step of the claims completion and the surface of the web step. Null outside the
            // completion, and once every missing claim has an outcome while Confirm is not written
            // yet. Names of the steps only — never a claim value (SPEC-007 UI-095).
            claim_completion = BuildClaimCompletionStatus(transaction)
        };
    }

    /// <summary>
    /// Builds the claims completion part of the public status: the current step and the surface of
    /// the web step, by their member names.
    /// </summary>
    /// <param name="transaction">Transaction.</param>
    /// <returns>Anonymous object with the step and the surface, or null when no step is on.</returns>
    private static object? BuildClaimCompletionStatus(Transaction transaction)
    {
        var step = transaction.GetCurrentClaimCompletionStep();

        return step is null
            ? null
            : new
            {
                step = step.Value.ToString(),
                surface = transaction.ClaimCompletion!.Surface?.ToString()
            };
    }
}
