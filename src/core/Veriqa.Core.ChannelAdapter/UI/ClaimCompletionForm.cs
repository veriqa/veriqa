// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net;
using System.Text;

using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The form of the web step of the claims completion (SPEC-041 L108, L109, L52): the fragment a page puts into its
/// card — the channel account the sign-in was confirmed with, the simple fields still missing and the
/// actions "Continue" and "Cancel sign-in". One form for every surface of the step; the page around
/// it (title, head, scripts) belongs to the surface. "Continue" is always shown and never disabled: it
/// saves the fields and skips an optional email nothing was typed for, and when the email of the page is
/// still to be confirmed the page it is answered with says so in the email block, in text. Beside simple
/// fields alone "Continue" stands under them; on a page with the email block it stands in the row of the
/// remaining actions, before "Cancel sign-in" — unless the block offers its own (no way can obtain the
/// email any more), which then stands there in its place. Once nothing but the simple fields is left —
/// no email block, or one that has recorded the email — the same button is named by the action that closes
/// the form: "Finish sign-in", or "Confirm" for a confirmation.
/// </summary>
/// <remarks>
/// The form is built from the primitives of the service pages (SPEC-015): a field is the email entry field
/// with a visible label (§4.10, §4.14) — a native drop-down list of the same class for a choice — and the
/// actions are the button and its secondary variant (§4.6). Every
/// value of the model is HTML-encoded, so no markup a user typed or a configuration stated is ever rendered
/// (SPEC-041 N70); the two markup slots of the model are the only exception, and the core fills them.
/// </remarks>
internal static class ClaimCompletionForm
{
    /// <summary>
    /// Element id of the form of the page — the simple fields and the right fields. The email block of the
    /// same page sends its actions with this form, so the values typed into the fields travel with them;
    /// with the email block the form is rendered even when there is no simple field to show.
    /// </summary>
    public const string FieldsFormId = "veriqa-claim-fields";

    /// <summary>
    /// Element id of "Continue" — always the main variant. The script of the page presses it on Enter in the
    /// empty address input of the email block and in the address input of the way without verification.
    /// </summary>
    public const string ContinueButtonId = "veriqa-claim-continue";

    /// <summary>
    /// Text of the button that submits the fields while something else is left on the page — the email block
    /// has not recorded the email.
    /// </summary>
    private const string SubmitButtonText = "Continue";

    /// <summary>
    /// Text of the button that submits the fields and closes the form of a sign-in.
    /// </summary>
    private const string FinishSignInButtonText = "Finish sign-in";

    /// <summary>
    /// Text of the button that submits the fields and closes the form of a confirmation (s2s): the words that
    /// name the action differ by the type of the transaction, the action itself does not.
    /// </summary>
    private const string FinishConfirmationButtonText = "Confirm";

    /// <summary>
    /// Class of the main button — of the form and of the email block alike.
    /// </summary>
    public const string ButtonClass = "veriqa-btn";

    /// <summary>
    /// Class that turns a button into the secondary variant — "Cancel sign-in" and "Check status" of the
    /// email block.
    /// </summary>
    public const string SecondaryModifierClass = "veriqa-btn-decline";

    /// <summary>
    /// Classes of the secondary button.
    /// </summary>
    public const string SecondaryButtonClass = ButtonClass + " " + SecondaryModifierClass;

    /// <summary>
    /// Text of the button that cancels the sign-in.
    /// </summary>
    private const string CancelButtonText = "Cancel sign-in";

    /// <summary>
    /// The default button of the form of the fields when "Continue" stands outside it: a submit button with no
    /// name, first in the form, kept out of view, of the tab order and of the accessibility tree, so that
    /// Enter in a field sends the form as "Continue" does.
    /// </summary>
    private const string DefaultSubmitButton =
        "<button type=\"submit\" class=\"veriqa-default-submit\" tabindex=\"-1\" aria-hidden=\"true\"></button>";

    /// <summary>
    /// Mark appended to the label of an optional field — and to the heading of an optional email block:
    /// a required field is the rule of the form, so the exception is what the label points out.
    /// </summary>
    internal const string OptionalMarkText = "optional";

    /// <summary>
    /// Separator between a label and its optional mark.
    /// </summary>
    private const string OptionalMarkSeparator = "\u00B7";

    /// <summary>
    /// Separator between the channel name, the identifier and the display name of the account.
    /// </summary>
    private const string AccountPartSeparator = " · ";

    /// <summary>
    /// Prefix of the element id of a field; the index of the field follows it. The claim name is not part
    /// of the id: an id and the label pointing at it must match exactly, whatever characters a claim name
    /// carries.
    /// </summary>
    private const string FieldIdPrefix = "veriqa-claim-";

