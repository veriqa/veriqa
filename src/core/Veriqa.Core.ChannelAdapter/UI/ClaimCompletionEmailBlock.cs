// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The email block of the form of the web step (SPEC-041 L108, C96): the fragment the form puts after its
/// fields. It obtains the email the way the block is on — a mail that confirms the address (by a code, by a
/// link or by either, as the mail carries), a mail the user sends or an address entered without
/// verification — and offers the other available ways, under the divider "OR", and a new mail within the
/// limits (E61). An optional email is skipped by "Continue" of the form with nothing typed into the block.
/// </summary>
/// <remarks>
/// <para>
/// Every action of the block is posted to one path, told apart by
/// <see cref="ClaimCompletionFormFields.EmailOperation"/>. The block is built from the primitives of the
/// form of the web step (SPEC-015 §4: field with a visible label, input, button and its secondary variant,
/// text link, divider, error message) and adds none. Every value of the model is HTML-encoded
/// (SPEC-041 N70).
/// </para>
/// <para>
/// The block has no form of its own: its inputs and buttons belong to the form of the page
/// (<see cref="ClaimCompletionForm.FieldsFormId"/>, the <c>form</c> attribute), which carries the right
/// fields, and each button sends that form to the path of the block (<c>formaction</c>) — so whatever the
/// user typed into the simple fields travels with every action of the block and comes back in the page the
/// action is answered with, with no script. The shared form is not checked by the browser
/// (<c>novalidate</c>): an action of the block must not wait for the fields to be valid, nor "Continue" for
/// the inputs of the block; the server judges both. Enter in an input of the block sends the shared form by
/// its default button — "Continue" beside simple fields, the first button of the block without them; the
/// script of the page turns Enter in the address input into the action of the address step, or into
/// "Continue" while the input is empty and on the way without verification, which has no button of its own.
/// </para>
/// <para>
/// The button that checks the confirmation is the one action of a mail confirmed elsewhere — by its link or
/// by the mail the user sent. The page presses it when the hub announces the confirmation, and its script
/// shows it only once its connection to the hub has not been alive for a while: the server renders it
/// hidden, so a page that loads with a live connection never flashes it. A live link
/// is watched on every step of the block: a step without that button carries a hidden one of the same
/// action for the page to press.
/// </para>
/// </remarks>
internal static class ClaimCompletionEmailBlock
{
    /// <summary>
    /// Heading of the block — it also names the address input of the first step of a way.
    /// </summary>
    private const string HeadingText = "Email address";

    /// <summary>
    /// Button of the address step that sends a mail carrying a code (with or without a link).
    /// </summary>
    private const string SendCodeText = "Send code";

    /// <summary>
    /// Button of the address step that sends a mail carrying a link only.
    /// </summary>
    private const string SendLinkText = "Send link";

    /// <summary>
    /// Link of the way "confirm via email" among the other ways, when its mail carries a code.
    /// </summary>
    private const string MethodCodeText = "Code by email";

    /// <summary>
    /// Link of the way "confirm via email" among the other ways, when its mail carries a link only.
    /// </summary>
    private const string MethodLinkText = "Link by email";

    /// <summary>
    /// Line under the code input of a mail that carries a link as well.
    /// </summary>
    private const string LinkInsteadOfCodeText = "Instead of entering the code, you can open the link from the email.";

    /// <summary>
    /// Line above the code input; the slot takes the masked address the code was sent to.
    /// </summary>
    private const string CodeSentText = "The code has been sent to {address}";

    /// <summary>
    /// Line of a live link; the slot takes the masked address the link was sent to.
    /// </summary>
    private const string LinkSentText = "The link has been sent to {address}. Open it and confirm the address.";

    /// <summary>
    /// Slot of <see cref="CodeSentText"/> and <see cref="LinkSentText"/> the address goes into.
    /// </summary>
    private const string AddressSlot = "{address}";

    /// <summary>
    /// Label of the code input.
    /// </summary>
    private const string CodeLabelText = "Code from the email";

    /// <summary>
    /// Button that checks the entered code.
    /// </summary>
    private const string ConfirmCodeText = "Confirm";

    /// <summary>
    /// Text link that returns a live mail to the address step, its input filled with the address of the mail.
    /// </summary>
    private const string SendAgainText = "Send again";

    /// <summary>
    /// Text link under a held address that asks for the email again, the address in the input.
    /// </summary>
    private const string ChangeText = "Change";

    /// <summary>
    /// Button that shows the block again once the address was confirmed elsewhere — by the link of the mail
    /// or by the mail the user sent.
    /// </summary>
    private const string CheckConfirmationText = "Check status";

    /// <summary>
    /// Line beside <see cref="CheckConfirmationText"/> while the link of the mail is live.
    /// </summary>
    private const string WaitingForConfirmationText = "Waiting for confirmation";

