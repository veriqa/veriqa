// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// What a simple field of the form of the claims completion takes (SPEC-041 C95) — the control it is
/// rendered as and the rule its value is judged by.
/// </summary>
internal enum ClaimCompletionFieldKind
{
    /// <summary>
    /// Free text, bounded by the maximum length of the field.
    /// </summary>
    Text,

    /// <summary>
    /// A calendar date (<c>YYYY-MM-DD</c>).
    /// </summary>
    Date,

    /// <summary>
    /// One value out of the list of the field.
    /// </summary>
    Choice
}

/// <summary>
/// One value a field of the <see cref="ClaimCompletionFieldKind.Choice"/> kind offers.
/// </summary>
/// <param name="Value">Value the claim receives when the user picks it.</param>
/// <param name="Label">Localized caption of the value.</param>
internal sealed record ClaimCompletionChoiceView(string Value, string Label);

/// <summary>
/// One simple field of the form of the claims completion (SPEC-041 R13).
/// </summary>
/// <param name="Claim">Name of the claim the field collects.</param>
/// <param name="Label">Localized label of the field.</param>
/// <param name="Required">Whether the claim is required — an empty field is then refused.</param>
/// <param name="Kind">What the field takes: text, a date or one value out of a list.</param>
/// <param name="MaxLength">Maximum length of a text value; null for a date and a choice.</param>
/// <param name="Choices">Values a choice offers, in the order shown; empty for a text and a date.</param>
/// <param name="Value">
/// Value the user entered, shown again when the page is rendered in answer to a request that posted it;
/// null — none.
/// </param>
/// <param name="Error">Localized error of the field; null — none.</param>
internal sealed record ClaimCompletionFieldView(
    string Claim,
    string Label,
    bool Required,
    ClaimCompletionFieldKind Kind,
    int? MaxLength,
    IReadOnlyList<ClaimCompletionChoiceView> Choices,
    string? Value,
    string? Error);
