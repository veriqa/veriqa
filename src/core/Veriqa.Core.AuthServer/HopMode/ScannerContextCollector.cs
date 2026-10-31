// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using Veriqa.Core.AuthServer.InitiatorContext;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Collector of the context of the device that scanned a hop link (SPEC-025 §8.1): the same reading of
/// the request the initiator context uses (<see cref="RequestContextCapture"/>, HOP-084), under the
/// switches of the hop mode, into a <see cref="ScannerContextSnapshot"/>. Every field but the required
/// ones is best-effort (HOP-081): the collection never fails the presentation.
/// </summary>
internal sealed class ScannerContextCollector
{
    /// <summary>
    /// Size limit of a snapshot, in bytes of its UTF-8 JSON (SPEC-025 HOP-115) — the common 128 KB limit
    /// the transaction service applies to the snapshots it accepts. The store does not check the scanner
    /// snapshot, so the collector keeps the limit.
    /// </summary>
    private const int MaxSnapshotSize = 128 * 1024;

    /// <summary>
    /// The shared reading of the observable request attributes.
    /// </summary>
    private readonly RequestContextCapture _requestContextCapture;

    /// <summary>
    /// Clock the capture moment is taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ScannerContextCollector> _logger;

    /// <summary>
    /// Creates the collector.
    /// </summary>
    /// <param name="requestContextCapture">The shared reading of the observable request attributes.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ScannerContextCollector(
        RequestContextCapture requestContextCapture,
        TimeProvider timeProvider,
        ILogger<ScannerContextCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(requestContextCapture);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _requestContextCapture = requestContextCapture;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Collects the context of the scanning device from the request of the first human resolve.
    /// </summary>
    /// <param name="httpContext">HTTP context of the presentation.</param>
    /// <param name="settings">What the hop mode captures, for this act.</param>
    /// <param name="scannedAt">Moment of the first-resolve mark the snapshot projects (HOP-071a).</param>
    /// <returns>
    /// The snapshot, without a fingerprint; null — the scanner context is not collected
    /// (<see cref="HopObservationSettings.CollectScannerContext"/> is off), the collection failed, or the
    /// snapshot exceeds the size limit.
    /// </returns>
    public ScannerContextSnapshot? Collect(HttpContext httpContext, HopObservationSettings settings, DateTimeOffset scannedAt)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.CollectScannerContext)
        {
            return null;
        }

        try
        {
            var observation = _requestContextCapture.Capture(
                httpContext,
                new RequestContextSwitches(
                    settings.CollectIpAddress,
                    settings.CollectUserAgent,
                    settings.CollectGeoLocation,
                    settings.StoreRawUserAgent));

            // The city is not taken: the scanner snapshot keeps the country alone (SPEC-025 §8.1). The
            // fingerprint is filled later, by a client signal, and never here (HOP-080).
            var snapshot = new ScannerContextSnapshot
            {
                ScannedAt = scannedAt,
                IpAddress = observation.IpAddress,
                IpTrusted = observation.IpTrusted,
                Browser = observation.UserAgent?.Browser,
                OsPlatform = observation.UserAgent?.OsPlatform,
                DeviceType = observation.UserAgent?.DeviceType,
                Fingerprint = null,
                GeoCountry = observation.GeoLocation?.Country,
                RawMetadata = observation.RawMetadata,
                CapturedAt = _timeProvider.GetUtcNow(),
                CollectorVersion = HopModeConstants.ScannerContextCollectorVersion
            };

            ReportMissingFields(settings, snapshot);

            // Browser and OsPlatform are not truncated by the normalizer, so the size is not bounded by
            // the construction of the snapshot; it is measured on the JSON, as the snapshot is stored.
            var size = JsonSerializer.SerializeToUtf8Bytes(snapshot).Length;
            if (size > MaxSnapshotSize)
            {
                _logger.LogWarning(
                    "The scanner context snapshot exceeds the size limit and is not attached. Size: {Size}, Limit: {Limit}",
                    size,
                    MaxSnapshotSize);

                return null;
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            // The message of a normalizer or GeoIP failure may quote the address or the User-Agent it
            // failed on, and neither goes above Debug (SPEC-025 HOP-111): the Warning names the type of
            // the failure alone, the exception itself is kept for the Debug diagnostics.
            _logger.LogWarning(
                "Failed to collect the scanner context — the presentation continues without it. ExceptionType: {ExceptionType}",
                ex.GetType().FullName);
            _logger.LogDebug(ex, "Failure of the scanner context collection");

            return null;
        }
    }

    /// <summary>
    /// Reports, at Debug, the fields a switched-on collection gave no value for (SPEC-025 HOP-081): the
    /// names of the fields, never their values.
    /// </summary>
    /// <param name="settings">What the hop mode captures, for this act.</param>
    /// <param name="snapshot">The collected snapshot.</param>
    private void ReportMissingFields(HopObservationSettings settings, ScannerContextSnapshot snapshot)
    {
        var missing = new List<string>();

        if (settings.CollectIpAddress && snapshot.IpAddress is null)
        {
            missing.Add(nameof(ScannerContextSnapshot.IpAddress));
        }

        if (settings.CollectUserAgent)
        {
            if (snapshot.Browser is null)
            {
                missing.Add(nameof(ScannerContextSnapshot.Browser));
            }

            if (snapshot.OsPlatform is null)
            {
                missing.Add(nameof(ScannerContextSnapshot.OsPlatform));
            }

            if (snapshot.DeviceType is null)
            {
                missing.Add(nameof(ScannerContextSnapshot.DeviceType));
            }
        }

        if (settings.CollectGeoLocation && snapshot.GeoCountry is null)
        {
            missing.Add(nameof(ScannerContextSnapshot.GeoCountry));
        }

        if (missing.Count is 0)
        {
            return;
        }

        _logger.LogDebug(
            "Part of the scanner context is not available. ResultCode: {ResultCode}, MissingFields: {MissingFields}",
            HopModeResultCodes.ScannerContextPartial,
            missing);
    }
}
