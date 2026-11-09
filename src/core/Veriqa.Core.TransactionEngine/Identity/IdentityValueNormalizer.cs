// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Configuration;

namespace Veriqa.Core.TransactionEngine.Identity;

/// <summary>
/// The closed set of normalization rules of the core, as code (SPEC-012 §4.12). It is the single
/// place both sides of a comparison are brought to a canonical form: the expectation of the relying
/// party when it is accepted, and the claim of the confirming party when the verdict is computed. A
/// second implementation of either side would be a second definition of "the same person".
/// </summary>
internal static class IdentityValueNormalizer
{
    /// <summary>
    /// Brings a value to the canonical form of its declared rule.
    /// </summary>
    /// <param name="rule">Normalization rule of the declared type.</param>
    /// <param name="value">Value as it was stated.</param>
    /// <param name="normalized">The canonical form.</param>
    /// <returns><see langword="false"/> when the value has no canonical form under this rule.</returns>
    public static bool TryNormalize(
        IdentityNormalizationRule rule,
        string? value,
        [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        return rule switch
        {
            IdentityNormalizationRule.Exact => Exact(trimmed, out normalized),
            IdentityNormalizationRule.E164Phone => E164PhoneNumber.TryNormalize(trimmed, out normalized),
            IdentityNormalizationRule.EmailDomainCaseFold => EmailDomainCaseFold(trimmed, out normalized),

            // A rule outside the enumeration cannot be compared by, and refusing here rather than
            // falling back to the strictest one keeps a broken declaration from silently comparing by
            // a rule its author did not choose. The startup validation refuses such a value already.
            _ => false
        };
    }

    /// <summary>
    /// The value as it stands, trimmed.
    /// </summary>
    /// <param name="trimmed">Trimmed value.</param>
    /// <param name="normalized">The canonical form.</param>
    /// <returns>Always <see langword="true"/> — a non-empty value is its own canonical form.</returns>
    private static bool Exact(string trimmed, [NotNullWhen(true)] out string? normalized)
    {
        normalized = trimmed;

        return true;
    }

    /// <summary>
    /// The canonical form of the domain part alone (<see cref="EmailDomain.ToCanonical"/>: lower case,
    /// an internationalized name as its A-label, no trailing dot): the local part of an address is
    /// case-SENSITIVE by RFC 5321, so folding it would call two different mailboxes one and the same.
    /// The domain is brought by the same rule the Email channel records the address by, so an expectation
    /// spelled in Unicode matches the A-label the channel confirmed.
    /// </summary>
    /// <param name="trimmed">Trimmed value.</param>
    /// <param name="normalized">The canonical form.</param>
    /// <returns><see langword="false"/> when the value is not an address of one local part and one domain.</returns>
    private static bool EmailDomainCaseFold(string trimmed, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;

        var separator = trimmed.LastIndexOf('@');
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            return false;
        }

        var local = trimmed[..separator];
        var domain = trimmed[(separator + 1)..];

        // An address holding a second '@' in its domain is not one this rule can fold: the domain is
        // what is being brought to its canonical form, and there would be two candidates for it.
        if (domain.Contains('@'))
        {
            return false;
        }

        normalized = string.Concat(local, "@", EmailDomain.ToCanonical(domain));

        return true;
    }
}
