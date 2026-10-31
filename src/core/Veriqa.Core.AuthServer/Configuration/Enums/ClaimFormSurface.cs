// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration.Enums;

/// <summary>
/// Where the claims completion form of an application is shown (SPEC-041 C34, L53) — the setting the
/// surface of the web step is chosen by when the step begins. The page of the device the sign-in was
/// started on is the sign-in page; the page of the device that confirmed it is opened by the link button
/// of the channel or is the confirmation page of the mail (Email in Pull mode). The mail page confirms the
/// sign-in only where the effective confirmation surface is
/// <see cref="Veriqa.Core.TransactionEngine.Domain.ConfirmationSurface.None"/> (SPEC-012 §4.4.2): on any
/// other surface the letter only attaches the mailbox, and the sign-in window asks the question and
/// confirms it.
/// </summary>
public enum ClaimFormSurface
{
    /// <summary>
    /// Chosen by the path of the sign-in (the default): the device that confirmed it, when the sign-in was
    /// confirmed on the confirmation page of the mail (Email in Pull mode on the effective confirmation
    /// surface <see cref="Veriqa.Core.TransactionEngine.Domain.ConfirmationSurface.None"/>) or when the
    /// channel account has no identifier to be shown by (a Telegram account without a username); the
    /// sign-in page otherwise — an Email sign-in the sign-in window confirmed included. The sign-in page is
    /// the fallback when the link button cannot be sent or is not delivered.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always the sign-in page, on the device the sign-in was started on.
    /// </summary>
    InitiatingPage = 1,

    /// <summary>
    /// The device that confirmed the sign-in; the sign-in page is the fallback when the link button
    /// cannot be sent or is not delivered.
    /// </summary>
    CrossDevicePage = 2,

    /// <summary>
    /// Only the device that confirmed the sign-in, with no fallback: when the link button cannot be sent
    /// or is not delivered, a missing <c>Required</c> claim fails the sign-in as unobtainable and a missing
    /// <c>Optional</c> one is skipped.
    /// </summary>
    CrossDevicePageOnly = 3
}
