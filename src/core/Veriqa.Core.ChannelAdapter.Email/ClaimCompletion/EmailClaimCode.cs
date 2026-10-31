// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// A code of a claims-completion mail as the store keeps it: never the code itself, only its hash.
/// </summary>
/// <param name="Hash">Hash of the code (<see cref="EmailClaimSecrets.HashOfCode"/>), hex.</param>
/// <param name="Address">Address the code was sent to — the one a match records (SPEC-041 L50).</param>
/// <param name="ExpiresAt">Moment the code stops being accepted (UTC).</param>
/// <param name="AttemptsLeft">Attempts left; zero — the code is spent.</param>
internal sealed record EmailClaimCode(string Hash, string Address, DateTimeOffset ExpiresAt, int AttemptsLeft)
    : IEmailStoredCode
{
    /// <summary>
    /// Whether the code is still accepted at a moment: it has attempts left and has not expired.
    /// </summary>
    /// <param name="now">Current moment.</param>
    /// <returns><c>true</c> when the code is live.</returns>
    public bool IsLive(DateTimeOffset now) => AttemptsLeft > 0 && now < ExpiresAt;
}
