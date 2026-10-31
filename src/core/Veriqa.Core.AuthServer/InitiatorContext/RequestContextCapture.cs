// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text.Json;

namespace Veriqa.Core.AuthServer.InitiatorContext;

/// <summary>
/// The one reading of the observable attributes of an HTTP request — IP with its trust flag, normalized
/// User-Agent, offline geolocation and the raw collection metadata — shared by every point of
/// observation: the initiator of a transaction (SPEC-017 §5) and the device that scanned a hop link
/// (SPEC-025 HOP-084). Each point states which attributes it collects; what it builds from them, and
/// what it logs about them, stays with the point.
/// </summary>
/// <remarks>
/// The unit writes no log of its own: the conditions a point reports (an untrusted IP, a truncated raw
/// User-Agent) are returned to it, so each point reports them under its own codes. An exception of the
/// normalizer or of the GeoIP provider is not caught here — the collection is best-effort at the level
/// of the point, which decides what a failure means for it.
/// </remarks>
internal sealed class RequestContextCapture
{
    /// <summary>
    /// Length limit of the raw User-Agent in RawMetadata (ICC-031 recommendation — 2 KB).
    /// </summary>
    internal const int MaxRawUserAgentLength = 2048;

    /// <summary>
    /// Name of the RawMetadata field with the truncated raw User-Agent.
    /// </summary>
    private const string RawUserAgentMetadataField = "raw_user_agent";

    /// <summary>
    /// Name of the RawMetadata field with the untrusted IP source flag (ICC-021).
    /// </summary>
    private const string IpUntrustedMetadataField = "ip_untrusted_forwarding";

    /// <summary>
    /// ForwardedHeaders settings — for assessing IP trustworthiness (ICC-021).
    /// </summary>
    private readonly IOptions<ForwardedHeadersOptions> _forwardedHeadersOptions;

    /// <summary>
    /// User-Agent normalizer.
    /// </summary>
    private readonly IUserAgentNormalizer _userAgentNormalizer;

    /// <summary>
    /// Offline GeoIP provider.
    /// </summary>
    private readonly IGeoIpProvider _geoIpProvider;

    /// <summary>
    /// Creates the request context capture.
    /// </summary>
    /// <param name="forwardedHeadersOptions">ForwardedHeaders settings.</param>
    /// <param name="userAgentNormalizer">User-Agent normalizer.</param>
    /// <param name="geoIpProvider">Offline GeoIP provider.</param>
    public RequestContextCapture(
        IOptions<ForwardedHeadersOptions> forwardedHeadersOptions,
        IUserAgentNormalizer userAgentNormalizer,
        IGeoIpProvider geoIpProvider)
    {
        _forwardedHeadersOptions = forwardedHeadersOptions;
        _userAgentNormalizer = userAgentNormalizer;
        _geoIpProvider = geoIpProvider;
    }

    /// <summary>
    /// Reads the attributes of the request the switches ask for. A switched-off attribute is neither
    /// read nor kept (data minimization, SPEC-017 ICC-033, SPEC-025 HOP-083).
    /// </summary>
    /// <param name="httpContext">HTTP context of the observed request.</param>
    /// <param name="switches">Which attributes to collect.</param>
    /// <returns>What was read; a field the request did not give is null.</returns>
    public RequestContextObservation Capture(HttpContext httpContext, RequestContextSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var (remoteIp, ipTrusted) = CollectIpAddress(httpContext, switches);
        var ipAddress = remoteIp?.ToString();
        var userAgent = CollectUserAgent(httpContext, switches, out var rawUserAgent);
        var geoLocation = CollectGeoLocation(switches, remoteIp, ipTrusted);
        var rawMetadata = BuildRawMetadata(switches, rawUserAgent, ipAddress, ipTrusted, out var rawUserAgentTruncated);

        return new RequestContextObservation(
            ipAddress,
            ipTrusted,
            userAgent,
            geoLocation,
            rawMetadata,
            rawUserAgentTruncated);
    }

    /// <summary>
    /// Collects the IP (best-effort): RemoteIpAddress already accounts for ForwardedHeadersMiddleware
    /// (ICC-020, HOP-095). The trust flag reflects the presence of configured KnownProxies /
    /// KnownIPNetworks (ICC-021) — that is, the deployment configuration, not the fact that this
    /// particular request passed through a known proxy (per-request proxy-route validation is not
    /// performed — the standard ForwardedHeadersMiddleware is reused, without parallel header processing).
    /// </summary>
    /// <param name="httpContext">HTTP context.</param>
    /// <param name="switches">Collection switches.</param>
    /// <returns>IP address (or null) and the trust flag.</returns>
    private (IPAddress? RemoteIp, bool IpTrusted) CollectIpAddress(HttpContext httpContext, RequestContextSwitches switches)
    {
        if (!switches.CollectIpAddress)
        {
            return (null, false);
        }

        var remoteIp = httpContext.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            // A reliable IP cannot be determined — the fields remain null (ICC-022)
            return (null, false);
        }

        // With an open ForwardedHeaders configuration (KnownProxies/KnownIPNetworks cleared)
        // the IP from X-Forwarded-For is not considered reliable (ICC-021). A configuration-level flag:
        // it does not mean this particular request actually passed through a known proxy
        // (see the method's XML doc and InitiatorContextSnapshot.IpTrusted)
        var forwardedHeaders = _forwardedHeadersOptions.Value;
        var ipTrusted = forwardedHeaders.KnownProxies.Count > 0 || forwardedHeaders.KnownIPNetworks.Count > 0;