    /// <summary>
    /// Instruction of the way where the user sends the mail, when no way "confirm via email" is offered
    /// among the other ways.
    /// </summary>
    private const string PushInstructionText =
        "Press the button to send an email that confirms your email address.";

    /// <summary>
    /// Instruction of the way where the user sends the mail, when the way "confirm via email" is offered
    /// among the other ways and its mail carries a code.
    /// </summary>
    private const string PushInstructionOrCodeText =
        "Press the button to send an email that confirms your email address, or choose confirmation by code.";

    /// <summary>
    /// Instruction of the way where the user sends the mail, when the way "confirm via email" is offered
    /// among the other ways and its mail carries a link only.
    /// </summary>
    private const string PushInstructionOrLinkText =
        "Press the button to send an email that confirms your email address, or choose confirmation by link.";

    /// <summary>
    /// Button that opens the prepared mail in the mail app.
    /// </summary>
    private const string PushSendText = "Send the email";

    /// <summary>
    /// Tip and accessible name of the mark of an address that is not confirmed — on the input of the way
    /// without verification and on a recorded address saved without verification.
    /// </summary>
    private const string NotConfirmedMarkText = "Email not confirmed.";

    /// <summary>
    /// Tip and accessible name of the mark of a recorded address that was confirmed.
    /// </summary>
    private const string ConfirmedMarkText = "Email confirmed.";

    /// <summary>
    /// Inner markup of the "alert" glyph of the mark of an address not confirmed — Lucide circle-alert
    /// (ISC), viewBox 0 0 24 24, drawn by its stroke.
    /// </summary>
    private const string NotConfirmedGlyph =
        """<circle cx="12" cy="12" r="10"/><line x1="12" x2="12" y1="8" y2="12"/><line x1="12" x2="12.01" y1="16" y2="16"/>""";

    /// <summary>
    /// Inner markup of the "accepted" glyph of the mark of a confirmed address — Lucide circle-check (ISC),
    /// viewBox 0 0 24 24, drawn by its stroke.
    /// </summary>
    private const string ConfirmedGlyph = """<circle cx="12" cy="12" r="10"/><path d="m9 12 2 2 4-4"/>""";

    /// <summary>
    /// Class of the wrapper of an input that carries a mark in its right part.
    /// </summary>
    private const string MarkedInputClass = "veriqa-marked-input";

    /// <summary>
    /// Class of the mark; its modifier gives its color.
    /// </summary>
    private const string MarkClass = "veriqa-input-mark";

    /// <summary>
    /// Modifier of the mark of an address not confirmed — the color of an error.
    /// </summary>
    private const string MarkNotConfirmedClass = "veriqa-input-mark--warn";

    /// <summary>
    /// Modifier of the mark of a confirmed address — the color of the text.
    /// </summary>
    private const string MarkConfirmedClass = "veriqa-input-mark--ok";

    /// <summary>
    /// Class of the tip of the mark, shown while the mark is hovered or focused.
    /// </summary>
    private const string MarkTipClass = "veriqa-input-mark-tip";

    /// <summary>
    /// Text of the divider above the other ways — the Natural Key of the divider of the sign-in window, which
    /// this assembly cannot reference.
    /// </summary>
    private const string OrText = "OR";

    /// <summary>
    /// Link of the way <see cref="EmailCompletionMethod.Push"/> among the other ways.
    /// </summary>
    private const string MethodPushText = "Email from my mailbox";

    /// <summary>
    /// Link of the way <see cref="EmailCompletionMethod.Unverified"/> among the other ways.
    /// </summary>
    private const string MethodUnverifiedText = "Enter email without verification";

    /// <summary>
    /// Button of an exhausted block — the action that ends the email.
    /// </summary>
    private const string ContinueText = "Continue";

    /// <summary>
    /// Element id of the heading; the address input of the first step is labelled by it.
    /// </summary>
    private const string HeadingId = "veriqa-email-heading";

    /// <summary>
    /// Element id of the code input.
    /// </summary>
    private const string CodeInputId = "veriqa-email-code";

    /// <summary>
    /// Class of the code input — the look of the code input of the sign-in window.
    /// </summary>
    private const string CodeInputClass = "veriqa-code-input";

    /// <summary>
    /// Element id of the address input of the first step of a way. The script of the page reads it to enable
    /// the button of the step only while the input holds an address.
    /// </summary>
    public const string AddressInputId = "veriqa-email-address";

    /// <summary>
    /// Element id of the button of the address step — "Send code" or "Send link". The script of the page
    /// keeps it disabled while the address input is empty and presses it on Enter in that input; the way
    /// without verification has no button of its own. The server renders it enabled, so a page without the
    /// script still sends the mail.
    /// </summary>
    public const string AddressActionButtonId = "veriqa-email-send";

