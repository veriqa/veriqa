// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// The code of a sign-in mail as the store keeps it (SPEC-016 §4): never the code itself, only its fingerprint,
/// and the action token of the mail it was sent in. The token is what makes the code and the link of one mail one
/// letter — the key the gate of a letter counts its attempt by — and it names the address the code confirms; the
/// action-token store already holds it, so keeping it here exposes nothing new.
/// </summary>
/// <param name="Hash">Fingerprint of the code (<c>EmailClaimSecrets.HashOfCode</c>), hex.</param>
/// <param name="LetterToken">Action token of the mail the code was sent in (<see cref="EmailActionToken"/>).</param>
/// <param name="ExpiresAt">Moment the code stops being accepted (UTC).</param>
/// <param name="AttemptsLeft">Attempts left; zero — the code is spent.</param>
internal sealed record EmailSignInCode(string Hash, string LetterToken, DateTimeOffset ExpiresAt, int AttemptsLeft)
    : IEmailStoredCode;
