// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Natural Keys of the buttons of the phone number request of claim completion (SPEC-041 L94, L73).
/// They are resolved into the language of the user of the channel snapshot through the shared channel
/// localizer, whichever channel shows them. The text of the request is not among them: it is a message
/// of the template mechanism, whose wording a level may override — <see cref="PhoneNumberRequestBuilder"/>
/// obtains it.
/// </summary>
internal static class ClaimCompletionPhoneStepTexts
{
    /// <summary>
    /// Label of the button that shares the number through the platform.
    /// </summary>
    public const string ShareButtonLabel = "📱 Send phone number";

    /// <summary>
    /// Label of the button that skips an <c>Optional</c> number.
    /// </summary>
    public const string SkipButtonLabel = "Skip";

    /// <summary>
    /// Label of the button that cancels the sign-in in place of a <c>Required</c> number.
    /// </summary>
    public const string CancelSignInButtonLabel = "Cancel sign-in";
}
