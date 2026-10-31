// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Configuration;

/// <summary>
/// WhatsApp tenant-credential settings group (SPEC-003 §17.3, CFG-202): the tenant level
/// (in self-hosted ≡ core). Includes the expected provider code, the credentials of each shipped
/// provider (Meta Cloud API, Twilio) and the identity (business number). WhatsApp has no core-transport
/// group (the channel is webhook-only, no <c>UpdateMode</c>/<c>WebhookBaseUrl</c>) — a permitted §17.3
/// edge case: the core-transport group is empty, the classification is still normative.
/// </summary>
/// <param name="Provider">Code of the expected delivery provider (the tenant's delivery identity).</param>
/// <param name="BusinessPhoneNumber">WhatsApp business number (identity, E.164).</param>
/// <param name="MetaCloudApi">Meta Cloud API credentials (relevant with the <c>MetaCloudApi</c> provider).</param>
/// <param name="Twilio">
/// Twilio credentials (relevant with the <c>Twilio</c> provider). Null when the group carries no Twilio part —
/// a group stored before the Twilio provider was shipped deserializes without it; the provider treats that as
/// no credentials.
/// </param>
public sealed record WhatsAppTenantCredentials(
    string Provider,
    string BusinessPhoneNumber,
    MetaCloudApiOptions MetaCloudApi,
    TwilioOptions? Twilio);
