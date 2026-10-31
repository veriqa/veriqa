// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Sample.CustomChannel.Adapter;

/// <summary>
/// String and numeric constants of the sample third-party channel.
/// </summary>
public static class AcmeChatConstants
{
    /// <summary>
    /// Channel type. Must satisfy the SPI contract <c>\A[a-z][a-z0-9-]{0,63}\z</c> — otherwise the
    /// server fails to start with an explicit error.
    /// </summary>
    public const string ChannelType = "acme-chat";

    /// <summary>
    /// Channel name shown on the sign-in button (a brand name — not localized).
    /// </summary>
    public const string DisplayName = "Acme Chat";

    /// <summary>
    /// Adapter version in semver format (recorded in the identity snapshot).
    /// </summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>
    /// Header carrying the shared secret of the webhook.
    /// </summary>
    public const string SignatureHeader = "X-Acme-Signature";

    /// <summary>
    /// Query parameter of the transaction in the deep link.
    /// </summary>
    public const string DeepLinkTransactionParameter = "tx";

    /// <summary>
    /// The message a user sends to the bot to start a sign-in by code. The wording is the channel's
    /// own; the core substitutes the code for the placeholder and shows the result on its pages.
    /// </summary>
    public const string StartMessageTemplate = "/veriqa " + ChannelCodeStart.CodePlaceholder;

    /// <summary>
    /// What the bot answers to a code the core found no sign-in by. The wording is the channel's
    /// own, and it is one sentence for every reason — mistyped, expired, already used — so the answer
    /// does not tell whether such a code ever existed.
    /// </summary>
    public const string UnmatchedCodeReplyText =
        "This code did not work. Check the code on the sign-in page and send it again, or open the page again.";

    /// <summary>
    /// Inner markup of the channel glyph (viewBox 0 0 24 24): a neutral "A" lettermark,
    /// static markup owned by the adapter's author.
    /// </summary>
    public const string IconSvgPath =
        """<path d="M12 2 3 22h4l1.8-4h6.4l1.8 4h4L12 2zm-1.7 12L12 9.6 13.7 14h-3.4z"/>""";

    /// <summary>
    /// Health-check response time reported by the sample channel.
    /// </summary>
    public static readonly TimeSpan HealthResponseTime = TimeSpan.Zero;
}
