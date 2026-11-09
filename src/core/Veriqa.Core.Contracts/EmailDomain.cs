// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Veriqa.Core.Contracts;

/// <summary>
/// The canonical form of the domain of an email address, as code: the one place a domain is brought to the
/// form in which every spelling of one name is the same string (SPEC-016 EM-041). The Email channel brings
/// the address it records, compares and counts by with it, and the identity comparison of the core brings
/// both sides of an email expectation with it — a second implementation would let one mailbox be two
/// identities, depending on which side spelled the domain how. The way back — the form a domain is shown
/// to a person in — lives here too, so the two directions of one mapping stay together.
/// </summary>
public static class EmailDomain
{
    /// <summary>
    /// The trailing dot of a fully qualified domain name, which names the same domain as the name without it.
    /// </summary>
    private const char RootLabelDot = '.';

    /// <summary>
    /// The prefix of an A-label (RFC 5890 §2.3.2.1), by which a name in its canonical form is told to have an
    /// internationalized label.
    /// </summary>
    private const string ALabelPrefix = "xn--";

    /// <summary>
    /// Brings a domain to its canonical form: without the trailing dot, an internationalized name as its
    /// A-label (IDNA, UTS #46 mapping), in lower case. A name the IDNA mapping refuses keeps its lower-case
    /// form: it is still one string per spelling, apart from the names that are valid.
    /// </summary>
    /// <param name="domain">The domain as written in the address.</param>
    /// <returns>The canonical form.</returns>
    public static string ToCanonical(string domain)
    {
        var withoutRootDot = domain.TrimEnd(RootLabelDot);
        var name = withoutRootDot.Length > 0 ? withoutRootDot : domain;

        try
        {
            // A new instance per call: the instance members of IdnMapping are not documented as thread-safe.
            return new IdnMapping().GetAscii(name).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return name.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Brings a domain to the form it is shown to a person in: a name holding an A-label is shown in Unicode
    /// (U-label), as the person typed it, rather than as the <c>xn--</c> string the canonical form keeps. A
    /// name without an A-label, and a name the IDNA mapping refuses, is shown as it stands.
    /// </summary>
    /// <param name="domain">The domain, normally in its canonical form.</param>
    /// <returns>The form to show.</returns>
    public static string ToDisplay(string domain)
    {
        if (!domain.Contains(ALabelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return domain;
        }

        try
        {
            return new IdnMapping().GetUnicode(domain);
        }
        catch (ArgumentException)
        {
            return domain;
        }
    }
}