    /// <summary>
    /// Suffix of the element id of the error of a field, appended to the id of the field.
    /// </summary>
    private const string FieldErrorIdSuffix = "-error";

    /// <summary>
    /// Input type of a text field.
    /// </summary>
    private const string TextInputType = "text";

    /// <summary>
    /// Input type of a date field — the browser posts it as <c>YYYY-MM-DD</c>.
    /// </summary>
    private const string DateInputType = "date";

    /// <summary>
    /// Renders the fragment of the form.
    /// </summary>
    /// <param name="model">What the form shows.</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <param name="localizer">Localizer of the texts of the form.</param>
    /// <returns>Markup of the fragment, to be put into the card of the page.</returns>
    public static string Render(ClaimCompletionFormModel model, string pathBase, IConfirmationPromptLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(pathBase);
        ArgumentNullException.ThrowIfNull(localizer);

        // The method lays the fragment out top-down: the notice slot, the account line, the error of the
        // submission, the form of the fields, the email block slot and the row of the remaining actions.
        // "Cancel sign-in" is the last action of that row on every surface, so leaving the sign-in never
        // stands above the fields; the account line carries no action. With the email block "Continue"
        // stands in that row, before "Cancel sign-in": a page with no simple field puts its whole form —
        // the right fields and "Continue" — there, and a page with simple fields keeps them in its form
        // above the block and puts only "Continue" there. When the block offers its own "Continue", the
        // form carries no "Continue": the block's one stands in that row in its place.

        var rightFields = RenderRightFields(model.RightFields);
        var cancelForm = RenderCancelForm(pathBase + model.CancelPath, rightFields, model.Language, localizer);
        var hasEmailBlock = !string.IsNullOrEmpty(model.EmailBlockHtml);
        var blockContinue = hasEmailBlock ? model.EmailBlockContinueHtml : null;

        // With the email block below the simple fields, "Continue" goes down into the row of the remaining
        // actions, past the block, and reaches the form of the fields by its form attribute.
        var continueInRow = model.Fields.Count > 0 && hasEmailBlock && blockContinue is null;

        // The button is named by what it does: "Continue" while the email block has not recorded the email,
        // otherwise the action that closes the form.
        var continueButton = new StringBuilder()
            .Append("<button type=\"submit\" id=\"").Append(ContinueButtonId)
            .Append(continueInRow ? "\" form=\"" + FieldsFormId : string.Empty)
            .Append("\" class=\"").Append(ButtonClass).Append("\">")
            .Append(Encode(localizer.ResolveOrBaseText(
                !model.Finishes ? SubmitButtonText
                : model.Confirmation ? FinishConfirmationButtonText
                : FinishSignInButtonText,
                model.Language)))
            .Append("</button>")
            .ToString();
        var rowForm = string.Empty;

        var html = new StringBuilder();

        html.Append(model.NoticeHtml);

        if (model.Account is not null)
        {
            html.Append("<p class=\"veriqa-instruction\">").Append(Encode(AccountLine(model.Account))).Append("</p>");
        }

        if (!string.IsNullOrEmpty(model.FormError))
        {
            html.Append("<p class=\"veriqa-msg err\" role=\"status\">").Append(Encode(model.FormError)).Append("</p>");
        }

        // With the email block the form is shared by the actions of the block, and the browser sends it by
        // itself on Enter in any of its inputs — by its default button, whichever input that was. A browser
        // check would then stop an action of the block on a simple field still empty, or "Continue" on a
        // half-typed address of the block; so the browser checks nothing there, and the server judges the
        // fields and the inputs of the block alike.
        var formStart = new StringBuilder()
            .Append("<form method=\"post\" id=\"").Append(FieldsFormId)
            .Append("\" action=\"").Append(Encode(pathBase + model.SubmitPath)).Append('"')
            .Append(hasEmailBlock ? " novalidate" : string.Empty)
            .Append('>')
            .Append(rightFields)
            .ToString();

        if (model.Fields.Count > 0)
        {
            // Enter in a field sends the form by its default button — the first submit button of the form in
            // tree order — which, with "Continue" down in the row, would be a button of the email block; the
            // hidden default button, first in the form, keeps Enter on "Continue".
            html.Append(formStart)
                .Append(continueInRow ? DefaultSubmitButton : string.Empty)
                .Append("<div class=\"veriqa-fields\">");

            var optionalMark = OptionalMark(localizer.ResolveOrBaseText(OptionalMarkText, model.Language));

            for (var index = 0; index < model.Fields.Count; index++)
            {
                html.Append(RenderField(model.Fields[index], index, optionalMark));
            }

            html.Append("</div>");

            if (continueInRow)
            {
                html.Append("</form>");
                rowForm = RowAction(continueButton);
            }
            else if (blockContinue is not null)
            {
                html.Append("</form>");
                rowForm = RowAction(blockContinue);
            }
            else
            {
                html.Append(continueButton)
                    .Append("</form>");
            }
        }
        else if (blockContinue is not null)
        {
            html.Append(formStart).Append("</form>");
            rowForm = RowAction(blockContinue);
        }
        else if (hasEmailBlock)
        {
            rowForm = formStart + continueButton + "</form>";
        }

        html.Append(model.EmailBlockHtml);

        html.Append(RenderActionsRow(rowForm, cancelForm));

        return html.ToString();
    }

