// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

using Veriqa.Core.Contracts;

namespace Veriqa.Sample.DotNet.Showcase.StreamingTv;

/// <summary>
/// The card of the person watching, built from the claims Veriqa issued for them. Which fields a
/// card carries depends on the channel the person signed in through: Telegram gives a picture,
/// WhatsApp a phone number, Email an address — and a field the channel did not give stays null so
/// the page can leave the line out instead of drawing an empty one. The age and the favourite genre
/// come from no channel: Veriqa collected them on its form inside the sign-in.
/// </summary>
/// <param name="DisplayName">The <c>name</c> claim; always present for a signed-in person.</param>
/// <param name="GivenName">The <c>given_name</c> claim, or null.</param>
/// <param name="FamilyName">The <c>family_name</c> claim, or null.</param>
/// <param name="ChannelType">The <c>channel_type</c> claim: the channel this session came through.</param>
/// <param name="Avatar">The <c>picture</c> claim — an image data URI, or null.</param>
/// <param name="PhoneMasked">The <c>phone_number</c> claim, already masked, or null.</param>
/// <param name="Email">The <c>email</c> claim, or null.</param>
/// <param name="Username">The <c>preferred_username</c> claim — the Telegram @username — or null.</param>
/// <param name="ChannelUserIdMasked">The Telegram user id from <c>channel_user_id</c>, already masked, or
/// null for any other channel: there the identifier is the phone number or the address, which the
/// card shows already.</param>
/// <param name="Age">Age in full years, worked out from the <c>birthdate</c> claim, or null when the
/// claim is absent or is not a date. The date itself is not part of the card.</param>
/// <param name="PreferredGenre">The <c>preferred_genre</c> claim when it names a genre of the
/// catalogue, or null.</param>
public sealed record ViewerProfile(
    string DisplayName,
    string? GivenName,
    string? FamilyName,
    string? ChannelType,
    string? Avatar,
    string? PhoneMasked,
    string? Email,
    string? Username,
    string? ChannelUserIdMasked,
    int? Age,
    string? PreferredGenre)
{
    /// <summary>The form of a <c>birthdate</c> claim (OIDC Core §5.1).</summary>
    private const string BirthdateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Shortest number that keeps any digit visible. A number with fewer digits than this is masked
    /// whole: on a short number the country code and the last two digits are most of it, so showing
    /// them would show the number.
    /// </summary>
    private const int ShortestPartiallyMaskedDigits = 6;

    /// <summary>How many trailing digits stay readable — enough to recognise one's own number.</summary>
    private const int VisibleTrailingDigits = 2;

    /// <summary>How many characters stay readable at each end of a masked channel user id.</summary>
    private const int VisibleIdentifierEdge = 2;

    /// <summary>The character a hidden digit is replaced with.</summary>
    private const char MaskCharacter = '*';

    /// <summary>The character an international number starts with.</summary>
    private const char PlusSign = '+';

    /// <summary>
    /// Builds the card from the claims of the signed-in person. The phone number and the Telegram
    /// user id are masked HERE, on the server: the browser is never handed either in full, so no
    /// script of the page can leak one. The full <c>channel_user_id</c> addresses the step-up and
    /// stays on the server. The date of birth is treated the same way: the card carries the age the
    /// caller worked out from it (<see cref="AgeOn"/>), never the date.
    /// </summary>
    /// <param name="user">The signed-in person.</param>
    /// <param name="age">Age of the person in full years, or null when it is not known.</param>
    /// <param name="preferredGenre">Favourite genre of the person, already checked against the
    /// catalogue, or null.</param>
    /// <returns>The card, or null when the person is not signed in.</returns>
    public static ViewerProfile? FromClaims(ClaimsPrincipal user, int? age, string? preferredGenre)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Identity?.IsAuthenticated is not true)
        {
            return null;
        }

        // The name is the one claim Veriqa always issues; a channel that gave nothing else still
        // gives this, so the card is never nameless.
        var displayName = Value(user, VeriqaClaimTypes.Name) ?? user.Identity.Name ?? string.Empty;
        var channelType = Value(user, VeriqaClaimTypes.ChannelType);
        var isTelegram = channelType is not null
            && SampleIdentityTypes.ForChannel(channelType) == SampleIdentityTypes.TelegramUserId;

        return new ViewerProfile(
            displayName,
            Value(user, VeriqaClaimTypes.GivenName),
            Value(user, VeriqaClaimTypes.FamilyName),
            channelType,
            Value(user, VeriqaClaimTypes.Picture),
            MaskPhone(Value(user, VeriqaClaimTypes.PhoneNumber)),
            Value(user, VeriqaClaimTypes.Email),
            Value(user, VeriqaClaimTypes.PreferredUsername),
            isTelegram ? MaskIdentifier(Value(user, VeriqaClaimTypes.ChannelUserId)) : null,
            age,
            preferredGenre);
    }

    /// <summary>
    /// Age in full years on a given day, from a <c>birthdate</c> claim. The comparison is by date,
    /// not by year, so the age grows on the birthday itself. A value that is absent, is not a
    /// <c>YYYY-MM-DD</c> date or lies in the future gives no age: the claim is outside data, and
    /// <c>Required</c> in the catalog does not guarantee it — the form can be switched off in
    /// configuration, and the token then arrives without it.
    /// </summary>
    /// <param name="birthdate">The claim value, or null.</param>
    /// <param name="today">The day the age is asked for.</param>
    /// <returns>The age, or null when it cannot be told.</returns>
    public static int? AgeOn(string? birthdate, DateOnly today)
    {
        var parsed = DateOnly.TryParseExact(
            birthdate,
            BirthdateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var born);

        if (!parsed || born > today)
        {
            return null;
        }

        var years = today.Year - born.Year;

        // The birthday of this year has not come yet: the last full year is the one before.
        return born.AddYears(years) > today ? years - 1 : years;
    }

    /// <summary>
    /// Masks an identifier: the first and last <see cref="VisibleIdentifierEdge"/> characters stay
    /// readable, everything between becomes <see cref="MaskCharacter"/>, so <c>123456789</c> reads as
    /// <c>12*****89</c>. An identifier too short to keep both edges and still hide something is
    /// masked whole.
    /// </summary>
    /// <param name="identifier">The identifier as the channel gave it, or null.</param>
    /// <returns>The masked identifier, or null when there was none.</returns>
    public static string? MaskIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        if (identifier.Length < ShortestPartiallyMaskedDigits)
        {
            return new string(MaskCharacter, identifier.Length);
        }

        var hidden = identifier.Length - (2 * VisibleIdentifierEdge);

        return string.Concat(
            identifier.AsSpan(0, VisibleIdentifierEdge),
            new string(MaskCharacter, hidden),
            identifier.AsSpan(identifier.Length - VisibleIdentifierEdge));
    }

    /// <summary>
    /// Reads a claim, treating an empty value as an absent one: a channel that has no value for a
    /// field is the same case as a channel that issued no claim at all.
    /// </summary>
    /// <param name="user">The signed-in person.</param>
    /// <param name="claimType">Claim name.</param>
    /// <returns>The value, or null.</returns>
    public static string? Value(ClaimsPrincipal user, string claimType)
    {
        ArgumentNullException.ThrowIfNull(user);

        var value = user.FindFirstValue(claimType);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Masks a phone number: the leading country-code digit and the last two digits stay readable,
    /// every other digit becomes <see cref="MaskCharacter"/>, and the separators of the original are
    /// kept, so <c>+7 999 123 45 34</c> reads as <c>+7 *** *** ** 34</c>.
    /// <para>
    /// Only the FIRST digit of the country code is kept when the number is written without
    /// separators: telling a one-digit code from a three-digit one needs a numbering-plan table, and
    /// a sample carries none. Erring towards the shorter code hides more of the number rather than
    /// less, which is the right way to be wrong about a mask.
    /// </para>
    /// </summary>
    /// <param name="phoneNumber">The number as the channel gave it, or null.</param>
    /// <returns>The masked number, or null when there was none.</returns>
    public static string? MaskPhone(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return null;
        }

        var digitCount = phoneNumber.Count(char.IsDigit);

        // A number too short to mask partially: every digit goes, and only the separators remain.
        var visibleLeadingDigits = digitCount < ShortestPartiallyMaskedDigits
            ? 0
            : LeadingCountryCodeDigits(phoneNumber);
        var visibleTrailingDigits = digitCount < ShortestPartiallyMaskedDigits ? 0 : VisibleTrailingDigits;

        var masked = new StringBuilder(phoneNumber.Length);
        var digitsSeen = 0;

        foreach (var character in phoneNumber)
        {
            if (!char.IsDigit(character))
            {
                masked.Append(character);
                continue;
            }

            var isVisible = digitsSeen < visibleLeadingDigits
                || digitsSeen >= digitCount - visibleTrailingDigits;
            masked.Append(isVisible ? character : MaskCharacter);
            digitsSeen++;
        }

        return masked.ToString();
    }

    /// <summary>
    /// How many leading digits belong to the country code: the digits before the first separator of
    /// an international number, and a single digit when the number is written as one run.
    /// </summary>
    /// <param name="phoneNumber">The number as the channel gave it.</param>
    /// <returns>Number of leading digits to keep readable.</returns>
    private static int LeadingCountryCodeDigits(string phoneNumber)
    {
        if (phoneNumber[0] is not PlusSign)
        {
            return 0;
        }

        var leadingDigits = 0;
        for (var index = 1; index < phoneNumber.Length && char.IsDigit(phoneNumber[index]); index++)
        {
            leadingDigits++;
        }

        // The whole number is one run of digits: keep the first one and no more.
        return leadingDigits == phoneNumber.Length - 1 ? 1 : leadingDigits;
    }
}
