// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The address of a message a transaction sends into a channel: the address of the transaction
/// (<see cref="TransactionMessageAddress"/>) on the in-channel surface (SPEC-036 TPL-130): the request of
/// the phone number, the notice of the web confirmation, the link of the claims completion form and the
/// messages after a completed sign-in. The templates an adapter asks for through
/// <c>IChannelMessageTemplates</c> reach the same address directly, since they may name a link purpose too.
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
