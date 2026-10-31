// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// What the form of the web step of the claims completion shows (SPEC-041 L108, L109): the channel account the
/// sign-in was confirmed with, the simple fields still missing and the actions the page offers. One model
/// for every surface of the step — the page of the device the sign-in was started on and the page of the
/// confirming device render the same form over it.
/// </summary>
/// <remarks>
/// The model is built where the catalog of the claims lives (<see cref="IClaimCompletionFormModelBuilder"/>);
/// the form itself reads nothing but this model, so the rendering assembly needs neither the store nor the
/// catalog. Every text in it is already localized; every piece of markup in it
/// (<see cref="EmailBlockHtml"/>, <see cref="NoticeHtml"/>) is produced by the core with its values
/// already encoded.
/// </remarks>
internal sealed record ClaimCompletionFormModel
{
    /// <summary>
    /// Language the texts of the page are served in — the content language, already checked against the
    /// locale files.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Channel account the sign-in was confirmed with; set on the page of the device the sign-in was
    /// started on only, null elsewhere.
    /// </summary>
    public ClaimCompletionAccountView? Account { get; init; }

    /// <summary>
    /// Simple fields still missing, in the order of the missing claims of the transaction; may be empty —
    /// then the form of the fields is not rendered at all.
    /// </summary>
    public required IReadOnlyList<ClaimCompletionFieldView> Fields { get; init; }

    /// <summary>
    /// Whether the transaction is a confirmation (s2s) rather than a sign-in — it changes only the words that
    /// name the action, never what the form does.
    /// </summary>
    public bool Confirmation { get; init; }

    /// <summary>
    /// Whether the main button of the form closes it: nothing but the simple fields is left on the page — no
    /// email block, or one that has obtained the email. Set by the page that knows the email block; false in
    /// a freshly built model.
    /// </summary>
    public bool Finishes { get; init; }

    /// <summary>
    /// Hidden fields every form of the fragment carries — what gives the browser the right to act on the
    /// transaction (the session identifier and the anti-forgery token on the page of the initiating
    /// device). Set by the page that renders the form; empty in a freshly built model.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> RightFields { get; init; } = [];

    /// <summary>
    /// Path the fields are submitted to, without the path base.
    /// </summary>
    public required string SubmitPath { get; init; }

    /// <summary>
    /// Path "cancel sign-in" is posted to, without the path base.
    /// </summary>
    public required string CancelPath { get; init; }

    /// <summary>
    /// Markup of the email block, rendered after the fields; its inputs and buttons belong to the form of the
    /// page (<see cref="ClaimCompletionForm.FieldsFormId"/>). Null — no such block.
    /// </summary>
    public string? EmailBlockHtml { get; init; }

    /// <summary>
    /// Markup of the email block's own "Continue" — no way can obtain the email any more
    /// (<see cref="ClaimCompletionEmailBlockMarkup.ContinueHtml"/>); the form then draws no "Continue" of its
    /// own and puts this one into the row of the final actions. Null — the block offers none.
    /// </summary>
    public string? EmailBlockContinueHtml { get; init; }

    /// <summary>
    /// Markup of the notice shown at the top of the fragment; null — no notice.
    /// </summary>
    public string? NoticeHtml { get; init; }

    /// <summary>
    /// Localized error of the submission as a whole; null — none.
    /// </summary>
    public string? FormError { get; init; }
}
