// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Platform;

/// <summary>
/// Body of <c>POST /conformance/scenario</c>.
/// </summary>
internal sealed record PlatformScenarioRequest
{
    /// <summary>
    /// <c>true</c> — the platform refuses to deliver a prompt until the next reset.
    /// </summary>
    public required bool PlatformCannotConfirm { get; init; }
}

/// <summary>
/// Body of <c>POST /conformance/follow-link</c>.
/// </summary>
internal sealed record FollowLinkRequest
{
    /// <summary>
    /// The deep link the user opens on the platform.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// The button the user presses on the prompt: <c>confirm</c> or <c>decline</c>.
    /// </summary>
    public required string Answer { get; init; }
}

/// <summary>
/// Body of <c>POST /conformance/send-text</c>.
/// </summary>
internal sealed record SendTextRequest
{
    /// <summary>
    /// The deep link that names the bot the text is sent to; the stub does not open it, so a transaction
    /// it may carry is not used.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// The plain text message the user sends.
    /// </summary>
    public required string Text { get; init; }
}

/// <summary>
/// Answer of <c>GET /conformance/platform-log</c>: what the adapter did on the platform since the
/// last reset, in order.
/// </summary>
internal sealed record PlatformLog
{
    /// <summary>
    /// The actions.
    /// </summary>
    public required IReadOnlyList<PlatformAction> Actions { get; init; }
}

/// <summary>
/// One action of the adapter on the platform.
/// </summary>
internal sealed record PlatformAction
{
    /// <summary>
    /// Kind of an action that sent a message.
    /// </summary>
    public const string SentKind = "sent";

    /// <summary>
    /// Kind of an action that edited a message already sent.
    /// </summary>
    public const string EditedKind = "edited";

    /// <summary>
    /// <see cref="SentKind"/> or <see cref="EditedKind"/>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Identifier of the credential the request of the adapter came with — never the credential.
    /// </summary>
    public required string Credential { get; init; }

    /// <summary>
    /// Chat of the message.
    /// </summary>
    public required string ChatId { get; init; }

    /// <summary>
    /// The message.
    /// </summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// Whether the action sent the message with these coordinates.
    /// </summary>
    /// <param name="chatId">Chat of the message.</param>
    /// <param name="messageId">The message.</param>
    /// <returns><c>true</c> — it did.</returns>
    public bool Sent(string chatId, string messageId) =>
        string.Equals(Kind, SentKind, StringComparison.Ordinal)
        && string.Equals(ChatId, chatId, StringComparison.Ordinal)
        && string.Equals(MessageId, messageId, StringComparison.Ordinal);
}

/// <summary>
/// Serialization of the service channel of the stand: snake_case names; a member missing or <c>null</c>
/// where the contract names it is an error of the stub.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PlatformScenarioRequest))]
[JsonSerializable(typeof(FollowLinkRequest))]
[JsonSerializable(typeof(SendTextRequest))]
[JsonSerializable(typeof(PlatformLog))]
internal sealed partial class PlatformStubJsonContext : JsonSerializerContext
{
}
