// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;

/// <summary>
/// An inbound webhook request as read by a delivery provider: the messages it carries, in the provider's
/// neutral form, and the provider's own parsed payload.
/// </summary>
/// <param name="Messages">
/// The messages of the request in the order they arrived; empty when the body is unreadable, foreign, or
/// carries nothing the channel acts on (a delivery status, for instance).
/// </param>
/// <param name="Raw">
/// The provider's parsed payload, carried as is to <c>ChannelInboundResult.RawEvent</c>; null when the
/// body could not be parsed at all.
/// </param>
public sealed record WhatsAppInboundEvent(
    IReadOnlyList<WhatsAppInboundMessage> Messages,
    object? Raw);
