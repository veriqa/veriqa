// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Globalization;
using System.Net.Mail;
using System.Text;

using MimeKit.Utils;

using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// The single source of truth for normalization and parsing of the Email channel's
/// email addresses (SPEC-016 §6.2). Used by both the Pull branch (endpoints) and the Push branch
/// (inbound processor) to prevent identity divergence between the modes (review feedback).
/// </summary>
internal static class EmailAddressHelper
{
    /// <summary>
    /// The canonical domain of the Gmail family.
    /// </summary>
    private const string GmailDomain = "gmail.com";

    /// <summary>
    /// The Gmail alias domain: mailboxes are the same, the domain is not.
    /// </summary>
    private const string GoogleMailDomain = "googlemail.com";

    /// <summary>
    /// The shipped Gmail canonicalization rules: dots in the local part are insignificant, everything
    /// from the first "+" up to the "@" is a user-chosen tag, and googlemail.com is an alias of
    /// gmail.com.
    /// </summary>
    private static readonly EmailProviderCanonicalization GmailRules =
        new(GmailDomain, RemoveLocalPartDots: true, StripPlusTag: true);

    /// <summary>
    /// The closed "domain → rules" table of provider-specific canonicalization (EM-042). Only the
    /// Gmail family ships: its rules are well known and stable, whereas for other providers they differ
    /// by version and plan, and merging two distinct identities by mistake is a security defect
    /// (SPEC-016 §2). A domain outside the table is never canonicalized — the result equals the one
    /// with the flag off. Extending the table is a separate decision, not a configuration surface.
    /// </summary>
    private static readonly FrozenDictionary<string, EmailProviderCanonicalization> ProviderRules =
        new Dictionary<string, EmailProviderCanonicalization>(StringComparer.OrdinalIgnoreCase)
        {
            [GmailDomain] = GmailRules,
            [GoogleMailDomain] = GmailRules,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes an email address according to the normalization settings (EM-041–EM-043).
    /// The case of the local part and domain is controlled by configuration; provider-specific
    /// canonicalization is applied on top of it when
    /// <see cref="EmailNormalizationOptions.ProviderSpecificCanonicalization"/> is enabled (EM-042).
    /// </summary>
    /// <remarks>
    /// This is the identity form: the returned string leaves the call site as a value
    /// (<c>ChannelUserId</c>/<c>email</c>/<c>Username</c>, the action token, the identity snapshot).
    /// A call site that only compares two addresses and returns a decision uses
    /// <see cref="ToComparisonForm"/> instead — it must not depend on the case settings.
    /// </remarks>
    /// <param name="email">The source email address.</param>
    /// <param name="normalization">Normalization settings (null — full lower-case).</param>
    /// <returns>The normalized email.</returns>
    public static string Normalize(string email, EmailNormalizationOptions? normalization)
    {
        // The method brings the address to a normalized form, taking case settings into account
        var trimmed = email.Trim();
        var atIndex = trimmed.LastIndexOf('@');

        // For an invalid/unexpected format keep safe behavior — full lower-case
        if (atIndex <= 0 || atIndex == trimmed.Length - 1)
        {
            return trimmed.ToLowerInvariant();
        }

        if (normalization is null)
        {
            return trimmed.ToLowerInvariant();
        }

        var localPart = trimmed[..atIndex];
        var domain = trimmed[(atIndex + 1)..];

        if (normalization.LowercaseLocalPart)
        {
            localPart = localPart.ToLowerInvariant();
        }

        if (normalization.LowercaseDomain)
        {
            domain = domain.ToLowerInvariant();
        }

        // Canonicalization runs on top of the case normalization and never governs case: the owner of
        // the case axis stays LowercaseLocalPart/LowercaseDomain (EM-041/EM-043).
        if (normalization.ProviderSpecificCanonicalization)
        {
            (localPart, domain) = Canonicalize(localPart, domain);
        }

        return $"{localPart}@{domain}";
    }

    /// <summary>
    /// Brings a value to the form in which two addresses are compared: an unconditional lower-case of
    /// both parts plus provider-specific canonicalization when it is enabled.
    /// </summary>
    /// <remarks>
    /// Used by the call sites whose output is a decision rather than an address — the Push allowlist
    /// check and the sender-alias match. Those are case-insensitive unconditionally and always have
    /// been: they never read <see cref="EmailNormalizationOptions.LowercaseLocalPart"/>/
    /// <see cref="EmailNormalizationOptions.LowercaseDomain"/>, and subordinating them to those
    /// settings would silently break the allowlist and the alias match on deployments that merely set
    /// <c>LowercaseLocalPart = false</c> and never enabled the new flag.
    /// <para>
    /// The input is either an address or an allowlist entry, and an entry comes in two shapes: with an
    /// "@" it is an address and gets the full treatment; without one it is a bare domain, so only the
    /// domain half of the canonicalization applies — running <see cref="Normalize"/> over it would hit
    /// the invalid-format branch and leave "googlemail.com" unmapped while the sender's domain had
    /// already become "gmail.com".
    /// </para>
    /// </remarks>
    /// <param name="value">Email address, or an allowlist entry (address or bare domain).</param>
    /// <param name="normalization">Normalization settings (null — no canonicalization).</param>
    /// <returns>The comparison form of the value.</returns>
    public static string ToComparisonForm(string value, EmailNormalizationOptions? normalization)
    {
        // The method removes the case difference unconditionally and adds canonicalization behind the flag
        var lowered = value.Trim().ToLowerInvariant();

        if (normalization?.ProviderSpecificCanonicalization is not true)
        {
            return lowered;
        }

        var atIndex = lowered.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == lowered.Length - 1)
        {
            // Not an address: a bare-domain allowlist entry (or a malformed value) — only the domain
            // alias mapping can apply.
            return CanonicalizeDomain(lowered);
        }

        var (localPart, domain) = Canonicalize(lowered[..atIndex], lowered[(atIndex + 1)..]);
        return $"{localPart}@{domain}";
    }

