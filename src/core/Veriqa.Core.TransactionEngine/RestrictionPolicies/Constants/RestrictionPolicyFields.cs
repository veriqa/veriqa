// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Names of the claim fields a restriction rule may check (SPEC-018 §5.2). The vocabulary is closed:
/// a rule names one of the fields below or a metadata field under <see cref="MetadataPrefix"/>.
/// Names are matched exactly, letter case included — they are identifiers, not user data.
/// </summary>
internal static class RestrictionPolicyFields
{
    /// <summary>
    /// Type of the channel the confirmation came through.
    /// </summary>
    public const string ChannelType = "channel.type";

    /// <summary>
    /// Whether the channel reported the sender as a bot.
    /// </summary>
    public const string IsBot = "is_bot";

    /// <summary>
    /// The whole email address.
    /// </summary>
    public const string EmailAddress = "email.address";

    /// <summary>
    /// Domain of the email address.
    /// </summary>
    public const string EmailDomain = "email.domain";

    /// <summary>
    /// Whether the channel stated that the user controls the email address.
    /// </summary>
    public const string EmailVerified = "email.verified";

    /// <summary>
    /// Phone number in E.164, as the adapter handed it over.
    /// </summary>
    public const string PhoneE164 = "phone.e164";

    /// <summary>
    /// Country calling code of the phone number. Not a value of its own: a rule on this field is a
    /// prefix comparison over the E.164 number (RP-026).
    /// </summary>
    public const string PhoneCountryCode = "phone.country_code";

    /// <summary>
    /// Base language tag of the locale.
    /// </summary>
    public const string LocaleLanguage = "locale.language";

    /// <summary>
    /// Username in the channel.
    /// </summary>
    public const string Username = "username";

    /// <summary>
    /// Prefix of a field that addresses a top-level scalar property of the channel metadata; the rest
    /// of the name is the property key.
    /// </summary>
    public const string MetadataPrefix = "metadata.";

    /// <summary>
    /// The fixed field names — every allowed name except the metadata form, which is recognized by
    /// prefix (see <see cref="TryGetMetadataKey"/>).
    /// </summary>
    public static FrozenSet<string> Known { get; } = new[]
    {
        ChannelType,
        IsBot,
        EmailAddress,
        EmailDomain,
        EmailVerified,
        PhoneE164,
        PhoneCountryCode,
        LocaleLanguage,
        Username
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether a name is an allowed field: one of <see cref="Known"/> or a metadata field with a
    /// non-empty key.
    /// </summary>
    /// <param name="field">Field name as the rule states it.</param>
    /// <returns><see langword="true"/> for an allowed field name.</returns>
    public static bool IsKnown(string? field) =>
        field is not null && (Known.Contains(field) || TryGetMetadataKey(field, out _));

    /// <summary>
    /// Takes the metadata key out of a field name of the metadata form.
    /// </summary>
    /// <param name="field">Field name as the rule states it.</param>
    /// <param name="key">Key of the metadata property the field addresses.</param>
    /// <returns>
    /// <see langword="false"/> when the name is not of the metadata form or carries no key after the
    /// prefix.
    /// </returns>
    public static bool TryGetMetadataKey(string? field, [NotNullWhen(true)] out string? key)
    {
        key = null;

        if (field is null || !field.StartsWith(MetadataPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = field[MetadataPrefix.Length..];
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        key = candidate;

        return true;
    }
}
