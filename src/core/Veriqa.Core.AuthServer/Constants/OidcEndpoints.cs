// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// OIDC endpoint paths of the authorization server.
/// </summary>
public static class OidcEndpoints
{
    /// <summary>
    /// Authorization path (Authorization Endpoint).
    /// </summary>
    public const string Authorize = "/connect/authorize";

    /// <summary>
    /// Authorization callback path (callback after the transaction completes).
    /// </summary>
    public const string AuthorizeCallback = "/connect/authorize/callback";

    /// <summary>
    /// Login confirmation path on the web page (POST), LoginConfirmationMode.OnWebPage mode
    /// (SPEC-012 §4.4, SPEC-007 UI-038). The "Yes" button with an AntiForgery token completes the login.
    /// </summary>
    public const string AuthorizeCallbackConfirm = "/connect/authorize/callback/confirm";

    /// <summary>
    /// Login decline path on the web page (POST), the "No" answer to the same confirmation question
    /// (LoginConfirmationMode.OnWebPage). Fails the transaction as declined by the user and returns the
    /// standard OAuth 2.0 <c>access_denied</c> error to the client.
    /// </summary>
    public const string AuthorizeCallbackDecline = "/connect/authorize/callback/decline";

    /// <summary>
    /// Path the "Cancel sign-in" button of the sign-in window posts to (POST): the sign-in ends as declined
    /// by the user, with the same <c>access_denied</c> answer to the client as the "No" of the web
    /// confirmation page. Under the callback path, so the browser nonce cookie of the callback reaches it.
    /// </summary>
    public const string AuthorizeCallbackCancel = "/connect/authorize/callback/cancel";

    /// <summary>
    /// Path the form of the claims completion submits its fields to (POST), on the page of the device the
    /// sign-in was started on (SPEC-041 C39). Under the callback path, so the browser nonce cookie of the
    /// callback reaches it.
    /// </summary>
    public const string AuthorizeCallbackClaims = "/connect/authorize/callback/claims";

    /// <summary>
    /// Path the form of the claims completion posts "cancel sign-in" to (POST): the sign-in ends as declined
    /// by the user, with the same answer to the client as the "No" of the web confirmation page
    /// (SPEC-041 C39, L109).
    /// </summary>
    public const string AuthorizeCallbackClaimsCancel = "/connect/authorize/callback/claims/cancel";

    /// <summary>
    /// Path the email block of the form of the claims completion posts its actions to (POST, SPEC-041 C39,
    /// L108): send a mail with a code or a link, check the code, save the address without verification,
    /// switch the way of obtaining the email and show the block again — told apart
    /// by a field of the form. Accepted from both surfaces of the web step with the right of the form.
    /// </summary>
    public const string AuthorizeCallbackClaimsEmail = "/connect/authorize/callback/claims/email";

    /// <summary>
    /// Page of the claims completion on the device that confirmed the sign-in (GET, SPEC-041 L52, N78): the
    /// link of the channel message opens it with the session identifier and the form token. Reading only —
    /// opening it spends nothing; its form posts to the action paths of the callback with the form token.
    /// The <c>/auth/…</c> base is the one of the user-facing pages that are not part of the OIDC protocol.
    /// </summary>
    public const string ClaimCompletionFormPage = "/auth/claims";

    /// <summary>
    /// Token exchange path (Token Endpoint).
    /// </summary>
    public const string Token = "/connect/token";

    /// <summary>
    /// User information path (UserInfo Endpoint).
    /// </summary>
    public const string UserInfo = "/connect/userinfo";

    /// <summary>
    /// Token revocation path (Revocation Endpoint, RFC 7009).
    /// </summary>
    public const string Revoke = "/connect/revoke";

    /// <summary>
    /// Token introspection path (Introspection Endpoint, RFC 7662). The response is formed by
    /// OpenIddict itself and carries token metadata only; claims are read from the userinfo endpoint.
    /// </summary>
    public const string Introspect = "/connect/introspect";

    /// <summary>
    /// Base path for the transaction status polling endpoint (SPEC-007 §5.4).
    /// Full path: /api/transaction/{id}/status
    /// </summary>
    public const string TransactionStatusBase = "/api/transaction";

    /// <summary>
    /// Server-to-server creation of a confirmation transaction (SPEC-039 C14). Same base path as the
    /// status surface, because it addresses the same resource — a transaction.
    /// </summary>
    public const string ConfirmationTransaction = TransactionStatusBase + "/confirmation";

    /// <summary>
    /// Server-to-server deletion of the collected claims of a subject (SPEC-041 C82):
    /// <c>DELETE</c> with the subject and, optionally, the application in the query — a subject of an
    /// integrator may contain a slash, so it is not a path segment. Mapped only where a collected claims
    /// store is registered.
    /// </summary>
    public const string CollectedClaims = "/api/collected-claims";

    /// <summary>
    /// Server-to-server exchange of a link handle for the identity of the sign-in it was issued for
    /// (<c>POST</c> with the handle in a JSON body). The handle is the value put into the address of a
    /// post-completion button in place of its placeholder. Mapped only where a link handle store is
    /// registered.
    /// </summary>
    public const string LinkHandleRedeem = "/api/link-handle/redeem";

    /// <summary>
    /// Channel-agnostic entry page of an ALREADY CREATED confirmation transaction (SPEC-039 C15),
    /// addressed by its public identifier. The <c>/auth/…</c> base is the one the user-facing pages of
    /// the contour already live under; the page is not part of the OIDC protocol surface, which is why
    /// it is not under <c>/connect/…</c>.
    /// </summary>
    public const string TransactionEntryPage = "/auth/transaction";

    /// <summary>
    /// Page of the core that ASKS the subject of a confirmation transaction and takes the user's
    /// answer (SPEC-039 R37). It sits under the entry page it continues, and outside
    /// <c>/connect/…</c> for the same reason: a transaction created server-to-server has no OIDC
    /// request behind it.
    /// </summary>
    public const string TransactionConfirmPage = TransactionEntryPage + "/confirm";

    /// <summary>
    /// The "Yes" answer to the question of <see cref="TransactionConfirmPage"/> (POST): finalizes the
    /// transaction on the server. Carries the AntiForgery token and the session_id.
    /// </summary>
    public const string TransactionConfirmAccept = TransactionConfirmPage + "/accept";

    /// <summary>
    /// The "No" answer to the same question (POST): records the refusal. Guarded exactly like the
    /// confirmation — declining changes state too.
    /// </summary>
    public const string TransactionConfirmDecline = TransactionConfirmPage + "/decline";

    /// <summary>
    /// Path the cancel button of the pages held by the session identifier posts to (POST, SPEC-007 UI-122):
    /// "Cancel confirmation" of the entry page of a confirmation, and "Cancel sign-in" or "Cancel
    /// confirmation" of the scanner page of the unified hop QR code. The transaction of either type ends as
    /// declined by the user, and the browser is shown the receipt of the refusal, worded by the type. Under
    /// the entry page, beside the question; guarded by the AntiForgery token like both answers. The sign-in
    /// window keeps its own path, <see cref="AuthorizeCallbackCancel"/>.
    /// </summary>
    public const string TransactionCancel = TransactionEntryPage + "/cancel";
}
