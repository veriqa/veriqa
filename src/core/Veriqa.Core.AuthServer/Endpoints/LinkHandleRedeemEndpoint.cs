// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Claims;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;

using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Server-to-server exchange of a link handle for the identity of the sign-in it was issued for
/// (<c>POST /api/link-handle/redeem</c>). The handle reaches the server of the relying party through the
/// address of a post-completion button; the relying party presents it here under its client credentials
/// token and learns who signed in.
/// </summary>
/// <remarks>
/// <para>
/// A handle is exchanged once and only by the client its sign-in is attributed to: the store hands the
/// record out and removes it in one operation, before anything of the transaction is read. Whatever
/// refuses after that — the transaction is gone, or the claims mapping failed — leaves the handle spent.
/// </para>
/// <para>
/// The identity of the answer is built by <see cref="IClaimsMapper"/> with the context the sign-in itself
/// is mapped with, so the subject is the one the tokens of this client carry. The exchange does not wait
/// for the authorization code: the button is pressed in the chat before, or without, the browser coming
/// back, and the identity is already resolved and confirmed by then.
/// </para>
/// </remarks>
internal static class LinkHandleRedeemEndpoint
{
    /// <summary>
    /// What the caller is told when the handle cannot be exchanged. Deliberately the same answer for a
    /// request stating no handle, an unknown, expired or already exchanged handle, a handle of another
    /// client and a sign-in that is no longer readable: separate answers would tell a holder of someone
    /// else's handle that it exists.
    /// </summary>
    private const string NotFoundMessage = "Link handle not found.";

    /// <summary>
    /// What the caller is told when the identity of the sign-in could not be built.
    /// </summary>
    private const string MappingFailedMessage = "Failed to read the identity of the sign-in. Try again later.";

    /// <summary>
    /// Closing sentence of the warning about a subject that could not be recorded: the exchange issues
    /// no code — it answers with the identity whether or not the record was made.
    /// </summary>
    private const string IssuedSubjectStoreFailureOutcome = "the identity is returned all the same.";

