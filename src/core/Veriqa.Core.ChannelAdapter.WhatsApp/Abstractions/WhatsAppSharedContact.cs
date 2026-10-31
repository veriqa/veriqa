// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;

/// <summary>
/// A contact card the user shared, in the provider-neutral form — the answer to the request for the
/// user's number of claim completion (SPEC-041 R9, L45, L46).
/// </summary>
/// <param name="PhoneNumber">Number of the card as received; null when the card carries none.</param>
/// <param name="SharedByRequest">
/// <see langword="true"/> — the card was shared by the platform's request contact button, so it is the
/// number of the sender's own account; <see langword="false"/> — a card sent by hand, not proven to be
/// the sender's.
/// </param>
/// <param name="Origin">The provider's own marker of how the card was shared, as received; null when absent.</param>
public sealed record WhatsAppSharedContact(
    string? PhoneNumber,
    bool SharedByRequest,
    string? Origin);
