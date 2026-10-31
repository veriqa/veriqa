// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// Rules a submitted value of a simple field of the claims completion is accepted by (SPEC-041 R13, N70):
/// trimmed at the edges, an empty value is a skip of an optional field and an error of a required one; a
/// value carrying markup characters or invisible control and format characters is refused; a text is bounded
/// by its maximum length, a date must be <c>YYYY-MM-DD</c> (OIDC Core §5.1, <c>birthdate</c>), a choice must be
/// one of the values its field offers.
/// </summary>
internal static class ClaimCompletionFieldRules
{
    /// <summary>
    /// Error of an empty required field.
    /// </summary>
    private const string RequiredErrorText = "This field is required.";

    /// <summary>
    /// Error of a value carrying characters that are not accepted.
    /// </summary>
    private const string ForbiddenCharactersErrorText = "The value contains characters that are not allowed.";

    /// <summary>
    /// Error of a text longer than the field admits.
    /// </summary>
    private const string TooLongErrorText = "The value is too long.";

    /// <summary>
    /// Error of a date not in the <c>YYYY-MM-DD</c> form.
    /// </summary>
    private const string InvalidDateErrorText = "Enter the date in the format YYYY-MM-DD.";

    /// <summary>
    /// Error of a value that is not one of the values a choice offers.
    /// </summary>
    private const string InvalidChoiceErrorText = "Choose one of the listed values.";

    /// <summary>
    /// Form of a date value — the one a date input posts and the one the claim carries.
    /// </summary>
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Opening angle bracket — with the closing one, what makes a value markup.
    /// </summary>
    private const int LessThanSign = '<';

    /// <summary>
    /// Closing angle bracket.
    /// </summary>
    private const int GreaterThanSign = '>';

    /// <summary>
    /// Zero-width non-joiner — a format character that is part of the writing of several languages, so a
    /// name may legitimately carry it.
    /// </summary>
    private const int ZeroWidthNonJoiner = 0x200C;

    /// <summary>
    /// Zero-width joiner — accepted for the same reason as <see cref="ZeroWidthNonJoiner"/>.
    /// </summary>
    private const int ZeroWidthJoiner = 0x200D;

    /// <summary>
    /// Takes the value of a field as submitted: trimmed at the edges, empty when the field was not posted.
    /// </summary>
    /// <param name="posted">Posted value; null — the field was not posted.</param>
    /// <returns>The value the rules are applied to and a valid value is written as.</returns>
    public static string Normalize(string? posted) => (posted ?? string.Empty).Trim();

    /// <summary>
    /// Checks a normalized value of a field and names its error in the language of the page.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="value">Value after <see cref="Normalize"/>; empty — the field is skipped.</param>
    /// <param name="localizer">Localizer of the error texts.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>Localized error; null — the value is accepted (an empty value of an optional field
    /// included: it is written as a skip).</returns>
    public static string? Check(
        ClaimCompletionFieldView field,
        string value,
        IConfirmationPromptLocalizer localizer,
        string language)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(localizer);

        var choices = field.Choices.Select(choice => choice.Value);

