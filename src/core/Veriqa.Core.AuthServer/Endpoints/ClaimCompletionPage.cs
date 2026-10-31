// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Hubs;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Page of the claims completion (SPEC-041 L52, L108). On the device the sign-in was started on it sits on
/// the callback, where the web confirmation page sits, and shows either the form of the web step or a
/// waiting state — the phone step asked in the channel (L44) or a step this page does not show; while the
/// sign-in is in the completion, the waiting state offers "Cancel sign-in" as the form does (L109). On the
/// device that confirmed the sign-in it shows the form of the web step only (N78).
/// </summary>
/// <remarks>
/// The page is built from the frame of the service pages (card, title, instruction, attribution); the form
/// itself is the shared component of every surface of the web step (<see cref="ClaimCompletionForm"/>), and
/// the email block is put into its slot here (<see cref="ClaimCompletionEmailBlock"/>).
/// On the device the sign-in was started on a watcher script, inlined under the CSP nonce of the request,
/// reloads the page through the callback when the step changes or the sign-in ends
/// (<see cref="ClaimCompletionPageScript"/>); a waiting state also carries a link that does the same by
/// hand, for a browser that runs no script. The script also listens to the hub of the sign-in window, so the
/// page leaves as soon as the status changes; the polling stays on whatever the connection does
/// (SPEC-007 UI-125). The same page counts down the time the sign-in has left, by the countdown of the
/// sign-in window (<see cref="PageCountdown"/>). The page of the other device does not watch the status and
/// counts nothing down (<see cref="ClaimCompletionPageState.Watched"/>). On both, a form with the email block
/// carries the script for the block: it presses "Check status" — or, on a step without it while a sent link
/// is live, the hidden button of the same action — as soon as the hub says the address was confirmed
/// elsewhere, shows "Check status" only while the connection to the hub is not alive, and follows what is
/// typed into the address input. On both, the script keeps the values typed into the simple fields in the
/// tab, so a reload loses none of them. The page of the other device carries the script only while the form
/// has simple fields or the block shows the address input or waits for such a confirmation, and the library
/// of the hub only for the latter.
/// </remarks>
internal static class ClaimCompletionPage
{
    /// <summary>
    /// Page title — also the representative key the content language is probed on.
    /// </summary>
    public const string PageTitle = "Complete your sign-in";

    /// <summary>
    /// Instruction above the form.
    /// </summary>
    private const string FormInstruction = "Fill in the missing details to finish signing in.";

    /// <summary>
    /// Instruction of the phone step; the slot takes the name of the channel the number is asked in.
    /// </summary>
    private const string PhoneInstruction = "Share your phone number in {0} to continue.";

    /// <summary>
    /// Instruction of every other waiting state.
    /// </summary>
    private const string WaitingInstruction = "Your sign-in is being completed. This page will continue on its own.";

    /// <summary>
    /// Text of the link that reloads a waiting page by hand — shown where no script does it.
    /// </summary>
    private const string CheckAgainLinkText = "Check again";

    /// <summary>
    /// Slot of <see cref="PhoneInstruction"/> the channel name goes into.
    /// </summary>
    private const string ChannelNameSlot = "{0}";

