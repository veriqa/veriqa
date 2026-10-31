// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// The request for a channel link (<c>IChannelAdapter.GetDeepLinkAsync</c>): the transaction the link
/// starts and where the link is going to be used. A record with init properties, so a field added later
/// leaves the callers that do not state it compiling and behaving as before.
/// </summary>
public sealed record DeepLinkRequest
{
    /// <summary>
    /// The transaction the link starts.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Where the link is going to be used. <see cref="DeepLinkPurpose.Button"/> when not stated.
    /// </summary>
    public DeepLinkPurpose Purpose { get; init; } = DeepLinkPurpose.Button;
}
