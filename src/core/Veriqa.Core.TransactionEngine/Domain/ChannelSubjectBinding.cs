// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// The channel subject a transaction is bound to: the first sender whose action the transaction
/// accepted (SPEC-007 UI-018, UI-022; SPEC-039 N34). An action of any other subject is refused and
/// changes nothing; an action of this one is accepted again.
/// </summary>
/// <remarks>
/// A subject is the pair (<see cref="ChannelType"/>, <see cref="ChannelUserId"/>), compared ordinally —
/// the same pair a channel snapshot names. The binding is written by an ordinary
/// <c>ITransactionStore.UpdateAsync</c> with the concurrency token, so of two senders racing for an
/// unbound transaction exactly one is bound.
/// </remarks>
public sealed record ChannelSubjectBinding
{
    /// <summary>
    /// Channel type of the bound subject.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Identifier of the bound subject within its channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// true — the answer on the core confirmation page is accepted only from the browser that carries
    /// the answer mark issued when the channel identity was attached; false — from any browser.
    /// </summary>
    public bool AnswerBrowserBound { get; init; }

    /// <summary>
    /// Binding of the subject a channel snapshot names.
    /// </summary>
    /// <param name="identity">Channel snapshot naming the subject.</param>
    /// <param name="answerBrowserBound">Whether the answer is bound to the browser of the attachment.</param>
    /// <returns>The binding.</returns>
    internal static ChannelSubjectBinding Of(ChannelIdentitySnapshot identity, bool answerBrowserBound) => new()
    {
        ChannelType = identity.ChannelType,
        ChannelUserId = identity.ChannelUserId,
        AnswerBrowserBound = answerBrowserBound
    };

    /// <summary>
    /// Checks whether a channel snapshot names this subject.
    /// </summary>
    /// <param name="identity">Channel snapshot of a sender.</param>
    /// <returns>true when the channel type and the user identifier both match ordinally.</returns>
    internal bool IsSameSubject(ChannelIdentitySnapshot identity)
        => string.Equals(ChannelType, identity.ChannelType, StringComparison.Ordinal)
            && string.Equals(ChannelUserId, identity.ChannelUserId, StringComparison.Ordinal);
}