    /// <summary>
    /// Registers the link handle exchange endpoint.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name (null — no limiting).</param>
    /// <returns>The route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapLinkHandleRedeemEndpoint(
        this IEndpointRouteBuilder endpoints,
        string? rateLimitPolicyName = null)
    {
        var builder = endpoints.MapPost(OidcEndpoints.LinkHandleRedeem, HandleRedeemAsync)
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
    /// Takes the record of the presented handle out of the store and answers with the identity of its
    /// sign-in as the calling client receives it.
    /// </summary>
    private static async Task<IResult> HandleRedeemAsync(
        LinkHandleRedeemRequest? body,
        HttpContext httpContext,
        ClaimsPrincipal user,
        [FromServices] ILinkHandleStore linkHandleStore,
        ITransactionService transactionService,
        IClaimsMapper claimsMapper,
        OidcClientsOptionsAccessor clientsAccessor,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(LinkHandleRedeemEndpoint));
        var cancellationToken = httpContext.RequestAborted;

        // The token must represent the CLIENT. Authentication itself was enforced by the route policy
        // (401); a valid USER token reaching here is the other refusal (403): a person who signed in must
        // not act on behalf of the application.
        if (!ClientCredentialsToken.TryGetClientId(user, out var clientId))
        {
            return Results.Forbid(
                authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme]);
        }

        // The answer carries the identity of a person, so it is never cached on the way.
        httpContext.Response.Headers.CacheControl = "no-store";

        var handle = body?.Handle;
        if (string.IsNullOrEmpty(handle))
        {
            return NotFound();
        }

        // The record is taken before anything of the transaction is read: the store compares the client
        // and removes the record in one operation, so a handle is exchanged at most once and a foreign
        // client learns nothing — not even that the handle exists.
        var record = await linkHandleStore.TakeAsync(LinkHandle.HashOf(handle), clientId, cancellationToken);
        if (record is null)
        {
            return NotFound();
        }

        var found = await transactionService.GetTransactionAsync(record.TransactionId, cancellationToken);

        if (found.IsFailure
            || found.Value is not
            {
                State: TransactionState.Completed,
                ResolvedIdentitySnapshot: { } resolvedIdentity,
                CompletionSnapshot: { } completion,
                OidcContext: { } oidcContext
            } transaction)
        {
            return NotFound();
        }

        // The same context the authorization endpoint maps the sign-in with, the scopes of the stored
        // authorization request included: a substituted mapper must produce here the subject it produces
        // for the tokens of this client.
        var scopeSet = new HashSet<string>(
            (oidcContext.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        // The avatar of the channel is asked for on the terms of every issuance: only for these scopes, and
        // the supplemented identity goes to the mapping and is never stored.
        var issuedIdentity = await httpContext.RequestServices.GetRequiredService<ChannelAvatarSupplement>()
            .SupplementAsync(transaction, resolvedIdentity, completion, scopeSet, cancellationToken);

        var claimsResult = await claimsMapper.MapToClaimsAsync(
            issuedIdentity,
            completion,
            new ClaimsMappingContext(
                transaction.GetApplicationId(),
                transaction.GetTenantId(),
                scopeSet,
                transaction.GetUiTimeZone()),
            cancellationToken);

        if (claimsResult.IsFailure)
        {
            return MappingFailed(logger, clientId, transaction, claimsResult.Error.Code);
        }

        var claims = claimsResult.Value;
        var subject = ValueOf(claims, OpenIddictConstants.Claims.Subject);

        if (string.IsNullOrEmpty(subject))
        {
            return MappingFailed(logger, clientId, transaction, OidcErrorCodes.ClaimsMappingFailed);
        }

        // The exchange hands the subject to the application just as the tokens do — and is the only place
        // that does when the browser never comes back — so the subject is recorded on the same terms.
        await IssuedSubjectRecorder.RecordAsync(
            httpContext,
            transaction,
            claims,
            logger,
            IssuedSubjectStoreFailureOutcome);

        // No wider than the sign-in: the channel claims leave only to a client whose entry allows the
        // scope that gates them in the tokens.
        var disclosesChannel = AllowsChannelScope(clientsAccessor.Current, clientId);

        // transaction.Id.ToString() — a Base62 string (alphabet 0-9, a-z, A-Z), log injection is excluded
        logger.LogInformation(
            "A link handle was exchanged. ClientId: {ClientId}, TransactionId: {TransactionId}",
            clientId,
            transaction.Id.ToString());

        return Results.Ok(new LinkHandleRedeemResponse
        {
            Subject = subject,
            ChannelType = disclosesChannel ? ValueOf(claims, VeriqaClaimTypes.ChannelType) : null,
            ChannelUserId = disclosesChannel ? ValueOf(claims, VeriqaClaimTypes.ChannelUserId) : null
        });
    }

    /// <summary>
    /// Value of the first claim of the given type among the claims the mapping produced.
    /// </summary>
    /// <param name="claims">Claims the mapping produced.</param>
    /// <param name="type">Claim type.</param>
    /// <returns>The value; null — the mapping produced no such claim.</returns>
    private static string? ValueOf(IReadOnlyList<Claim> claims, string type) =>
        claims.FirstOrDefault(claim => string.Equals(claim.Type, type, StringComparison.Ordinal))?.Value;

    /// <summary>
    /// Whether the entry of a client allows the scope gating the channel claims. A client without an entry
    /// is answered as one that does not.
    /// </summary>
    /// <param name="clients">Current OIDC clients configuration.</param>
    /// <param name="clientId">Identifier of the calling client.</param>
    /// <returns><c>true</c> when the entry of the client allows the scope.</returns>
    private static bool AllowsChannelScope(OidcClientsOptions clients, string clientId) =>
        clients.Clients
            .FirstOrDefault(entry => string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
            ?.AllowedScopes.Contains(VeriqaScopes.Channel, StringComparer.Ordinal) is true;

    /// <summary>
    /// The single "not found" answer of this surface.
    /// </summary>
    /// <returns>The 404 answer.</returns>
    private static IResult NotFound() =>
        Results.Problem(
            detail: NotFoundMessage,
            statusCode: StatusCodes.Status404NotFound,
            title: OidcErrorCodes.TransactionNotFound);

    /// <summary>
    /// The answer of a claims mapping that refused or produced no subject. The handle is spent by now.
    /// </summary>
    /// <param name="logger">Logger of the surface.</param>
    /// <param name="clientId">Identifier of the calling client.</param>
    /// <param name="transaction">Transaction of the sign-in.</param>
    /// <param name="errorCode">Code of the failure.</param>
    /// <returns>The 500 answer.</returns>
    private static IResult MappingFailed(ILogger logger, string clientId, Transaction transaction, string errorCode)
    {
        // transaction.Id.ToString() — a Base62 string (alphabet 0-9, a-z, A-Z), log injection is excluded
        logger.LogError(
            "Claims mapping error on a link handle exchange. ClientId: {ClientId}, TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
            clientId,
            transaction.Id.ToString(),
            errorCode);

        return Results.Problem(
            detail: MappingFailedMessage,
            statusCode: StatusCodes.Status500InternalServerError,
            title: OidcErrorCodes.ClaimsMappingFailed);
    }
}

/// <summary>
/// Body of the request exchanging a link handle. Unknown fields are ignored — the ordinary behaviour of the
/// deserializer.
/// </summary>
internal sealed class LinkHandleRedeemRequest
{
    /// <summary>
    /// The handle taken from the address of the post-completion button. Required.
    /// </summary>
    [JsonPropertyName("handle")]
    public string? Handle { get; init; }
}

/// <summary>
/// Body of the answer of the link handle exchange: the identity of the sign-in as the calling client
/// receives it in its tokens.
/// </summary>
internal sealed class LinkHandleRedeemResponse
{
    /// <summary>
    /// Subject of the signed-in user for the calling client.
    /// </summary>
    [JsonPropertyName(OpenIddictConstants.Claims.Subject)]
    public required string Subject { get; init; }

    /// <summary>
    /// Channel the user signed in through; absent — the mapping produced no such claim, or the entry of
    /// the client does not allow the scope gating it.
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.ChannelType)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelType { get; init; }

    /// <summary>
    /// Identifier of the user in that channel; absent on the same terms as <see cref="ChannelType"/>.
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.ChannelUserId)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelUserId { get; init; }
}
