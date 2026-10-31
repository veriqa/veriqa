// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace Veriqa.Sample.PseudoChannels.Adapter;

/// <summary>
/// Body of the relay webhook: what the host, playing the platform, tells the core about a sign-in
/// at the provider. The host serializes this very type, the adapter deserializes it.
/// </summary>
public sealed record PseudoChannelWebhookPayload
{
    /// <summary>
    /// What happened: one of <see cref="PseudoChannelConstants.Intents"/>.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.Intent)]
    public string? Intent { get; init; }

    /// <summary>
    /// Veriqa transaction the event belongs to.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.TransactionId)]
    public string? TransactionId { get; init; }

    /// <summary>
    /// Subject identifier the provider issued.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.Subject)]
    public string? Subject { get; init; }

    /// <summary>
    /// Email address.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.Email)]
    public string? Email { get; init; }

    /// <summary>
    /// Whether the provider verified the email address; null — the provider made no statement.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.EmailVerified)]
    public bool? EmailVerified { get; init; }

    /// <summary>
    /// Full name in a displayable form.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.Name)]
    public string? Name { get; init; }

    /// <summary>
    /// Given name.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.GivenName)]
    public string? GivenName { get; init; }

    /// <summary>
    /// Family name.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.FamilyName)]
    public string? FamilyName { get; init; }

    /// <summary>
    /// URL of the profile picture.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.Picture)]
    public string? Picture { get; init; }

    /// <summary>
    /// Locale as a BCP 47 language tag.
    /// </summary>
    [JsonPropertyName(PseudoChannelConstants.Fields.Locale)]
    public string? Locale { get; init; }
}
