// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// Resolves the tenant a calling OIDC client belongs to. Called once per request by the entry points
/// that know the client but have no transaction to read the tenant off: a path that creates a
/// transaction stores the answer on its request context — from there every reader takes it through
/// <see cref="TransactionContextExtensions.GetTenantId"/> — and a page that is handed no transaction
/// (the <c>form_post</c> response page receives the HTTP context only) asks again.
/// <para>
/// The mapping <c>client_id</c> → tenant is deterministic, which is why the tenant travels with the
/// transaction rather than being read from an ambient scope: the scope is opened only by the channel
/// paths (webhook, polling, prompt expiry), and the HTTP entry points have none.
/// </para>
/// <para>
/// The tenant is a convenience for resolving tenant-level configuration keys, <b>not an access
/// boundary</b>: isolation is provided by <c>client_id</c>, which sits above the tenant.
/// </para>
/// </summary>
/// <remarks>
/// <b>Who answers this.</b> The engine ships <see cref="DefaultClientTenantResolver"/>, which always
/// answers null — the single default tenant of an installation that never heard of tenants, and the
/// correct answer for a host using the engine without the auth server. The auth server ships one that
/// reads the tenant off the calling client's own configuration entry
/// (<c>Veriqa:OpenIddict:Clients[].TenantId</c>): the entry already describes the client, so a fact
/// about that client belongs there rather than in a map of its own.
/// <para>
/// The auth server's registration DISPLACES the engine's shipped one rather than queueing behind it
/// (<c>ReplaceShippedClientTenantResolver</c>), so the order of preference is the same on every path,
/// including a host that wires the engine first: a resolver of the host, then the auth server's, then
/// the engine's null answer.
/// </para>
/// <para>
/// A host that registers its own resolver — one deriving the tenant from a catalog, a route, or a
/// contour of its own — wins over both, whatever the call order, when it registers with
/// <c>AddSingleton</c> or <c>Replace</c>. <c>TryAdd</c> wins only while the slot is still free —
/// that is, before either the Transaction Engine or the auth server has registered one, wherever in
/// the composition that happens.
/// </para>
/// </remarks>
public interface IClientTenantResolver
{
    /// <summary>
    /// Resolves the tenant of an OIDC client.
    /// </summary>
    /// <param name="clientId">Client identifier of the request; null — the request states none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tenant identifier, or null when the client belongs to no explicit tenant.</returns>
    ValueTask<string?> ResolveTenantAsync(string? clientId, CancellationToken cancellationToken = default);
}
