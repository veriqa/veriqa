// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// What carried the right to act on the form of the claims completion (SPEC-041 N78, C39) — and so which
/// page the answer to the action belongs to.
/// </summary>
internal enum ClaimCompletionRightCarrier
{
    /// <summary>
    /// The binding of the page of the device the sign-in was started on: the AntiForgery token, the session
    /// identifier and the browser nonce cookie of the callback.
    /// </summary>
    InitiatingPage = 0,

    /// <summary>
    /// The form token of the page of the device that confirmed the sign-in.
    /// </summary>
    FormToken = 1
}
