// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Sample.PseudoChannels.Adapter;

/// <summary>
/// What the adapter learned from the core about a transaction, readable by the host. The core
/// talks to a channel through its adapter only; the phone pages live in the host, so this is how
/// the question and its outcome get from one to the other. One instance per process, shared by
/// every pseudo-channel.
/// <para>
/// Both reads are addressed by the transaction AND the user in the channel: the core asks one user
/// and reports to one user, and a second account that signed in at the provider for the same
/// transaction — one whose start the core did not accept — is answered as if nothing were known.
/// </para>
/// </summary>
public interface IPseudoChannelExchange
{
    /// <summary>
    /// Returns the question context the core handed to the adapter for the transaction, when the
    /// question was addressed to this user.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelUserId">User in the channel who asks for it — the provider's subject.</param>
    /// <param name="promptContext">The context, when the core asked this user in this channel.</param>
    /// <returns><see langword="false"/> — the core has not asked this user (yet), or the state expired.</returns>
    bool TryGetPrompt(
        TransactionId transactionId,
        string channelUserId,
        [NotNullWhen(true)] out ConfirmationPromptContext? promptContext);

    /// <summary>
    /// Returns the outcome the core reported to the adapter for the transaction, when the notice
    /// was addressed to this user.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelUserId">User in the channel who asks for it — the provider's subject.</param>
    /// <param name="outcome">The latest notice reported to this user, when there is one.</param>
    /// <returns><see langword="false"/> — the core has reported nothing to this user (yet), or the state expired.</returns>
    bool TryGetOutcome(
        TransactionId transactionId,
        string channelUserId,
        [NotNullWhen(true)] out TransactionOutcomeNotice? outcome);
}
