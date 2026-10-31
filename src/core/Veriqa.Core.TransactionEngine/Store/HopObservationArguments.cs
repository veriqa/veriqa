// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Store;

/// <summary>
/// Argument checks of the three hop observation writes of <see cref="ITransactionStore"/>, shared by
/// the shipped stores so each of them refuses the same inputs the same way.
/// </summary>
internal static class HopObservationArguments
{
    /// <summary>
    /// Refuses a mark outside <see cref="HopObservationMark"/>.
    /// </summary>
    /// <param name="mark">Mark to check.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mark"/> is not a defined value.</exception>
    internal static void ThrowIfUndefined(HopObservationMark mark)
    {
        if (!Enum.IsDefined(mark))
        {
            throw new ArgumentOutOfRangeException(nameof(mark), mark, "Unknown hop observation mark.");
        }
    }

    /// <summary>
    /// Refuses a missing scanner snapshot and one that already carries a fingerprint: the fingerprint
    /// is written only by the later one-time fill (SPEC-025 HOP-080).
    /// </summary>
    /// <param name="snapshot">Snapshot to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="snapshot"/> carries a fingerprint.</exception>
    internal static void ThrowIfInvalid(ScannerContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Fingerprint is not null)
        {
            throw new ArgumentException(
                "The scanner context is attached without a fingerprint; the fingerprint is filled by a separate one-time write.",
                nameof(snapshot));
        }
    }

    /// <summary>
    /// Refuses an empty fingerprint.
    /// </summary>
    /// <param name="fingerprint">Fingerprint to check.</param>
    /// <exception cref="ArgumentException"><paramref name="fingerprint"/> is null, empty or whitespace.</exception>
    internal static void ThrowIfInvalidFingerprint(string fingerprint)
        => ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
}
