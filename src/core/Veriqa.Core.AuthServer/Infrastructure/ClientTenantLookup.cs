// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// Resolves the tenant of the calling client for the HTTP entry points of the auth server. No ambient
/// tenant scope is open on those paths (only the channel paths — webhook, polling, prompt expiry — open
/// one), so a request that knows its client but has no transaction to read the tenant off asks the
/// published <see cref="IClientTenantResolver"/> here and states the answer in its resolution context;
/// without it the request would resolve past the tenant level entirely (SPEC-012 CFG-042).
/// </summary>
internal static class ClientTenantLookup
{
    /// <summary>
    /// Resolves the tenant of the calling client through the published port.
    /// </summary>
    /// <remarks>
    /// A failing resolver must not fail the request: the tenant is a resolution layer for tenant-level
    /// configuration keys, not an access boundary (SPEC-003 CA-164), so an unavailable mapping degrades to
    /// "no tenant stated" — the same answer a self-hosted installation gives — and the fact is logged
    /// rather than surfaced to the caller. Cancellation is left to propagate: the request is gone.
    /// </remarks>
    /// <param name="httpContext">HTTP context (source of the resolver and of the cancellation token).</param>
    /// <param name="clientId">Client identifier of the request.</param>
    /// <param name="logger">Logger of the calling entry point.</param>
    /// <returns>Tenant identifier, or null when none is stated or the resolution failed.</returns>
    public static async Task<string?> ResolveAsync(
        HttpContext httpContext,
        string? clientId,
        ILogger logger) =>
        (await ResolveWithOutcomeAsync(httpContext, clientId, logger)).TenantId;

    /// <summary>
    /// Resolves the tenant of the calling client like <see cref="ResolveAsync"/>, and tells a failed
    /// resolution apart from a client that states no tenant — the one caller that needs the difference
    /// keeps the collected claims store off a sign-in whose tenant is unknown (SPEC-041 E62).
    /// </summary>
    /// <param name="httpContext">HTTP context (source of the resolver and of the cancellation token).</param>
    /// <param name="clientId">Client identifier of the request.</param>
    /// <param name="logger">Logger of the calling entry point.</param>
    /// <returns>The tenant (null when none is stated or the resolution failed) and whether it failed.</returns>
    public static async Task<ClientTenantResolution> ResolveWithOutcomeAsync(
        HttpContext httpContext,
        string? clientId,
        ILogger logger)
    {
        var resolver = httpContext.RequestServices.GetRequiredService<IClientTenantResolver>();

        try
        {
            return new ClientTenantResolution(
                await resolver.ResolveTenantAsync(clientId, httpContext.RequestAborted),
                Failed: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Tenant resolution failed for client {ClientId}; the request is resolved without a tenant level",
                clientId);
            return new ClientTenantResolution(null, Failed: true);
        }
    }
}

/// <summary>
/// Answer of <see cref="ClientTenantLookup.ResolveWithOutcomeAsync"/>.
/// </summary>
/// <param name="TenantId">Tenant of the client; null — none stated, or the resolution failed.</param>
/// <param name="Failed">Whether the resolver threw.</param>
internal readonly record struct ClientTenantResolution(string? TenantId, bool Failed);
