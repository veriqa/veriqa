// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Store;

/// <summary>
/// Argument checks and conditions of the claims completion writes of <see cref="ITransactionStore"/>
/// (<see cref="ITransactionStore.TrySetWebStepSurfaceAsync"/>,
/// <see cref="ITransactionStore.TryRecordClaimResultAsync"/>,
/// <see cref="ITransactionStore.TrySetClaimFormTokenHashAsync"/>), shared by the shipped stores so each of
/// them refuses the same inputs and accepts the same writes.
/// </summary>
internal static class ClaimCompletionWrites
{
    /// <summary>
    /// Refuses a surface outside <see cref="WebStepSurface"/>.
    /// </summary>
    /// <param name="expected">Value the surface must hold now; null — none.</param>
    /// <param name="surface">Value to write.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either value is not a defined one.</exception>
    internal static void ThrowIfUndefined(WebStepSurface? expected, WebStepSurface surface)
    {
        if (expected is { } current && !Enum.IsDefined(current))
        {
            throw new ArgumentOutOfRangeException(nameof(expected), expected, "Unknown web step surface.");
        }

        if (!Enum.IsDefined(surface))
        {
            throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown web step surface.");
        }
    }

    /// <summary>
    /// Whether the pair is one of the two moves the surface makes (SPEC-041 C90, L53): "none → any"
    /// when the web step begins, and "cross-device page → initiating page" when the link to the other
    /// device could not be delivered. There is no way back.
    /// </summary>
    /// <param name="expected">Value the surface must hold now; null — none.</param>
    /// <param name="surface">Value to write.</param>
    /// <returns>true when the pair is a move of the surface.</returns>
    internal static bool IsSurfaceMove(WebStepSurface? expected, WebStepSurface surface)
        => expected is null
           || (expected is WebStepSurface.CrossDevicePage && surface is WebStepSurface.InitiatingPage);

    /// <summary>
    /// Refuses a form token fingerprint that states nothing.
    /// </summary>
    /// <param name="tokenHash">Fingerprint to write.</param>
    /// <exception cref="ArgumentException"><paramref name="tokenHash"/> is null, empty or whitespace.</exception>
    internal static void ThrowIfInvalidFormTokenHash(string tokenHash)
        => ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

    /// <summary>
    /// Refuses an outcome that is neither a collected value nor a skip.
    /// </summary>
    /// <param name="result">Outcome to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The claim is empty; a value comes without a method or a method without a value; a skip is marked
    /// verified.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The method is not a defined value.</exception>
    internal static void ThrowIfInvalid(ClaimCompletionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrWhiteSpace(result.Claim))
        {
            throw new ArgumentException("The outcome names no claim.", nameof(result));
        }

        if ((result.Value is null) != (result.Method is null))
        {
            throw new ArgumentException(
                "A collected value carries both a value and a method; a skip carries neither.",
                nameof(result));
        }

        if (result.Value is null && result.Verified)
        {
            throw new ArgumentException("A skipped claim cannot be verified.", nameof(result));
        }

        if (result.Method is { } method && !Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(result), method, "Unknown claim collection method.");
        }
    }

    /// <summary>
    /// The condition of <see cref="ITransactionStore.TryRecordClaimResultAsync"/> over the values a
    /// store read: the transaction is Pending, carries a completion mark listing the claim as missing,
    /// and holds no outcome for it yet.
    /// </summary>
    /// <param name="state">Stored state of the transaction.</param>
    /// <param name="items">Stored list of missing claims; null — no completion mark.</param>
    /// <param name="results">Stored outcomes.</param>
    /// <param name="claim">Claim the outcome is for.</param>
    /// <returns>true when the outcome may be recorded.</returns>
    internal static bool CanRecord(
        TransactionState state,
        IReadOnlyList<ClaimCompletionItem>? items,
        IReadOnlyList<ClaimCompletionResult> results,
        string claim)
        => state is TransactionState.Pending
           && items is not null
           && items.Any(item => VeriqaClaimTypes.NameComparer.Equals(item.Claim, claim))
           && !results.Any(recorded => VeriqaClaimTypes.NameComparer.Equals(recorded.Claim, claim));

    /// <summary>
    /// The outcomes with one more appended, as a new list — the stored one is shared and never mutated.
    /// </summary>
    /// <param name="results">Stored outcomes.</param>
    /// <param name="result">Outcome to append.</param>
    /// <returns>New read-only list of outcomes.</returns>
    internal static IReadOnlyList<ClaimCompletionResult> Append(
        IReadOnlyList<ClaimCompletionResult> results,
        ClaimCompletionResult result)
    {
        ClaimCompletionResult[] appended = [.. results, result];

        return Array.AsReadOnly(appended);
    }
}