    /// <summary>
    /// Element id of the button "Check status". The script of the page presses it when the hub says the
    /// address was confirmed, and shows it only while its connection to the hub is not alive; a block without
    /// the button gives the script nothing to press.
    /// </summary>
    public const string CheckConfirmationButtonId = "veriqa-email-check";

    /// <summary>
    /// Element id of the hidden button that watches a live link on a step without "Check status" — the
    /// address step brought back over it, among others. The page presses it when the hub says the address was
    /// confirmed; the script never shows it.
    /// </summary>
    public const string WatchConfirmationButtonId = "veriqa-email-watch";

    /// <summary>
    /// Class of the main button.
    /// </summary>
    private const string ButtonClass = ClaimCompletionForm.ButtonClass;

    /// <summary>
    /// Classes of the secondary button.
    /// </summary>
    private const string SecondaryButtonClass = ClaimCompletionForm.SecondaryButtonClass;

    /// <summary>
    /// Class of a text link.
    /// </summary>
    private const string TextLinkClass = "veriqa-text-link";

    /// <summary>
    /// Classes of the text link that stands at the end of the line under an input — a secondary action of
    /// that input.
    /// </summary>
    private const string EndTextLinkClass = TextLinkClass + " veriqa-text-link--end";

    /// <summary>
    /// What the block assumes of the mails when nothing is known of them: none was sent, and one may be.
    /// </summary>
    private static readonly ClaimEmailState NothingSent = new(
        SelectedMethod: null,
        CodeAddressMasked: null,
        CodeLive: false,
        LinkAddressMasked: null,
        LinkLive: false,
        MailBudgetLeft: true,
        LastAddress: null);

    /// <summary>
    /// Renders the fragment of the block.
    /// </summary>
    /// <param name="view">What the block shows.</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <returns>The markup of the fragment and what the form of the page takes from it.</returns>
    public static ClaimCompletionEmailBlockMarkup Render(
        ClaimCompletionEmailBlockView view,
        string pathBase,
        IConfirmationPromptLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(pathBase);
        ArgumentNullException.ThrowIfNull(localizer);

        // The method lays the block out top-down: the heading, the step of the current way, the error of the
        // block, under the divider the other ways and, last, the hidden button that watches a live link. The
        // heading names the address input, so it stands only over a step that shows one. A block that no way
        // can obtain the email in shows nothing else, since nothing else would lead anywhere, and hands the
        // form its own "Continue" for the row of the final actions; an obtained email — held or recorded —
        // shows its address in a read-only input, marked as confirmed or not, with the link that changes it
        // while it may be changed.
        var actions = new BlockActions(pathBase + view.ActionPath);

        if (view.RecordedAddress is { } recorded)
        {
            return new ClaimCompletionEmailBlockMarkup(
                "<section class=\"veriqa-fields\" aria-labelledby=\"" + HeadingId + "\">"
                + Heading(view, localizer)
                + Field(RecordedInput(recorded, view.RecordedVerified, view.Language, localizer)
                    + (view.RecordedChangeable
                        ? actions.TextLink(
                            ClaimCompletionFormFields.EmailOperation,
                            ClaimCompletionFormFields.EmailOperations.Change,
                            localizer.ResolveOrBaseText(ChangeText, view.Language),
                            EndTextLinkClass)
                        : string.Empty))
                + "</section>",
                ContinueHtml: null,
                HasAddressInput: false,
                ConfirmationButtonId: null);
        }

        var step = new StepMarkup(string.Empty);
        string? ownContinue = null;

        if (!view.Exhausted && view.Current is { } current)
        {
            step = RenderCurrent(current, view, actions, localizer);
        }
        else
        {
            ownContinue = actions.Button(
                ClaimCompletionFormFields.EmailOperations.Refresh,
                localizer.ResolveOrBaseText(ContinueText, view.Language),
                ButtonClass);
        }

        var html = new StringBuilder();

        if (step.HasAddressInput)
        {
            html.Append("<section class=\"veriqa-fields\" aria-labelledby=\"").Append(HeadingId).Append("\">")
                .Append(Heading(view, localizer));
        }
        else
        {
            html.Append("<section class=\"veriqa-fields\">");
        }

        html.Append(step.Html);

        if (!string.IsNullOrEmpty(view.Error))
        {
            html.Append("<p class=\"veriqa-msg err\" role=\"status\">").Append(Encode(view.Error)).Append("</p>");
        }

        if (!view.Exhausted && view.Alternatives.Count > 0)
        {
            html.Append(RenderAlternatives(view, actions, localizer));
        }

        // A link once sent confirms the address for as long as it lives, whatever step the block shows now —
        // the address step brought back by "Send again" included. A step without "Check status" still gets a
        // button for the page to press when the hub announces the confirmation, never shown to the user: the
        // page learns of the confirmation by itself.
        var confirmationButtonId = step.HasCheckConfirmation ? CheckConfirmationButtonId : null;

        if (confirmationButtonId is null && LinkLive(view))
        {
            html.Append(actions.Button(
                ClaimCompletionFormFields.EmailOperations.Refresh,
                localizer.ResolveOrBaseText(CheckConfirmationText, view.Language),
                SecondaryButtonClass,
                WatchConfirmationButtonId,
                hidden: true));
            confirmationButtonId = WatchConfirmationButtonId;
        }

        html.Append("</section>");

        return new ClaimCompletionEmailBlockMarkup(
            html.ToString(),
            ownContinue,
            step.HasAddressInput,
            confirmationButtonId);
    }

