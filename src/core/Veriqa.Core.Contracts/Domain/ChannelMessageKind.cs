// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Kind of content carried by a channel message (SPEC-003 §6.2).
/// An extensible value rather than a closed enum: a third-party adapter may declare a kind of its
/// own without recompiling anything else, exactly like <c>HttpMethod</c>/<c>MediaTypeNames</c> in the BCL.
/// </summary>
/// <param name="Value">Content kind code (see <see cref="ChannelMessageKinds"/>).</param>
public readonly record struct ChannelMessageKind(string Value)
{
    /// <summary>
    /// Content kind code. Never empty for a value built through the constructor;
    /// <c>default(ChannelMessageKind)</c> carries <see langword="null"/> and denotes an unknown kind.
    /// </summary>
    public string Value { get; } = Validate(Value);

    /// <summary>
    /// Plain text without markup.
    /// </summary>
    /// <remarks>
    /// Expression-bodied on purpose: a static auto-property without an initializer would be
    /// <c>default</c>, and every well-known kind would compare equal to every other one.
    /// </remarks>
    public static ChannelMessageKind PlainText => new(ChannelMessageKinds.PlainText);

    /// <summary>
    /// A request for the user's phone number (see <see cref="ChannelMessageKinds.PhoneNumberRequest"/>).
    /// </summary>
    public static ChannelMessageKind PhoneNumberRequest => new(ChannelMessageKinds.PhoneNumberRequest);

    /// <summary>
    /// A link to the claim completion form (see <see cref="ChannelMessageKinds.ClaimFormLink"/>).
    /// </summary>
    public static ChannelMessageKind ClaimFormLink => new(ChannelMessageKinds.ClaimFormLink);

    /// <summary>
    /// A text with link and callback buttons under it (see <see cref="ChannelMessageKinds.ActionButtons"/>).
    /// </summary>
    public static ChannelMessageKind ActionButtons => new(ChannelMessageKinds.ActionButtons);

    /// <summary>
    /// Rejects an empty code so that a constructed kind can never collide with <c>default</c>.
    /// </summary>
    /// <param name="value">Content kind code.</param>
    /// <returns>The validated code.</returns>
    private static string Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
