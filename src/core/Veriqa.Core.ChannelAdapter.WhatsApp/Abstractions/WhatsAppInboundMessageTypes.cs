// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;

/// <summary>
/// Neutral values of <see cref="WhatsAppInboundMessage.MessageType"/> for a provider whose wire format has
/// no type of its own to report. A provider that has one reports it as received.
/// </summary>
public static class WhatsAppInboundMessageTypes
{
    /// <summary>
    /// A text message.
    /// </summary>
    public const string Text = "text";

    /// <summary>
    /// A press of a reply button.
    /// </summary>
    public const string Button = "button";
}