    /// <summary>
    /// The heading of the block, with the mark of an optional email; it names the address input.
    /// </summary>
    /// <param name="view">What the block shows.</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <returns>Markup of the heading.</returns>
    private static string Heading(ClaimCompletionEmailBlockView view, IConfirmationPromptLocalizer localizer)
    {
        var heading = new StringBuilder()
            .Append("<h2 class=\"veriqa-field-label\" id=\"").Append(HeadingId).Append("\">")
            .Append(Encode(localizer.ResolveOrBaseText(HeadingText, view.Language)));

        if (!view.Required)
        {
            heading.Append(ClaimCompletionForm.OptionalMark(
                localizer.ResolveOrBaseText(ClaimCompletionForm.OptionalMarkText, view.Language)));
        }

        return heading.Append("</h2>").ToString();
    }

    /// <summary>
    /// Whether a link sent by the way "confirm via email" is live and the list still allows a link.
    /// </summary>
    /// <param name="view">What the block shows.</param>
    /// <returns><c>true</c> when the address may still be confirmed by a link.</returns>
    private static bool LinkLive(ClaimCompletionEmailBlockView view) =>
        view.State is { LinkLive: true } && view.MailContent is { } content && content.CarriesLink();

    /// <summary>
    /// Renders the step of the current way.
    /// </summary>
    /// <param name="current">The current way.</param>
    /// <param name="view">What the block shows.</param>
    /// <param name="actions">Builder of the actions of the block.</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <returns>Markup of the step.</returns>
    private static StepMarkup RenderCurrent(
        EmailCompletionMethod current,
        ClaimCompletionEmailBlockView view,
        BlockActions actions,
        IConfirmationPromptLocalizer localizer) => current switch
        {
            EmailCompletionMethod.Code or EmailCompletionMethod.MagicLink => RenderMail(current, view, actions, localizer),
            EmailCompletionMethod.Push => RenderPush(view, actions, localizer),
            EmailCompletionMethod.Unverified => RenderUnverified(view, localizer),
            _ => new StepMarkup(string.Empty)
        };

