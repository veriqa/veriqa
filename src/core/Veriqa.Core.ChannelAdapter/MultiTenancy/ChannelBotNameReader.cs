// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// The one reading of the name a person sees the chat of a channel under, for the adapters whose chat is
/// named by a username behind a prefix. An adapter states where the username comes from, the prefix of
/// its platform and how a failure is reported; the trimming, the prefix and the answer to a failure are
/// the same for all of them and live here.
/// </summary>
internal static class ChannelBotNameReader
{
    /// <summary>
    /// Reads the username from its source and returns it the way the person sees it: trimmed, with
    /// exactly one prefix in front. The name only decorates a text, so a failure to obtain it
    /// is reported and answered with no name, never thrown. A cancelled request is not such a failure:
    /// the cancellation goes on to the caller.
    /// </summary>
    /// <param name="readUsername">
    /// Source of the username, called inside the catch of this method: the raw username, with or without
    /// the prefix, or null when the source has none to give.
    /// </param>
    /// <param name="prefix">Prefix the platform of the channel shows a username behind.</param>
    /// <param name="reportFailure">
    /// Writes the warning of a failed reading. The adapter keeps the wording of its own log line, so the
    /// message template stays a constant where it is written.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The prefixed name; null — the source gave none, or the reading failed.</returns>
    public static async ValueTask<string?> ReadAsync(
        Func<CancellationToken, ValueTask<string?>> readUsername,
        char prefix,
        Action<Exception> reportFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            // A username from an upper configuration layer may already carry the prefix, so it is
            // dropped before the one shown to the user is put in front.
            var username = (await readUsername(cancellationToken))?.Trim().TrimStart(prefix);

            return string.IsNullOrEmpty(username)
                ? null
                : prefix + username;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            reportFailure(ex);

            return null;
        }
    }
}
