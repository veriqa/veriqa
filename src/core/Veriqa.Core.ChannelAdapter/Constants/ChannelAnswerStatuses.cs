// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Intermediate channel statuses the channel layer itself publishes for any channel, whatever
/// adapter serves it. They travel in <c>TransactionChannelStatusChangedEvent</c> and do not change
/// the lifecycle state of the transaction.
/// </summary>
internal static class ChannelAnswerStatuses
{
    /// <summary>
    /// The sender entered the transaction and the answer is now awaited in the channel: the bot
    /// reacted to the start, so the page has nothing left to offer as a way around a silent chat.
    /// </summary>
    public const string ChannelAnswered = "channel_answered";
}
