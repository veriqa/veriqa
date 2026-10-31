// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Surface the web step of the claims completion is shown on (SPEC-041 L53) — decided when the web
/// step begins and stored with the transaction.
/// </summary>
/// <remarks>
/// The column of a relational store and the field of a document store hold the member name, the way
/// the state of a transaction is held.
/// </remarks>
public enum WebStepSurface
{
    /// <summary>
    /// The page of the device the sign-in was started on.
    /// </summary>
    InitiatingPage = 0,

    /// <summary>
    /// A page opened on the device that confirmed the sign-in (the link sent into the channel, the
    /// page of the email).
    /// </summary>
    CrossDevicePage = 1
}
