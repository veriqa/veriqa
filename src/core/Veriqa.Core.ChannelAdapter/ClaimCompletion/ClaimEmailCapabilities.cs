// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// What the mail infrastructure of a tenant can do for the email completion (SPEC-041 C96): send the
/// completion mail — the condition of the <c>Code</c> and <c>MagicLink</c> methods — and receive the
/// message of the user — the condition of the <c>Push</c> method.
/// </summary>
/// <param name="CanSend">Whether a completion mail can be sent.</param>
/// <param name="CanReceivePush">Whether a message sent by the user can be received.</param>
internal sealed record ClaimEmailCapabilities(bool CanSend, bool CanReceivePush)
{
    /// <summary>
    /// Nothing can be sent or received: what a deployment without the Email adapter has.
    /// </summary>
    public static ClaimEmailCapabilities None { get; } = new(CanSend: false, CanReceivePush: false);
}
