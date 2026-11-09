// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// OIDC token exchange endpoint (POST /connect/token).
/// Handles the authorization_code, refresh_token and client_credentials grant types, and the
/// confirmation token grant (SPEC-039 C51).
/// </summary>
public static class TokenEndpoint
{
    /// <summary>
    /// The single description of every refusal of the confirmation token grant that concerns the
    /// transaction itself. Deliberately one text for "not parsable", "no such transaction", "not a
    /// confirmation", "another client's", "not completed" and "already redeemed": separate answers would
    /// let a caller enumerate identifiers and learn whether a transaction was redeemed (SPEC-039 E53).
    /// </summary>
    private const string ConfirmationGrantInvalidDescription =
        "The transaction cannot be exchanged for a token.";

    /// <summary>
    /// Description of the refusal of a confirmation token request whose scope lacks openid or asks for
    /// offline_access.
    /// </summary>
    private const string ConfirmationGrantScopeDescription =
        "The scope must contain 'openid' and must not contain 'offline_access'.";

    /// <summary>
    /// Description of the refusal of a confirmation token request without a transaction identifier.
    /// </summary>
    private const string ConfirmationGrantMissingTransactionDescription =
        "The 'transaction_id' parameter is required.";

    /// <summary>
    /// Description of the refusal when the claims of the confirming party could not be built.
    /// </summary>
    private const string ConfirmationGrantServerErrorDescription =
        "The token could not be issued. Try again later.";

