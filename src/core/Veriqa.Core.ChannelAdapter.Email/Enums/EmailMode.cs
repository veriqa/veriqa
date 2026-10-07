// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.Enums;

/// <summary>
/// Operating mode of the Email adapter (SPEC-016 §1).
/// </summary>
public enum EmailMode
{
    /// <summary>
    /// Pull mode (Email Link): Veriqa sends the user an email with a magic link.
    /// The user clicks the button or scans the QR code to confirm.
    /// </summary>
    Pull,

    /// <summary>
    /// Push mode (the one-tap email): the user sends an email to Veriqa.
    /// Login is confirmed once an email is received from a verified sender.
    /// </summary>
    Push
}
