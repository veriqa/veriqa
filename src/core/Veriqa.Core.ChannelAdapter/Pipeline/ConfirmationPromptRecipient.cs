// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The one a confirmation question is asked of: the channel it is asked in and the account in that
/// channel (<see cref="IConfirmationPromptContextFactory"/>). The channel names the channel in the text of
/// the question; the snapshot is what the masked identity slots of the text are filled from.
/// </summary>
/// <param name="ChannelType">Type of the channel the question is asked in; an empty string — the channel
/// is not known.</param>
/// <param name="ChannelUserId">Identifier of the user within the channel; an empty string — not known. The
/// anomaly heuristic keys its history on it.</param>
/// <param name="Identity">Channel snapshot of the account asked; null — none is known.</param>
public sealed record ConfirmationPromptRecipient(
    string ChannelType,
    string ChannelUserId,
    ChannelIdentitySnapshot? Identity);
