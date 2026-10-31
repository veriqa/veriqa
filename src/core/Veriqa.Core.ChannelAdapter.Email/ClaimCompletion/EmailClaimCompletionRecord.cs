// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// What the Email adapter keeps about the email completion of one transaction (SPEC-041 R12, L50): the
/// method the user chose, the last code, the last link and the correlation token of the <c>Push</c>
/// method, the address the completion obtained and holds until the form is finished — and, apart from them,
/// the code of its last sign-in mail (SPEC-016 §4). Each part is replaced
/// whole when a new one takes effect; the code of the sign-in and the code of the completion are separate
/// parts, so neither replaces nor spends the other.
/// </summary>
/// <param name="SelectedMethod">Method the user chose; null — none was chosen.</param>
/// <param name="Code">The last code that took effect; null — none was sent.</param>
/// <param name="Link">The last link that took effect; null — none was sent.</param>
/// <param name="Push">The correlation token of the <c>Push</c> method; null — none was issued.</param>
/// <param name="SignInCode">The code of the last sign-in mail that took effect; null — none was sent.</param>
/// <param name="Held">The address held until the form of the web step is finished; null — none is held.</param>
internal sealed record EmailClaimCompletionRecord(
    EmailCompletionMethod? SelectedMethod,
    EmailClaimCode? Code,
    EmailClaimLink? Link,
    EmailClaimPushToken? Push,
    EmailSignInCode? SignInCode,
    EmailClaimHeldAddress? Held)
{
    /// <summary>
    /// The record of a transaction nothing was kept for yet.
    /// </summary>
    public static EmailClaimCompletionRecord Empty { get; } = new(null, null, null, null, null, null);
}
