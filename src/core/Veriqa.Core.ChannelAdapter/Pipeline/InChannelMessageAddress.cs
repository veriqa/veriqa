// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The address of a message a transaction sends into a channel: the address of the transaction
/// (<see cref="TransactionMessageAddress"/>) on the in-channel surface (SPEC-036 TPL-130). In today's
/// delivery it addresses the request of the phone number, the notice of the web confirmation and the link
/// of the claims completion form. The confirmation prompt of the adapters and the welcome after a sign-in
/// are addressed today by the kind, the surface and the channel, on their own.
/// </summary>
internal static class InChannelMessageAddress
{
    /// <summary>
    /// Builds the point of the fallback chain.
    /// </summary>
    /// <param name="messageKind">Kind of the message.</param>
    /// <param name="transaction">Transaction the message is about.</param>
    /// <param name="channelType">Channel the message goes through.</param>
    /// <returns>The point of the fallback chain.</returns>
    internal static ConfigDimensionValues Of(string messageKind, Transaction transaction, string channelType) =>
        TransactionMessageAddress.Of(messageKind, MessageSurfaces.InChannel, transaction, channelType);
}
