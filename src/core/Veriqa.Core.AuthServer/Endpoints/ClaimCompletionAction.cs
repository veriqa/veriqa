// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Action of the page of the claims completion (SPEC-041 C39, L108) — what the right to act is checked for.
/// </summary>
internal enum ClaimCompletionAction
{
    /// <summary>
    /// Submit the simple fields.
    /// </summary>
    Submit = 0,

    /// <summary>
    /// Cancel the sign-in.
    /// </summary>
    Cancel = 2,

    /// <summary>
    /// An action of the email block — held to the conditions of <see cref="Submit"/>: the web step shown on
    /// the page the request comes from.
    /// </summary>
    Email = 3
}