        return FindError(field.Required, field.Kind, field.MaxLength, choices, value) switch
        {
            FieldError.Required => localizer.ResolveOrBaseText(RequiredErrorText, language),
            FieldError.ForbiddenCharacters => localizer.ResolveOrBaseText(ForbiddenCharactersErrorText, language),
            FieldError.InvalidDate => localizer.ResolveOrBaseText(InvalidDateErrorText, language),
            FieldError.InvalidChoice => localizer.ResolveOrBaseText(InvalidChoiceErrorText, language),
            FieldError.TooLong => localizer.ResolveOrBaseText(TooLongErrorText, language),
            _ => null
        };
    }

    /// <summary>
    /// Whether a value kept in the collected claims store would be accepted by the field its catalog row
    /// describes now (SPEC-041 L58) — the same rules <see cref="Check"/> applies to a submitted value, so a
    /// value the form would refuse today is asked for again instead of being handed out.
    /// </summary>
    /// <param name="completion">Completion of the catalog row of the claim.</param>
    /// <param name="value">Stored value.</param>
    /// <returns>true — the value is accepted.</returns>
    public static bool AcceptsStoredValue(ClaimCompletionDefinition completion, string value)
    {
        ArgumentNullException.ThrowIfNull(completion);

        var (kind, maxLength) = ShapeOf(completion);
        var choices = (completion.Options ?? []).Select(option => option.Value);

        // A stored value is a value, never a skip: an empty one is refused like an empty required field.
        // A stored choice the list of the row no longer offers is refused the same way.
        return FindError(required: true, kind, maxLength, choices, value) is FieldError.None;
    }

    /// <summary>
    /// The shape of the field of a catalog row: a text field is bounded by the maximum length of the row;
    /// a date and a choice have no length bound — the format of the one and the list of the other bound
    /// the value.
    /// </summary>
    /// <param name="completion">Completion of the catalog row of the claim.</param>
    /// <returns>What the field takes, and the maximum length of a text value.</returns>
    public static (ClaimCompletionFieldKind Kind, int? MaxLength) ShapeOf(ClaimCompletionDefinition completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        return completion.Format switch
        {
            ClaimFieldFormat.Date => (ClaimCompletionFieldKind.Date, null),
            ClaimFieldFormat.Choice => (ClaimCompletionFieldKind.Choice, null),
            _ => (ClaimCompletionFieldKind.Text, completion.MaxLength)
        };
    }

    /// <summary>
    /// The rules of a field: the rule a normalized value breaks, or none.
    /// </summary>
    /// <param name="required">Whether the field is required.</param>
    /// <param name="kind">What the field takes.</param>
    /// <param name="maxLength">Maximum length of a text value; null — unbounded (a date, a choice).</param>
    /// <param name="choices">Values a choice offers; not read for a text and a date.</param>
    /// <param name="value">Value after <see cref="Normalize"/>; empty — the field is skipped.</param>
    /// <returns>The broken rule; <see cref="FieldError.None"/> — the value is accepted.</returns>
    private static FieldError FindError(
        bool required,
        ClaimCompletionFieldKind kind,
        int? maxLength,
        IEnumerable<string> choices,
        string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // The method applies the rules in order: emptiness, then the form of the field. A choice is
        // judged by its list alone — a value the list states is accepted as it is stated. A text and a
        // date are refused forbidden characters first; a date is then parsed exactly, a text is bounded
        // by its length in UTF-16 code units.
        if (value.Length == 0)
        {
            return required ? FieldError.Required : FieldError.None;
        }

        if (kind is ClaimCompletionFieldKind.Choice)
        {
            return choices.Contains(value, StringComparer.Ordinal)
                ? FieldError.None
                : FieldError.InvalidChoice;
        }

        if (HasForbiddenCharacters(value))
        {
            return FieldError.ForbiddenCharacters;
        }

        if (kind is ClaimCompletionFieldKind.Date)
        {
            return DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? FieldError.None
                : FieldError.InvalidDate;
        }

        return maxLength is { } bound && value.Length > bound
            ? FieldError.TooLong
            : FieldError.None;
    }

    /// <summary>
    /// Whether a value carries a character that is not accepted: the angle brackets of markup, a control
    /// character or an invisible format character (bidirectional overrides, zero-width marks), except the
    /// two joiners of <see cref="ZeroWidthNonJoiner"/> and <see cref="ZeroWidthJoiner"/>.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>true when the value must be refused.</returns>
    private static bool HasForbiddenCharacters(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is LessThanSign or GreaterThanSign)
            {
                return true;
            }

            var category = Rune.GetUnicodeCategory(rune);

            if (category is UnicodeCategory.Control)
            {
                return true;
            }

            if (category is UnicodeCategory.Format && rune.Value is not (ZeroWidthNonJoiner or ZeroWidthJoiner))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A rule of a field a value breaks.
    /// </summary>
    private enum FieldError
    {
        /// <summary>No rule is broken.</summary>
        None,

        /// <summary>A required field is empty.</summary>
        Required,

        /// <summary>The value carries characters that are not accepted.</summary>
        ForbiddenCharacters,

        /// <summary>A date is not in the <c>YYYY-MM-DD</c> form.</summary>
        InvalidDate,

        /// <summary>A choice is not one of the values its field offers.</summary>
        InvalidChoice,

        /// <summary>A text is longer than the field admits.</summary>
        TooLong
    }
}