    private static ILogger GetLogger(HttpContext httpContext)
        => httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(TokenEndpoint));

    /// <summary>
    /// Registers the token endpoint.
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name (null — no limiting).</param>
    /// <returns>Router for chaining.</returns>
    public static IEndpointRouteBuilder MapTokenEndpoint(this IEndpointRouteBuilder endpoints, string? rateLimitPolicyName = null)
    {
        var builder = endpoints.MapPost(OidcEndpoints.Token, (Delegate)HandleTokenAsync);
        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            builder.RequireRateLimiting(rateLimitPolicyName);
        }
        return endpoints;
    }

    /// <summary>
    /// Handles the token exchange request.
    /// </summary>
    private static async Task<IResult> HandleTokenAsync(HttpContext httpContext)
    {
        // The method handles exchanging an authorization code / refresh token for an access_token + id_token

        var request = httpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            return Results.BadRequest(OidcErrorCodes.OidcRequestInvalid);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            return await HandleCodeOrRefreshGrantAsync(httpContext);
        }

        if (request.IsClientCredentialsGrantType())
        {
            return HandleClientCredentialsGrant(request);
        }

        if (string.Equals(request.GrantType, ConfirmationTokenGrant.GrantType, StringComparison.Ordinal))
        {
            return await HandleConfirmationTokenGrantAsync(httpContext, request);
        }

        return Results.Forbid(
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    /// <summary>
    /// Handles the confirmation token grant (SPEC-039 C51, E53): exchanges a completed confirmation
    /// transaction the calling client created, a single time, for the identity token of the person who
    /// confirmed it.
    /// </summary>
    /// <param name="httpContext">HTTP context.</param>
    /// <param name="request">OpenIddict request, already authenticated by the server: the client, its
    /// secret, its permission for this grant and its scope permissions were all checked before the
    /// passthrough handed the request over.</param>
    /// <returns>SignIn result via OpenIddict, or an OIDC error through the server scheme.</returns>
    private static async Task<IResult> HandleConfirmationTokenGrantAsync(
        HttpContext httpContext,
        OpenIddictRequest request)
    {
        // The order is the contract: every refusal is decided before the issuance mark is written, the
        // claims are built before it too, and the token is signed only by the call that won the mark.

        // The server rejects a token request that authenticates no client long before the passthrough.
        // Said out loud rather than redeeming a transaction for an unnamed caller.
        if (string.IsNullOrEmpty(request.ClientId))
        {
            return ConfirmationGrantInvalid();
        }

        // An identity token needs openid; offline_access would bring a refresh token that issues a new
        // identity token again and again, past the single redemption. A scope outside the client's
        // AllowedScopes does not reach this point: the server's scope-permission check covers every
        // token request made by an identified client, this grant included.
        if (!request.HasScope(OpenIddictConstants.Scopes.OpenId)
            || request.HasScope(OpenIddictConstants.Scopes.OfflineAccess))
        {
            return ConfirmationGrantError(
                OpenIddictConstants.Errors.InvalidScope,
                ConfirmationGrantScopeDescription);
        }

        var rawTransactionId = (string?)request.GetParameter(ConfirmationTokenGrant.TransactionIdParameter);
        if (string.IsNullOrEmpty(rawTransactionId))
        {
            return ConfirmationGrantError(
                OpenIddictConstants.Errors.InvalidRequest,
                ConfirmationGrantMissingTransactionDescription);
        }

        // From here on every refusal about the transaction is the same answer (E53).
        var transactionId = SessionIdMapper.ToTransactionId(rawTransactionId);
        if (transactionId is null)
        {
            return ConfirmationGrantInvalid();
        }

        var transactionService = httpContext.RequestServices.GetRequiredService<ITransactionService>();
        var found = await transactionService.GetTransactionAsync(transactionId.Value, httpContext.RequestAborted);
        if (found.IsFailure)
        {
            return ConfirmationGrantInvalid();
        }

        var transaction = found.Value;

        // A login transaction would hand out the identity past the code flow and its PKCE.
        if (!string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal))
        {
            return ConfirmationGrantInvalid();
        }

        // The transaction belongs to the client it is attributed to. A foreign client gets the answer of
        // a transaction that does not exist, and the fact goes to the log (as on the result surface).
        if (!string.Equals(transaction.GetApplicationId(), request.ClientId, StringComparison.Ordinal))
        {
            GetLogger(httpContext).LogWarning(
                "A confirmation token was requested for a transaction attributed to another application. "
                + "ClientId: {ClientId}, RequestId: {RequestId}",
                request.ClientId,
                httpContext.TraceIdentifier);

            return ConfirmationGrantInvalid();
        }

        // Completed is the only outcome that has a confirming party. matched_type, null included, is not
        // a condition: a verdict is not a gate (SPEC-039 E43). The mark is checked here as well, so an
        // already redeemed transaction costs no claims mapping; the write below is what decides a race.
        if (transaction is not
            {
                State: TransactionState.Completed,
                ResolvedIdentitySnapshot: { } resolvedIdentity,
                CompletionSnapshot: { } completion
            }
            || transaction.IdentityMatch?.IdentityTokenIssuedAt is not null)
        {
            return ConfirmationGrantInvalid();
        }

        var scopes = request.GetScopes();
        var scopeSet = new HashSet<string>(scopes, StringComparer.Ordinal);

        // The avatar of the channel is asked for as /connect/authorize asks for it when it issues the code of
        // a sign-in: only for the scopes of this request, and the supplemented identity is never stored.
        var issuedIdentity = await httpContext.RequestServices.GetRequiredService<ChannelAvatarSupplement>()
            .SupplementAsync(transaction, resolvedIdentity, completion, scopeSet, httpContext.RequestAborted);

        // The claims are built in the attribution context of the transaction, exactly as /connect/authorize
        // builds them when it issues the code of a sign-in, and bounded by the scope of this request.
        var claimsMapper = httpContext.RequestServices.GetRequiredService<IClaimsMapper>();
        var claimsResult = await claimsMapper.MapToClaimsAsync(
            issuedIdentity,
            completion,
            new ClaimsMappingContext(
                transaction.GetApplicationId(),
                transaction.GetTenantId(),
                scopeSet,
                transaction.GetUiTimeZone()),
            httpContext.RequestAborted);

        if (claimsResult.IsFailure)
        {
            GetLogger(httpContext).LogError(
                "Claims mapping failed on the confirmation token grant. ClientId: {ClientId}, ErrorCode: {ErrorCode}",
                request.ClientId,
                claimsResult.Error.Code);

            return ConfirmationGrantError(
                OpenIddictConstants.Errors.ServerError,
                ConfirmationGrantServerErrorDescription);
        }

        // Of two simultaneous exchanges exactly one wins the mark; the other is told what any redeemed
        // transaction is told.
        if (!await transactionService.TryMarkIdentityTokenIssuedAsync(transactionId.Value, httpContext.RequestAborted))
        {
            return ConfirmationGrantInvalid();
        }

        var identity = new ClaimsIdentity(
            claimsResult.Value,
            TokenValidationParameters.DefaultAuthenticationType,
            OpenIddictConstants.Claims.Name,
            OpenIddictConstants.Claims.Role);

        var principal = new ClaimsPrincipal(identity);

        // With openid among the scopes the server issues the identity token next to the access token on
        // its own; without offline_access it issues no refresh token.
        principal.SetScopes(scopes);

        var claimScopes = await CatalogClaimScopes.ResolveAsync(
            httpContext.RequestServices.GetRequiredService<IConfigurationResolver>(),
            transaction.GetTenantId(),
            transaction.GetApplicationId(),
            httpContext.RequestAborted);

        foreach (var claim in principal.Claims)
        {
            claim.SetDestinations(ClaimDestinationsHelper.GetDestinations(claim, scopeSet, claimScopes));
        }

        GetLogger(httpContext).LogDebug(
            "Confirmation token issued. TransactionId: {TransactionId}, SubHash: {SubHash}",
            transactionId.Value.ToString(),
            LogMasking.Fingerprint(resolvedIdentity.Subject));

        return Results.SignIn(
            principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// The single refusal of the confirmation token grant about the transaction (SPEC-039 E53).
    /// </summary>
    /// <returns>The invalid_grant answer.</returns>
    private static IResult ConfirmationGrantInvalid() =>
        ConfirmationGrantError(OpenIddictConstants.Errors.InvalidGrant, ConfirmationGrantInvalidDescription);

    /// <summary>
    /// An OIDC error of the token endpoint, returned the standard way: a challenge of the server scheme
    /// carrying the error and its description.
    /// </summary>
    /// <param name="error">OIDC error code.</param>
    /// <param name="description">Error description.</param>
    /// <returns>The error answer.</returns>
    private static IResult ConfirmationGrantError(string error, string description) =>
        Results.Forbid(
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }),
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    /// <summary>
    /// Handles the client_credentials grant type (RFC 6749 §4.4, SPEC-039 R2).
    /// </summary>
    /// <param name="request">OpenIddict request, already authenticated by the server: the client
    /// identifier, its secret and its permission for this grant were all checked before the
    /// passthrough handed the request over.</param>
    /// <returns>SignIn result via OpenIddict.</returns>
    private static IResult HandleClientCredentialsGrant(OpenIddictRequest request)
    {
        // The method issues a token representing the calling client itself, with no user behind it

        // The client identifier cannot be absent here: OpenIddict rejects a client_credentials request
        // that does not authenticate a client long before the passthrough. Said out loud rather than
        // silently issuing a token for an unnamed client.
        if (string.IsNullOrEmpty(request.ClientId))
        {
            return Results.Forbid(
                authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        // The per-user rate limit of the code/refresh branch does not apply: there is no user to
        // partition by. This grant is limited per client at the routes that accept its token.
        return Results.SignIn(
            ClientCredentialsToken.Create(request.ClientId, request.GetScopes()),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Handles the authorization_code and refresh_token grant types.
    /// </summary>
    /// <param name="httpContext">HTTP context.</param>
    /// <returns>SignIn result via OpenIddict.</returns>
    private static async Task<IResult> HandleCodeOrRefreshGrantAsync(HttpContext httpContext)
    {
        // The method extracts the validated principal and sets the claim destinations

        var authResult = await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        if (authResult?.Principal is null)
        {
            return Results.Forbid(
                authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var claimsPrincipal = authResult.Principal;

        // Per-user rate limiting keyed by sub (TASK-016, SPEC-002).
        // Protection against excessive token exchange by a single user (e.g. refresh spam).
        // Sub has the format {channel_type}:{channel_user_id} — already normalized (CA-022).
        // The partition is scoped to the token-exchange operation so this check does not share
        // the budget with the channel auth-start/confirm checks (see UserAuthRateLimitOperations).
        var sub = claimsPrincipal.GetClaim(OpenIddictConstants.Claims.Subject);
        if (sub is not null)
        {
            var userAuthRateLimiter = httpContext.RequestServices.GetService<IUserAuthRateLimiter>();
            if (userAuthRateLimiter is not null)
            {
                var rateLimitResult = await userAuthRateLimiter.TryAcquireAsync(
                    UserAuthRateLimitOperations.TokenExchange + sub, httpContext.RequestAborted);
                if (!rateLimitResult.IsAllowed)
                {
                    var logger = GetLogger(httpContext);
                    logger.LogWarning(
                        "Per-user token rate limit exceeded. SubHash: {SubHash}",
                        LogMasking.Fingerprint(sub));

                    // Take the actual wait time from the limiter result.
                    // Fall back to the window length only when the metadata is unavailable.
                    var retryAfterSeconds = (int)Math.Ceiling(rateLimitResult.RetryAfter.TotalSeconds);
                    if (retryAfterSeconds <= 0)
                    {
                        var rateLimitOptions = httpContext.RequestServices
                            .GetService<Microsoft.Extensions.Options.IOptions<RateLimitOptions>>();
                        retryAfterSeconds = rateLimitOptions?.Value.UserAuthWindowSeconds ?? 60;
                    }

                    // Write the response directly: OpenIddict maps temporarily_unavailable → HTTP 400
                    // on the token endpoint (a switch on the error code), so Results.Forbid does not guarantee 429.
                    // RFC 6749 §5.2: error = "temporarily_unavailable", Cache-Control: no-store, Pragma: no-cache.
                    // Use the same JSON constant format as OnRejected in AddVeriqaRateLimiting,
                    // so both paths return a uniform response body.
                    httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    httpContext.Response.Headers.RetryAfter =
                        retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                    httpContext.Response.Headers.CacheControl = "no-store";
                    httpContext.Response.Headers.Pragma = "no-cache";
                    httpContext.Response.ContentType = "application/json; charset=UTF-8";

                    await httpContext.Response.WriteAsync(
                        RateLimitOptions.TokenRateLimitErrorJson,
                        httpContext.RequestAborted);

                    return new AlreadyWrittenResult();
                }
            }
        }

        // Compute the destinations taking into account the scopes from the principal and the catalog of the
        // calling client — the tokens of a code or a refresh are issued here, so the gate of the catalog
        // (SPEC-041 R21) has to hold here too. No transaction is left to read the tenant off, so it is
        // resolved from the client, as /connect/authorize resolves it.
        var scopes = claimsPrincipal.GetScopes();
        var scopeSet = new HashSet<string>(scopes, StringComparer.Ordinal);

        var clientId = httpContext.GetOpenIddictServerRequest()?.ClientId;
        var claimScopes = await CatalogClaimScopes.ResolveAsync(
            httpContext.RequestServices.GetRequiredService<IConfigurationResolver>(),
            await ClientTenantLookup.ResolveAsync(httpContext, clientId, GetLogger(httpContext)),
            clientId,
            httpContext.RequestAborted);

        foreach (var claim in claimsPrincipal.Claims)
        {
            claim.SetDestinations(ClaimDestinationsHelper.GetDestinations(claim, scopeSet, claimScopes));
        }

        return Results.SignIn(
            claimsPrincipal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Stub result: the response has already been written to <see cref="HttpContext.Response"/>.
    /// ExecuteAsync does nothing, so the status code and body are not overwritten.
    /// </summary>
    private sealed class AlreadyWrittenResult : IResult
    {
        /// <inheritdoc/>
        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
    }
}
