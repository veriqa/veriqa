// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Context of the device that scanned a hop link (SPEC-025 §8.1): observable request attributes
/// captured on the first human resolve of the link.
/// </summary>
/// <remarks>
/// A separate point of observation from <see cref="InitiatorContextSnapshot"/> — the scanning phone
/// rather than the initiating desktop — and therefore a separate field of the transaction, never
/// merged into the initiator one (HOP-093). Field names and their JSON names follow the initiator
/// snapshot, so the two snapshots of one transaction speak one vocabulary.
/// <para>
/// Captured once and immutable afterwards, with one exception: <see cref="Fingerprint"/> is filled
/// later, once, by the first writer (HOP-080). A store therefore takes the snapshot without it and
/// fills it through <c>ITransactionStore.TryFillScannerFingerprintAsync</c>.
/// </para>
/// </remarks>
public sealed class ScannerContextSnapshot
{
    /// <summary>
    /// Moment of the scan (UTC, required) — the projection of the first-resolve mark of the
    /// transaction, not a second source of it (HOP-071a).
    /// </summary>
    [JsonPropertyName("scanned_at")]
    public required DateTimeOffset ScannedAt { get; init; }

    /// <summary>
    /// Source IP of the scanning device (best-effort, respecting ForwardedHeaders). Sensitive.
    /// </summary>
    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; init; }

    /// <summary>
    /// Trustworthiness of the IP delivery infrastructure — the same semantics as
    /// <see cref="InitiatorContextSnapshot.IpTrusted"/>: it reflects the deployment configuration and
    /// does NOT mean "IP verified".
    /// </summary>
    [JsonPropertyName("ip_trusted")]
    public bool IpTrusted { get; init; }

    /// <summary>
    /// Normalized browser name (best-effort).
    /// </summary>
    [JsonPropertyName("browser")]
    public string? Browser { get; init; }

    /// <summary>
    /// Normalized OS/platform (best-effort).
    /// </summary>
    [JsonPropertyName("os_platform")]
    public string? OsPlatform { get; init; }

    /// <summary>
    /// Device type (best-effort). Allowed values — the constants of
    /// <see cref="InitiatorContextFields.DeviceTypes"/>, the same vocabulary the initiator uses.
    /// </summary>
    [JsonPropertyName("device_type")]
    public string? DeviceType { get; init; }

    /// <summary>
    /// Hash of the scanning browser computed by the interstitial page and delivered by a client
    /// signal. Sensitive. The only field filled after the snapshot is captured — once, by the first
    /// writer (HOP-080); null until then or when fingerprint collection is off.
    /// </summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    /// <summary>
    /// Country by GeoIP (approximate, best-effort).
    /// </summary>
    [JsonPropertyName("geo_country")]
    public string? GeoCountry { get; init; }

    /// <summary>
    /// Raw collection metadata in one field, shaped as for the initiator. Sensitive.
    /// </summary>
    [JsonPropertyName("raw_metadata")]
    public JsonElement? RawMetadata { get; init; }

    /// <summary>
    /// Snapshot capture time (UTC, required).
    /// </summary>
    [JsonPropertyName("captured_at")]
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Context collector version (semver, required).
    /// </summary>
    [JsonPropertyName("collector_version")]
    public required string CollectorVersion { get; init; }

    /// <summary>
    /// Copies the snapshot with <see cref="Fingerprint"/> replaced — the one field a store keeps
    /// apart from the rest of the snapshot, because it is written later and conditionally.
    /// </summary>
    /// <remarks>
    /// Every member is listed by hand: a member added to this class is added here in the same change,
    /// otherwise a store rehydrating the snapshot quietly loses it.
    /// </remarks>
    /// <param name="fingerprint">Fingerprint the copy carries; null — none.</param>
    /// <returns>Copy of the snapshot carrying <paramref name="fingerprint"/>.</returns>
    internal ScannerContextSnapshot WithFingerprint(string? fingerprint) => new()
    {
        ScannedAt = ScannedAt,
        IpAddress = IpAddress,
        IpTrusted = IpTrusted,
        Browser = Browser,
        OsPlatform = OsPlatform,
        DeviceType = DeviceType,
        Fingerprint = fingerprint,
        GeoCountry = GeoCountry,
        RawMetadata = RawMetadata,
        CapturedAt = CapturedAt,
        CollectorVersion = CollectorVersion
    };
}