    /// <summary>
    /// Texts of the page an entry of <c>AuthPage:Texts</c> may reword for a tenant, an application or a
    /// <c>ui_config</c> record: the title and the instruction above the form.
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } = [PageTitle, FormInstruction];

    /// <summary>
    /// Builds the page.
    /// </summary>
    /// <param name="state">What the page shows.</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <param name="cspNonce">CSP nonce of this request; without one the script goes out without the
    /// attribute, as every inline script of the contour does.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Overrides of the texts of the page, resolved over its own transaction.</param>
    /// <param name="language">Content language of the page, already probed on <see cref="PageTitle"/>.</param>
    /// <param name="branding">Effective branding, resolved over the context of the page's own transaction.</param>
    /// <returns>HTML page.</returns>
    public static string Build(
        ClaimCompletionPageState state,
        string pathBase,
        string? cspNonce,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string language,
        CorePageBranding? branding)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pathBase);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(texts);

        // The method picks the instruction and the body of the state: the form under its instruction, or a
        // waiting instruction with "Cancel sign-in", where the state carries the right to it, and the link
        // that reloads the page. Every text is HTML-encoded; the only
        // markup inserted as it is comes from the form component, which encodes its own values.
        var pageTitle = texts.Localize(localizer, PageTitle, language);
        var callbackUrl = TransactionStatusMessageFactory.BuildCallbackUrl(state.SessionId);

        string instruction;
        string body;

        // The email block goes into the slot of the form here. The block sends its actions with the form
        // of the page, which carries the right fields, so what the user typed into the simple fields is
        // not lost to an action of the block; the block also tells the form whether it offers its own
        // "Continue" and whether its address step holds an address yet to be sent, and tells the page
        // whether its step shows the address input or "Check status" — what the script of the page drives.
        var block = state is { Form: not null, EmailBlock: { } emailBlock }
            ? ClaimCompletionEmailBlock.Render(emailBlock, pathBase, localizer)
            : null;

        if (state.Form is not null)
        {
            // The main button closes the form when nothing but the simple fields is left: no email block, or
            // one that has obtained the email — held until the form is finished, or recorded.
            var form = block is null
                ? state.Form with { Finishes = true }
                : state.Form with
                {
                    EmailBlockHtml = block.Html,
                    EmailBlockContinueHtml = block.ContinueHtml,
                    Finishes = state.EmailBlock!.RecordedAddress is not null
                };

            instruction = texts.Localize(localizer, FormInstruction, language);
            body = ClaimCompletionForm.Render(form, pathBase, localizer);
        }
        else
        {
            instruction = WaitingInstructionOf(state, localizer, language);
            var checkAgainText = AuthPageLocalization.Localize(localizer, CheckAgainLinkText, language);

            // The device the sign-in was started on may cancel it while it waits, as from its form.
            var cancel = state.CancelRightFields is null
                ? string.Empty
                : ClaimCompletionForm.RenderCancelAction(
                    OidcEndpoints.AuthorizeCallbackClaimsCancel, state.CancelRightFields, pathBase, language, localizer);

            // A watched page reloads itself, so the link is for the browser that runs no script: it goes
            // inside noscript there and stays in the open on a page that carries no watcher.
            var checkAgain = $"""<a href="{WebUtility.HtmlEncode(pathBase + callbackUrl)}" class="veriqa-link">{WebUtility.HtmlEncode(checkAgainText)}</a>""";

            body = cancel + (state.Watched ? $"<noscript>{checkAgain}</noscript>" : checkAgain);
        }

        // The countdown belongs to the watcher: its element, its rules and its script go out together or
        // not at all, so a page that runs no script of the core shows no figure that never moves. A page
        // that does not watch the status still carries the script while its email block shows something the
        // script drives — the address input or a button to press on a confirmation made elsewhere — and the
        // library of the hub only for that button: while the block waits for such a confirmation — on any
        // step while a sent link is live. It also carries the script while the form has simple fields, whose
        // typed values the script keeps in the tab over reloads.
        var needsHub = state.Watched || block is { ConfirmationButtonId: not null };
        var watcher = needsHub || block is { HasAddressInput: true } || state.Form is { Fields.Count: > 0 }
            ? ClaimCompletionPageScript.BuildWatcher(
                WatcherConfigOf(state, block, pathBase),
                needsHub ? state.SignalRClientPath : null,
                cspNonce)
            : string.Empty;
        var countdown = state.Watched ? PageCountdown.Markup : string.Empty;
        var countdownCss = state.Watched ? PageCountdown.Css : null;

        return $$"""
            <!DOCTYPE html>
            <html lang="{{WebUtility.HtmlEncode(language)}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{WebUtility.HtmlEncode(pageTitle)}}</title>
              {{CorePageHead.BuildServicePageHead(branding, countdownCss)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{WebUtility.HtmlEncode(pageTitle)}}</h1>
                <p class="veriqa-instruction">{{WebUtility.HtmlEncode(instruction)}}</p>
                {{body}}
                {{countdown}}
              </main>
              {{CorePageAttribution.BuildLine(language)}}
              {{watcher}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Builds the configuration of the script of the page. A page that does not watch the status gets no
    /// remainder to count down. A form with the email block also has the script press "Check status" (or the
    /// hidden button of the same action, <see cref="ClaimCompletionEmailBlockMarkup.ConfirmationButtonId"/>)
    /// when the hub says the address was confirmed elsewhere — by the link of the mail or by the mail the
    /// user sent (the code <see cref="ClaimCompletionChannelStatusKeys.EmailConfirmed"/>) — so the block shows
    /// the confirmed address with no press of the user and keeps what the user typed into the simple fields,
    /// which the press sends; and drive the elements of the block.
    /// </summary>
    /// <param name="state">What the page shows.</param>
    /// <param name="block">The rendered email block; null — the page shows none.</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <returns>The configuration of the script.</returns>
    private static ClaimCompletionPageScriptConfig WatcherConfigOf(
        ClaimCompletionPageState state,
        ClaimCompletionEmailBlockMarkup? block,
        string pathBase)
    {
        var config = ClaimCompletionPageScript.BuildConfig(
            state.SessionId,
            pathBase,
            state.Watched ? state.RemainingMs : null,
            state.Step?.ToString(),
            state.Surface?.ToString()) with
        {
            WatchesStatus = state.Watched,
            SimpleFields = state.Form is { Fields.Count: > 0 }
                ? new ClaimCompletionPageScriptSimpleFields(
                    FormId: ClaimCompletionForm.FieldsFormId,
                    NamePrefix: ClaimCompletionFormFields.ClaimValuePrefix,
                    StorageKey: ClaimCompletionPageScript.SimpleFieldsStorageKeyPrefix + state.SessionId)
                : null
        };

        return state.Form is not null && block is not null
            ? config with
            {
                ChannelAction = new ClaimCompletionPageScriptChannelAction(
                    Method: SignalRConstants.OnChannelStatusChangedMethod,
                    ChannelStatus: ClaimCompletionChannelStatusKeys.EmailConfirmed,
                    ButtonId: block.ConfirmationButtonId ?? ClaimCompletionEmailBlock.CheckConfirmationButtonId,
                    RecheckAfterAwayMs: ClaimCompletionPageScript.RecheckAfterAwayMs),
                EmailBlock = new ClaimCompletionPageScriptEmailBlock(
                    CheckButtonId: ClaimCompletionEmailBlock.CheckConfirmationButtonId,
                    AddressInputId: ClaimCompletionEmailBlock.AddressInputId,
                    AddressActionButtonId: ClaimCompletionEmailBlock.AddressActionButtonId,
                    ContinueButtonId: ClaimCompletionForm.ContinueButtonId,
                    HubSilenceMs: ClaimCompletionPageScript.HubSilenceBeforeCheckButtonMs)
            }
            : config;
    }

    /// <summary>
    /// Picks the instruction of a waiting state: the phone step names the channel the number is asked in; the
    /// web step on the device that confirmed the sign-in says the form is there (SPEC-041 L77) — read off the
    /// surface, not off a channel status, so the line holds without one; every other state waits neutrally.
    /// </summary>
    /// <param name="state">What the page shows.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>The localized instruction.</returns>
    private static string WaitingInstructionOf(
        ClaimCompletionPageState state,
        IConfirmationPromptLocalizer localizer,
        string language)
    {
        if (state.PhoneChannelName is not null)
        {
            return AuthPageLocalization.Localize(localizer, PhoneInstruction, language)
                .Replace(ChannelNameSlot, state.PhoneChannelName, StringComparison.Ordinal);
        }

        return state is { Step: ClaimCompletionStep.WebStep, Surface: WebStepSurface.CrossDevicePage }
            ? AuthPageLocalization.Localize(localizer, AuthPageStrings.ClaimFormOnPhone, language)
            : AuthPageLocalization.Localize(localizer, WaitingInstruction, language);
    }
}