    /// <summary>
    /// Renders the way "confirm via email": the address step — the address, filled with the address of the
    /// last mail, and the button that sends the mail, named by what the mail carries — until something the
    /// mail carries is live; then the line of the address the mail went to and what confirms it — the code
    /// input, "Check status" beside a live link, or both with a line saying the link may be opened
    /// instead of entering the code — and, while mails are left, the text link "Send again", which brings the
    /// address step back. The address step is also shown over a live mail when the view asks for it
    /// (<see cref="ClaimCompletionEmailBlockView.AddressStep"/>) and a mail is left. With nothing live and no
    /// mail left the step is empty: the error of the block says why.
    /// </summary>
    /// <param name="current">The method that names the way.</param>
    /// <param name="view">What the block shows.</param>
    /// <param name="actions">Builder of the actions of the block.</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <returns>Markup of the step.</returns>
    private static StepMarkup RenderMail(
        EmailCompletionMethod current,
        ClaimCompletionEmailBlockView view,
        BlockActions actions,
        IConfirmationPromptLocalizer localizer)
    {
        var state = view.State ?? NothingSent;
        var language = view.Language;
        var content = MailContentOf(view, current);

        // A code or a link left live by a mail the list no longer allows is not offered.
        var codeLive = content.CarriesCode() && state.CodeLive;
        var linkLive = content.CarriesLink() && state.LinkLive;

        // The filled input stands for "Send again" in the address step: the button of the step sends the
        // mail, to the same address or to the one typed over it.
        if ((!codeLive && !linkLive) || (view.AddressStep && state.MailBudgetLeft))
        {
            if (!state.MailBudgetLeft)
            {
                return new StepMarkup(string.Empty);
            }

            var sendText = content.CarriesCode() ? SendCodeText : SendLinkText;

            return AddressStep(
                ClaimCompletionFormFields.EmailOperations.Send,
                view.EnteredAddress ?? state.LastAddress,
                localizer.ResolveOrBaseText(sendText, language),
                actions);
        }

        var html = new StringBuilder();

        if (codeLive)
        {
            html.Append(AddressLine(localizer.ResolveOrBaseText(CodeSentText, language), state.CodeAddressMasked));

            var codeField = new StringBuilder()
                .Append("<label class=\"veriqa-field-label\" for=\"").Append(CodeInputId).Append("\">")
                .Append(Encode(localizer.ResolveOrBaseText(CodeLabelText, language)))
                .Append("</label>")
                .Append("<input class=\"veriqa-input ").Append(CodeInputClass).Append("\" id=\"").Append(CodeInputId)
                .Append("\" name=\"").Append(ClaimCompletionFormFields.EmailCode)
                .Append("\" type=\"text\" inputmode=\"numeric\" autocomplete=\"one-time-code\"")
                .Append(BlockActions.InputAttributes)
                .Append(" />")
                .ToString();

            html.Append(Field(
                codeField
                + actions.Button(
                    ClaimCompletionFormFields.EmailOperations.Verify,
                    localizer.ResolveOrBaseText(ConfirmCodeText, language),
                    ButtonClass)));

            if (linkLive)
            {
                html.Append(Instruction(localizer.ResolveOrBaseText(LinkInsteadOfCodeText, language)));
            }
        }
        var waitingText = localizer.ResolveOrBaseText(WaitingForConfirmationText, language);

        if (!codeLive)
        {
            // A live link alone: the line of waiting goes on the line of the address.
            html.Append(AddressLine(localizer.ResolveOrBaseText(LinkSentText, language), state.LinkAddressMasked, waitingText))
                .Append(CheckConfirmationButton(actions, localizer, language));
        }
        else if (linkLive)
        {
            html.Append(Instruction(waitingText))
                .Append(CheckConfirmationButton(actions, localizer, language));
        }

        // "Send again" only brings the address step back, so it sends nothing and is not disabled while the
        // minimal interval between mails runs: the button of the address step sends, and a mail asked for
        // too soon is refused by the server with the error of the block.
        if (state.MailBudgetLeft)
        {
            html.Append(actions.TextLink(
                ClaimCompletionFormFields.EmailOperation,
                ClaimCompletionFormFields.EmailOperations.Address,
                localizer.ResolveOrBaseText(SendAgainText, language)));
        }

        return new StepMarkup(html.ToString(), HasCheckConfirmation: linkLive);
    }

    /// <summary>
    /// Renders the way where the user sends the mail: the instruction — which also names the way "confirm via
    /// email" when it is offered under the divider, by what its mail carries, as its link there does — the
    /// prepared mail (or nothing, when
    /// there is none to offer — the error of the block says why) and "Check status".
    /// </summary>
    /// <param name="view">What the block shows.</param>
    /// <param name="actions">Builder of the actions of the block.</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <returns>Markup of the step.</returns>
    private static StepMarkup RenderPush(ClaimCompletionEmailBlockView view, BlockActions actions, IConfirmationPromptLocalizer localizer)
    {
        var language = view.Language;
        var instruction = MailAlternative(view) is not { } mailWay
            ? PushInstructionText
            : MailContentOf(view, mailWay).CarriesCode() ? PushInstructionOrCodeText : PushInstructionOrLinkText;
        var html = new StringBuilder()
            .Append(Instruction(localizer.ResolveOrBaseText(instruction, language)));

        if (view.PushMailto is { Length: > 0 } mailto)
        {
            html.Append(Field(
                "<a class=\"" + ButtonClass + "\" href=\"" + Encode(mailto) + "\">"
                + Encode(localizer.ResolveOrBaseText(PushSendText, language))
                + "</a>"));
        }

        html.Append(CheckConfirmationButton(actions, localizer, language));

        return new StepMarkup(html.ToString(), HasCheckConfirmation: true);
    }

    /// <summary>
    /// Renders the way without verification: the address input with the mark of an address not confirmed in
    /// its right part. The step has no button of its own: "Continue" of the form saves the typed address.
    /// </summary>
    /// <param name="view">What the block shows.</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <returns>Markup of the step.</returns>
    private static StepMarkup RenderUnverified(ClaimCompletionEmailBlockView view, IConfirmationPromptLocalizer localizer) =>
        new(
            Field(Marked(
                AddressInput(view.EnteredAddress),
                Mark(confirmed: false, localizer.ResolveOrBaseText(NotConfirmedMarkText, view.Language)))),
            HasAddressInput: true);

