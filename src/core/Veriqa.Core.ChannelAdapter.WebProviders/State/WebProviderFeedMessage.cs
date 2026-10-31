// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.State;

/// <summary>
/// One message of the core in the feed of an account — what a chat of a messenger would show: the
/// text, and under it the link to the claim form or the buttons, whichever the message carries. At
/// most one of the two is present; neither — a plain text.
/// </summary>
/// <param name="Text">Text of the message.</param>
/// <param name="ClaimFormLink">The link to the claim form; <c>null</c> — not a message of that kind.</param>
/// <param name="ActionButtons">The buttons, links and items of the menu alike; <c>null</c> — not a
/// message of that kind.</param>
/// <param name="SentAt">Moment the core sent the message.</param>
internal sealed record WebProviderFeedMessage(
    string Text,
    ClaimFormLinkContent? ClaimFormLink,
    ActionButtonsContent? ActionButtons,
    DateTimeOffset SentAt);
