// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;

/// <summary>
/// How a tenant of the run is spelled in the messages of the suite and in the keys of
/// <c>Conformance:TenantCredentials</c>.
/// </summary>
internal static class TenantNames
{
    /// <summary>
    /// The tenant in a message of the suite.
    /// </summary>
    /// <param name="tenant">The tenant; <c>null</c> — the implicit one.</param>
    /// <returns>For example <c>tenant 'shop-a'</c>.</returns>
    public static string Describe(string? tenant) =>
        tenant is null ? "the implicit tenant" : "tenant '" + tenant + "'";

    /// <summary>
    /// The key of the tenant in <c>Conformance:TenantCredentials</c>.
    /// </summary>
    /// <param name="tenant">The tenant; <c>null</c> — the implicit one.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string? tenant) => tenant ?? ConformanceConfigKeys.ImplicitTenant;
}
