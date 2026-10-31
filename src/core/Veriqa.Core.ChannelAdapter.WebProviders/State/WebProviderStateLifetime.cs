// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.State;

/// <summary>
/// How long the state a web provider channel keeps for a transaction lives — the session of its page,
/// the question, the outcome: until the end of the transaction plus the window the engine keeps a
/// completed transaction readable in. The adapter has no lifetime setting of its own; both terms are
/// the engine's.
/// </summary>
internal static class WebProviderStateLifetime
{
    /// <summary>
    /// Moment the state of a transaction ends.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="engineOptions">Configuration of the transaction engine.</param>
    /// <returns>The end of the transaction plus the retention of a completed one.</returns>
    public static DateTimeOffset EndOf(Transaction transaction, TransactionEngineOptions engineOptions)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(engineOptions);

        return transaction.ExpiresAt + TimeSpan.FromSeconds(engineOptions.CompletedRetentionSeconds);
    }
}
