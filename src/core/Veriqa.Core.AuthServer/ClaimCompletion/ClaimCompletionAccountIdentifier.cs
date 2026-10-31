// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// The one rule of the identifier the claims completion shows the channel account by (SPEC-041 L109): a
/// value the owner of the account does not choose at will, so the user can tell which account the sign-in
/// is completed for. The same rule serves every surface — the form shows the account by it, and the choice
/// of the surface of the web step reads its absence.
/// </summary>
internal static class ClaimCompletionAccountIdentifier
{
    /// <summary>
    /// Prefix of a channel username.
    /// </summary>
    private const char UsernamePrefix = '@';

    /// <summary>
    /// Resolves the identifier of the channel account of a snapshot.
    /// </summary>
    /// <remarks>
    /// The first non-blank source wins, in one order for every channel:
    /// <list type="number">
    /// <item><description>the email address as it is — the name the Email adapter puts into the username
    /// (the display name of the sender or the local part) is no identifier;</description></item>
    /// <item><description>the username, prefixed with <c>@</c> (a leading <c>@</c> of the value is not
    /// doubled);</description></item>
    /// <item><description>the phone number, masked by the one mask of a channel account
    /// (<see cref="ChannelIdentityMask.MaskPhoneNumber"/>): the last four digits stay visible.</description></item>
    /// </list>
    /// The channel user id and the display name are no identifiers: the first means nothing to the user,
    /// the second is chosen at will.
    /// </remarks>
    /// <param name="snapshot">Channel snapshot of the transaction.</param>
    /// <returns>The identifier; null when the account has none (a Telegram account without a username).</returns>
    public static string? Resolve(ChannelIdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.IsNullOrWhiteSpace(snapshot.Email))
        {
            return snapshot.Email;
        }

        var username = snapshot.Username?.TrimStart(UsernamePrefix);
        if (!string.IsNullOrWhiteSpace(username))
        {
            return UsernamePrefix + username;
        }

        return ChannelIdentityMask.MaskPhoneNumber(snapshot.PhoneNumber);
    }
}
