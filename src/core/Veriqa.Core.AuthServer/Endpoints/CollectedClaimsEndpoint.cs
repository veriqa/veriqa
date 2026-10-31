// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Claims;

using Microsoft.AspNetCore.Mvc;

using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.Contracts.Audit;
using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Server-to-server deletion of the collected claims of a subject
/// (<c>DELETE /api/collected-claims?sub=&lt;sub&gt;[&amp;client_id=&lt;application&gt;]</c>, SPEC-041 C82): the
/// operator of personal data erases what Veriqa keeps about a person (R29) from its own server.
/// </summary>
/// <remarks>
/// <para>
/// The caller is a client holding the right (<see cref="OidcClientOptions.AllowCollectedClaimsDeletion"/>) and
/// authenticated by a client credentials token (N87). The subject is taken in the form the application of
/// <c>client_id</c> received it — the calling client itself when the parameter is absent — and the subjects
/// issued to applications are looked up in the tenant of the calling client; a subject not found there is the
/// core one. The answer is <c>204</c> whether or not anything was stored: the deletion is idempotent
/// (RFC 9110 §9.3.5).
/// </para>
/// <para>
/// The tenant is resolved by <see cref="IClientTenantResolver"/> directly and not through the lookup the
/// browser paths use, which answers "no tenant" when resolution fails: here that answer would delete the
/// records of the implicit tenant instead of the caller's. A failure of the resolver or of the store is a
/// <c>503</c>. The resolver fails before anything is deleted. When the subject stands for several core subjects,
/// those the store deleted before it failed stay deleted and recorded in the audit journal (every deletion made
/// is recorded, SPEC-041 L85); repeating the request completes the deletion.
/// </para>
/// </remarks>
internal static class CollectedClaimsEndpoint
{
    /// <summary>
    /// Registers the deletion endpoint. The route is mapped only where a collected claims store is registered.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapCollectedClaimsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        // No rate limiting policy: the right is granted to one client of the integrator explicitly.
        endpoints.MapDelete(OidcEndpoints.CollectedClaims, HandleDeleteAsync)
            .RequireAuthorization(policy =>
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser());

        return endpoints;
    }

    /// <summary>
    /// Checks the caller and the request, resolves the tenant of the caller and runs the deletion.
    /// </summary>
    private static async Task<IResult> HandleDeleteAsync(
        HttpContext httpContext,
        ClaimsPrincipal user,
        OidcClientsOptionsAccessor clientsAccessor,
        IClientTenantResolver tenantResolver,
        [FromServices] CollectedClaimsDeletion deletion,
        ILoggerFactory loggerFactory)
    {
        // The order of the answers: the route policy has already answered 401; a user token or a client
        // without the right is 403; a request without a subject is 400; a dependency that did not answer is
        // 503 — and nothing is deleted before every check has passed.
        var logger = loggerFactory.CreateLogger(typeof(CollectedClaimsEndpoint));
        var cancellationToken = httpContext.RequestAborted;

        if (!ClientCredentialsToken.TryGetClientId(user, out var callerId)
            || !HoldsDeletionRight(clientsAccessor.Current, callerId))
        {
            return Results.Forbid(
                authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme]);
        }

        var query = httpContext.Request.Query;

        if (!TryReadSingle(query, OpenIddictConstants.Claims.Subject, out var subject))
        {
            return RequestInvalid("The request states no sub, or states it more than once.");
        }

        var applicationId = callerId;

        if (query.ContainsKey(OpenIddictConstants.Parameters.ClientId))
        {
            if (!TryReadSingle(query, OpenIddictConstants.Parameters.ClientId, out var statedApplicationId))
            {
                return RequestInvalid("The stated client_id is empty or stated more than once.");
            }

            applicationId = statedApplicationId;
        }

        string? tenantId;

        try
        {
            tenantId = await tenantResolver.ResolveTenantAsync(callerId, cancellationToken);
        }
        catch (Exception exception) when (!IsAbortOf(exception, cancellationToken))
        {
            logger.LogWarning(
                exception,
                "Tenant resolution failed for client {ClientId}; no collected claims were deleted",
                callerId);

            return StoreUnavailable();
        }

        try
        {
            await deletion.DeleteAsync(
                new CollectedClaimsDeletionRequest
                {
                    TenantId = tenantId,
                    ApplicationId = applicationId,
                    Subject = subject,
                    Actor = callerId,
                    ClientId = callerId,
                    DeletionBasis = AuditDeletionBases.Endpoint
                },
                cancellationToken);
        }
        catch (Exception exception) when (!IsAbortOf(exception, cancellationToken))
        {
            logger.LogWarning(
                exception,
                "The collected claims store failed to delete subject {MaskedSubject} at the request of client {ClientId}",
                IdentifierMask.Mask(subject),
                callerId);

            return StoreUnavailable();
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Whether an exception is the cancellation of the request itself — the caller went away, and there is
    /// nobody to answer. A cancellation from inside a dependency (a timeout of the database) is a dependency
    /// that did not answer.
    /// </summary>
    /// <param name="exception">Caught exception.</param>
    /// <param name="requestAborted">Token of the request.</param>
    /// <returns><c>true</c> when the request was aborted.</returns>
    private static bool IsAbortOf(Exception exception, CancellationToken requestAborted) =>
        exception is OperationCanceledException && requestAborted.IsCancellationRequested;

    /// <summary>
    /// Whether the entry of a client grants the right to delete collected claims.
    /// </summary>
    /// <param name="clients">Current OIDC clients configuration.</param>
    /// <param name="clientId">Identifier of the calling client.</param>
    /// <returns><c>true</c> when the entry of the client states the right.</returns>
    private static bool HoldsDeletionRight(OidcClientsOptions clients, string clientId) =>
        clients.Clients
            .FirstOrDefault(entry => string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
            ?.AllowCollectedClaimsDeletion is true;

    /// <summary>
    /// Reads a query parameter stated exactly once and not blank.
    /// </summary>
    /// <param name="query">Query of the request.</param>
    /// <param name="name">Parameter name.</param>
    /// <param name="value">The value, when it is stated once and not blank.</param>
    /// <returns><c>true</c> when the parameter is stated once and not blank.</returns>
    private static bool TryReadSingle(IQueryCollection query, string name, out string value)
    {
        value = string.Empty;

        if (!query.TryGetValue(name, out var values) || values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return true;
    }

    /// <summary>
    /// The 400 answer of a malformed request; the detail names no value of the request.
    /// </summary>
    /// <param name="detail">Message free of any caller-supplied value.</param>
    /// <returns>The 400 answer.</returns>
    private static IResult RequestInvalid(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: OidcErrorCodes.OidcRequestInvalid);

    /// <summary>
    /// The 503 answer of a store or a tenant resolution that did not answer.
    /// </summary>
    /// <returns>The 503 answer.</returns>
    private static IResult StoreUnavailable() =>
        Results.Problem(
            detail: "The collected claims could not be deleted now. Try again later.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: OidcErrorCodes.CollectedClaimsStoreUnavailable);
}
