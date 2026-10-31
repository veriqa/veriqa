// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using OpenIddict.Abstractions;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The rule that names a failed sign-in to its OAuth 2.0 client: which <c>error</c> of the
/// authorization error response (RFC 6749 §4.1.2.1) stands for the reason the transaction was failed
/// with. The one place the two vocabularies meet (SPEC-002 §4.3).
/// </summary>
/// <remarks>
/// The client is told the class of the ending and nothing more. The reason code itself stays on the
/// transaction — in the audit trail and the logs — and is never sent as <c>error_description</c>: it
/// is a code of the core, and what it names (a policy, a channel, a step) is not the client's to learn.
/// </remarks>
internal static class SignInFailureOAuthError
{
    /// <summary>
    /// Resolves the OAuth 2.0 error code of a sign-in failed with the given reason.
    /// </summary>
    /// <remarks>
    /// <c>access_denied</c> — "the resource owner or authorization server denied the request": the
    /// user refused the sign-in, the party that asked for it called it off, or the server refused it
    /// by a rule of its own — a policy on the sender or the channel, the binding of the transaction to
    /// another subject, the per-user rate limit, a claim the client requires and the channel cannot
    /// supply. Trying the same request again meets the same answer. A sign-in superseded by a newer one
    /// of the same user is placed here as well, so that it could never read as a fault of the server;
    /// the callback does not return it to the client at all — it answers such a sign-in with the
    /// "session expired" page.
    /// <para>
    /// <c>server_error</c> — "an unexpected condition that prevented the server from fulfilling the
    /// request": every other reason, an unknown one and none at all. The list above is closed on
    /// purpose: a reason added to the engine later is reported as a fault of the server until it is
    /// placed here, so no new code can tell a client that its user refused when nobody did.
    /// </para>
    /// <para>
    /// The rule covers every refusal code of the engine, whether or not a sign-in is moved to
    /// <c>Failed</c> with it in the current composition: which codes reach that state is decided by
    /// the callers of the engine, not here.
    /// </para>
    /// </remarks>
    /// <param name="reasonCode">The reason code of the failed transaction; null or empty — none.</param>
    /// <returns><c>access_denied</c> or <c>server_error</c>.</returns>
    internal static string Resolve(string? reasonCode) => reasonCode switch
    {
        TransactionErrorCodes.DeclinedByUser
            or TransactionErrorCodes.CancelledByClient
            or TransactionErrorCodes.SecurityPolicyViolation
            or TransactionErrorCodes.RestrictionPolicyViolation
            or TransactionErrorCodes.BotRejected
            or TransactionErrorCodes.ChannelNotAllowed
            or TransactionErrorCodes.ChannelSubjectMismatch
            or TransactionErrorCodes.UserRateLimitExceeded
            or TransactionErrorCodes.RequiredClaimUnobtainable
            or TransactionErrorCodes.SupersededByNewSignIn => OpenIddictConstants.Errors.AccessDenied,
        _ => OpenIddictConstants.Errors.ServerError
    };
}
