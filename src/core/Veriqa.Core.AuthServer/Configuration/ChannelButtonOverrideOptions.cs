// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.UiConfig;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Settings of the channel buttons of the sign-in page as the <c>ui_config</c> record states them
/// (SPEC-012 §9, §10.6) — the member the <c>ui_config</c> level of
/// <see cref="Resolution.AuthServerConfigKeys.ChannelButtonShowChannelButton"/> is read at. Every field
/// is optional: an unstated one leaves the level unset, and the value comes from the level below.
/// <para>
/// The type exists for the record alone, for the reason <see cref="AuthPageCancelOverrideOptions"/>
/// does: the global section, the tenant record and the OIDC client entry state the same setting by
/// path, while a self-hosted catalog of records is bound by the names of its properties.
/// </para>
/// </summary>
public sealed class ChannelButtonOverrideOptions
{
    /// <summary>
    /// On which devices the sign-in page shows the channel button; null — the record does not state
    /// it. An unknown enum value is read as null (the record evolution discipline), so a value this
    /// version does not know degrades to the level below instead of failing the page.
    /// </summary>
    [JsonPropertyName("show_channel_button")]
    [JsonConverter(typeof(TolerantNullableEnumConverter<ChannelButtonVisibility>))]
    public ChannelButtonVisibility? ShowChannelButton { get; set; }
}
