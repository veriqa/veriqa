// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// A code of a mail as the store keeps it — the part a check of the code reads (<see cref="EmailCodeCheck"/>):
/// its fingerprint, its deadline and the attempts it has left.
/// </summary>
internal interface IEmailStoredCode
{
    /// <summary>
    /// Fingerprint of the code (<see cref="EmailClaimSecrets.HashOfCode"/>), hex.
    /// </summary>
    string Hash { get; }

    /// <summary>
    /// Moment the code stops being accepted (UTC).
    /// </summary>
    DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// Attempts left; zero — the code is spent.
    /// </summary>
    int AttemptsLeft { get; }
}
