// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// A link of a claims-completion mail as the store keeps it: the hash of its token, never the token.
/// </summary>
/// <param name="TokenHash">Hash of the token of the link (<see cref="EmailClaimSecrets.HashOfToken"/>), hex.</param>
/// <param name="Address">Address the link was sent to — the one its use records (SPEC-041 L50).</param>
/// <param name="ExpiresAt">Moment the link stops being accepted (UTC).</param>
/// <param name="Used">Whether the link was used; a link is used once.</param>
internal sealed record EmailClaimLink(string TokenHash, string Address, DateTimeOffset ExpiresAt, bool Used)
{
    /// <summary>
    /// Whether the link is still accepted at a moment: it is unused and has not expired.
    /// </summary>
    /// <param name="now">Current moment.</param>
    /// <returns><c>true</c> when the link is live.</returns>
    public bool IsLive(DateTimeOffset now) => !Used && now < ExpiresAt;
}
