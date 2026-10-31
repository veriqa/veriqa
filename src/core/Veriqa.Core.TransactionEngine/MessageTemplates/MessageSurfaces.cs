// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.MessageTemplates;

/// <summary>
/// Values of the <c>surface</c> dimension of the template key (SPEC-036 §4.3, TPL-123): WHERE the
/// message is shown. Surfaces are constants, never magic strings — a render point names one the same
/// way it names its kind.
/// <para>
/// The channel refines the surface and never appears without one (SPEC-036 §4.6), so a render point
/// telling the channels of one kind apart names both: the surface first, the channel after it.
/// </para>
/// </summary>
public static class MessageSurfaces
{
    /// <summary>
    /// Inside the channel itself: a message of a messenger, or the mail a user sends back — as opposed
    /// to a page the product serves.
    /// </summary>
    public const string InChannel = "in-channel";

    /// <summary>
    /// A page of the core the product serves to the user, as opposed to the channel itself. In today's
    /// delivery the "Yes"/"No" question page asks the confirmation prompt at it, and the sign-in window and
    /// the launch page of a hop show the lines of the channels of the page at it (the answer of the bot,
    /// the started transition, the scanned QR code). Spelled as the receipt of an
    /// outcome spells the same place: one place of showing is written the same way in every group
    /// (SPEC-036 §4.3).
    /// </summary>
    public const string CorePage = "core-page";
}
