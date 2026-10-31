// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net.Mail;
using System.Text;

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// The form of an address entered in the email block of the web step: one rule for every way that takes it,
/// with the mail side and without.
/// </summary>
internal static class ClaimEmailAddress
{
    /// <summary>
    /// The longest address taken, in UTF-8 octets: a path is at most 256 octets with its angle brackets
    /// (RFC 5321 §4.5.3.1.3), which leaves 254 for the address.
    /// </summary>
    private const int MaxAddressOctets = 254;

    /// <summary>
    /// The ASCII characters a local part admits besides letters and digits: the symbols of <c>atext</c> and
    /// the dot of a <c>dot-atom</c> (RFC 5322 §3.2.3).
    /// </summary>
    private const string LocalPartAsciiSymbols = "!#$%&'*+-/=?^_`{|}~.";

    /// <summary>
    /// The ASCII characters a domain admits besides letters and digits: the hyphen of a label and the dot
    /// between labels (RFC 1035 §2.3.1).
    /// </summary>
    private const string DomainAsciiSymbols = "-.";

    /// <summary>
    /// The code point ranges of the Unicode property Default_Ignorable_Code_Point (DerivedCoreProperties):
    /// characters rendered as nothing — among them combining grapheme joiner, variation selectors and the
    /// Hangul fillers, which Unicode classes as marks and letters.
    /// </summary>
    private static readonly (int First, int Last)[] DefaultIgnorableRanges =
    [
        (0x00AD, 0x00AD),
        (0x034F, 0x034F),
        (0x061C, 0x061C),
        (0x115F, 0x1160),
        (0x17B4, 0x17B5),
        (0x180B, 0x180F),
        (0x200B, 0x200F),
        (0x202A, 0x202E),
        (0x2060, 0x206F),
        (0x3164, 0x3164),
        (0xFE00, 0xFE0F),
        (0xFEFF, 0xFEFF),
        (0xFFA0, 0xFFA0),
        (0xFFF0, 0xFFF8),
        (0x1BCA0, 0x1BCA3),
        (0x1D173, 0x1D17A),
        (0xE0000, 0xE0FFF),
    ];

    /// <summary>
    /// Takes an entered address only when it is a bare address (addr-spec) the form admits (SPEC-041 N70).
    /// The text is trimmed and must be at most <see cref="MaxAddressOctets"/> UTF-8 octets; the parser must
    /// read it as an address equal to the text itself, so a display name, angle brackets, a comment or a list
    /// is refused rather than reduced to its mailbox. Every character must then be admitted by its part: the
    /// local part takes ASCII letters, digits and <see cref="LocalPartAsciiSymbols"/>, the domain takes ASCII
    /// letters, digits and <see cref="DomainAsciiSymbols"/>; beyond ASCII both take only letters, decimal
    /// digits and combining marks (the LetterDigits categories of RFC 5892 §2.1) outside
    /// <see cref="DefaultIgnorableRanges"/>. Anything else is refused — a quoted local part, a domain literal,
    /// whitespace, control, format, private-use or unassigned characters, symbols. The address becomes the
    /// recipient of a code or a link, the key of the send limits and the value of the <c>email</c> claim, so
    /// it admits only what can be seen and typed back.
    /// </summary>
    /// <param name="entered">The address as entered.</param>
    /// <returns>The trimmed address; null — the text is not a bare address the form admits.</returns>
    public static string? TakeBare(string entered)
    {
        var trimmed = entered.Trim();

        return trimmed.Length > 0
            && Encoding.UTF8.GetByteCount(trimmed) <= MaxAddressOctets
            && MailAddress.TryCreate(trimmed, out var parsed)
            && string.Equals(parsed.Address, trimmed, StringComparison.Ordinal)
            && IsAdmitted(parsed.User, LocalPartAsciiSymbols)
            && IsAdmitted(parsed.Host, DomainAsciiSymbols)
                ? trimmed
                : null;
    }

    /// <summary>
    /// Whether every character of a part of the address is admitted: an ASCII letter, digit or one of the
    /// part's symbols, or a non-ASCII letter, decimal digit or combining mark that is not default-ignorable.
    /// </summary>
    /// <param name="part">The local part or the domain, as the parser read it.</param>
    /// <param name="asciiSymbols">The ASCII characters the part admits besides letters and digits.</param>
    /// <returns>true when the part is admitted.</returns>
    private static bool IsAdmitted(string part, string asciiSymbols)
    {
        foreach (var rune in part.EnumerateRunes())
        {
            var admitted = rune.IsAscii
                ? Rune.IsLetterOrDigit(rune) || asciiSymbols.Contains((char)rune.Value, StringComparison.Ordinal)
                : IsLetterDigit(rune) && !IsDefaultIgnorable(rune);

            if (!admitted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a character belongs to the LetterDigits categories of RFC 5892 §2.1: a letter other than a
    /// titlecase one, a decimal digit or a combining mark.
    /// </summary>
    /// <param name="rune">The character.</param>
    /// <returns>true for a letter, a decimal digit or a combining mark.</returns>
    private static bool IsLetterDigit(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark;

    /// <summary>
    /// Whether a character is default-ignorable (<see cref="DefaultIgnorableRanges"/>).
    /// </summary>
    /// <param name="rune">The character.</param>
    /// <returns>true for a default-ignorable character.</returns>
    private static bool IsDefaultIgnorable(Rune rune)
    {
        foreach (var (first, last) in DefaultIgnorableRanges)
        {
            if (rune.Value >= first && rune.Value <= last)
            {
                return true;
            }
        }

        return false;
    }
}
