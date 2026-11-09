// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Reads policy fields off the channel identity snapshot. Stateless.
/// </summary>
/// <remarks>
/// The core does not format claims (RP-020): the snapshot carries them in the form the adapter
/// contract declares, and this reader only derives from them — the domain of an address, the base
/// language of a locale. A value that is not in its declared form is not repaired; it is read as an
/// absent claim (RP-021). The adapter's free-form additional claims are never read (RP-024).
/// </remarks>
internal sealed class RestrictionClaimReader(ILogger<RestrictionClaimReader> logger) : IRestrictionClaimReader
{
    /// <summary>
    /// Separator between the local part and the domain of an email address.
    /// </summary>
    private const char EmailSeparator = '@';

    /// <summary>
    /// Separators that end the base language subtag of a locale, in either spelling of the tag.
    /// </summary>
    private static readonly char[] LocaleSubtagSeparators = ['-', '_'];

    /// <inheritdoc />
    public string? Read(ChannelIdentitySnapshot identity, string field)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (RestrictionPolicyFields.TryGetMetadataKey(field, out var metadataKey))
        {
            return ReadMetadata(identity.RawMetadata, metadataKey);
        }

        switch (field)
        {
            case RestrictionPolicyFields.ChannelType:
                return PresentOrNull(identity.ChannelType);

            case RestrictionPolicyFields.IsBot:
                return FormatBoolean(identity.IsBot);

            case RestrictionPolicyFields.EmailAddress:
                return ReadEmailAddress(identity.Email);

            case RestrictionPolicyFields.EmailDomain:
                return ReadEmailDomain(identity.Email);

            case RestrictionPolicyFields.EmailVerified:
                return identity.EmailVerified is { } verified ? FormatBoolean(verified) : null;

            case RestrictionPolicyFields.PhoneE164:
                return ReadCanonicalPhoneNumber(identity.PhoneNumber);

            // The country code is not cut out of the number: that takes a table of codes, which the
            // core does not keep. The field yields the same number, and the rule compares its prefix
            // (RP-026).
            case RestrictionPolicyFields.PhoneCountryCode:
                return ReadCanonicalPhoneNumber(identity.PhoneNumber);

            case RestrictionPolicyFields.LocaleLanguage:
                return ReadLocaleLanguage(identity.Locale);

            case RestrictionPolicyFields.Username:
                return PresentOrNull(identity.Username);

            default:
                // A defensive path: the shape validation of the policy rejects an unknown field name
                // before it can reach an evaluation.
                logger.LogWarning(
                    "Restriction policy rule names an unknown claim field; the claim is read as absent. Field: {Field}",
                    field);

                return null;
        }
    }

    /// <summary>
    /// The whole address, trimmed: the local part lower-cased, the domain in its canonical form
    /// (<see cref="EmailDomain.ToCanonical"/>); absent without a separator or with an empty part on
    /// either side of the last one. The value of an <c>email.address</c> rule is read by the same
    /// derivation, so a rule and a claim spelling one domain differently still compare equal.
    /// </summary>
    /// <param name="email">The address.</param>
    /// <returns>The derived address, or null.</returns>
    internal static string? ReadEmailAddress(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var separatorIndex = trimmed.LastIndexOf(EmailSeparator);

        return separatorIndex <= 0 || separatorIndex == trimmed.Length - 1
            ? null
            : string.Concat(
                trimmed[..separatorIndex].ToLowerInvariant(),
                EmailSeparator.ToString(),
                EmailDomain.ToCanonical(trimmed[(separatorIndex + 1)..]));
    }

    /// <summary>
    /// The part after the LAST separator, in its canonical form (<see cref="EmailDomain.ToCanonical"/>);
    /// absent wherever the address itself is absent — without a separator or with an empty part on
    /// either side of the last one: a value that is not an address has no domain to derive (RP-021).
    /// </summary>
    private static string? ReadEmailDomain(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var separatorIndex = trimmed.LastIndexOf(EmailSeparator);

        return separatorIndex <= 0 || separatorIndex == trimmed.Length - 1
            ? null
            : EmailDomain.ToCanonical(trimmed[(separatorIndex + 1)..]);
    }

    /// <summary>
    /// The number exactly as the adapter handed it over, when it is canonical E.164; absent
    /// otherwise. The format is judged by the one E.164 rule of the core — a national spelling,
    /// separators or surrounding whitespace are a breach of the adapter contract, not something to
    /// repair here.
    /// </summary>
    private static string? ReadCanonicalPhoneNumber(string? phoneNumber) =>
        E164PhoneNumber.IsCanonical(phoneNumber) ? phoneNumber : null;

    /// <summary>
    /// The base language subtag — what precedes the first subtag separator — lower-cased.
    /// </summary>
    private static string? ReadLocaleLanguage(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return null;
        }

        var separatorIndex = locale.IndexOfAny(LocaleSubtagSeparators);
        var language = separatorIndex < 0 ? locale : locale[..separatorIndex];

        return PresentOrNull(language)?.ToLowerInvariant();
    }

    /// <summary>
    /// A scalar top-level property of the channel metadata in its string form (RP-023). Anything else
    /// — metadata that is not an object, a missing key, a null, an array, a nested object — is an
    /// absent claim. The key is matched exactly, as JSON property names are.
    /// </summary>
    private static string? ReadMetadata(JsonElement? rawMetadata, string key)
    {
        if (rawMetadata is not { ValueKind: JsonValueKind.Object } metadata
            || !metadata.TryGetProperty(key, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => PresentOrNull(property.GetString()),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => FormatBoolean(true),
            JsonValueKind.False => FormatBoolean(false),
            _ => null
        };
    }

    /// <summary>
    /// A boolean field in the string form rules compare.
    /// </summary>
    private static string FormatBoolean(bool value) =>
        value ? RestrictionClaimValues.True : RestrictionClaimValues.False;

    /// <summary>
    /// An empty or whitespace-only value is an absent claim.
    /// </summary>
    private static string? PresentOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
