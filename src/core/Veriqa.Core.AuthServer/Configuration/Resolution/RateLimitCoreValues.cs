// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// The CORE getters of every rate-limit key for the ONE step that cannot read them any other way
/// (SPEC-007 §6.1, CFG-223): the framework composition that configures the limiter policies
/// synchronously before the DI container exists (through <see cref="ConfigCoreValues"/> — not a
/// resolver: it answers the core level and nothing else).
/// <para>
/// At RUNTIME these keys are read through the canonical resolver, and their core level is bound there
/// by the address each key declares (<see cref="RateLimitConfigKeys"/>) — the same section and the same
/// members the options class below is bound from, so the two answers cannot part. The getters here
/// exist only because a composition step that runs before the container has no binding to read.
/// </para>
/// </summary>
internal static class RateLimitCoreValues
{
    /// <summary>
    /// Declares the core getters of all rate-limit keys.
    /// Registers every <see cref="RateLimitConfigKeys"/> getter from the bound options; a caller resolves
    /// only the subset it needs. Registration is lazy (<c>() =&gt; config.X</c>), so getters for keys a caller
    /// never resolves are simply never evaluated and have no side effects — a single all-keys factory
    /// therefore costs a caller nothing for the keys it does not resolve. Behavior for self-hosted
    /// N=1 is the core value 1:1 — no second precedence mechanism (CFG-202/CFG-235). The method neither
    /// validates the option values nor null-checks <paramref name="config"/>: it moves values 1:1.
    /// </summary>
    /// <param name="config">Bound rate-limit options (normally <c>IOptions&lt;RateLimitOptions&gt;.Value</c>).</param>
    /// <returns>Reader of all 26 rate-limit core values.</returns>
    internal static ConfigCoreValues Declare(RateLimitOptions config) =>
        ConfigCoreValues.Declare(provider => Declare(provider, () => config));

    /// <summary>
    /// Declares the core getters of all rate-limit keys into a binding registry.
    /// </summary>
    /// <param name="provider">Registry of core bindings.</param>
    /// <param name="options">Source of the current rate-limit options.</param>
    private static void Declare(IConfigCoreBindings provider, Func<RateLimitOptions> options)
    {
        // Declare all endpoint (12 permit + 12 window) and UserAuth (permit + window) getters.
        provider.RegisterCore(RateLimitConfigKeys.AuthorizePermitLimit, () => options().AuthorizePermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.SignalRPermitLimit, () => options().SignalRPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.WebhookPermitLimit, () => options().WebhookPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.TokenPermitLimit, () => options().TokenPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.CallbackPermitLimit, () => options().CallbackPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.PollingPermitLimit, () => options().PollingPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.EmailStartPermitLimit, () => options().EmailStartPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.ConfirmationCreatePermitLimit, () => options().ConfirmationCreatePermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.ConfirmationResultPermitLimit, () => options().ConfirmationResultPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.ConfirmationPagesPermitLimit, () => options().ConfirmationPagesPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.HopResolvePermitLimit, () => options().HopResolvePermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.HopSignalPermitLimit, () => options().HopSignalPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.AuthorizeWindowSeconds, () => options().AuthorizeWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.SignalRWindowSeconds, () => options().SignalRWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.WebhookWindowSeconds, () => options().WebhookWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.TokenWindowSeconds, () => options().TokenWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.CallbackWindowSeconds, () => options().CallbackWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.PollingWindowSeconds, () => options().PollingWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.EmailStartWindowSeconds, () => options().EmailStartWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.ConfirmationCreateWindowSeconds, () => options().ConfirmationCreateWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.ConfirmationResultWindowSeconds, () => options().ConfirmationResultWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.ConfirmationPagesWindowSeconds, () => options().ConfirmationPagesWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.HopResolveWindowSeconds, () => options().HopResolveWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.HopSignalWindowSeconds, () => options().HopSignalWindowSeconds);
        provider.RegisterCore(RateLimitConfigKeys.UserAuthPermitLimit, () => options().UserAuthPermitLimit);
        provider.RegisterCore(RateLimitConfigKeys.UserAuthWindowSeconds, () => options().UserAuthWindowSeconds);
    }
}