    /// <summary>
    /// Applies the provider rules to an already split address: drops the insignificant dots, cuts the
    /// plus tag and maps the domain to its canonical alias.
    /// </summary>
    /// <remarks>
    /// The address is left untouched when the domain is outside the table. Inside the table the two
    /// halves are canonicalized independently: the alias mapping of the domain is decided by the
    /// domain alone, so a local part no rule may touch (see <see cref="CanonicalizeLocalPart"/>) never
    /// holds that mapping back. Coupling them would make the comparison form of
    /// <c>+tag@googlemail.com</c> disagree with the comparison form of the bare-domain allowlist entry
    /// <c>googlemail.com</c>, which maps the alias unconditionally — enabling the flag would then
    /// narrow the allowlist instead of widening it.
    /// </remarks>
    /// <param name="localPart">Local part of the address.</param>
    /// <param name="domain">Domain of the address.</param>
    /// <returns>The canonicalized parts, or the input ones when no rule applies.</returns>
    private static (string LocalPart, string Domain) Canonicalize(string localPart, string domain) =>
        ProviderRules.TryGetValue(domain, out var rules)
            ? (CanonicalizeLocalPart(localPart, rules), MapToCanonicalDomain(domain, rules))
            : (localPart, domain);

    /// <summary>
    /// Applies the local-part half of the provider rules: drops the insignificant dots and cuts the
    /// plus tag.
    /// </summary>
    /// <remarks>
    /// The local part is returned as it came when it is quoted (a dot inside quotes is significant, so
    /// removing it would change the address) and when the whole local part turns out to be a tag
    /// (<c>+tag@gmail.com</c>) — an empty local part is not an address.
    /// </remarks>
    /// <param name="localPart">Local part of the address.</param>
    /// <param name="rules">Rules of the family the domain belongs to.</param>
    /// <returns>The canonicalized local part, or the input one when no rule applies.</returns>
    private static string CanonicalizeLocalPart(string localPart, EmailProviderCanonicalization rules)
    {
        if (localPart.Contains('"'))
        {
            return localPart;
        }

        var canonicalLocalPart = localPart;

        if (rules.StripPlusTag)
        {
            var plusIndex = canonicalLocalPart.IndexOf(EmailAdapterConstants.PlusAddressSeparator);
            if (plusIndex >= 0)
            {
                canonicalLocalPart = canonicalLocalPart[..plusIndex];
            }
        }

        if (rules.RemoveLocalPartDots)
        {
            canonicalLocalPart = canonicalLocalPart.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        return canonicalLocalPart.Length is 0 ? localPart : canonicalLocalPart;
    }

    /// <summary>
    /// Maps a bare domain to its canonical alias; a domain outside the table is returned unchanged.
    /// </summary>
    /// <param name="domain">Domain to map.</param>
    /// <returns>The canonical domain, or the input one.</returns>
    private static string CanonicalizeDomain(string domain) =>
        ProviderRules.TryGetValue(domain, out var rules) ? MapToCanonicalDomain(domain, rules) : domain;

    /// <summary>
    /// Returns the canonical domain of the family, keeping the caller's spelling when it already is
    /// that domain — the canonicalization maps aliases, it does not govern case (EM-041).
    /// </summary>
    /// <param name="domain">Domain of the address.</param>
    /// <param name="rules">Rules of the family the domain belongs to.</param>
    /// <returns>The domain to use.</returns>
    private static string MapToCanonicalDomain(string domain, EmailProviderCanonicalization rules) =>
        string.Equals(domain, rules.CanonicalDomain, StringComparison.OrdinalIgnoreCase)
            ? domain
            : rules.CanonicalDomain;

    /// <summary>
    /// Provider-specific canonicalization rules of one mailbox-provider family (EM-042).
    /// </summary>
    /// <param name="CanonicalDomain">The domain every alias of the family maps to.</param>
    /// <param name="RemoveLocalPartDots">Dots in the local part are insignificant.</param>
    /// <param name="StripPlusTag">Everything from the first "+" up to the "@" is a user-chosen tag.</param>
    private sealed record EmailProviderCanonicalization(
        string CanonicalDomain,
        bool RemoveLocalPartDots,
        bool StripPlusTag);

    /// <summary>
    /// Returns the local part of the email address (up to the "@" character) — the verified fallback
    /// for the preferred_username claim (SPEC-016 §6.3), or null on an unexpected format.
    /// </summary>
    /// <param name="normalizedEmail">The normalized email.</param>
    /// <returns>The local part of the address or null.</returns>
    public static string? GetLocalPart(string normalizedEmail)
    {
        var atIndex = normalizedEmail.LastIndexOf('@');
        return atIndex > 0 ? normalizedEmail[..atIndex] : null;
    }

    /// <summary>
    /// Extracts the sender display name — the alias in "Dmitrii &lt;user@example.com&gt;" — from a raw
    /// address header value and makes it safe to carry in a claim (SPEC-016 §6.3).
    /// An alias in RFC 2047 encoded-word form (<c>=?utf-8?B?...?=</c>) is decoded first: the .NET parser
    /// returns it verbatim, and whether a provider hands the header over already decoded is not ours to
    /// rely on.
    /// The alias is typed by the sender and is covered by neither SPF, DKIM nor DMARC, so it is treated
    /// as untrusted input and sanitized (see <see cref="SanitizeDisplayName"/>).
    /// </summary>
    /// <param name="rawAddress">Raw address header value.</param>
    /// <returns>The sanitized display name, or null when the header carries none.</returns>
    public static string? ExtractDisplayName(string? rawAddress)
    {
        // The method parses the header with the same .NET MailAddress parser used for the address itself,
        // so the alias and the address are always read out of one and the same interpretation of the header
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return null;
        }

        string displayName;

        try
        {
            displayName = new MailAddress(rawAddress.Trim()).DisplayName;
        }
        catch (FormatException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        // Decoding precedes sanitization: an encoded word hides the characters the sanitizer exists to
        // remove (control and format characters), and it would otherwise clean the encoded form instead.
        // UTF-8 bytes keep an already decoded non-ASCII alias intact — MimeKit reads 8-bit text as UTF-8
        // first.
        var decodedDisplayName = Rfc2047.DecodePhrase(Encoding.UTF8.GetBytes(displayName));

        return SanitizeDisplayName(decodedDisplayName);
    }

    /// <summary>
    /// Sanitizes a sender-supplied display name: Unicode format characters are dropped, then the shared
    /// channel-value sanitizer collapses control characters, truncates to
    /// <see cref="EmailAdapterConstants.MaxSenderDisplayNameLength"/> at a code-point boundary and trims.
    /// Format characters (category Cf — bidi overrides such as U+202E, zero-width characters, and the tag
    /// characters outside the BMP such as U+E0041) are removed rather than replaced with a space: they are
    /// invisible, so a replacement would leave a visible gap where the sender put nothing, while keeping the
    /// string free of reordering tricks is the whole point. The category is taken per code point, so a
    /// format character encoded as a surrogate pair is recognized as one.
    /// Homoglyphs are deliberately NOT addressed here — a look-alike name is indistinguishable from a
    /// legitimate one, which is exactly why the value lands in <c>preferred_username</c> (a hint an RP must
    /// not treat as an identifier) and never in <c>name</c>.
    /// </summary>
    /// <param name="displayName">Raw display name from the address header.</param>
    /// <returns>The safe display name, or null when nothing meaningful is left.</returns>
    private static string? SanitizeDisplayName(string displayName)
    {
        var builder = new StringBuilder(displayName.Length);

        // A lone surrogate is enumerated as a one-unit replacement rune, so the position stays aligned with
        // the source and the source units are what gets copied.
        var position = 0;

        foreach (var rune in displayName.EnumerateRunes())
        {
            var runeLength = rune.Utf16SequenceLength;

            if (Rune.GetUnicodeCategory(rune) is not UnicodeCategory.Format)
            {
                builder.Append(displayName, position, runeLength);
            }

            position += runeLength;
        }

        return MessageValueSanitizer.Sanitize(
            builder.ToString(),
            EmailAdapterConstants.MaxSenderDisplayNameLength);
    }
}
