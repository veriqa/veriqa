// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.Contracts;

/// <summary>
/// Values of the Email channel mode claim <see cref="VeriqaClaimTypes.VeriqaEmailMode"/> (SPEC-016 §6.3).
/// They are stated next to the name of the claim, because readers other than the Email adapter tell the
/// modes apart: the claims completion reads a <see cref="Pull"/> sign-in as one confirmed on the
/// confirmation page of the mail, on the device the user read it on (SPEC-041 L53), only where the
/// effective confirmation surface is
/// <see cref="Veriqa.Core.TransactionEngine.Domain.ConfirmationSurface.None"/> (SPEC-012 §4.4.2) — on any
/// other surface the sign-in window confirmed it.
/// </summary>
public static class VeriqaEmailModes
{
    /// <summary>
    /// Pull mode: the user received a mail and continued the sign-in on its confirmation page. That page
    /// confirms the sign-in only on the effective confirmation surface
    /// <see cref="Veriqa.Core.TransactionEngine.Domain.ConfirmationSurface.None"/>; on any other surface it
    /// only attaches the mailbox, and the sign-in window asks the question.
    /// </summary>
    public const string Pull = "pull";

    /// <summary>
    /// Push mode: the user sent a mail from their mailbox; there is no confirmation page.
    /// </summary>
    public const string Push = "push";
}
