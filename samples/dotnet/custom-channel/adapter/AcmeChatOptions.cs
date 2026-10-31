// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Sample.CustomChannel.Adapter;

/// <summary>
/// Settings of the sample third-party channel. The options belong to the adapter's author:
/// the Veriqa core knows nothing about this section and does not bind it — the host does.
/// </summary>
public sealed class AcmeChatOptions
{
    /// <summary>
    /// Configuration section of the sample channel.
    /// </summary>
    public const string SectionName = "AcmeChat";

    /// <summary>
    /// Shared secret the platform sends in the webhook signature header.
    /// A webhook without the exact value is rejected with 403 by the pipeline.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the deep link opened from the sign-in window.
    /// </summary>
    public string DeepLinkBaseUrl { get; set; } = "https://chat.acme.example/start";

    /// <summary>
    /// Plays a platform whose link cannot carry a transaction: the channel declares a start by code,
    /// the deep link only opens the chat, and the user starts the sign-in by sending the code shown on
    /// the sign-in page. Wins over <see cref="CodeFallback"/> when both are on.
    /// </summary>
    public bool StartByCode { get; set; }

    /// <summary>
    /// Plays a platform whose link carries the transaction but does not always deliver it: the deep
    /// link stays as it is, and the code shown on the sign-in page is a second way to start.
    /// </summary>
    public bool CodeFallback { get; set; }
}
