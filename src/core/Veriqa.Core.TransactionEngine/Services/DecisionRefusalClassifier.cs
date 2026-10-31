// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// What a refused write of the user's decision means for the answer the user is shown.
/// </summary>
public enum DecisionRefusalClass
{
    /// <summary>
    /// The transaction ran out of time (or was already reaped) while the question was on screen.
    /// The user is told so by the receipt of an expired outcome.
    /// </summary>
    Expired,

    /// <summary>
    /// A decision was recorded in parallel — a double submit, a "back" in the browser, the
    /// channel answering at the same moment. The class states that a decision already stands, and
    /// nothing about what the sender is told: which outcome that decision left, and whether this
    /// sender may be told it, is the business of the surface that answers.
    /// </summary>
    Race,

    /// <summary>
    /// Something of ours refused: the identity resolver, the store. Not the user's business, and
    /// not something the transaction may be left waiting after.
    /// </summary>
    Downstream
}

/// <summary>
/// The single reading of a refused decision write: which kind of answer the error code of the
/// refusal calls for.
/// <para>
/// It lives in the engine, next to the codes it reads, because the surfaces that have to answer a
/// refusal — the confirmation page and the channel pipeline — are in different assemblies. One
/// table of the rule means the same refusal cannot mean two different things depending on where
/// the decision came from.
/// </para>
/// </summary>
public static class DecisionRefusalClassifier
{
    /// <summary>
    /// Reads what a refused write of the decision means.
    /// </summary>
    /// <remarks>
    /// The five codes below belong to the transaction lifecycle and are reserved for it, so this
    /// stays a classification of lifecycle outcomes and not of arbitrary extension-point strings: a
    /// refusal coming from an identity resolver travels out with a code of its own and lands in the
    /// downstream class, which is where it belongs. So does an empty or unknown code.
    /// A transaction waiting for missing claims (SPEC-041 L91) is one whose user has already
    /// answered: the answer stands as the completion mark, so the refusal it gives a later decision is
    /// read as a race.
    /// </remarks>
    /// <param name="errorCode">Code the write failed with.</param>
    /// <returns>What the refusal means for the answer.</returns>
    public static DecisionRefusalClass Classify(string errorCode) => errorCode switch
    {
        TransactionErrorCodes.TransactionExpired or TransactionErrorCodes.TransactionNotFound
            => DecisionRefusalClass.Expired,
        TransactionErrorCodes.InvalidStateTransition
            or TransactionErrorCodes.ConcurrencyConflict
            or TransactionErrorCodes.ClaimCompletionInProgress
            => DecisionRefusalClass.Race,
        _ => DecisionRefusalClass.Downstream
    };
}
