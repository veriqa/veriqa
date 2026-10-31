// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Optional name of the chat a channel talks to the user through (public SPI, additive).
/// An adapter implements it <i>in addition</i> to <see cref="IChannelAdapter"/> when the user sees
/// the chat under a name of its own — a bot username, an account handle. The pages of the core put
/// that name into the texts telling the user which chat to look at, and the text of a confirmation
/// question may name the chat it is asked in by it; without it the same texts are shown in their
/// nameless wording, never with the channel name in place of the chat name.
/// </summary>
public interface IChannelBotName
{
    /// <summary>
    /// Returns the name under which the user sees the chat in the channel, ready to be shown as is
    /// (for example <c>@acme_bot</c>).
    /// </summary>
    /// <remarks>
    /// Called under the tenant scope of the transaction, the way
    /// <see cref="IChannelAdapter.GetDeepLinkAsync"/> is, so an adapter with per-tenant credentials
    /// answers with the name of that tenant's chat. The value is shown to the user and is therefore
    /// expected to be short; a caller is free to drop a value it cannot fit and fall back to the
    /// nameless wording.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name, or null (or an empty string) when the channel has none to show.</returns>
    ValueTask<string?> GetBotNameAsync(CancellationToken cancellationToken = default);
}
