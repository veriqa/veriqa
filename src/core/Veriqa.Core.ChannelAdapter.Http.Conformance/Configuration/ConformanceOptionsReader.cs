// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Microsoft.Extensions.Configuration;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;

/// <summary>
/// Reads and checks the keys <c>Conformance:*</c>. A key the run cannot go on without is a
/// <see cref="ConformanceSetupException"/> naming the key. The keys of the channel under test
/// (<c>Veriqa:Channels:{channel}:*</c>) are not checked here: the start of the composition checks them
/// exactly as the host does.
/// </summary>
internal static class ConformanceOptionsReader
{
    /// <summary>
    /// Spelling of the mode <see cref="ConformanceModes.Calls"/>.
    /// </summary>
    public const string CallsModeName = "calls";

    /// <summary>
    /// Spelling of the mode <see cref="ConformanceModes.Interactive"/>.
    /// </summary>
    public const string InteractiveModeName = "interactive";

    /// <summary>
    /// Spelling of the mode <see cref="ConformanceModes.Automatic"/>.
    /// </summary>
    public const string AutomaticModeName = "automatic";

    /// <summary>
    /// Default wait for an event, in seconds.
    /// </summary>
    private const int DefaultEventTimeoutSeconds = 60;

    /// <summary>
    /// Default wait for an event in the interactive mode, in seconds: a person answers on the platform.
    /// </summary>
    private const int DefaultInteractiveEventTimeoutSeconds = 300;

    /// <summary>
    /// Separator of the segments of a URL path.
    /// </summary>
    private const char PathSeparator = '/';

    /// <summary>
    /// Separator of the paths of the configuration; a tenant containing it cannot name a key of
    /// <c>TenantCredentials</c>.
    /// </summary>
    private static readonly string KeyDelimiter = ConfigurationPath.KeyDelimiter;

