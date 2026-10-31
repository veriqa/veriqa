// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// The correlation token the <c>Push</c> method of the email completion issued for a transaction, kept so
/// that the block of the web step shows the same prepared mail while the token lives.
/// </summary>
/// <param name="Token">
/// The correlation token. Unlike a code or a link it is kept as it is: the prepared mail is rebuilt from
/// it, and the correlation store keeps it the same way.
/// </param>
/// <param name="ExpiresAt">Moment the correlation expires (UTC).</param>
internal sealed record EmailClaimPushToken(string Token, DateTimeOffset ExpiresAt);
