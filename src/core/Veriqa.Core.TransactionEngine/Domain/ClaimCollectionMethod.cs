// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// How the value of a claim was obtained from the user (SPEC-041 C37, L58) — recorded with every
/// collected value, because a value obtained by a method no longer allowed is collected again.
/// </summary>
/// <remarks>
/// The numeric values are part of the storage format of a transaction.
/// </remarks>
public enum ClaimCollectionMethod
{
    /// <summary>
    /// Received in the channel on the request of the core (the phone number, SPEC-041 L44).
    /// </summary>
    ChannelRequest = 0,

    /// <summary>
    /// Entered into the form of the web step.
    /// </summary>
    Form = 1,

    /// <summary>
    /// Email confirmed by a message the user sent from that address (SPEC-041 C96 <c>Push</c>).
    /// </summary>
    EmailPush = 2,

    /// <summary>
    /// Email confirmed by a code sent to that address (SPEC-041 C96 <c>Code</c>).
    /// </summary>
    EmailCode = 3,

    /// <summary>
    /// Email confirmed by a link sent to that address (SPEC-041 C96 <c>MagicLink</c>).
    /// </summary>
    EmailMagicLink = 4,

    /// <summary>
    /// Email entered without a confirmation (SPEC-041 C96 <c>Unverified</c>).
    /// </summary>
    EmailUnverified = 5
}
