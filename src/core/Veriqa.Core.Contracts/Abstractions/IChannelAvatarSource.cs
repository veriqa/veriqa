// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Optional source of the avatar of a channel user, read when the tokens are issued (public SPI,
/// additive). An adapter implements it <i>in addition</i> to <see cref="IChannelAdapter"/> when the
/// avatar costs a separate request to the platform: the core asks for it only for an issuance that
/// requested the <c>avatar</c> scope and only when the snapshots of the transaction carry no
/// <c>picture</c> already, so a chat answer never waits for it. A channel that does not implement it
/// is never asked for an avatar.
/// </summary>
public interface IChannelAvatarSource
{
    /// <summary>
    /// Returns the avatar of the channel user the transaction was confirmed by.
    /// </summary>
    /// <remarks>
    /// Called under the tenant scope of the transaction, the way
    /// <see cref="IChannelBotName.GetBotNameAsync"/> is, so an adapter with per-tenant credentials
    /// reads the avatar with the credentials of that tenant. The caller bounds the call by a time
    /// budget of its own and cancels the token when the budget runs out; a failure or a cancelled
    /// call leaves the issuance without the avatar and never fails it. The value is checked by the
    /// caller with <see cref="Veriqa.Core.Contracts.AvatarDataUri.IsAcceptedAvatar"/>; a value it
    /// does not accept is dropped.
    /// </remarks>
    /// <param name="channelUserId">Identifier of the user in the channel, as the identity snapshot of
    /// the transaction states it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An avatar data URI or an https URL accepted by
    /// <see cref="Veriqa.Core.Contracts.AvatarDataUri.IsAcceptedAvatar"/>; null — the user has none
    /// the channel can provide.</returns>
    ValueTask<string?> GetAvatarAsync(string channelUserId, CancellationToken cancellationToken = default);
}