    /// <summary>
    /// The read-only input of an obtained address, marked as confirmed or not. It has no name: the server
    /// holds or has recorded the address, and the form posts nothing of it.
    /// </summary>
    /// <param name="address">The obtained address, in full.</param>
    /// <param name="confirmed">Whether the address was confirmed.</param>
    /// <param name="language">Language of the texts of the block.</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <returns>Markup of the input with its mark.</returns>
    private static string RecordedInput(string address, bool confirmed, string language, IConfirmationPromptLocalizer localizer) =>
        Marked(
            "<input class=\"veriqa-input\" type=\"email\" readonly value=\"" + Encode(address) + "\""
            + " aria-labelledby=\"" + HeadingId + "\" />",
            Mark(confirmed, localizer.ResolveOrBaseText(confirmed ? ConfirmedMarkText : NotConfirmedMarkText, language)));

    /// <summary>
    /// An input with a mark in its right part.
    /// </summary>
    /// <param name="input">Markup of the input.</param>
    /// <param name="mark">Markup of the mark.</param>
    /// <returns>Markup of the pair.</returns>
    private static string Marked(string input, string mark) =>
        "<div class=\"" + MarkedInputClass + "\">" + input + mark + "</div>";

    /// <summary>
    /// The mark of whether an address is confirmed: an inline glyph with its text as the accessible name and as
    /// a tip shown while the mark is hovered or focused — it takes the focus, so a tap on a phone shows the
    /// tip too.
    /// </summary>
    /// <param name="confirmed">Whether the address is confirmed.</param>
    /// <param name="text">Localized text of the mark, not yet encoded.</param>
    /// <returns>Markup of the mark.</returns>
    private static string Mark(bool confirmed, string text) =>
        "<span class=\"" + MarkClass + " " + (confirmed ? MarkConfirmedClass : MarkNotConfirmedClass) + "\""
        + " tabindex=\"0\" role=\"img\" aria-label=\"" + Encode(text) + "\">"
        + "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\""
        + " stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">"
        + (confirmed ? ConfirmedGlyph : NotConfirmedGlyph)
        + "</svg>"
        + "<span class=\"" + MarkTipClass + "\" aria-hidden=\"true\">" + Encode(text) + "</span>"
        + "</span>";

    /// <summary>
    /// The address step of a way: the address input, named by the heading of the block, and the button of
    /// the step. The button is always on screen. The script of the page disables it while the input is empty;
    /// the server renders it enabled, so a page without the script still sends the mail, and the server
    /// judges the address (SPEC-041 L112).
    /// </summary>
    /// <param name="operation">The action of the button (<see cref="ClaimCompletionFormFields.EmailOperations"/>).</param>
    /// <param name="value">Address the input is filled with; null — it is empty.</param>
    /// <param name="buttonText">Localized text of the button.</param>
    /// <param name="actions">Builder of the actions of the block.</param>
    /// <returns>Markup of the step.</returns>
    private static StepMarkup AddressStep(string operation, string? value, string buttonText, BlockActions actions) =>
        new(
            Field(AddressInput(value) + actions.Button(
                operation, buttonText, ButtonClass, AddressActionButtonId)),
            HasAddressInput: true);

    /// <summary>
    /// The address input of a way, named by the heading of the block.
    /// </summary>
    /// <param name="value">Address the input is filled with; null — it is empty.</param>
    /// <returns>Markup of the input.</returns>
    private static string AddressInput(string? value) =>
        "<input class=\"veriqa-input\" id=\"" + AddressInputId
        + "\" name=\"" + ClaimCompletionFormFields.EmailAddress
        + "\" type=\"email\" autocomplete=\"email\""
        + (value is null ? string.Empty : " value=\"" + Encode(value) + "\"")
        + BlockActions.InputAttributes
        + " aria-labelledby=\"" + HeadingId + "\""
        + " />";

    /// <summary>
    /// The button "Check status" of a confirmation made elsewhere — the secondary variant, rendered hidden:
    /// the page presses it by itself, and its script shows it only when the page cannot learn of the
    /// confirmation.
    /// </summary>
    /// <param name="actions">Builder of the actions of the block.</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <param name="language">Language of the texts of the block.</param>
    /// <returns>Markup of the button.</returns>
    private static string CheckConfirmationButton(
        BlockActions actions,
        IConfirmationPromptLocalizer localizer,
        string language) =>
        Field(actions.Button(
            ClaimCompletionFormFields.EmailOperations.Refresh,
            localizer.ResolveOrBaseText(CheckConfirmationText, language),
            SecondaryButtonClass,
            CheckConfirmationButtonId,
            hidden: true));

