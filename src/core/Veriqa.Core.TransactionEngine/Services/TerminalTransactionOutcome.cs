// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// The single reading of "which terminal outcome a transaction ended in", as the outside reads it.
/// <para>
/// It lives in the engine, next to the states it reads, for the same reason
/// <see cref="DecisionRefusalClassifier"/> does: the surfaces that have to answer about an outcome —
/// the authenticated result endpoint of the auth server and the channel pipeline — are in different
/// assemblies. One table of the mapping means the same state cannot be reported as two different
/// outcomes depending on who is asked.
/// </para>
/// </summary>
public static class TerminalTransactionOutcome
{
    /// <summary>
    /// Reads the terminal outcome of a transaction.
    /// </summary>
    /// <remarks>
    /// The vocabulary is the product's existing outcome vocabulary — a second spelling of the same
    /// four facts would be a second vocabulary. A terminal failure that is NOT the user's refusal is
    /// reported as such rather than folded into a refusal or an expiry: callers act on this value,
    /// and telling them "the person said no" about a resolution failure would be telling them
    /// something untrue.
    /// <para>
    /// The one exception is a sign-in closed because its channel subject began a newer one
    /// (<see cref="TransactionErrorCodes.SupersededByNewSignIn"/>): to the outside it reads as an
    /// expiry. Nothing went wrong with it and nobody refused it — it stopped waiting for an answer,
    /// which is what an expiry is, and the surfaces that answer about an outcome already treat an
    /// expired sign-in the way a superseded one has to be treated. The state, the reason code and
    /// the event of the transaction are not read from this table and stay a failure with that code.
    /// </para>
    /// <para>
    /// "Confirmed" here means the transaction reached <see cref="TransactionState.Completed"/>. A
    /// transaction sitting in <see cref="TransactionState.Confirmed"/> has answered but has not
    /// finished — <see cref="Transaction.IsTerminal"/> does not count it either — so this reading
    /// gives <see langword="null"/> for it, and a caller that owes an answer supplies its own
    /// "not in yet" value.
    /// </para>
    /// </remarks>
    /// <param name="transaction">Transaction being read.</param>
    /// <returns>The outcome, or null when the transaction has not ended yet.</returns>
    public static TransactionOutcome? Of(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.State switch
        {
            TransactionState.Completed => TransactionOutcome.Confirmed,
            TransactionState.Expired => TransactionOutcome.Expired,
            TransactionState.Failed => transaction.StateReasonCode switch
            {
                TransactionErrorCodes.DeclinedByUser => TransactionOutcome.Declined,
                TransactionErrorCodes.SupersededByNewSignIn => TransactionOutcome.Expired,
                _ => TransactionOutcome.Failed
            },
            _ => null
        };
    }
}
