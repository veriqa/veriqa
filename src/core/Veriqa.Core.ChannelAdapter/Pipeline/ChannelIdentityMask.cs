// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;

using Veriqa.Core.Contracts;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The one home of the masks a channel account is shown by to the user — on a page, in a message, in
/// a log line of a channel adapter. One rule per kind of value: an email address keeps its first
/// character and its domain, a phone number keeps its last four digits, a name keeps the first character
/// of every word.
/// </summary>
/// <remarks>
/// The mask of the identifiers in the audit trail and the logs of the transaction engine is a separate
/// rule with a purpose of its own (correlation without disclosure) and does not live here.
/// </remarks>
public static class ChannelIdentityMask
{
    /// <summary>
    /// What the hidden part of a value is shown as.
    /// </summary>
    private const string HiddenPart = "***";

    /// <summary>
    /// Separator of the local part and the domain of an email address.
    /// </summary>
    private const char EmailAtSign = '@';

    /// <summary>
    /// Prefix of a channel username, dropped before a name is masked.
    /// </summary>
    private const char UsernamePrefix = '@';

    /// <summary>
    /// Leading sign of an international phone number, kept by the mask.
    /// </summary>
    private const char PhonePlusSign = '+';

    /// <summary>
    /// Character a hidden digit of the phone number is shown as.
    /// </summary>
    private const char PhoneMaskCharacter = '•';

    /// <summary>
    /// How many trailing digits of the phone number the mask leaves visible.
    /// </summary>
    private const int PhoneVisibleDigits = 4;

    /// <summary>
    /// Separator the masked words of a name are joined with.
    /// </summary>
    private const char WordSeparator = ' ';

    /// <summary>
    /// Masks an email address: first character of the local part + <c>***</c> + <c>@</c> + domain
    /// (<c>user@gmail.com</c> → <c>u***@gmail.com</c>); a domain holding an A-label is shown in Unicode
    /// (<see cref="EmailDomain.ToDisplay"/>).
    /// </summary>
    /// <param name="email">The email address.</param>
    /// <returns>The masked email; <c>***</c> for an empty value or a value without a local part.</returns>
    public static string MaskEmail(string? email) => MaskEmailOrNull(email) ?? HiddenPart;

    /// <summary>
    /// The rule of <see cref="MaskEmail"/> for a caller that has to tell "nothing to show" apart from a
    /// masked address — a slot value, which is absent rather than a bare <c>***</c>.
    /// </summary>
    /// <param name="email">The email address.</param>
    /// <returns>The masked email; null for an empty value or a value without a local part.</returns>
    internal static string? MaskEmailOrNull(string? email)
    {
        // The method masks the local part of the email to protect personal data
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        // LastIndexOf — as the Email adapter splits an address: for a quoted local part with '@' inside
        // ("a@b"@example.com) masking by the first '@' would leak part of the local part
        var atIndex = email.LastIndexOf(EmailAtSign);
        if (atIndex <= 0)
        {
            return null;
        }

        var local = email[..atIndex];

        // The address is kept with its domain as an A-label; the person is shown the name they typed.
        var domain = EmailDomain.ToDisplay(email[(atIndex + 1)..]);
        var firstChar = local.Length > 0 ? local[0].ToString() : string.Empty;

        return $"{firstChar}{HiddenPart}{EmailAtSign}{domain}";
    }

    /// <summary>
    /// Masks a phone number down to its last four digits: a leading <c>+</c> is kept, every other digit is
    /// shown as a bullet (U+2022) and every character but a digit is dropped; with four digits or fewer
    /// every digit is hidden (<c>+7 912 345-67-89</c> → <c>+•••••••6789</c>).
    /// </summary>
    /// <param name="phoneNumber">Phone number as the channel states it.</param>
    /// <returns>The masked number; null when the value is blank or holds no digit.</returns>
    public static string? MaskPhoneNumber(string? phoneNumber)
    {
        // The method keeps the digits only, hides all but the last four of them (all of them when there
        // are no more than four), and puts the plus sign back when the number started with it.
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return null;
        }

        var trimmed = phoneNumber.Trim();
        var digits = trimmed.Where(char.IsAsciiDigit).ToArray();

        if (digits.Length == 0)
        {
            return null;
        }

        var hidden = digits.Length > PhoneVisibleDigits ? digits.Length - PhoneVisibleDigits : digits.Length;
        var masked = new StringBuilder(digits.Length + 1);

        if (trimmed[0] == PhonePlusSign)
        {
            masked.Append(PhonePlusSign);
        }

        masked.Append(PhoneMaskCharacter, hidden);
        masked.Append(digits, hidden, digits.Length - hidden);

        return masked.ToString();
    }

    /// <summary>
    /// Masks a name — a username or a display name: every whitespace-separated word becomes its first
    /// character + <c>***</c>, the words joined by one space (<c>Ivan Petrov</c> → <c>I*** P***</c>). A
    /// leading <c>@</c> of the value is dropped first.
    /// </summary>
    /// <remarks>
    /// The first character is a whole text element: a surrogate pair or an emoji sequence opening a word
    /// is kept whole rather than cut in half.
    /// </remarks>
    /// <param name="value">The name.</param>
    /// <returns>The masked name; null when nothing but whitespace and <c>@</c> is left.</returns>
    public static string? MaskName(string? value)
    {
        // The method splits the value into words, keeps the first text element of each and joins the
        // masked words with a single space.
        var name = value?.Trim().TrimStart(UsernamePrefix);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var masked = new StringBuilder(name.Length + (words.Length * HiddenPart.Length));

        foreach (var word in words)
        {
            if (masked.Length > 0)
            {
                masked.Append(WordSeparator);
            }

            masked.Append(word, 0, StringInfo.GetNextTextElementLength(word));
            masked.Append(HiddenPart);
        }

        return masked.ToString();
    }
}
