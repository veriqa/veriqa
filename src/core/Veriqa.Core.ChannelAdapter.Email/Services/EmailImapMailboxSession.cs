// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Enums;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// The connection of one tenant's polling loop to its mailbox: connected over TLS, signed in and
/// with the configured folder open. It is kept between two visits and reopened when it is lost or
/// when the settings it was opened with are no longer the effective ones.
/// </summary>
/// <remarks>
/// An instance belongs to one polling loop and is used by that loop only (the IMAP client is not
/// thread-safe). The protocol exchange is never logged: it carries the sign-in with its credentials.
/// The certificate of the server is checked by the defaults of the client, and nothing here relaxes them.
/// </remarks>
internal sealed class EmailImapMailboxSession : IAsyncDisposable
{
    /// <summary>
    /// SASL mechanism of a sign-in by an access token as standardized (RFC 7628) — the preferred one.
    /// </summary>
    private const string OAuthBearerMechanism = "OAUTHBEARER";

    /// <summary>
    /// SASL mechanism of a sign-in by an access token that predates the standard and is still the
    /// only one some servers announce.
    /// </summary>
    private const string XOAuth2Mechanism = "XOAUTH2";

    /// <summary>
    /// Access token of the mailbox, used when the sign-in is by OAuth 2.0.
    /// </summary>
    private readonly EmailImapOAuthTokenCache _tokens;

    /// <summary>
    /// Logger of the polling loop that owns this session.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// The IMAP client. Replaced by a fresh one whenever the connection is dropped, so a connection
    /// that failed half-way never leaks its state into the next one.
    /// </summary>
    private ImapClient _client = new();

    /// <summary>
    /// The open folder; null — the mailbox is not open.
    /// </summary>
    private IMailFolder? _folder;

    /// <summary>
    /// Fingerprint of the settings the mailbox was opened with.
    /// </summary>
    private string? _openedWith;

    /// <summary>
    /// Creates the mailbox session of one tenant's polling loop.
    /// </summary>
    /// <param name="tokens">Access token of the mailbox for the sign-in by OAuth 2.0.</param>
    /// <param name="logger">Logger of the owning polling loop.</param>
    public EmailImapMailboxSession(EmailImapOAuthTokenCache tokens, ILogger logger)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Whether the connection to the server is still up. A failure with the connection up concerns
    /// one mail; a failure with the connection down concerns the mailbox.
    /// </summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>
    /// Returns the configured folder, open for reading and flag changes: the one already open while
    /// the connection is alive and the settings are unchanged, otherwise a newly opened one.
    /// </summary>
    /// <param name="imap">Mailbox settings of the tenant, already judged by the validator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The open folder.</returns>
    public async Task<IMailFolder> OpenFolderAsync(EmailImapOptions imap, CancellationToken cancellationToken)
    {
        // The method reuses the open mailbox when it can and otherwise connects, signs in and opens the folder
        ArgumentNullException.ThrowIfNull(imap);

        var settings = Fingerprint(imap);

        if (_folder is { IsOpen: true }
            && _client.IsConnected
            && _client.IsAuthenticated
            && string.Equals(settings, _openedWith, StringComparison.Ordinal))
        {
            // One round trip that both proves the connection is alive and lets the server report
            // what has arrived since the previous visit.
            await _client.NoOpAsync(cancellationToken);
            return _folder;
        }

        await CloseAsync();

        // TLS always: implicit on the port reserved for it, an upgrade before the sign-in on any other.
        var tls = imap.Port == EmailAdapterConstants.ImapImplicitTlsPort
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        await _client.ConnectAsync(imap.Host, imap.Port, tls, cancellationToken);
        await AuthenticateAsync(imap, cancellationToken);

        IMailFolder folder;

        try
        {
            folder = await _client.GetFolderAsync(imap.Folder, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        }
        catch (FolderNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"The IMAP folder '{imap.Folder}' does not exist in the mailbox on '{imap.Host}'.",
                ex);
        }

        _folder = folder;
        _openedWith = settings;

        _logger.LogInformation(
            "Email IMAP polling: connected to {Host}:{Port}, folder {Folder} is open",
            imap.Host,
            imap.Port,
            imap.Folder);

        return folder;
    }

    /// <summary>
    /// Drops the connection: signs out politely when the server still listens, and replaces the
    /// client either way. Never throws — it runs on the failure path and at shutdown.
    /// </summary>
    public async Task CloseAsync()
    {
        _folder = null;
        _openedWith = null;

        if (_client.IsConnected)
        {
            try
            {
                // Bounded by its own deadline rather than by the token of the loop: the sign-out is
                // cleanup and has to run when the loop is being cancelled too.
                using var deadline = new CancellationTokenSource(
                    TimeSpan.FromSeconds(EmailAdapterConstants.ImapDisconnectTimeoutSeconds));

                await _client.DisconnectAsync(true, deadline.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Email IMAP polling: the sign-out from the mailbox did not complete");
            }
        }

        _client.Dispose();
        _client = new ImapClient();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _client.Dispose();
    }

    /// <summary>
    /// Signs in to the mailbox by the configured way: user name and password, or an access token.
    /// </summary>
    /// <param name="imap">Mailbox settings of the tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task AuthenticateAsync(EmailImapOptions imap, CancellationToken cancellationToken)
    {
        if (imap.AuthMode is not EmailImapAuthMode.OAuth2)
        {
            await _client.AuthenticateAsync(imap.Username, imap.Password, cancellationToken);
            return;
        }

        // Which mechanism to use is known only after the server has announced its own, so the
        // server is asked first and the token second — a server that takes no token costs no request.
        var mechanisms = _client.AuthenticationMechanisms;
        var useOAuthBearer = mechanisms.Contains(OAuthBearerMechanism);

        if (!useOAuthBearer && !mechanisms.Contains(XOAuth2Mechanism))
        {
            throw new InvalidOperationException(
                $"The IMAP server '{imap.Host}' announces neither {OAuthBearerMechanism} nor {XOAuth2Mechanism}, "
                + "so it cannot be signed in to with an access token.");
        }

        var accessToken = await _tokens.GetAccessTokenAsync(imap.OAuth, cancellationToken);

        SaslMechanism mechanism = useOAuthBearer
            ? new SaslMechanismOAuthBearer(imap.Username, accessToken)
            : new SaslMechanismOAuth2(imap.Username, accessToken);

        try
        {
            await _client.AuthenticateAsync(mechanism, cancellationToken);
        }
        catch (AuthenticationException)
        {
            // The mailbox refused this token (expired early, revoked, or issued without the right to
            // read mail): it must not be offered again.
            _tokens.Invalidate();
            throw;
        }
    }

    /// <summary>
    /// Computes the fingerprint of everything that decides which mailbox a connection belongs to
    /// and how it was signed in to. The secrets among the settings are compared through the digest
    /// and are not kept by the session.
    /// </summary>
    /// <param name="imap">Mailbox settings of the tenant.</param>
    /// <returns>Hex digest of the settings.</returns>
    private static string Fingerprint(EmailImapOptions imap)
    {
        // Only the credentials of the configured sign-in take part: the other set is ignored by the
        // sign-in, so a change in it is no reason to reconnect.
        var credentials = imap.AuthMode is EmailImapAuthMode.OAuth2
            ? EmailImapOAuthTokenCache.Fingerprint(imap.OAuth)
            : imap.Password;

        var identity = string.Join(
            '\0',
            imap.Host,
            imap.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            imap.Username,
            imap.AuthMode.ToString(),
            credentials,
            imap.Folder);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
