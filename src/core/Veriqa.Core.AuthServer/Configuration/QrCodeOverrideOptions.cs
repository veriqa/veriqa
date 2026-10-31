// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.UiConfig;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// QR settings of the sign-in page as an owning level ABOVE the core states them (SPEC-012 §9,
/// §10.6): the same three fields as the global section <see cref="QrCodeOptions"/>, under the same
/// names and with the same meaning — but every one of them optional. An unstated field leaves its
/// key's level unset, and the value comes from the level below, so an application that states the
/// visibility alone keeps the scale of the level under it.
/// <para>
/// One type serves both records that can carry the section: the OIDC client entry, bound from the
/// application configuration by property name, and the <c>ui_config</c> record, deserialized from
/// JSONB by the snake_case names below.
/// </para>
/// </summary>
public sealed class QrCodeOverrideOptions
{
    /// <summary>
    /// Whether the sign-in page shows the QR code at all; null — the level does not state it.
    /// An unknown enum value is read as null (the record evolution discipline), so a value this
    /// version does not know degrades to the level below instead of failing the page.
    /// </summary>
    [JsonPropertyName("show_qr_code")]
    [JsonConverter(typeof(TolerantNullableEnumConverter<QrCodeVisibility>))]
    public QrCodeVisibility? ShowQrCode { get; set; }

    /// <summary>
    /// QR pixel scale (px per module); null — the level does not state it. The allowed range is the
    /// same as on the core level and is checked ON EVERY level (see
    /// <see cref="IsPixelsPerModuleInRange"/>): a value outside it leaves its step of the resolution
    /// chain unset and does so SILENTLY — the page keeps a QR of the scale below rather than none at
    /// all, and the operator is told about the value once per configuration snapshot instead, by the
    /// report that walks the whole catalog of scales (SPEC-012 CFG-240).
    /// </summary>
    [JsonPropertyName("pixels_per_module")]
    public int? PixelsPerModule { get; set; }

    /// <summary>
    /// Per-channel overrides of <see cref="PixelsPerModule"/>, keyed by channel type; null — the
    /// level does not state them. Every value is bound by the same range as the default, and the map
    /// is read entry by entry: a value outside the range costs only the channel it was stated for —
    /// that channel takes the next step of the chain, while the entries next to it keep working.
    /// Every rejected entry is named by the same snapshot report, one line per entry.
    /// </summary>
    [JsonPropertyName("pixels_per_module_by_channel")]
    [JsonConverter(typeof(TolerantMapConverter<IDictionary<string, int>, int>))]
    public IDictionary<string, int>? PixelsPerModuleByChannel { get; set; }

    /// <summary>
    /// Tells whether a pixel scale is inside the range the setting allows on any level
    /// ([<see cref="QrCodeOptions.MinPixelsPerModule"/>; <see cref="QrCodeOptions.MaxPixelsPerModule"/>]).
    /// It is the predicate of the DOMAIN declared by the scale key, and the resolution mechanism is
    /// what applies it: no level, reader or report calls it on its own.
    /// </summary>
    /// <param name="pixelsPerModule">Pixel scale to check.</param>
    /// <returns><c>true</c> when the value is inside the allowed range.</returns>
    public static bool IsPixelsPerModuleInRange(int pixelsPerModule) =>
        pixelsPerModule is >= QrCodeOptions.MinPixelsPerModule and <= QrCodeOptions.MaxPixelsPerModule;
}
