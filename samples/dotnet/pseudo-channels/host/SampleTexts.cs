// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// Texts of the sample's own pages. English only: the sample has no localization of its own, and
/// the pages of the embedded Veriqa keep theirs.
/// </summary>
internal static class SampleTexts
{
    /// <summary>Heading of the question page.</summary>
    public const string QuestionTitle = "Confirm the sign-in";

    /// <summary>Lead of the question page.</summary>
    public const string QuestionLead = "A sign-in is waiting for your answer.";

    /// <summary>Label of the application row.</summary>
    public const string ApplicationLabel = "Signing in to";

    /// <summary>Label of the device row.</summary>
    public const string DeviceLabel = "Requested from";

    /// <summary>Label of the location row.</summary>
    public const string LocationLabel = "Location";

    /// <summary>Label of the row with the moment the sign-in was started.</summary>
    public const string StartedLabel = "Started";

    /// <summary>Label of the account row.</summary>
    public const string AccountLabel = "Your account";

    /// <summary>Warning above the buttons.</summary>
    public const string QuestionWarning =
        "Answer Yes only if you started this sign-in yourself. " +
        "If someone sent you the QR code or the link, answer No.";

    /// <summary>Caption of the confirming button.</summary>
    public const string YesButton = "Yes, sign me in";

    /// <summary>Caption of the declining button.</summary>
    public const string NoButton = "No";

    /// <summary>Heading of a page that reports the state of a sign-in.</summary>
    public const string StatusTitle = "Sign-in";

    /// <summary>Shown when the link names no sign-in that can still be continued.</summary>
    public const string LinkNotValid =
        "This link does not lead to a sign-in that is still waiting. Go back to the other screen and start again.";

    /// <summary>Shown when the sign-in at the provider did not go through.</summary>
    public const string ProviderFailed =
        "The sign-in was not completed. Go back to the other screen and start again.";

    /// <summary>Shown when this browser did not sign in at the provider for this sign-in.</summary>
    public const string SessionMissing =
        "This page works only in the browser that has just signed in for it. " +
        "Scan the QR code on the other screen again.";

    /// <summary>Shown when no question is asked of this account on this page.</summary>
    public const string ContinueOnOtherScreen = "Nothing to answer here — continue on the other screen.";

    /// <summary>Shown when the answer was delivered and its outcome is not known to the page.</summary>
    public const string AnswerSent = "Answer sent — check the other screen.";

    /// <summary>Shown when the answer could not be accepted.</summary>
    public const string AnswerRejected =
        "The answer was not accepted. Go back to the other screen and start again.";

    /// <summary>Heading of the desktop page of an anonymous visitor.</summary>
    public const string HomeTitle = "Pseudo-channels sample";

    /// <summary>Lead of the desktop page of an anonymous visitor.</summary>
    public const string HomeLead =
        "Sign in here, scan the QR code with a phone, and finish the sign-in on the phone.";

    /// <summary>Caption of the sign-in link.</summary>
    public const string SignInLink = "Sign in";

    /// <summary>Heading of the desktop page of a signed-in user.</summary>
    public const string SignedInTitle = "Signed in";

    /// <summary>Lead of the desktop page of a signed-in user.</summary>
    public const string SignedInLead = "The claims Veriqa issued for this session:";

    /// <summary>Caption of the sign-out link.</summary>
    public const string SignOutLink = "Sign out";

    /// <summary>Heading of the desktop page of a sign-in that did not go through.</summary>
    public const string SignInFailedTitle = "Not signed in";

    /// <summary>Text of the desktop page of a sign-in that did not go through.</summary>
    public const string SignInFailedText = "The sign-in was declined or could not be completed.";

    /// <summary>Caption of the link back to the desktop page.</summary>
    public const string BackLink = "Back";
}