    /// <summary>
    /// Reads the options of a run.
    /// </summary>
    /// <param name="configuration">Configuration of the suite.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ConformanceSetupException">A key is missing or invalid.</exception>
    public static ConformanceOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConformanceConfigKeys.SectionName);

        var channelType = Required(section, ConformanceConfigKeys.ChannelType);
        var mode = ReadMode(section);
        var channelUserId = Required(section, ConformanceConfigKeys.ChannelUserId);
        var tenants = ReadTenants(section);
        var eventTimeout = ReadEventTimeout(section, mode);
        var tenantCredentials = ReadTenantCredentials(section, tenants);
        var automatic = mode is ConformanceModes.Automatic;

        return new ConformanceOptions
        {
            ChannelType = channelType,
            Mode = mode,
            ChannelUserId = channelUserId,
            Tenants = tenants,
            ReportPath = Optional(section, ConformanceConfigKeys.ReportPath),
            EventTimeout = eventTimeout,
            TenantCredentials = tenantCredentials,
            PlatformStubUrl = automatic ? ReadPlatformStubUrl(section) : null,
            PlatformCannotConfirm = automatic && ReadPlatformCannotConfirm(section)
        };
    }

    /// <summary>
    /// The canonical spelling of a mode.
    /// </summary>
    /// <param name="mode">A single mode.</param>
    /// <returns>Its name.</returns>
    public static string NameOf(ConformanceModes mode) => mode switch
    {
        ConformanceModes.Calls => CallsModeName,
        ConformanceModes.Interactive => InteractiveModeName,
        ConformanceModes.Automatic => AutomaticModeName,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a single mode.")
    };

    /// <summary>
    /// The value of a required key.
    /// </summary>
    /// <param name="section">Section of the suite.</param>
    /// <param name="member">Name of the key.</param>
    /// <returns>The value.</returns>
    private static string Required(IConfigurationSection section, string member) =>
        Optional(section, member)
        ?? throw Invalid(member, "is not set.");

    /// <summary>
    /// The value of an optional key; empty — not set.
    /// </summary>
    /// <param name="section">Section of the suite.</param>
    /// <param name="member">Name of the key.</param>
    /// <returns>The value, or <c>null</c>.</returns>
    private static string? Optional(IConfigurationSection section, string member)
    {
        var value = section[member];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// The mode; unset — <see cref="ConformanceModes.Calls"/>.
    /// </summary>
    /// <param name="section">Section of the suite.</param>
    /// <returns>The mode.</returns>
    private static ConformanceModes ReadMode(IConfigurationSection section)
    {
        var value = Optional(section, ConformanceConfigKeys.Mode);

        return value?.ToLowerInvariant() switch
        {
            null or CallsModeName => ConformanceModes.Calls,
            InteractiveModeName => ConformanceModes.Interactive,
            AutomaticModeName => ConformanceModes.Automatic,
            _ => throw Invalid(
                ConformanceConfigKeys.Mode,
                "is not one of " + CallsModeName + ", " + InteractiveModeName + ", " + AutomaticModeName + ".")
        };
    }

    /// <summary>
    /// The tenants of the run; unset — the implicit tenant alone.
    /// </summary>
    /// <param name="section">Section of the suite.</param>
    /// <returns>The tenants, in the order of the configuration.</returns>
    private static List<string?> ReadTenants(IConfigurationSection section)
    {
        var list = section.GetSection(ConformanceConfigKeys.Tenants);
        if (!string.IsNullOrWhiteSpace(list.Value))
        {
            throw Invalid(ConformanceConfigKeys.Tenants, "is a single value, not a list: state it as Tenants:0, Tenants:1, ...");
        }

        var tenants = new List<string?>();
        foreach (var item in list.GetChildren())
        {
            var member = ConfigurationPath.Combine(ConformanceConfigKeys.Tenants, item.Key);
            var value = item.Value?.Trim();

            if (string.IsNullOrEmpty(value))
            {
                throw Invalid(member, "is empty.");
            }

            if (value.Contains(KeyDelimiter, StringComparison.Ordinal))
            {
                throw Invalid(member, "contains '" + KeyDelimiter + "', which cannot name a key of " + ConformanceConfigKeys.TenantCredentials + ".");
            }

            var tenant = value is ConformanceConfigKeys.ImplicitTenant ? null : value;
            if (tenants.Contains(tenant))
            {
                throw Invalid(member, "repeats a tenant listed before.");
            }

            tenants.Add(tenant);
        }

        if (tenants.Count is 0)
        {
            tenants.Add(null);
        }

        return tenants;
    }

    /// <summary>
    /// The wait for an event: a positive whole number of seconds.
    /// </summary>
    /// <param name="section">Section of the suite.</param>
    /// <param name="mode">Mode of the run.</param>
    /// <returns>The wait.</returns>
    private static TimeSpan ReadEventTimeout(IConfigurationSection section, ConformanceModes mode)
    {
        var value = Optional(section, ConformanceConfigKeys.EventTimeoutSeconds);
        if (value is null)
        {
            return TimeSpan.FromSeconds(mode is ConformanceModes.Interactive
                ? DefaultInteractiveEventTimeoutSeconds
                : DefaultEventTimeoutSeconds);
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            throw Invalid(ConformanceConfigKeys.EventTimeoutSeconds, "is not a positive whole number of seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The credential identifiers by tenant; every tenant named must be a tenant of the run.
    /// </summary>
    /// <param name="section">Section of the suite.</param>
    /// <param name="tenants">Tenants of the run.</param>
    /// <returns>The map.</returns>
    private static Dictionary<string, string> ReadTenantCredentials(IConfigurationSection section, List<string?> tenants)
    {
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in section.GetSection(ConformanceConfigKeys.TenantCredentials).GetChildren())
        {
            var member = ConfigurationPath.Combine(ConformanceConfigKeys.TenantCredentials, item.Key);
            var tenant = item.Key is ConformanceConfigKeys.ImplicitTenant ? null : item.Key;

            if (!tenants.Contains(tenant))
            {
                throw Invalid(member, "names a tenant that is not listed in " + ConformanceConfigKeys.PathOf(ConformanceConfigKeys.Tenants) + ".");
            }

            if (string.IsNullOrWhiteSpace(item.Value))
            {
                throw Invalid(member, "is empty.");
            }

            credentials[item.Key] = item.Value.Trim();
        }

        return credentials;
    }

    /// <summary>
    /// The base address of the platform stub: an absolute <c>http</c> or <c>https</c> URL without a query
    /// or a fragment, required in the automatic mode. It is normalized to end with <c>/</c>, so the paths
    /// of the stub resolve under it, a path prefix of the address included. A query or a fragment is
    /// refused rather than dropped: the paths of the stub cannot carry it, and dropping it silently would
    /// call an address the stand did not give.
    /// </summary>
    /// <param name="section">Section of the suite.</param>
    /// <returns>The address.</returns>
    private static Uri ReadPlatformStubUrl(IConfigurationSection section)
    {
        var value = Optional(section, ConformanceConfigKeys.PlatformStubUrl)
            ?? throw Invalid(ConformanceConfigKeys.PlatformStubUrl, "is not set; the " + AutomaticModeName + " mode drives the platform stub of the stand.");

        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw Invalid(ConformanceConfigKeys.PlatformStubUrl, "is not an absolute http or https URL.");
        }

        if (url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            throw Invalid(ConformanceConfigKeys.PlatformStubUrl, "has a query or a fragment; it is a base address the paths of the stub are added to.");
        }

        return url.AbsoluteUri.EndsWith(PathSeparator)
            ? url
            : new Uri(url.AbsoluteUri + PathSeparator, UriKind.Absolute);
    }

    /// <summary>
    /// Whether the platform of the stand has a case in which it cannot carry a confirmation out;
    /// unset — <c>false</c>.
    /// </summary>
    /// <param name="section">Section of the suite.</param>
    /// <returns>The flag.</returns>
    private static bool ReadPlatformCannotConfirm(IConfigurationSection section)
    {
        var value = Optional(section, ConformanceConfigKeys.PlatformCannotConfirm);
        if (value is null)
        {
            return false;
        }

        return bool.TryParse(value, out var flag)
            ? flag
            : throw Invalid(ConformanceConfigKeys.PlatformCannotConfirm, "is neither true nor false.");
    }

    /// <summary>
    /// The error of a key of the suite.
    /// </summary>
    /// <param name="member">Key under <c>Conformance</c>.</param>
    /// <param name="problem">What is wrong, as the end of a sentence.</param>
    /// <returns>The exception.</returns>
    private static ConformanceSetupException Invalid(string member, string problem) =>
        new("Invalid conformance configuration: '" + ConformanceConfigKeys.PathOf(member) + "' " + problem);
}
