// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace Veriqa.Sample.CustomChannel.Adapter;

/// <summary>
/// Webhook body of the sample platform. The shape belongs to the adapter's author — the core hands
/// over the raw request body and never interprets its format.
/// </summary>
/// <param name="TransactionId">Veriqa transaction the user started from the deep link.</param>
/// <param name="Code">Code the user sent to the bot, already without the command it was wrapped in.
/// Read only when the channel declares a start by code.</param>
/// <param name="UserId">User identifier on the platform.</param>
/// <param name="DisplayName">User display name on the platform.</param>
public sealed record AcmeChatWebhookPayload(
    [property: JsonPropertyName("transaction_id")] string? TransactionId,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("display_name")] string? DisplayName);
