// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Logging;

using MimeKit;

using QRCoder;

using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;
using Veriqa.Core.ChannelAdapter.Email.Templates;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// SMTP implementation of the outbound email delivery provider (SPEC-016 §7.1).
/// Uses MailKit to connect to the SMTP server with STARTTLS/SSL support.
/// Registered as a Singleton: keeps a single persistent TCP+TLS+AUTH connection,
/// reused across requests. Serialization via SemaphoreSlim guarantees
/// thread safety (MailKit SmtpClient is not thread-safe).
/// On a failure the connection is reset; the next call reconnects automatically.
/// Credentials are resolved per send (SPEC-003 §17.4), so the connection is reused only while the
/// effective credentials still match the snapshot it was opened with — otherwise it is dropped and
/// reopened (SPEC-003 §17.8). No per-tenant client cache: at N=1 the effective credentials are the core
/// ones, so the snapshot matches for as long as the configuration does and one connection serves every send.
/// The mail body itself belongs to the template layer (<see cref="IEmailMessageComposer"/>, and
/// <see cref="ClaimCompletionMailComposer"/> for the mails of the claims completion): this class receives a
/// ready subject and body and only transports them — every kind of mail over the one connection below.
/// </summary>
internal sealed class SmtpEmailOutboundSender : IEmailOutboundSender, IAsyncDisposable
{
    /// <summary>
    /// Name of the sign-in mail in the log.
    /// </summary>
    private const string LoginMailKind = "sign-in";

    /// <summary>
    /// Name of a mail of the claims completion in the log.
    /// </summary>
    private const string ClaimCompletionMailKind = "claims completion";

    /// <summary>
    /// Canonical layer resolver (SPEC-003 §17.4, SPEC-012 CFG-234): resolves the Email tenant-credential
    /// group per send. Never snapshot the options in the constructor — that would pin one tenant's
    /// credentials into a singleton and defeat configuration reload.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Mail body assembly over the template registry (SPEC-003 CA-160).
    /// </summary>
    private readonly IEmailMessageComposer _composer;

    /// <summary>
    /// Body assembly of the mails of the claims completion (SPEC-041 L51).
    /// </summary>
    private readonly ClaimCompletionMailComposer _claimCompletionComposer;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<SmtpEmailOutboundSender> _logger;

    /// <summary>
    /// The persistent SMTP client, reused across sends.
    /// Access only through <see cref="_lock"/>.
    /// </summary>
    private readonly SmtpClient _client = new();

    /// <summary>
    /// Semaphore for exclusive access to <see cref="_client"/>.
    /// MailKit SmtpClient is not thread-safe.
    /// </summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Snapshot of the credentials <see cref="_client"/> is currently connected with; null — no
    /// connection has been established yet. Guarded by <see cref="_lock"/>, like the client itself.
    /// Exists because credentials are resolved per send (per tenant, and reloadable): the open
    /// connection is only reusable while the effective credentials still match this snapshot.
    /// </summary>
    private SmtpConnectionCredentials? _connectedWith;

