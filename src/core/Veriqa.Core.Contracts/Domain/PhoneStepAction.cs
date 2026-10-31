// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// The secondary action the phone step of claim completion offers next to sharing the number
/// (SPEC-041 L73). Which one is offered follows the level of the phone requirement: an optional
/// number can be skipped, a required one can only be refused together with the sign-in.
/// </summary>
public enum PhoneStepAction
{
    /// <summary>
    /// Go on without the number — offered when the number is optional.
    /// </summary>
    Skip = 0,

    /// <summary>
    /// Cancel the sign-in — offered when the number is required.
    /// </summary>
    CancelSignIn = 1
}
