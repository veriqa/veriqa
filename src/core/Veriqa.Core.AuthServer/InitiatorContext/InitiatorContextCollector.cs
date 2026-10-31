// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.InitiatorContext;

/// <summary>
/// Initiator context collector (SPEC-017 §5): IP accounting for ForwardedHeaders (ICC-020),
/// normalized User-Agent (ICC-012), offline geolocation (ICC-013), client
/// application name from the OIDC clients configuration (ClientId → DisplayName).
/// All fields except the required ones are best-effort (ICC-011): unavailability does not block
/// transaction creation.
/// </summary>
internal sealed class InitiatorContextCollector : IInitiatorContextCollector
{
    /// <summary>
    /// Context collector version (semver, snapshot CollectorVersion field).
    /// </summary>
    private const string CollectorVersion = "1.0.0";

    /// <summary>
    /// Initiator context check settings.
    /// </summary>
    private readonly IOptions<InitiatorContextOptions> _options;

    /// <summary>
    /// Guarded entry point to the OIDC clients snapshot (source of DisplayName).
    /// </summary>
    private readonly OidcClientsOptionsAccessor _clients;

    /// <summary>
    /// The shared reading of the observable request attributes (IP, User-Agent, GeoIP, RawMetadata).
    /// </summary>
    private readonly RequestContextCapture _requestContextCapture;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<InitiatorContextCollector> _logger;

    /// <summary>
    /// Clock the snapshot capture moment is taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the initiator context collector.
    /// </summary>
    /// <param name="options">Initiator context check settings.</param>
    /// <param name="clients">Guarded entry point to the OIDC clients snapshot.</param>
    /// <param name="requestContextCapture">The shared reading of the observable request attributes.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Time provider.</param>
    public InitiatorContextCollector(
        IOptions<InitiatorContextOptions> options,
        OidcClientsOptionsAccessor clients,
        RequestContextCapture requestContextCapture,
        ILogger<InitiatorContextCollector> logger,
        TimeProvider timeProvider)
    {
        _options = options;
        _clients = clients;
        _requestContextCapture = requestContextCapture;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public InitiatorContextSnapshot? Collect(HttpContext httpContext, string? clientId)
    {
        // The method builds the context snapshot: required fields + best-effort IP/UA/geo.
        // Any collection error does not block transaction creation (ICC-011).

        var options = _options.Value;

        // Collection is fully disabled — the snapshot is not built (ICC-033)
        if (!options.Enabled)
        {
            return null;
        }

        try
        {
            // The client application name is required (ICC-010): ClientId → DisplayName,
            // fallback — the ClientId itself; without a client identifier the context is not built
            var applicationName = ResolveClientApplicationName(clientId);
            if (applicationName is null)
            {
                _logger.LogDebug("Initiator context not built: ClientId is missing");
                return null;
            }

            var now = _timeProvider.GetUtcNow();

            var observation = _requestContextCapture.Capture(
                httpContext,
                new RequestContextSwitches(
                    options.CollectIpAddress,
                    options.CollectUserAgent,
                    options.CollectGeoLocation,
                    options.StoreRawUserAgent));

            ReportCaptureConditions(observation);

            return new InitiatorContextSnapshot
            {
                ClientApplicationName = applicationName,
                InitiatedAt = now,
                IpAddress = observation.IpAddress,
                IpTrusted = observation.IpTrusted,
                Browser = observation.UserAgent?.Browser,
                OsPlatform = observation.UserAgent?.OsPlatform,
                DeviceType = observation.UserAgent?.DeviceType,
                GeoCountry = observation.GeoLocation?.Country,
                GeoCity = observation.GeoLocation?.City,
                RawMetadata = observation.RawMetadata,
                CapturedAt = now,
                CollectorVersion = CollectorVersion
            };
        }
        catch (Exception ex)
        {
            // Context collection never causes a transaction creation error (ICC-011)
            _logger.LogWarning(ex, "Failed to collect the initiator context — the transaction is created without context");
            return null;
        }
    }

    /// <summary>
    /// Resolves the client application name: DisplayName from the client configuration,
    /// fallback — the ClientId itself (SPEC-017 §5.2, field No. 2).
    /// </summary>
    /// <param name="clientId">OIDC client identifier.</param>
    /// <returns>Application name, or null if the ClientId is missing.</returns>
    private string? ResolveClientApplicationName(string? clientId)
    {
        // Through the guarded accessor, not IOptions<OidcClientsOptions>: IOptions binds once, at its first
        // read (the client seeder makes it at host start), and never re-binds, so a client renamed or
        // added after start would keep answering with the
        // stale DisplayName here while the rest of the request reads the live section. The accessor
        // serves the live value, and a broken section degrades this best-effort field to the ClientId
        // (ICC-010) instead of reaching the catch that Collect wraps this call in.
        return _clients.Current.ApplicationNameOf(clientId);
    }

    /// <summary>
    /// Reports the conditions of the capture under the codes of the initiator context: an IP marked
    /// untrusted (ICC-021) and a raw User-Agent truncated to the limit (ICC-031). Neither value itself is
    /// logged (ICC-060).
    /// </summary>
    /// <param name="observation">What the capture read from the request.</param>
    private void ReportCaptureConditions(RequestContextObservation observation)
    {
        if (observation.IsIpUntrusted)
        {
            _logger.LogInformation(
                "Initiator IP obtained with an open ForwardedHeaders configuration and marked as untrusted. Code: {Code}",
                InitiatorContextResultCodes.IpUntrusted);
        }

        if (observation.RawUserAgentTruncated)
        {
            _logger.LogWarning(
                "Raw User-Agent exceeded the limit and was truncated. Code: {Code}, Limit: {Limit}",
                InitiatorContextResultCodes.ContextTooLarge,
                RequestContextCapture.MaxRawUserAgentLength);
        }
    }
}
