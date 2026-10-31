// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// Which ways of obtaining the email are available (SPEC-041 C96): the ONE judgement the writer of the
/// requirements of a transaction, the startup check of the configuration and the email block of the web
/// step all ask, so the three cannot disagree about whether the email has a completion path.
/// <para>
/// The list is the effective value of <see cref="ClaimCompletionConfigKeys.EmailMethods"/> — the resolver
/// has already applied the narrowing of the levels; this type adds only what the surface and the mail
/// infrastructure of the tenant allow.
/// </para>
/// </summary>
/// <remarks>
/// Singleton: it holds the resolver and the port, both singletons.
/// </remarks>
internal sealed class EmailCompletionAvailability
{
    /// <summary>
    /// The canonical resolver of the effective setting values.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Mail side of the email completion; null — the Email adapter is not added or not enabled.
    /// </summary>
    private readonly IClaimCompletionEmail? _email;

    /// <summary>
    /// Creates the judgement.
    /// </summary>
    /// <param name="resolver">Canonical resolver of the effective setting values.</param>
    /// <param name="email">
    /// Registrations of the mail side of the email completion: none without an enabled Email adapter.
    /// </param>
    public EmailCompletionAvailability(IConfigurationResolver resolver, IEnumerable<IClaimCompletionEmail> email)
    {
        ArgumentNullException.ThrowIfNull(email);

        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

        // The last registration is the one the container would hand out for the single service.
        _email = email.LastOrDefault();
    }

    /// <summary>
    /// Mail side of the email completion the judgement asks — the one the email block of the web step acts
    /// through, so both see the same registration; null without an enabled Email adapter.
    /// </summary>
    public IClaimCompletionEmail? Email => _email;

    /// <summary>
    /// Normalizes an entered or stored email address the way the email block writes it: through the mail side
    /// when the Email adapter is on, otherwise as a bare address trimmed at the edges.
    /// </summary>
    /// <param name="address">Address as entered or stored.</param>
    /// <returns>The normalized address; null — not an address the email completion admits.</returns>
    public string? NormalizeAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return _email is { } email
            ? email.NormalizeAddress(address)
            : ClaimEmailAddress.TakeBare(address);
    }

    /// <summary>
    /// The methods of a list that are available on a surface, in the order of the list (SPEC-041 C96).
    /// These are the values of the list, not yet the ways the user is offered: <c>Code</c> and
    /// <c>MagicLink</c> are both listed here and become one way where the block is built.
    /// </summary>
    /// <param name="list">Effective list of methods.</param>
    /// <param name="surface">Surface the web step is shown on.</param>
    /// <param name="capabilities">What the mail infrastructure of the tenant can do.</param>
    /// <returns>The available methods; empty — the email cannot be obtained on this surface.</returns>
    public static IReadOnlyList<EmailCompletionMethod> Resolve(
        IReadOnlyList<EmailCompletionMethod> list,
        WebStepSurface surface,
        ClaimEmailCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(capabilities);

        return [.. list.Where(method => IsAvailable(method, surface, capabilities))];
    }

    /// <summary>
    /// What the mail infrastructure of a tenant can do for the email completion.
    /// </summary>
    /// <param name="tenantId">Tenant; null — the implicit tenant of a self-hosted deployment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capabilities; <see cref="ClaimEmailCapabilities.None"/> without the Email adapter.</returns>
    public ValueTask<ClaimEmailCapabilities> GetCapabilitiesAsync(
        string? tenantId,
        CancellationToken cancellationToken) =>
        _email is null
            ? ValueTask.FromResult(ClaimEmailCapabilities.None)
            : _email.GetCapabilitiesAsync(tenantId, cancellationToken);

    /// <summary>
    /// The effective list of methods in a scope of resolution.
    /// </summary>
    /// <param name="scope">Scope of resolution — the ownership context the list is resolved for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective list; the shipped one when no level states a list.</returns>
    public async ValueTask<IReadOnlyList<EmailCompletionMethod>> ReadListAsync(
        ConfigResolutionScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var resolved = await _resolver.ResolveAsync(
            ClaimCompletionConfigKeys.EmailMethods,
            scope,
            ConfigDimensionValues.None,
            cancellationToken);

        return resolved.Value ?? [];
    }

    /// <summary>
    /// The methods available on a surface in a scope of resolution: the effective list, judged by the
    /// capabilities of the tenant of the scope.
    /// </summary>
    /// <param name="scope">Scope of resolution — the ownership context the list is resolved for.</param>
    /// <param name="surface">Surface the web step is shown on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The available methods; empty — the email cannot be obtained on this surface.</returns>
    public async ValueTask<IReadOnlyList<EmailCompletionMethod>> ResolveAsync(
        ConfigResolutionScope scope,
        WebStepSurface surface,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var list = await ReadListAsync(scope, cancellationToken);
        var capabilities = await GetCapabilitiesAsync(scope.Context.TenantId, cancellationToken);

        return Resolve(list, surface, capabilities);
    }

    /// <summary>
    /// Whether one method is available: <c>Push</c> on the page of the confirming device with the inbound
    /// mail of the tenant, <c>Code</c> and <c>MagicLink</c> with its outbound mail, <c>Unverified</c> always.
    /// </summary>
    /// <param name="method">Method.</param>
    /// <param name="surface">Surface the web step is shown on.</param>
    /// <param name="capabilities">What the mail infrastructure of the tenant can do.</param>
    /// <returns><c>true</c> when the method is available.</returns>
    private static bool IsAvailable(
        EmailCompletionMethod method,
        WebStepSurface surface,
        ClaimEmailCapabilities capabilities) => method switch
        {
            EmailCompletionMethod.Push => surface is WebStepSurface.CrossDevicePage && capabilities.CanReceivePush,
            EmailCompletionMethod.Code or EmailCompletionMethod.MagicLink => capabilities.CanSend,
            EmailCompletionMethod.Unverified => true,
            _ => false
        };
}
