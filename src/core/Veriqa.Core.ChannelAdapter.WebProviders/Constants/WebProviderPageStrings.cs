// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Constants;

/// <summary>
/// Natural Keys of the texts the page of a web provider channel states for itself. Every other text
/// of the page is the core's — the question, the messages, the outcome — and goes out as the core
/// rendered it. None of these texts depends on the type of the transaction: a sign-in and a
/// confirmation go through the same page.
/// </summary>
internal static class WebProviderPageStrings
{
    /// <summary>
    /// Answer "Yes" to the question — the key the question page of the core answers with, so an
    /// override of it reaches this page too.
    /// </summary>
    public const string ConfirmButton = "Yes";

    /// <summary>
    /// Answer "No" to the question — the key the question page of the core answers with.
    /// </summary>
    public const string DeclineButton = "No";

    /// <summary>
    /// Text of the track of the slider that takes "Yes" in place of its button, and the accessible name
    /// of its handle.
    /// </summary>
    public const string ConfirmSlider = "Slide to answer Yes";

    /// <summary>
    /// Text the track of the slider shows in place of <see cref="ConfirmSlider"/> once the handle is
    /// drawn to its end and not let go yet: letting go answers "Yes".
    /// </summary>
    public const string ReleaseSlider = "Release to answer Yes";

    /// <summary>
    /// Line under the brand name in the head of the page, naming the channel the brand is reached
    /// through; <c>{0}</c> — the name of the channel.
    /// </summary>
    public const string Via = "via {0}";

    /// <summary>
    /// Line of the page while it waits for the question, a message or the outcome.
    /// </summary>
    public const string Waiting = "Please wait a moment.";

    /// <summary>
    /// Line of the page of a transaction that ended without an outcome shown here.
    /// </summary>
    public const string RequestEnded = "This request has ended.";

    /// <summary>
    /// Line of the page a link of an unknown or ended transaction opens.
    /// </summary>
    public const string RequestNotFound = "This request was not found or has expired.";

    /// <summary>
    /// Line of the page the provider sends the user back to without a confirmed account: an error of
    /// the provider, a cancellation by the user, a state OpenIddict refused.
    /// </summary>
    public const string ProviderRefused = "Your account was not confirmed by the provider. Open the link again to try once more.";

    /// <summary>
    /// Line of the page opened, or a form posted, by a browser that holds no mark of the page.
    /// </summary>
    public const string NotInThisBrowser = "This page is not available in this browser.";

    /// <summary>
    /// Line of the page when its state could not be stored or read.
    /// </summary>
    public const string Failed = "Something went wrong. Open the link again to try once more.";

    /// <summary>
    /// Line of a form whose anti-forgery token is refused — the key the pages of the core answer the
    /// same refusal with.
    /// </summary>
    public const string PageOutOfDate = "This page is out of date";

    /// <summary>
    /// Texts of the page an owning level may override through <c>AuthPage.Texts</c>.
    /// </summary>
    public static IReadOnlyList<string> OverridableTexts { get; } =
        [Via, ConfirmButton, DeclineButton, ConfirmSlider, ReleaseSlider, Waiting, RequestEnded, RequestNotFound, ProviderRefused, NotInThisBrowser, Failed, .. CorePageCloseAction.OverridableTexts];
}