    /// <summary>
    /// Renders "Cancel sign-in" on its own — the row of actions of a page that shows no form of the web
    /// step yet lets the sign-in be cancelled (SPEC-041 L109): the same form of a single action, the same
    /// text and the same look as the action of <see cref="Render"/>.
    /// </summary>
    /// <param name="cancelPath">Path the cancellation is posted to, without the path base.</param>
    /// <param name="rightFields">Hidden fields that give the browser the right to act on the transaction.</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <param name="language">Content language of the page.</param>
    /// <param name="localizer">Localizer of the texts of the form.</param>
    /// <returns>Markup of the row, to be put into the card of the page.</returns>
    public static string RenderCancelAction(
        string cancelPath,
        IReadOnlyList<KeyValuePair<string, string>> rightFields,
        string pathBase,
        string language,
        IConfirmationPromptLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(cancelPath);
        ArgumentNullException.ThrowIfNull(rightFields);
        ArgumentNullException.ThrowIfNull(pathBase);
        ArgumentNullException.ThrowIfNull(localizer);

        return RenderActionsRow(
            RenderCancelForm(pathBase + cancelPath, RenderRightFields(rightFields), language, localizer));
    }

    /// <summary>
    /// Renders the form of "Cancel sign-in" — the one markup of that action for <see cref="Render"/> and
    /// <see cref="RenderCancelAction"/>.
    /// </summary>
    /// <param name="action">Target of the form, path base included.</param>
    /// <param name="rightFields">Markup of the right fields.</param>
    /// <param name="language">Content language of the page.</param>
    /// <param name="localizer">Localizer of the texts of the form.</param>
    /// <returns>Markup of the form.</returns>
    private static string RenderCancelForm(
        string action,
        string rightFields,
        string language,
        IConfirmationPromptLocalizer localizer) =>
        RenderActionForm(action, rightFields, localizer.ResolveOrBaseText(CancelButtonText, language));

    /// <summary>
    /// Wraps a button that stands in the row of the actions outside a form of its own, so that it shares the
    /// row as the forms do — in equal parts, whatever the paddings of the button.
    /// </summary>
    /// <param name="button">Markup of the button.</param>
    /// <returns>Markup of the action.</returns>
    private static string RowAction(string button) => "<div class=\"veriqa-field\">" + button + "</div>";

    /// <summary>
    /// Wraps the forms of the actions into their row; no row when there is no action to show.
    /// </summary>
    /// <param name="actionForms">Markup of the forms of the row, in the order shown; an empty one — no such action.</param>
    /// <returns>Markup of the row, or an empty string.</returns>
    private static string RenderActionsRow(params string[] actionForms)
    {
        var forms = string.Concat(actionForms);

        return forms.Length > 0
            ? "<div class=\"veriqa-actions\">" + forms + "</div>"
            : string.Empty;
    }

    /// <summary>
    /// Markup of the mark of an optional field, appended to its label: the separator and the localized word.
    /// </summary>
    /// <param name="localizedText">The mark, already localized.</param>
    /// <returns>Markup of the mark, its leading space included.</returns>
    internal static string OptionalMark(string localizedText) =>
        $" <span class=\"veriqa-field-optional\">{OptionalMarkSeparator} {Encode(localizedText)}</span>";

