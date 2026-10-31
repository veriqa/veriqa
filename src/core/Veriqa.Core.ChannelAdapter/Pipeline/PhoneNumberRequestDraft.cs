// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// A phone number request ready to be sent, and the record tying its chat back to the transaction.
/// </summary>
/// <param name="Message">Message to deliver into the chat of the user of the channel snapshot.</param>
/// <param name="Correlation">Record of the request, keyed by that chat.</param>
internal sealed record PhoneNumberRequestDraft(ChannelMessage Message, PhoneRequestCorrelation Correlation);
