// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Text;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Content of a <see cref="ChannelMessageKind.ClaimFormLink"/> message: one button under the message
/// text (<see cref="ChannelMessage.Text"/>) that opens the claim completion form on the device that
/// confirmed the sign-in (SPEC-041 C76).
/// </summary>
/// <remarks>
/// The link carries the form token — the right to act on the form (SPEC-041 N78) — so it is a secret:
/// an adapter opens it by the platform's own URL button and never writes it to a log. The text
/// representation of this record leaves the link out for the same reason.
/// </remarks>
public sealed record ClaimFormLinkContent
{
    /// <summary>
    /// Absolute https URL of the form page, the form token included.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Label of the button, already localized by the caller.
    /// </summary>
    public required string ButtonLabel { get; init; }

    /// <summary>
    /// Writes the members of the text representation — the label only: the link carries the form
    /// token, and a record printed into a log or a debugger view must not reveal it.
    /// </summary>
    /// <param name="builder">Builder of the text representation.</param>
    /// <returns>true — members were written.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(nameof(ButtonLabel)).Append(" = ").Append(ButtonLabel);
        return true;
    }
}
