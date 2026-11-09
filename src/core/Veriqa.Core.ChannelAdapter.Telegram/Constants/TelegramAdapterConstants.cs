// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Telegram.Constants;

/// <summary>
/// Telegram adapter constants (SPEC-003 §21).
/// Contains the string literals of the adapter itself. The wordings of a message it shows the user
/// are not among them: those belong to the message and are declared with it
/// (<see cref="Veriqa.Core.TransactionEngine.MessageTemplates.MessageTemplateNaturalKeys"/>).
/// </summary>
internal static class TelegramAdapterConstants
{
    /// <summary>
    /// Adapter version in semver format.
    /// </summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>
    /// Telegram bot /start command.
    /// </summary>
    public const string BotCommandStart = "/start";

    /// <summary>
    /// Deep link prefix for authentication (matches <see cref="CallbackDataPrefixes.Auth"/>).
    /// </summary>
    public const string DeepLinkPrefix = CallbackDataPrefixes.Auth;

    /// <summary>
    /// The at-sign a bot username is shown to the user with; it is not part of the username itself
    /// and has no place in a deep link.
    /// </summary>
    public const char BotUsernamePrefix = '@';

    /// <summary>
    /// HTTP header of the Telegram Bot API webhook secret token.
    /// </summary>
    public const string WebhookSecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    /// <summary>
    /// Confirm button text.
    /// </summary>
    public const string ConfirmButtonText = "Confirm ✅";

    /// <summary>
    /// Decline button text.
    /// </summary>
    public const string DeclineButtonText = "Decline ❌";

    /// <summary>
    /// Largest payload of a callback button, in bytes of its UTF-8 form. Named by the platform: Bot API,
    /// <c>InlineKeyboardButton.callback_data</c> — "1-64 bytes" (the wording the XML documentation of
    /// <c>InlineKeyboardButton.CallbackData</c> in the referenced <c>Telegram.Bot</c> package repeats).
    /// </summary>
    public const int CallbackDataMaxBytes = 64;

    /// <summary>
    /// Bytes of the field name of a shared contact in a Telegram update (<c>message.contact</c>, Bot API
    /// <c>Message</c> object) — what the ownership answer looks for to claim a contact event, which carries
    /// no Veriqa marker. A substring match, so a user's own text with this word in quotes also matches: the
    /// ownership contract allows such a false positive.
    /// </summary>
    public static ReadOnlySpan<byte> ContactFieldMarker => "\"contact\""u8;

    /// <summary>
    /// Maximum raw metadata size in bytes (CA-005). Alias of the contract-wide
    /// <see cref="ChannelAdapterLimits.MaxRawMetadataSize"/> — the limit is identical for every channel,
    /// so this class does not carry its own literal.
    /// </summary>
    public const int MaxRawMetadataSize = ChannelAdapterLimits.MaxRawMetadataSize;

    /// <summary>
    /// Maximum number of API call retry attempts.
    /// </summary>
    public const int MaxRetryAttempts = 3;

    /// <summary>
    /// Base delay between retried API calls (in milliseconds).
    /// </summary>
    public const int RetryBaseDelayMs = 1000;

    /// <summary>
    /// Timeout of the getUpdates long-poll request (in seconds, TASK-051 — extracted magic constant).
    /// </summary>
    public const int LongPollTimeoutSeconds = 30;

    /// <summary>
    /// Named <see cref="System.Net.Http.IHttpClientFactory"/> client the Telegram Bot client sends its
    /// requests through.
    /// </summary>
    public const string HttpClientName = "telegram-bot-api";

    /// <summary>
    /// Timeout of one Bot API request, in seconds — an explicit bound instead of the default 100 s of
    /// <see cref="System.Net.Http.HttpClient"/>, so a stuck request fails instead of holding the bot.
    /// </summary>
    /// <remarks>
    /// Invariant: the value is greater than <see cref="LongPollTimeoutSeconds"/>. The getUpdates request of
    /// <c>TelegramPollingService</c> goes through the same client and is held open by Telegram for up to
    /// the long-poll timeout when there is nothing to deliver; a smaller value would cut every empty
    /// long-poll short.
    /// </remarks>
    public const int BotApiTimeoutSeconds = 45;

    /// <summary>
    /// Time budget of reading the avatar of a user, in seconds: the profile photos, the file and its
    /// download together. When it runs out the avatar is left out.
    /// </summary>
    public const int AvatarBudgetSeconds = 4;

    /// <summary>
    /// Webhook path relative to the base URL.
    /// </summary>
    public const string WebhookPath = "/api/channels/telegram/webhook";

    /// <summary>
    /// Separator of the parts packed into the opaque inbound token: the callback query id and the
    /// coordinates of the message the button belongs to. Neither part can contain it — Telegram
    /// identifiers are numeric strings.
    /// </summary>
    /// <remarks>
    /// The layout is the adapter's own business: the core carries the token back untouched and never
    /// parses it. The coordinates are packed here on purpose — the callback query id exists only
    /// inside the inbound event, and the message coordinates must survive a process restart or a
    /// different instance, where an in-process prompt store would have nothing to offer (CA-142).
    /// </remarks>
    public const string InboundTokenSeparator = "|";

    /// <summary>
    /// Number of parts in a well-formed inbound token. Only the first one (the callback query id) is
    /// always filled; the two coordinate slots are empty when the press carried no source message.
    /// </summary>
    public const int InboundTokenPartCount = 3;
}
