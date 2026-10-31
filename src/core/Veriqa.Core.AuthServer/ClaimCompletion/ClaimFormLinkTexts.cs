// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// Natural Key of the label of the link button of the claims completion (SPEC-041 C76, L52), resolved into
/// the language of the user of the channel snapshot through the shared channel localizer, whichever channel
/// shows it. The text above the button is not here: it is the message of the kind
/// <c>claim-form-link</c> of the message templates, and the label is not part of that message.
/// </summary>
internal static class ClaimFormLinkTexts
{
    /// <summary>
    /// Label of the button that opens the form.
    /// </summary>
    public const string ButtonLabel = "Open form";
}