    /// <summary>
    /// Renders the other available ways under the divider "OR": a text link each, posting the name of its way.
    /// The way "confirm via email" is one link, whichever of its two methods names it, named by what its mail
    /// carries.
    /// </summary>
    /// <param name="view">What the block shows.</param>
    /// <param name="actions">Builder of the actions of the block.</param>
    /// <param name="localizer">Localizer of the texts of the block.</param>
    /// <returns>Markup of the divider and the links.</returns>
    private static string RenderAlternatives(ClaimCompletionEmailBlockView view, BlockActions actions, IConfirmationPromptLocalizer localizer)
    {
        var language = view.Language;
        var links = new StringBuilder();
        var mailWayShown = false;

        foreach (var method in view.Alternatives)
        {
            string? text;

            if (ClaimEmailMailContents.IsMailMethod(method))
            {
                // One link for the two mail methods, even when a view lists both.
                text = mailWayShown
                    ? null
                    : localizer.ResolveOrBaseText(MailContentOf(view, method).CarriesCode() ? MethodCodeText : MethodLinkText, language);
                mailWayShown = true;
            }
            else
            {
                text = method switch
                {
                    EmailCompletionMethod.Push => localizer.ResolveOrBaseText(MethodPushText, language),
                    EmailCompletionMethod.Unverified => localizer.ResolveOrBaseText(MethodUnverifiedText, language),
                    _ => null
                };
            }

            if (text is not null)
            {
                links.Append(actions.TextLink(ClaimCompletionFormFields.EmailMethod, method.ToString(), text));
            }
        }

        // A list with nothing to show under it gets no divider either.
        return links.Length == 0
            ? string.Empty
            : "<div class=\"veriqa-divider\">" + Encode(localizer.ResolveOrBaseText(OrText, language)) + "</div>" + links;
    }

    /// <summary>
    /// The method that names the way "confirm via email" among the other ways of the block.
    /// </summary>
    /// <param name="view">What the block shows.</param>
    /// <returns>The first mail method of the other ways; null — the way is not among them.</returns>
    private static EmailCompletionMethod? MailAlternative(ClaimCompletionEmailBlockView view) =>
        view.Alternatives
            .Where(ClaimEmailMailContents.IsMailMethod)
            .Select(method => (EmailCompletionMethod?)method)
            .FirstOrDefault();

    /// <summary>
    /// What the mail of the way "confirm via email" carries; a view that does not say is read by the method
    /// that names the way.
    /// </summary>
    /// <param name="view">What the block shows.</param>
    /// <param name="method">The method that names the way.</param>
    /// <returns>The content of the mail.</returns>
    private static ClaimEmailMailContent MailContentOf(ClaimCompletionEmailBlockView view, EmailCompletionMethod method) =>
        view.MailContent
        ?? (method is EmailCompletionMethod.MagicLink ? ClaimEmailMailContent.Link : ClaimEmailMailContent.Code);

    /// <summary>
    /// A line about an address: where the live code or link went, masked. The address stands out in bold.
    /// </summary>
    /// <param name="text">Localized text with the address slot.</param>
    /// <param name="address">The address; null — the slot stays empty.</param>
    /// <param name="continuation">Localized sentence the line goes on with; null — none.</param>
    /// <returns>Markup of the line.</returns>
    private static string AddressLine(string text, string? address, string? continuation = null)
    {
        var slot = text.IndexOf(AddressSlot, StringComparison.Ordinal);
        var line = slot < 0
            ? Encode(text)
            : Encode(text[..slot])
              + "<strong>" + Encode(address ?? string.Empty) + "</strong>"
              + Encode(text[(slot + AddressSlot.Length)..]);

        if (continuation is not null)
        {
            line += " " + Encode(continuation);
        }

        return "<p class=\"veriqa-instruction\">" + line + "</p>";
    }

    /// <summary>
    /// A line of text of the block.
    /// </summary>
    /// <param name="text">Localized text, not yet encoded.</param>
    /// <returns>Markup of the line.</returns>
    private static string Instruction(string text) =>
        "<p class=\"veriqa-instruction\">" + Encode(text) + "</p>";

    /// <summary>
    /// A group of the block that spaces itself from the next one — an input with its button, or a button.
    /// </summary>
    /// <param name="content">Markup of the group, already encoded.</param>
    /// <returns>Markup of the group.</returns>
    private static string Field(string content) =>
        "<div class=\"veriqa-field\">" + content + "</div>";

    /// <summary>
    /// HTML-encodes a value for text content and attribute values alike.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The encoded value.</returns>
    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    /// <summary>
    /// Markup of a step of the block.
    /// </summary>
    /// <param name="Html">Markup of the step.</param>
    /// <param name="HasAddressInput">Whether the step is an address step — it shows the address input
    /// (<see cref="AddressInputId"/>).</param>
    /// <param name="HasCheckConfirmation">Whether the step shows "Check status"
    /// (<see cref="CheckConfirmationButtonId"/>) — the mail is confirmed elsewhere.</param>
    private readonly record struct StepMarkup(
        string Html,
        bool HasAddressInput = false,
        bool HasCheckConfirmation = false);