    /// <summary>
    /// Creates the SMTP delivery provider.
    /// </summary>
    /// <param name="resolver">Canonical layer resolver (SPEC-003 §17.4).</param>
    /// <param name="composer">Mail body assembly over the template registry.</param>
    /// <param name="claimCompletionComposer">Body assembly of the mails of the claims completion.</param>
    /// <param name="logger">Logger.</param>
    public SmtpEmailOutboundSender(
        IConfigurationResolver resolver,
        IEmailMessageComposer composer,
        ClaimCompletionMailComposer claimCompletionComposer,
        ILogger<SmtpEmailOutboundSender> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _claimCompletionComposer = claimCompletionComposer ?? throw new ArgumentNullException(nameof(claimCompletionComposer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool SupportsClaimCompletionEmail => true;

    /// <inheritdoc />
    public Task<EmailSendResult> SendLoginEmailAsync(
        EmailLoginMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return SendAsync(
            async (outbound, token) => BuildLoginMimeMessage(message, await _composer.ComposeAsync(message, token), outbound),
            LoginMailKind,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<EmailSendResult> SendClaimCompletionEmailAsync(
        EmailClaimCompletionMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return SendAsync(
            async (outbound, token) => BuildMimeMessage(
                message.ToAddress,
                await _claimCompletionComposer.ComposeAsync(message, token),
                outbound,
                attachQrImage: null),
            ClaimCompletionMailKind,
            cancellationToken);
    }

    /// <summary>
    /// Sends one mail over the persistent SMTP connection — the one transport every kind of mail of this
    /// provider shares, so a mail of the claims completion opens no second client.
    /// </summary>
    /// <param name="buildMessage">
    /// Builds the MIME message out of the effective outbound settings of the tenant (the From branding).
    /// </param>
    /// <param name="mailKind">What is being sent, for the log only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The email send result.</returns>
    private async Task<EmailSendResult> SendAsync(
        Func<EmailOutboundOptions, CancellationToken, ValueTask<MimeMessage>> buildMessage,
        string mailKind,
        CancellationToken cancellationToken)
    {
        // The method builds the email and sends it over the persistent SMTP connection.
        // The connection is established on the first call or after a failure.

        // Resolve the tenant credentials per send (CA-162): the tenant comes from the ambient
        // context; null — the default implicit tenant (self-hosted N=1, behavior 1:1).
        // Missing credentials degrade exactly like an unconfigured SMTP section: log + Fail, no exception.
        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError(
                "Failed to resolve the tenant Email credentials to send the mail. Error: {ErrorCode}",
                credentialsResult.Error.Code);

            return EmailSendResult.Fail(
                EmailAdapterConstants.ErrorCodeDeliveryFailed,
                "Email credentials are not configured.");
        }

        var outbound = credentialsResult.Value.Outbound;

        var smtp = outbound.Smtp;
        if (smtp is null)
        {
            _logger.LogError("SMTP settings are not configured. Cannot send the mail.");
            return EmailSendResult.Fail(
                EmailAdapterConstants.ErrorCodeDeliveryFailed,
                "SMTP settings are not configured.");
        }

        // The startup validator sees the core options only, so credentials a tenant level supplies reach
        // this point unchecked. The same credential rule is therefore applied here, per send, before any
        // connection is opened: a half-filled pair would otherwise send anonymously, and a whitespace
        // value would go into AUTH. Refused as a failed send, not an exception — the path stays alive.
        var credentialsViolation = EmailOptionsValidator.SmtpCredentialsViolation(smtp);
        if (credentialsViolation is not null)
        {
            _logger.LogError(
                "The effective SMTP credentials are refused before connecting. Reason: {Reason}",
                credentialsViolation);

            return EmailSendResult.Fail(
                EmailAdapterConstants.ErrorCodeDeliveryFailed,
                credentialsViolation);
        }

        // The body belongs to the template layer: this provider only transports what it is handed.
        var email = await buildMessage(outbound, cancellationToken);

        // Snapshot of the effective credentials of this send — compared against the snapshot the
        // current connection was opened with (see below). Built outside the lock: it is a pure value.
        var effectiveCredentials = SmtpConnectionCredentials.From(smtp);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // A single invariant governs reuse: this connection may be reused exactly while
            // _connectedWith still describes it. Every teardown clears the snapshot, so everything that
            // used to need its own branch — credentials changed between sends, the previous send
            // failed, a rejected AUTH left the socket open, a teardown that itself failed — arrives
            // here as "the snapshot no longer describes this connection" and reconnects.
            // It fails closed by construction: a connection we cannot vouch for is never reused, which
            // is what keeps one tenant's mail off another tenant's authenticated session. Everything
            // runs inside the existing semaphore — no second synchronization primitive, and two
            // connections can never exist at once.
            var snapshotDescribesConnection = _client.IsConnected && effectiveCredentials.Equals(_connectedWith);

            // A live connection opened under a different snapshot is worth a word in the log: unlike
            // the other ways of losing reusability, this one is a configuration change, not a failure.
            if (_client.IsConnected && _connectedWith is not null && !snapshotDescribesConnection)
            {
                _logger.LogInformation(
                    "Effective SMTP credentials changed — reconnecting before the send. Host: {Host}:{Port}",
                    smtp.Host,
                    smtp.Port);

                await DisconnectSafelyAsync();
            }

            // Matching the snapshot only means we believe the connection is ours — not that it is still
            // alive. MailKit's IsConnected is a cached flag over the stream, so a relay that closed the
            // session on its own idle timeout still reads as connected until the next I/O. Probe it —
            // otherwise the send is the probe, and the user's sign-in pays for a connection that went
            // stale while nothing was being sent.
            if (snapshotDescribesConnection)
            {
                await DropStaleConnectionAsync(cancellationToken);

                // A failed probe drops the connection and with it the snapshot — re-read both.
                snapshotDescribesConnection = _client.IsConnected && effectiveCredentials.Equals(_connectedWith);
            }

            if (!snapshotDescribesConnection)
            {
                await ConnectAndAuthAsync(smtp, cancellationToken);
                _connectedWith = effectiveCredentials;
            }

            var sentMessageId = await _client.SendAsync(email, cancellationToken);

            _logger.LogInformation(
                "The {MailKind} mail was sent to [MASKED]. MessageId: {MessageId}",
                mailKind,
                sentMessageId);

            return EmailSendResult.Ok(sentMessageId);
        }
        catch (OperationCanceledException)
        {
            // On request cancellation the client may be in an intermediate state
            await DisconnectSafelyAsync();
            throw;
        }
        catch (Exception ex)
        {
            // After any failure we reset the connection: the next call reconnects cleanly.
            // The log text below warns about the trap this creates — the retry a user makes ("the mail
            // never arrived") can succeed on a fresh connection while nothing was actually fixed. It
            // deliberately asserts nothing about whether this particular teardown worked:
            // DisconnectSafelyAsync swallows a failed disconnect into a warning, so "the connection has
            // been dropped" would not always be true.
            await DisconnectSafelyAsync();

            _logger.LogError(
                ex,
                "Error sending the {MailKind} mail via SMTP {Host}:{Port}. A repeated attempt may succeed "
                    + "on a new connection while this cause persists — a retry that works is not evidence "
                    + "that the problem is gone.",
                mailKind,
                smtp.Host,
                smtp.Port);

            return EmailSendResult.Fail(
                EmailAdapterConstants.ErrorCodeDeliveryFailed,
                ex.Message);
        }
        finally
        {
            // try-catch in case DisposeAsync timed out and already called _lock.Dispose()
            try { _lock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Connects to the SMTP server and authenticates (if credentials are provided).
    /// </summary>
    /// <param name="smtp">SMTP settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task ConnectAndAuthAsync(EmailSmtpOptions smtp, CancellationToken cancellationToken)
    {
        // The method establishes the connection and authenticates against the SMTP server.

        // ConnectAsync refuses to run on an open client, and this method is reachable with one still
        // open: the caller reconnects whenever the snapshot stops describing the connection, and a
        // teardown that failed leaves the socket up while clearing the snapshot (DisconnectSafelyAsync
        // swallows its own error). Guaranteeing the precondition here rather than at the call site is
        // deliberate: the method owns "connect from scratch", so a future caller cannot forget it.
        // If the teardown fails again, ConnectAsync throws and the send fails — closed, never reused.
        if (_client.IsConnected)
        {
            await DisconnectSafelyAsync();
        }

        await _client.ConnectAsync(smtp.Host, smtp.Port, SelectSocketOptions(smtp), cancellationToken);

        // The question "does this configuration authenticate?" is answered by the options themselves,
        // because the options validator has to answer it identically — see EmailSmtpOptions.
        if (smtp.RequiresAuthentication())
        {
            await _client.AuthenticateAsync(smtp.Username, smtp.Password, cancellationToken);
        }
    }

    /// <summary>
    /// Chooses how the connection is secured: implicit TLS, mandatory STARTTLS, or STARTTLS only when
    /// the server advertises it.
    /// </summary>
    /// <remarks>
    /// MailKit does not refuse AUTH over a plain channel — it authenticates with whatever mechanism the
    /// server announced. With <see cref="SecureSocketOptions.Auto"/> a STARTTLS offer that is missing
    /// (or stripped by an intermediary) therefore sends the password in clear text, so a connection that
    /// authenticates insists on STARTTLS unless the deployment opted out through
    /// <see cref="EmailSmtpOptions.RequireStartTls"/>. An anonymous connection carries no secret and
    /// keeps <see cref="SecureSocketOptions.Auto"/>.
    /// </remarks>
    /// <param name="smtp">SMTP settings.</param>
    /// <returns>The socket options for <see cref="SmtpClient"/>.</returns>
    private static SecureSocketOptions SelectSocketOptions(EmailSmtpOptions smtp)
    {
        if (smtp.UseSsl)
        {
            return SecureSocketOptions.SslOnConnect;
        }

        if (smtp.RequireStartTls && smtp.RequiresAuthentication())
        {
            return SecureSocketOptions.StartTls;
        }

        return SecureSocketOptions.Auto;
    }

    /// <summary>
    /// Probes the open connection with NOOP and drops it when the server no longer answers, so the
    /// caller opens a fresh one instead of sending into a dead session.
    /// </summary>
    /// <remarks>
    /// Sparse traffic is the normal case for this channel — a magic link goes out when somebody signs
    /// in, which on a small deployment can mean hours between sends — so an idle timeout on the relay
    /// between two sends is expected rather than exceptional. Without the probe the first send after
    /// such a gap fails, the user sees "the mail never arrived", and the retry succeeds because the
    /// failure itself dropped the dead connection.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task DropStaleConnectionAsync(CancellationToken cancellationToken)
    {
        // The probe carries its own deadline. The failure it exists for is a flow discarded by a NAT or
        // firewall without a FIN or RST: nothing answers and nothing fails either, so without a deadline
        // of its own the probe would wait out MailKit's network timeout (SmtpClient.Timeout, which this
        // sender leaves at its default) or the caller's cancellation — spent holding the semaphore,
        // with every concurrent
        // sign-in queued behind it. A probe that is slow is a stale probe, which is the same answer.
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(TimeSpan.FromSeconds(EmailAdapterConstants.SmtpLivenessProbeTimeoutSeconds));

        try
        {
            await _client.NoOpAsync(probe.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The request itself was cancelled — not a stale connection; the caller owns this case
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(
                ex,
                "The reused SMTP connection did not answer NOOP within the probe deadline — it is treated as stale for this send.");

            await DisconnectSafelyAsync();
        }
        catch (SmtpCommandException ex) when (ex.StatusCode is not SmtpStatusCode.ServiceNotAvailable)
        {
            // The server answered with a refusal, and its code does not announce a closure, so the
            // connection is demonstrably alive — liveness is the only thing this probe tests. Dropping
            // it here would reconnect on every single send for as long as the relay keeps refusing NOOP.
            // 421 is excluded above precisely because it is not that case: it is how a relay announces
            // that it is closing an idle session, so the reply parses cleanly and arrives as this
            // exception type while the channel is going away — the very failure this probe exists for.
            _logger.LogInformation(
                ex,
                "The SMTP server refused NOOP with {StatusCode}, but answering proves the connection is alive — it is kept.",
                ex.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                ex,
                "The reused SMTP connection did not answer NOOP — it is treated as stale for this send.");

            await DisconnectSafelyAsync();
        }
    }

    /// <summary>
    /// Safely tears down the SMTP connection, ignoring disconnect errors.
    /// Called after any failure so the next call starts from a clean connection.
    /// </summary>
    private async Task DisconnectSafelyAsync()
    {
        // The method disconnects from the SMTP server without guaranteeing a graceful shutdown (quit: false)

        // The snapshot describes an open connection — once it is gone, so is the snapshot
        _connectedWith = null;

        if (!_client.IsConnected)
        {
            return;
        }

        try
        {
            await _client.DisconnectAsync(quit: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to drop the SMTP connection after a failure");
        }
    }

    /// <summary>
    /// Builds the MimeMessage of the sign-in mail: the envelope around the body, and the magic link QR
    /// code when the rendered body references it.
    /// </summary>
    /// <param name="message">The email data.</param>
    /// <param name="body">The ready mail body (subject, HTML and text parts, QR reference flag).</param>
    /// <param name="outbound">The effective tenant outbound settings (From branding), resolved per send.</param>
    /// <returns>The ready <see cref="MimeMessage"/>.</returns>
    private static MimeMessage BuildLoginMimeMessage(
        EmailLoginMessage message,
        EmailBodyContent body,
        EmailOutboundOptions outbound)
    {
        // The magic link QR code is embedded as a CID attachment (SPEC-016 §4.3, EM-105):
        // cross-device scenario — the email is opened on one device, sign-in completes on another.
        // A CID attachment is rendered by mail clients without loading external content.
        // The attachment is transport, the reference to it is body: it is generated only when the
        // template says it referenced the image, so a template without a QR does not ship an
        // attachment nothing points at.
        return BuildMimeMessage(
            message.ToAddress,
            body,
            outbound,
            body.EmbedsQrImage
                ? (BodyBuilder bodyBuilder) =>
                {
                    var qrImage = bodyBuilder.LinkedResources.Add(
                        EmailAdapterConstants.PullQrContentId + ".png",
                        GenerateQrPng(EmailMagicLink.Build(message)),
                        new ContentType("image", "png"));
                    qrImage.ContentId = EmailAdapterConstants.PullQrContentId;
                }
        : null);
    }

    /// <summary>
    /// Builds a MimeMessage around the body the template layer produced.
    /// </summary>
    /// <param name="toAddress">Recipient address.</param>
    /// <param name="body">The ready mail body (subject, HTML and text parts).</param>
    /// <param name="outbound">The effective tenant outbound settings (From branding), resolved per send.</param>
    /// <param name="attachQrImage">Adds the linked image the body references; null — the mail has none.</param>
    /// <returns>The ready <see cref="MimeMessage"/>.</returns>
    private static MimeMessage BuildMimeMessage(
        string toAddress,
        EmailBodyContent body,
        EmailOutboundOptions outbound,
        Action<BodyBuilder>? attachQrImage)
    {
        // The method builds a MimeMessage with the HTML email body
        var email = new MimeMessage();

        email.From.Add(new MailboxAddress(
            outbound.FromName,
            outbound.FromAddress));

        email.To.Add(MailboxAddress.Parse(toAddress));

        // Reply-To (SPEC-016 EM-033: a presentation header, not identity): when the tenant set a
        // Reply-To address, add it so replies go to a monitored mailbox rather than the From sender.
        // The value is validated at startup (EmailOptionsValidator); a null/blank value adds no header.
        if (!string.IsNullOrWhiteSpace(outbound.ReplyToAddress))
        {
            email.ReplyTo.Add(MailboxAddress.Parse(outbound.ReplyToAddress));
        }

        email.Subject = body.Subject;

        // A part the composer returned empty is a part the mail does not have: no step of the template
        // ladder states that edition, which is how a deployment says "this mail is HTML only" (or the
        // other way round, SPEC-016 §4.3). Assigning it anyway would ship an empty text/plain — an empty
        // screen in a client that prefers it, rather than a part the client simply does not find.
        var bodyBuilder = new BodyBuilder();

        if (!string.IsNullOrEmpty(body.HtmlBody))
        {
            bodyBuilder.HtmlBody = body.HtmlBody;
        }

        if (!string.IsNullOrEmpty(body.TextBody))
        {
            bodyBuilder.TextBody = body.TextBody;
        }

        attachQrImage?.Invoke(bodyBuilder);

        email.Body = bodyBuilder.ToMessageBody();

        return email;
    }

    /// <summary>
    /// Generates the magic link QR-code PNG (encodings and correction level — as in the auth page QR).
    /// </summary>
    /// <param name="magicLink">The magic link URL.</param>
    /// <returns>The QR-code PNG bytes.</returns>
    private static byte[] GenerateQrPng(string magicLink)
    {
        // The method builds the magic link QR code for cross-device sign-in from the email
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(magicLink, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(EmailAdapterConstants.PullQrPixelsPerModule);
    }

    /// <summary>
    /// Identity of the SMTP connection: the credential values that determine which server and which
    /// account a connection belongs to. Used to decide whether an open connection may be reused for
    /// the next send (the credentials are resolved per send and may differ per tenant, or change on
    /// configuration reload).
    /// The password is held as a hash, never in clear text: the snapshot outlives the send, and the
    /// class must not keep the secret around longer than needed (core-rules §10). The hash is used
    /// only for equality — it is never logged and never leaves this type.
    /// </summary>
    /// <param name="Host">SMTP host.</param>
    /// <param name="Port">SMTP port.</param>
    /// <param name="UseSsl">Whether an implicit SSL connection is used.</param>
    /// <param name="RequireStartTls">
    /// Whether STARTTLS is mandatory before AUTH — part of the identity because it decides how the
    /// connection was secured: a plain connection opened under <c>false</c> must not be reused once the
    /// setting turns <c>true</c>.
    /// </param>
    /// <param name="Username">Account name (empty — no authentication).</param>
    /// <param name="PasswordHash">Stable hash of the password (empty — no password).</param>
    private sealed record SmtpConnectionCredentials(
        string Host,
        int Port,
        bool UseSsl,
        bool RequireStartTls,
        string Username,
        string PasswordHash)
    {
        /// <summary>
        /// Builds the connection-identity snapshot from the effective SMTP settings.
        /// </summary>
        /// <param name="smtp">Effective SMTP settings of this send.</param>
        /// <returns>The snapshot.</returns>
        public static SmtpConnectionCredentials From(EmailSmtpOptions smtp)
        {
            return new SmtpConnectionCredentials(
                smtp.Host ?? string.Empty,
                smtp.Port,
                smtp.UseSsl,
                smtp.RequireStartTls,
                smtp.Username ?? string.Empty,
                HashPassword(smtp.Password));
        }

        /// <summary>
        /// Computes a stable hash of the password so the snapshot never stores the secret in clear text.
        /// Not a password-storage hash: this value never leaves the process and is only compared for
        /// equality, so a plain cryptographic digest is sufficient (no KDF/salt needed — a salt would
        /// in fact break equality across sends).
        /// </summary>
        /// <param name="password">Password (null/empty — no password).</param>
        /// <returns>Hex hash, or an empty string when there is no password.</returns>
        private static string HashPassword(string? password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return string.Empty;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(hash);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The method gracefully ends the SMTP session and releases resources on application shutdown.
        // A short timeout: do not block shutdown on a stuck TCP SYN or a slow SMTP server.
        var acquired = await _lock.WaitAsync(TimeSpan.FromSeconds(EmailAdapterConstants.SmtpShutdownLockTimeoutSeconds));
        try
        {
            // Perform a graceful disconnect ONLY when the semaphore is acquired: SmtpClient is not
            // thread-safe, we must not touch _client while another thread owns it (an active
            // send). If the semaphore is not acquired within the timeout — skip the disconnect (review feedback).
            if (acquired && _client.IsConnected)
            {
                try
                {
                    await _client.DisconnectAsync(quit: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to gracefully finish the SMTP session on shutdown");
                }
            }
            else if (!acquired)
            {
                _logger.LogWarning(
                    "Failed to acquire the SMTP semaphore within {TimeoutSeconds} s on shutdown; graceful disconnect skipped.",
                    EmailAdapterConstants.SmtpShutdownLockTimeoutSeconds);
            }
        }
        finally
        {
            if (acquired)
            {
                _lock.Release();
            }
            _client.Dispose();
            _lock.Dispose();
        }
    }
}
