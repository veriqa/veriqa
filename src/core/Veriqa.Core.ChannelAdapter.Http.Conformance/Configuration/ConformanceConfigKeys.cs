// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;

/// <summary>
/// Names of the configuration keys the suite reads. Its own keys live under <c>Conformance</c>; the
/// channel under test is read from the keys the host reads (<c>Veriqa:Channels:{channel}</c>), so the
/// suite checks exactly the configuration an operator gives Veriqa.
/// </summary>
internal static class ConformanceConfigKeys
{
    /// <summary>
    /// Section of the keys of the suite.
    /// </summary>
    public const string SectionName = "Conformance";

    /// <summary>
    /// The registration <c>Veriqa:Channels:{type}</c> under test. Required.
    /// </summary>
    public const string ChannelType = "ChannelType";

    /// <summary>
    /// <c>calls</c> / <c>interactive</c> / <c>automatic</c>; default <c>calls</c>.
    /// </summary>
    public const string Mode = "Mode";

    /// <summary>
    /// The user of the channel the calls outside events are addressed to. Required.
    /// </summary>
    public const string ChannelUserId = "ChannelUserId";

    /// <summary>
    /// List of the tenants of the run; <see cref="ImplicitTenant"/> — the implicit tenant.
    /// </summary>
    public const string Tenants = "Tenants";

    /// <summary>
    /// Path of the JSON report; unset — no report.
    /// </summary>
    public const string ReportPath = "ReportPath";

    /// <summary>
    /// How long the receiver waits for an event, in whole seconds.
    /// </summary>
    public const string EventTimeoutSeconds = "EventTimeoutSeconds";

    /// <summary>
    /// Map tenant → identifier of the platform credential of that tenant at the stand.
    /// </summary>
    public const string TenantCredentials = "TenantCredentials";

    /// <summary>
    /// Base address of the platform stub of the stand. Required in the automatic mode.
    /// </summary>
    public const string PlatformStubUrl = "PlatformStubUrl";

    /// <summary>
    /// Whether the platform of the stand has a case in which it cannot carry a confirmation out;
    /// default <c>false</c>. Read in the automatic mode.
    /// </summary>
    public const string PlatformCannotConfirm = "PlatformCannotConfirm";

    /// <summary>
    /// Spelling of the implicit tenant (<c>null</c>) in <see cref="Tenants"/> and in the keys of
    /// <see cref="TenantCredentials"/>.
    /// </summary>
    public const string ImplicitTenant = "-";

    /// <summary>
    /// Subsection of a channel section that makes it an HTTP channel.
    /// </summary>
    public const string ChannelHttpSectionName = "Http";

    /// <summary>
    /// Member of the <c>Http</c> subsection holding the base URL of the external adapter.
    /// </summary>
    public const string BaseUrlMember = "BaseUrl";

    /// <summary>
    /// Member of the <c>Http</c> subsection holding the current transport secret.
    /// </summary>
    public const string TransportSecretMember = "TransportSecret";

    /// <summary>
    /// Member of the <c>Http</c> subsection holding the previous transport secret.
    /// </summary>
    public const string PreviousTransportSecretMember = "PreviousTransportSecret";

    /// <summary>
    /// Full path of a key of the suite.
    /// </summary>
    /// <param name="member">Name of the key under <see cref="SectionName"/>.</param>
    /// <returns>The path, for example <c>Conformance:ChannelType</c>.</returns>
    public static string PathOf(string member) => ConfigurationPath.Combine(SectionName, member);
}