    /// <summary>
    /// Builds the actions of the block: buttons of the form of the page that send it — the right fields it
    /// carries, the simple fields and the inputs of the block — to the action path of the block.
    /// </summary>
    /// <param name="actionPath">Path the actions are posted to, path base included, not yet encoded.</param>
    private sealed class BlockActions(string actionPath)
    {
        /// <summary>
        /// Target of every action, already encoded.
        /// </summary>
        private readonly string _action = Encode(actionPath);

        /// <summary>
        /// Attribute every input of the block carries besides its own: it belongs to the form of the page. It
        /// is not <c>required</c>: "Continue" of that form does not ask for an input of the block.
        /// </summary>
        public const string InputAttributes = " form=\"" + ClaimCompletionForm.FieldsFormId + "\"";

        /// <summary>
        /// A button of an action of the block.
        /// </summary>
        /// <param name="operation">The action (<see cref="ClaimCompletionFormFields.EmailOperations"/>).</param>
        /// <param name="buttonText">Localized text of the button.</param>
        /// <param name="buttonClass">Classes of the button.</param>
        /// <param name="buttonId">Element id of the button; null — the button has none.</param>
        /// <param name="hidden">Whether the button is rendered hidden; the script of the page may show it.</param>
        /// <returns>Markup of the button.</returns>
        public string Button(
            string operation,
            string buttonText,
            string buttonClass,
            string? buttonId = null,
            bool hidden = false) =>
            Submit(ClaimCompletionFormFields.EmailOperation, operation, buttonText, buttonClass, buttonId, hidden);

        /// <summary>
        /// A text link of the block that posts one field of its own — an action, or the name of a way, which
        /// the handler reads as the switch to that way.
        /// </summary>
        /// <param name="name">Name of the field the link posts.</param>
        /// <param name="value">Value of that field.</param>
        /// <param name="text">Localized text of the link.</param>
        /// <param name="linkClass">Classes of the link.</param>
        /// <returns>Markup of the link.</returns>
        public string TextLink(string name, string value, string text, string linkClass = TextLinkClass) =>
            Submit(name, value, text, linkClass, buttonId: null, hidden: false);

        /// <summary>
        /// A submit button of the form of the page that sends it to the action path of the block, posting one
        /// field of its own.
        /// </summary>
        /// <param name="name">Name of the field the button posts.</param>
        /// <param name="value">Value of that field.</param>
        /// <param name="text">Localized text of the button.</param>
        /// <param name="buttonClass">Classes of the button.</param>
        /// <param name="buttonId">Element id of the button; null — the button has none.</param>
        /// <param name="hidden">Whether the button is rendered hidden.</param>
        /// <returns>Markup of the button.</returns>
        private string Submit(string name, string value, string text, string buttonClass, string? buttonId, bool hidden) =>
            "<button type=\"submit\""
            + (buttonId is null ? string.Empty : " id=\"" + Encode(buttonId) + "\"")
            + (hidden ? " hidden" : string.Empty)
            + " class=\"" + buttonClass
            + "\" form=\"" + ClaimCompletionForm.FieldsFormId
            + "\" formaction=\"" + _action
            + "\" name=\"" + name
            + "\" value=\"" + Encode(value) + "\">"
            + Encode(text)
            + "</button>";
    }
}

/// <summary>
/// The rendered email block and what the form of the page takes from it.
/// </summary>
/// <param name="Html">Markup of the block.</param>
/// <param name="ContinueHtml">Markup of the block's own "Continue" — no way can obtain the email any more,
/// and that action ends it (E61); the form puts it into the row of the final actions in place of its own;
/// null — the block offers none.</param>
/// <param name="HasAddressInput">Whether the block shows the address input of an address step
/// (<see cref="ClaimCompletionEmailBlock.AddressInputId"/>) — the script of the page follows what is typed into
/// it.</param>
/// <param name="ConfirmationButtonId">Element id of the button the page presses when the hub of the sign-in
/// window says the address was confirmed elsewhere — "Check status"
/// (<see cref="ClaimCompletionEmailBlock.CheckConfirmationButtonId"/>) or, on a step without it while a link is
/// live, the hidden one (<see cref="ClaimCompletionEmailBlock.WatchConfirmationButtonId"/>); null — the block
/// waits for no confirmation made elsewhere.</param>
internal sealed record ClaimCompletionEmailBlockMarkup(
    string Html,
    string? ContinueHtml,
    bool HasAddressInput,
    string? ConfirmationButtonId);
