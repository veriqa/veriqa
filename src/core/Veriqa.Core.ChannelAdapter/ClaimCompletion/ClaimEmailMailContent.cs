// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// What a completion mail carries (SPEC-041 C96, R12). The user is offered ONE way "confirm via email",
/// whichever of <see cref="EmailCompletionMethod.Code"/> and <see cref="EmailCompletionMethod.MagicLink"/>
/// the effective list of methods names; the list decides only what the mail of that way carries.
/// </summary>
internal enum ClaimEmailMailContent
{
    /// <summary>
    /// A code the user enters in the block — the list names <see cref="EmailCompletionMethod.Code"/> only.
    /// </summary>
    Code,

    /// <summary>
    /// A link to the confirmation page — the list names <see cref="EmailCompletionMethod.MagicLink"/> only.
    /// </summary>
    Link,

    /// <summary>
    /// Both, in one mail: the address is confirmed by whichever the user does first — the list names
    /// both methods.
    /// </summary>
    CodeAndLink
}

/// <summary>
/// The one statement of how the list of methods maps onto the way "confirm via email" — for the block
/// that shows the way, the handler that acts on it and the mail side that sends its mail.
/// </summary>
internal static class ClaimEmailMailContents
{
    /// <summary>
    /// Whether a method is one of the two the way "confirm via email" stands for.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns><c>true</c> for <see cref="EmailCompletionMethod.Code"/> and <see cref="EmailCompletionMethod.MagicLink"/>.</returns>
    public static bool IsMailMethod(EmailCompletionMethod method) =>
        method is EmailCompletionMethod.Code or EmailCompletionMethod.MagicLink;

    /// <summary>
    /// What the mail of the way carries, by the methods available to the transaction.
    /// </summary>
    /// <param name="methods">The available methods.</param>
    /// <returns>The content of the mail; null — the list names neither method, the way is not offered.</returns>
    public static ClaimEmailMailContent? Of(IReadOnlyList<EmailCompletionMethod> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);

        var withCode = methods.Contains(EmailCompletionMethod.Code);
        var withLink = methods.Contains(EmailCompletionMethod.MagicLink);

        return (withCode, withLink) switch
        {
            (true, true) => ClaimEmailMailContent.CodeAndLink,
            (true, false) => ClaimEmailMailContent.Code,
            (false, true) => ClaimEmailMailContent.Link,
            _ => null
        };
    }

    /// <summary>
    /// The ways the user is offered, in the order of the list: the two mail methods stand as one way, at
    /// the position of the first of them and under its name.
    /// </summary>
    /// <param name="methods">The available methods, in the order of the effective list.</param>
    /// <returns>The ways; of the two mail methods at most the first one is among them.</returns>
    public static IReadOnlyList<EmailCompletionMethod> WaysOf(IReadOnlyList<EmailCompletionMethod> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);

        var ways = new List<EmailCompletionMethod>(methods.Count);
        var mailWayListed = false;

        foreach (var method in methods)
        {
            if (IsMailMethod(method))
            {
                if (mailWayListed)
                {
                    continue;
                }

                mailWayListed = true;
            }

            ways.Add(method);
        }

        return ways;
    }

    /// <summary>
    /// Whether the mail carries a code.
    /// </summary>
    /// <param name="content">The content of the mail.</param>
    /// <returns><c>true</c> when a code is sent.</returns>
    public static bool CarriesCode(this ClaimEmailMailContent content) =>
        content is ClaimEmailMailContent.Code or ClaimEmailMailContent.CodeAndLink;

    /// <summary>
    /// Whether the mail carries a link.
    /// </summary>
    /// <param name="content">The content of the mail.</param>
    /// <returns><c>true</c> when a link is sent.</returns>
    public static bool CarriesLink(this ClaimEmailMailContent content) =>
        content is ClaimEmailMailContent.Link or ClaimEmailMailContent.CodeAndLink;
}
