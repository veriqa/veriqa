// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Security header constants (SPEC-007 §6.3).
/// </summary>
public static class SecurityHeaderConstants
{
    /// <summary>
    /// CSP nonce length in bytes (128 bits).
    /// </summary>
    public const int CspNonceLengthBytes = 16;

    /// <summary>
    /// HttpContext.Items key for the CSP nonce.
    /// Source of truth — <see cref="Veriqa.Core.ChannelAdapter.Constants.HttpContextItemKeys.CspNonce"/>:
    /// the key is shared with channel adapter pages (the lower layer), which also
    /// contain inline scripts and must inject the nonce (review feedback).
    /// </summary>
    public const string CspNonceItemKey = Veriqa.Core.ChannelAdapter.Constants.HttpContextItemKeys.CspNonce;

    /// <summary>
    /// HttpContext.Items key carrying the extra CSP source the <c>form-action</c> directive of THIS
    /// response must allow — the target a generated page posts to. It is written only by four pages
    /// (SPEC-007 UI-091): the auto-posting page of the <c>form_post</c> branches of the authorization
    /// response, which posts the issued code to the relying party's already validated
    /// <c>redirect_uri</c>; the web sign-in confirmation page, whose answers end on that same
    /// <c>redirect_uri</c> through a chain of redirects; the sign-in window that carries the
    /// "Cancel sign-in" button, whose post ends there the same way; and the page of the claims completion
    /// on the device the sign-in was started on, whose submission and "Cancel sign-in" end there as well.
    /// Every other response leaves the key unset and keeps the policy at <c>form-action 'self'</c> by
    /// construction — a sign-in window rendered without that button and the page of the claims
    /// completion on the device that confirmed the sign-in included.
    /// <para>
    /// The value is a CSP source expression, derived from the PARSED redirect URI rather than from the
    /// raw string, and never a wildcard: <c>form-action *</c> is not introduced under any circumstance.
    /// </para>
    /// </summary>
    public const string FormActionTargetItemKey = "Veriqa.Csp.FormActionTarget";

    /// <summary>
    /// Content-Security-Policy header.
    /// </summary>
    public const string ContentSecurityPolicyHeader = "Content-Security-Policy";

    /// <summary>
    /// X-Content-Type-Options header.
    /// </summary>
    public const string XContentTypeOptionsHeader = "X-Content-Type-Options";

    /// <summary>
    /// X-Content-Type-Options header value.
    /// </summary>
    public const string XContentTypeOptionsValue = "nosniff";

    /// <summary>
    /// X-Frame-Options header.
    /// </summary>
    public const string XFrameOptionsHeader = "X-Frame-Options";

    /// <summary>
    /// X-Frame-Options header value.
    /// </summary>
    public const string XFrameOptionsValue = "DENY";

    /// <summary>
    /// Referrer-Policy header.
    /// </summary>
    public const string ReferrerPolicyHeader = "Referrer-Policy";

    /// <summary>
    /// Referrer-Policy header value.
    /// </summary>
    public const string ReferrerPolicyValue = "strict-origin-when-cross-origin";
}
