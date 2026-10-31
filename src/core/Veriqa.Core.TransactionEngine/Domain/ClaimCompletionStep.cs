// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Step of the claims completion a missing claim is collected on (SPEC-041 L91). The steps run in the
/// order of their values: the phone number in the channel first, then the web step.
/// </summary>
/// <remarks>
/// The numeric values are part of the storage format of a transaction (the list of missing claims is
/// stored with them) and their order is the order of the steps.
/// </remarks>
public enum ClaimCompletionStep
{
    /// <summary>
    /// The phone number, asked for in the channel of the transaction (SPEC-041 L44).
    /// </summary>
    Phone = 0,

    /// <summary>
    /// Email and the simple fields, collected on one page of the web step (SPEC-041 L52, L53).
    /// </summary>
    WebStep = 1
}
