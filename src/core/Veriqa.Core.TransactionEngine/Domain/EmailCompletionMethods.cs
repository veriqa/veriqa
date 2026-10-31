// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// The one translation between a configured method of obtaining the email (<see cref="EmailCompletionMethod"/>,
/// SPEC-041 C96) and the way a collected value is recorded with (<see cref="ClaimCollectionMethod"/>, C37):
/// the page that writes an email and the check of a stored value against the configured list (SPEC-041
/// L58) both translate here, so they cannot disagree about which method a record names.
/// </summary>
public static class EmailCompletionMethods
{
    /// <summary>
    /// The collection method a value obtained by an email method is recorded with.
    /// </summary>
    /// <param name="method">Configured email method.</param>
    /// <returns>The matching collection method.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a member of the enumeration.</exception>
    public static ClaimCollectionMethod ToCollectionMethod(EmailCompletionMethod method) => method switch
    {
        EmailCompletionMethod.Push => ClaimCollectionMethod.EmailPush,
        EmailCompletionMethod.Code => ClaimCollectionMethod.EmailCode,
        EmailCompletionMethod.MagicLink => ClaimCollectionMethod.EmailMagicLink,
        EmailCompletionMethod.Unverified => ClaimCollectionMethod.EmailUnverified,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Not an email completion method.")
    };

    /// <summary>
    /// The email method a recorded collection method names.
    /// </summary>
    /// <param name="method">Collection method of a recorded value.</param>
    /// <param name="emailMethod">The matching email method; the default when there is none.</param>
    /// <returns><c>false</c> when the collection method is not one of the email methods.</returns>
    public static bool TryFromCollectionMethod(ClaimCollectionMethod method, out EmailCompletionMethod emailMethod)
    {
        switch (method)
        {
            case ClaimCollectionMethod.EmailPush:
                emailMethod = EmailCompletionMethod.Push;
                return true;
            case ClaimCollectionMethod.EmailCode:
                emailMethod = EmailCompletionMethod.Code;
                return true;
            case ClaimCollectionMethod.EmailMagicLink:
                emailMethod = EmailCompletionMethod.MagicLink;
                return true;
            case ClaimCollectionMethod.EmailUnverified:
                emailMethod = EmailCompletionMethod.Unverified;
                return true;
            default:
                emailMethod = default;
                return false;
        }
    }
}