        return (remoteIp, ipTrusted);
    }

    /// <summary>
    /// Collects and normalizes the User-Agent (best-effort, ICC-012).
    /// </summary>
    /// <param name="httpContext">HTTP context.</param>
    /// <param name="switches">Collection switches.</param>
    /// <param name="rawUserAgent">Raw User-Agent string (for RawMetadata when StoreRawUserAgent is enabled).</param>
    /// <returns>Normalized UA data, or null.</returns>
    private UserAgentInfo? CollectUserAgent(HttpContext httpContext, RequestContextSwitches switches, out string? rawUserAgent)
    {
        rawUserAgent = null;

        if (!switches.CollectUserAgent)
        {
            return null;
        }

        rawUserAgent = httpContext.Request.Headers.UserAgent.ToString();

        return _userAgentNormalizer.Normalize(rawUserAgent);
    }

    /// <summary>
    /// Determines the approximate geolocation from the IP (best-effort, ICC-013).
    /// Uses the same IPAddress written to the snapshot — geo and IP do not diverge.
    /// An untrusted IP is not looked up (SPEC-017 ICC-022): anyone can put an address into
    /// X-Forwarded-For under an open ForwardedHeaders configuration, so a region derived from it would
    /// show the user a place of the caller's choosing. The IP itself stays in the observation, marked
    /// untrusted (ICC-021). Both points of observation read the request through this unit, so the rule
    /// holds for the scanner of a hop link as well (SPEC-025 HOP-084).
    /// </summary>
    /// <param name="switches">Collection switches.</param>
    /// <param name="remoteIp">The collected IP.</param>
    /// <param name="ipTrusted">Trust flag of the collected IP.</param>
    /// <returns>Geolocation, or null.</returns>
    private GeoIpLocation? CollectGeoLocation(RequestContextSwitches switches, IPAddress? remoteIp, bool ipTrusted)
    {
        if (!switches.CollectGeoLocation || remoteIp is null || !ipTrusted || !_geoIpProvider.IsAvailable)
        {
            return null;
        }

        return _geoIpProvider.Lookup(remoteIp);
    }

    /// <summary>
    /// Builds RawMetadata: the truncated raw UA (if StoreRawUserAgent is enabled)
    /// and the untrusted IP source flag (ICC-021, ICC-031).
    /// </summary>
    /// <param name="switches">Collection switches.</param>
    /// <param name="rawUserAgent">Raw User-Agent string.</param>
    /// <param name="ipAddress">The collected IP.</param>
    /// <param name="ipTrusted">IP trust flag.</param>
    /// <param name="rawUserAgentTruncated">Whether the raw User-Agent exceeded the limit and was truncated.</param>
    /// <returns>JSON metadata element, or null.</returns>
    private static JsonElement? BuildRawMetadata(
        RequestContextSwitches switches,
        string? rawUserAgent,
        string? ipAddress,
        bool ipTrusted,
        out bool rawUserAgentTruncated)
    {
        rawUserAgentTruncated = false;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (switches.StoreRawUserAgent && !string.IsNullOrWhiteSpace(rawUserAgent))
        {
            if (rawUserAgent.Length > MaxRawUserAgentLength)
            {
                // The excess is truncated (ICC-031)
                rawUserAgentTruncated = true;
                rawUserAgent = rawUserAgent[..MaxRawUserAgentLength];
            }

            metadata[RawUserAgentMetadataField] = rawUserAgent;
        }

        if (ipAddress is not null && !ipTrusted)
        {
            metadata[IpUntrustedMetadataField] = true;
        }

        if (metadata.Count is 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(metadata);
    }
}

/// <summary>
/// Which attributes of a request a point of observation collects — the switches every point states
/// under the same names (SPEC-025 HOP-083).
/// </summary>
/// <param name="CollectIpAddress">Collect the IP address.</param>
/// <param name="CollectUserAgent">Collect and normalize the User-Agent.</param>
/// <param name="CollectGeoLocation">Look the IP up in the offline GeoIP database.</param>
/// <param name="StoreRawUserAgent">Keep the truncated raw User-Agent in RawMetadata.</param>
internal readonly record struct RequestContextSwitches(
    bool CollectIpAddress,
    bool CollectUserAgent,
    bool CollectGeoLocation,
    bool StoreRawUserAgent);

/// <summary>
/// What <see cref="RequestContextCapture"/> read from a request.
/// </summary>
/// <param name="IpAddress">IP address; null — not collected or not determinable. Sensitive.</param>
/// <param name="IpTrusted">Trustworthiness of the IP delivery infrastructure (ICC-021); false when no IP was collected.</param>
/// <param name="UserAgent">Normalized User-Agent; null — not collected or not recognized.</param>
/// <param name="GeoLocation">Geolocation by IP; null — not collected, not available, or the IP is untrusted (ICC-022).</param>
/// <param name="RawMetadata">Raw collection metadata; null — nothing to keep. Sensitive.</param>
/// <param name="RawUserAgentTruncated">The raw User-Agent exceeded <see cref="RequestContextCapture.MaxRawUserAgentLength"/> and was truncated.</param>
internal sealed record RequestContextObservation(
    string? IpAddress,
    bool IpTrusted,
    UserAgentInfo? UserAgent,
    GeoIpLocation? GeoLocation,
    JsonElement? RawMetadata,
    bool RawUserAgentTruncated)
{
    /// <summary>
    /// An IP was collected under an open ForwardedHeaders configuration and is marked untrusted (ICC-021).
    /// </summary>
    public bool IsIpUntrusted => IpAddress is not null && !IpTrusted;
}
