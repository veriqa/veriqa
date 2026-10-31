// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// Result of checking a code entered for the email completion.
/// </summary>
/// <param name="Status">Verdict on the code.</param>
/// <param name="Address">
/// Address the matched code was sent to — the one the email is recorded with (SPEC-041 L50); null unless
/// <see cref="ClaimEmailCodeStatus.Matched"/>.
/// </param>
/// <param name="AttemptsLeft">Attempts the live code has after this one.</param>
internal sealed record ClaimEmailCodeCheck(ClaimEmailCodeStatus Status, string? Address, int AttemptsLeft);
