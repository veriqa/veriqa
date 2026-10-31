// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Veriqa.Core.Contracts;

/// <summary>
/// The E.164 rule of the core, as code: the one place a phone number is brought to its canonical form
/// and judged against it. A channel adapter hands the core a number in E.164 with the leading plus
/// (SPEC-003 CA-009) — bringing it there is the adapter's job, and this is what it brings it with; the
/// core takes a number only when it already is that form and never repairs one that is not.
/// </summary>
public static class E164PhoneNumber
{
    /// <summary>
    /// Maximum number of digits an E.164 number carries (ITU-T E.164).
    /// </summary>
    private const int MaxDigits = 15;

    /// <summary>
    /// Characters a phone number is written with for readability. They carry no value and are dropped
    /// before the number is judged.
    /// </summary>
    private const string Separators = " -().";

    /// <summary>
    /// Brings a phone number to E.164: whitespace around it and readability separators are dropped,
    /// and what remains has to be an optional leading plus and 1 to 15 digits whose first one is not
    /// zero.
    /// </summary>
    /// <param name="value">The number as it was stated.</param>
    /// <param name="normalized">The canonical form, always with the leading plus.</param>
    /// <returns><see langword="false"/> when the value is not a number in any spelling.</returns>
    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var body = trimmed.StartsWith('+') ? trimmed.AsSpan(1) : trimmed.AsSpan();
        var digits = new StringBuilder(MaxDigits + 1);
        digits.Append('+');

        foreach (var character in body)
        {
            if (Separators.Contains(character, StringComparison.Ordinal))
            {
                continue;
            }

            if (!char.IsAsciiDigit(character) || digits.Length > MaxDigits)
            {
                return false;
            }

            digits.Append(character);
        }

        // A country code never starts with zero, so a leading zero means a national spelling — a
        // different number space, which this rule is not entitled to guess the country of.
        if (digits.Length is 1 || digits[1] is '0')
        {
            return false;
        }

        normalized = digits.ToString();

        return true;
    }

    /// <summary>
    /// Whether a value already is the canonical E.164 form: <see cref="TryNormalize"/> leaves it exactly
    /// as it stands.
    /// </summary>
    /// <param name="value">The number as it was handed over.</param>
    /// <returns>
    /// <see langword="false"/> for a value with no E.164 form and for one the normalization would
    /// change — a missing leading plus, readability separators, whitespace around it.
    /// </returns>
    public static bool IsCanonical(string? value) =>
        TryNormalize(value, out var normalized) && string.Equals(normalized, value, StringComparison.Ordinal);
}
