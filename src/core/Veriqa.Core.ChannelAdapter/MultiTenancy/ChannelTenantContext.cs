// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// Ambient tenant context of the processing path (SPEC-003 §17.4, CA-164).
/// A minimal addition of tenant context to the processing path: the tenant is stated before
/// credential selection by whoever legitimately knows it — WHICH paths those are is settled in one
/// place, <see cref="BeginScope"/>, and is not restated here; the adapter and the client factory
/// read it without changing the <c>IChannelAdapter</c> operation signatures (CA-010…CA-016 are not
/// violated, CA-100 is preserved).
/// <see cref="CurrentTenantId"/> = null means the default implicit tenant (self-hosted N=1).
/// Implemented via <see cref="AsyncLocal{T}"/> — isolation per logical request flow; no static
/// adapter cache (multiple DI containers per process — OK).
/// </summary>
public static class ChannelTenantContext
{
    /// <summary>
    /// Ambient storage of the current request's tenant scope; null — no scope was opened.
    /// </summary>
    /// <remarks>
    /// The scope is stored as an object rather than as the tenant string itself, because the two
    /// states a bare string cannot tell apart are exactly the two this context has to distinguish:
    /// "a scope was opened for the default tenant" (self-hosted N=1) and "nobody opened a scope at
    /// all" (a path that forgot to). Both read as <see langword="null"/> tenant — see
    /// <see cref="IsEstablished"/>.
    /// </remarks>
    private static readonly AsyncLocal<TenantScopeState?> CurrentScope = new();

    /// <summary>
    /// How the default implicit tenant (null) is spelled in diagnostic messages — one spelling for
    /// every message that names a tenant, so a log line does not read differently per channel.
    /// </summary>
    internal const string DefaultTenantDisplayName = "<default>";

    /// <summary>
    /// Tenant identifier of the current request; null — the default implicit tenant (self-hosted).
    /// </summary>
    public static string? CurrentTenantId => CurrentScope.Value?.TenantId;

    /// <summary>
    /// Whether a tenant scope is open on the current flow — true even when it was opened for the
    /// default implicit tenant (self-hosted N=1, <see cref="CurrentTenantId"/> = null).
    /// <para>
    /// It answers "did anybody state whose request this is", which a null tenant alone cannot: a path
    /// that never opened a scope lands on the default tenant silently, exactly like a self-hosted
    /// installation that legitimately has one. Nested scopes keep it true until the outermost one is
    /// disposed.
    /// </para>
    /// </summary>
    public static bool IsEstablished => CurrentScope.Value is not null;

    /// <summary>
    /// Sets the current request's tenant for the duration of processing (scope via <see cref="IDisposable"/>).
    /// On scope exit the previous value is restored (nesting is safe).
    /// <para>
    /// Open to any caller, but not to any path: WHO the current request belongs to is decided by
    /// whoever legitimately knows it — from the transport or from an object already on hand. Inside
    /// this tree those are the webhook pipeline (tenant route segment / per-tenant secret), the
    /// polling supervisors (iteration over tenants), the prompt expiry handler (the reference's own
    /// tenant), the delivery of a claim completion message and the starter of its phone step (the
    /// channel identity snapshot of the transaction they act for) and the web paths of a channel (the
    /// transaction they have just loaded). A channel
    /// living outside this tree opens the scope on the very same terms, on its own path OUTSIDE a
    /// request: its supervisor or polling loop, around the processing of one tenant taken from
    /// <see cref="IPollingTenantSource"/>, and its health probe, which states the default implicit
    /// tenant explicitly (null), because otherwise its credential read is indistinguishable from a
    /// request path that forgot to open a scope. What an adapter must not do is open a scope ON A
    /// REQUEST PATH — the webhook: that would repoint every credential read below it, this one
    /// included, at a tenant of its choosing instead of the one the host has already stated there.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant identifier; null — the default implicit tenant.</param>
    /// <returns>Scope that restores the previous tenant on Dispose.</returns>
    public static IDisposable BeginScope(string? tenantId)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new TenantScopeState(tenantId);
        return new TenantScope(previous);
    }

    /// <summary>
    /// State of one open tenant scope.
    /// </summary>
    /// <param name="TenantId">Tenant of the scope; null — the default implicit tenant.</param>
    private sealed record TenantScopeState(string? TenantId);

    /// <summary>
    /// Scope that restores the previous tenant (RAII via using).
    /// </summary>
    private sealed class TenantScope : IDisposable
    {
        /// <summary>
        /// Previous scope state (restored on Dispose); null — there was no scope.
        /// </summary>
        private readonly TenantScopeState? _previous;

        /// <summary>
        /// Creates a scope remembering the previous state.
        /// </summary>
        /// <param name="previous">Previous scope state.</param>
        public TenantScope(TenantScopeState? previous)
        {
            _previous = previous;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            CurrentScope.Value = _previous;
        }
    }
}