    /// <summary>
    /// Renders one field: the visible label, the control — an input for a text and a date, a native
    /// drop-down list for a choice — and the error under it.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="index">Index of the field in the form — the tail of its element id.</param>
    /// <param name="optionalMark">Markup of the mark of an optional field (<see cref="OptionalMark"/>).</param>
    /// <returns>Markup of the field.</returns>
    private static string RenderField(ClaimCompletionFieldView field, int index, string optionalMark)
    {
        // The label names the input by its id; the optional mark is read with the label, while the required
        // state of the other fields is read off the input itself. A field with an error points at the error
        // and is marked invalid.
        var id = FieldIdPrefix + index.ToString(CultureInfo.InvariantCulture);
        var errorId = id + FieldErrorIdSuffix;
        var hasError = !string.IsNullOrEmpty(field.Error);

        var html = new StringBuilder()
            .Append("<div class=\"veriqa-field\">")
            .Append("<label class=\"veriqa-field-label\" for=\"").Append(id).Append("\">")
            .Append(Encode(field.Label));

        if (!field.Required)
        {
            html.Append(optionalMark);
        }

        html.Append("</label>");

        // The attributes the two controls share: the required state and the pointer at the error.
        var state = new StringBuilder();

        if (field.Required)
        {
            state.Append(" required");
        }

        if (hasError)
        {
            state.Append(" aria-invalid=\"true\" aria-describedby=\"").Append(errorId).Append('"');
        }

        var name = Encode(ClaimCompletionFormFields.ClaimValueName(field.Claim));

        if (field.Kind is ClaimCompletionFieldKind.Choice)
        {
            // The list opens with an empty option that carries no value: nothing is chosen for the user,
            // an optional field can be left empty, and a required one is refused by the browser until a
            // value is picked. The option the user chose before is shown chosen again.
            html.Append("<select class=\"veriqa-input\" id=\"").Append(id)
                .Append("\" name=\"").Append(name).Append('"')
                .Append(state)
                .Append('>')
                .Append("<option value=\"\"></option>");

            foreach (var choice in field.Choices)
            {
                html.Append("<option value=\"").Append(Encode(choice.Value)).Append('"');

                if (string.Equals(choice.Value, field.Value, StringComparison.Ordinal))
                {
                    html.Append(" selected");
                }

                html.Append('>').Append(Encode(choice.Label)).Append("</option>");
            }

            html.Append("</select>");
        }
        else
        {
            var isDate = field.Kind is ClaimCompletionFieldKind.Date;

            html.Append("<input class=\"veriqa-input\" id=\"").Append(id)
                .Append("\" name=\"").Append(name)
                .Append("\" type=\"").Append(isDate ? DateInputType : TextInputType)
                .Append("\" value=\"").Append(Encode(field.Value ?? string.Empty)).Append('"');

            if (!isDate && field.MaxLength is { } maxLength)
            {
                html.Append(" maxlength=\"").Append(maxLength.ToString(CultureInfo.InvariantCulture)).Append('"');
            }

            html.Append(state).Append(" />");
        }

        if (hasError)
        {
            html.Append("<p class=\"veriqa-msg err\" id=\"").Append(errorId).Append("\" role=\"status\">")
                .Append(Encode(field.Error!))
                .Append("</p>");
        }

        return html.Append("</div>").ToString();
    }

    /// <summary>
    /// Renders a form of a single action: the right fields and one secondary button.
    /// </summary>
    /// <param name="action">Target of the form, path base included.</param>
    /// <param name="rightFields">Markup of the right fields.</param>
    /// <param name="buttonText">Localized text of the button.</param>
    /// <returns>Markup of the form.</returns>
    private static string RenderActionForm(string action, string rightFields, string buttonText) =>
        new StringBuilder()
            .Append("<form method=\"post\" action=\"").Append(Encode(action)).Append("\">")
            .Append(rightFields)
            .Append("<button type=\"submit\" class=\"").Append(SecondaryButtonClass).Append("\">").Append(Encode(buttonText)).Append("</button>")
            .Append("</form>")
            .ToString();

    /// <summary>
    /// Renders the right fields as hidden inputs.
    /// </summary>
    /// <param name="rightFields">Names and values of the right fields.</param>
    /// <returns>Markup of the hidden inputs.</returns>
    private static string RenderRightFields(IReadOnlyList<KeyValuePair<string, string>> rightFields)
    {
        var html = new StringBuilder();

        foreach (var (name, value) in rightFields)
        {
            html.Append("<input type=\"hidden\" name=\"").Append(Encode(name))
                .Append("\" value=\"").Append(Encode(value)).Append("\" />");
        }

        return html.ToString();
    }

    /// <summary>
    /// Composes the line of the account: the channel, then the identifier, then the display name — the
    /// parts the account has.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <returns>Text of the line, not yet encoded.</returns>
    private static string AccountLine(ClaimCompletionAccountView account) =>
        string.Join(
            AccountPartSeparator,
            new[] { account.ChannelName, account.Identifier, account.DisplayName }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// HTML-encodes a value for text content and attribute values alike.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The encoded value.</returns>
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
