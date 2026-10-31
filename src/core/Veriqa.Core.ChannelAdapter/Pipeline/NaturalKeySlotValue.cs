// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Value of a slot that is a configured text — a Natural Key the message renderer translates into the
/// language of the recipient, and shows as it is when no locale file carries it. It is how a name the
/// pages translate (the brand name of the sign-in page) reaches a text in the same language.
/// </summary>
/// <param name="NaturalKey">The configured text, sanitized.</param>
internal sealed record NaturalKeySlotValue(string NaturalKey)
{
    /// <summary>
    /// The configured text itself — what a consumer that does not translate shows.
    /// </summary>
    /// <returns>The Natural Key.</returns>
    public override string ToString() => NaturalKey;
}
