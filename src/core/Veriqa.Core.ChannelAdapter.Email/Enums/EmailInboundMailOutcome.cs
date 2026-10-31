// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.Enums;

/// <summary>
/// What the source of an inbound mail should do with it once the shared handler has answered.
/// </summary>
internal enum EmailInboundMailOutcome
{
    /// <summary>
    /// The mail did its work, now or on an earlier delivery: the sign-in is completed, the identity of a
    /// confirmation is attached, or the address of the email completion is recorded.
    /// </summary>
    Completed,

    /// <summary>
    /// The mail confirmed nothing: it failed the verification, belongs to no active transaction, or the
    /// processor could not produce a confirmation for it. The source does not hand it in again — the
    /// user starts the sign-in anew.
    /// </summary>
    Rejected,

    /// <summary>
    /// A transient failure: the mail is to be handed in again later.
    /// </summary>
    RetryLater
}
