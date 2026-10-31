// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// The marks of the Email letters on the budget of the confirmation operation: a letter is identified by
/// the token of its link (Pull) or by the message-id of its mail (Push), and each mark is kept as a
/// processed entry of <see cref="IEmailPushCorrelationStore"/> — its atomic "first one wins" and its
/// retention window, the in-process and the Redis store alike.
/// </summary>
/// <remarks>
/// The entries share the store with the message-ids of the inbound dedup, so they are kept apart by a
/// prefix: an entry of a mark begins with it, and an inbound mail whose message-id begins with it is not
/// handled (<see cref="IsReservedKey"/>) — otherwise a mail could set a mark of another letter before that
/// letter arrives. Marking the budget of a mail therefore never makes its redelivery look processed: a mail
/// asked to be handed in again still reaches the gate. The letter's key is kept as a SHA-256 fingerprint
/// bound to the transaction — the token of a link is never at rest in clear text.
/// </remarks>
internal sealed class EmailLetterBudgetChargeMark : ILetterBudgetChargeMark
{
    /// <summary>
    /// Prefix that keeps the marks apart from the message-ids of the inbound dedup in the same store.
    /// </summary>
    private const string KeyPrefix = "letter-budget:";

    /// <summary>
    /// Kind of the mark claimed by the pass that takes the budget.
    /// </summary>
    private const string ClaimKind = "claim";

    /// <summary>
    /// Kind of the mark set once the budget has let the letter through.
    /// </summary>
    private const string AllowedKind = "allowed";

    /// <summary>
    /// Separator between the parts of the key.
    /// </summary>
    private const char KeySeparator = ':';

    /// <summary>
    /// Store the marks are kept in.
    /// </summary>
    private readonly IEmailPushCorrelationStore _store;

    /// <summary>
    /// Creates the marks over the store of the Email channel.
    /// </summary>
    /// <param name="store">Correlation store of the Email channel.</param>
    public EmailLetterBudgetChargeMark(IEmailPushCorrelationStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Whether a value coming from outside — the message-id of an inbound mail — falls into the keys of the
    /// marks. Compared ignoring case: a store of the integrator may compare its keys so.
    /// </summary>
    /// <param name="candidate">The value to check.</param>
    /// <returns>true — the value is shaped as a key of a mark and must not reach the store.</returns>
    internal static bool IsReservedKey(string candidate) =>
        candidate.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<bool> TryClaimChargeAsync(
        TransactionId transactionId,
        string letterKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(letterKey);

        return _store.TryRegisterProcessedMessageAsync(KeyOf(ClaimKind, transactionId, letterKey), cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkAllowedAsync(
        TransactionId transactionId,
        string letterKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(letterKey);

        // A pass that could not claim or read the mark records it too, so "already there" can happen and is no failure.
        _ = await _store.TryRegisterProcessedMessageAsync(
            KeyOf(AllowedKind, transactionId, letterKey), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> IsAllowedAsync(
        TransactionId transactionId,
        string letterKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(letterKey);

        return _store.IsMessageProcessedAsync(KeyOf(AllowedKind, transactionId, letterKey), cancellationToken);
    }

    /// <summary>
    /// The key of a mark: the prefix, the kind of the mark, the transaction and the fingerprint of the letter.
    /// </summary>
    /// <param name="kind">Kind of the mark.</param>
    /// <param name="transactionId">Transaction the letter answers.</param>
    /// <param name="letterKey">What identifies the letter.</param>
    /// <returns>The key.</returns>
    private static string KeyOf(string kind, TransactionId transactionId, string letterKey) =>
        string.Concat(
            KeyPrefix,
            kind,
            KeySeparator.ToString(),
            transactionId.ToString(),
            KeySeparator.ToString(),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(letterKey))));
}
