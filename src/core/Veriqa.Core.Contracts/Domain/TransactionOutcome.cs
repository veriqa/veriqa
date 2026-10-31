// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// What the core tells the user about a transaction (SPEC-003 §6.2): one of its terminal outcomes
/// or the non-terminal state of a claims completion (<see cref="AwaitingClaims"/>).
/// An extensible value rather than a closed enum: a new outcome does not break already compiled
/// adapters, which must show the status text generically instead of failing on an unknown value.
/// </summary>
/// <param name="Value">Outcome code (see <see cref="TransactionOutcomeCodes"/>).</param>
public readonly record struct TransactionOutcome(string Value)
{
    /// <summary>
    /// Outcome code. Never empty for a value built through the constructor;
    /// <c>default(TransactionOutcome)</c> carries <see langword="null"/> and denotes an unknown outcome.
    /// </summary>
    public string Value { get; } = Validate(Value);

    /// <summary>
    /// The user confirmed and the transaction completed.
    /// </summary>
    /// <remarks>
    /// Expression-bodied on purpose: a static auto-property without an initializer would be
    /// <c>default</c>, and all the outcomes would compare equal to each other.
    /// </remarks>
    public static TransactionOutcome Confirmed => new(TransactionOutcomeCodes.Confirmed);

    /// <summary>
    /// The user declined and the decline was recorded.
    /// </summary>
    /// <remarks>Expression-bodied for the same reason as <see cref="Confirmed"/>.</remarks>
    public static TransactionOutcome Declined => new(TransactionOutcomeCodes.Declined);

    /// <summary>
    /// The transaction expired by TTL.
    /// </summary>
    /// <remarks>Expression-bodied for the same reason as <see cref="Confirmed"/>.</remarks>
    public static TransactionOutcome Expired => new(TransactionOutcomeCodes.Expired);

    /// <summary>
    /// The transaction ended in an error shown to the user.
    /// </summary>
    /// <remarks>Expression-bodied for the same reason as <see cref="Confirmed"/>.</remarks>
    public static TransactionOutcome Failed => new(TransactionOutcomeCodes.Failed);

    /// <summary>
    /// The user confirmed, and the transaction is not over: it waits for the claims the sign-in
    /// still lacks. The only value that is not terminal — see
    /// <see cref="TransactionOutcomeNotice.IsTerminal"/>.
    /// </summary>
    /// <remarks>Expression-bodied for the same reason as <see cref="Confirmed"/>.</remarks>
    public static TransactionOutcome AwaitingClaims => new(TransactionOutcomeCodes.AwaitingClaims);

    /// <summary>
    /// Rejects an empty code so that a constructed outcome can never collide with <c>default</c>.
    /// </summary>
    /// <param name="value">Outcome code.</param>
    /// <returns>The validated code.</returns>
    private static string Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
