// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Settings of the cancel button of the sign-in window and of the entry page of a confirmation as the
/// <c>ui_config</c> record states
/// them (SPEC-012 §9, §10.6) — the member the <c>ui_config</c> level of
/// <see cref="Resolution.AuthServerConfigKeys.CancelEnabled"/> is read at. Every field is optional: an
/// unstated one leaves the level unset, and the value comes from the level below.
/// <para>
/// The type exists for the record alone. The global section, the tenant record and the OIDC client
/// entry state the same setting by path and are bound into no object for it; the record is a typed
/// serialization contract, and a self-hosted catalog of records is bound by the names of its
/// properties, so a member the record does not carry would never reach the level.
/// </para>
/// </summary>
public sealed class AuthPageCancelOverrideOptions
{
    /// <summary>
    /// Whether the cancel button is offered; null — the record does not state it.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
