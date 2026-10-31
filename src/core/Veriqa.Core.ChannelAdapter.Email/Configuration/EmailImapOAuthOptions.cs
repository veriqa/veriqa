// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Enums;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// OAuth 2.0 settings of the mailbox sign-in. Read and validated only when
/// <see cref="EmailImapOptions.AuthMode"/> is <see cref="EmailImapAuthMode.OAuth2"/>.
/// </summary>
/// <remarks>
/// The section carries the long-lived data an access token is obtained WITH, never the access token
/// itself: that one lives for about an hour. Which of the two standard grants is used follows from
/// <see cref="RefreshToken"/> — set, the token is obtained by the refresh token (RFC 6749 §6, the
/// mailbox of a particular user); empty, by the credentials of the application (RFC 6749 §4.4,
/// access of the application without a user).
/// </remarks>
public sealed class EmailImapOAuthOptions
{
    /// <summary>
    /// Token endpoint of the authorization server. An absolute <c>https</c> URL.
    /// </summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the application registered with the authorization server.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Secret of the application. Required when <see cref="RefreshToken"/> is empty; with a refresh
    /// token it is sent only when set.
    /// Must be stored via the standard secrets mechanism from SPEC-012 (EM-052).
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Refresh token of the mailbox user, issued outside Veriqa. Empty — the access token is obtained
    /// by the credentials of the application.
    /// Must be stored via the standard secrets mechanism from SPEC-012 (EM-052).
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Requested scope. Required when <see cref="RefreshToken"/> is empty; with a refresh token it is
    /// sent only when set.
    /// </summary>
    public string Scope { get; set; } = string.Empty;
}
