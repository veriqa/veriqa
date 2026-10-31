// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using MailKit;
using MailKit.Search;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// Per-tenant supervisor that reads the Push-mode mailbox over IMAP (SPEC-016 §7.2): the inbound
/// provider that needs neither a mail provider calling the host nor a public address of the host.
/// </summary>
/// <remarks>
/// <para>
/// The supervisor takes the tenants that pull their inbound mail from <see cref="IPollingTenantSource"/>
/// and runs one loop per tenant, each with its own connection, its own credentials and its own
/// access token. A deployment whose inbound provider is not the mailbox has no such tenant and
/// therefore no loop and no connection.
/// </para>
/// <para>
/// A loop visits the mailbox no less often than the configured interval over a connection it keeps
/// open. A visit takes the unread mails of the folder that are recent enough to carry a live token
/// and hands each one to <see cref="EmailInboundMailPipeline"/> — the very handler the webhook
/// calls; what the loop adds is only the answer of its transport to the outcome: a handled or a
/// refused mail is marked as read, anything else stays unread and is taken again on a later visit
/// for as long as it is inside the window. Mails are never deleted.
/// </para>
/// </remarks>
internal sealed class EmailImapPollingService : PerTenantPollingSupervisor
{
    /// <summary>
    /// What a mail is asked for before it is read: enough to place it against the window and the size limit.
    /// </summary>
    private const MessageSummaryItems MailFacts =
        MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Size;

    /// <summary>
    /// Scope factory: every mail is handled in a scope of its own, and the handler takes its
    /// collaborators from that scope — this service is a singleton and must not capture them.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Canonical layer resolver: the mailbox settings are the tenant's and are resolved on every visit.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Core-level Email settings — the default the token lifetime of a tenant falls back to.
    /// </summary>
    private readonly IOptionsMonitor<EmailOptions> _options;

    /// <summary>
    /// Factory of the client the access token of a mailbox is requested with.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Clock of the window and of the pauses.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the mailbox polling service.
    /// </summary>
    /// <param name="tenantSource">Source of the tenants that pull their inbound mail.</param>
    /// <param name="scopeFactory">Scope factory the scope of each mail is created by.</param>
    /// <param name="resolver">Canonical layer resolver.</param>
    /// <param name="options">Core-level Email settings.</param>
    /// <param name="httpClientFactory">Factory of the client of the token endpoint.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public EmailImapPollingService(
        IPollingTenantSource tenantSource,
        IServiceScopeFactory scopeFactory,
        IConfigurationResolver resolver,
        IOptionsMonitor<EmailOptions> options,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<EmailImapPollingService> logger)
        : base(tenantSource, logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    protected override string ChannelType => ChannelTypes.Email;

    /// <inheritdoc />
    protected override int RetryBaseDelayMilliseconds => EmailAdapterConstants.ImapRetryBaseDelayMs;

    /// <summary>
    /// The loop of one tenant: visits the mailbox, pauses, and visits again until cancelled. A
    /// refusal of the mailbox or of the authorization server never leaves the loop — it is logged
    /// without secrets and retried after a growing pause, so it takes down neither the application
    /// nor the loop of another tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier (<c>null</c> — the default implicit tenant).</param>
    /// <param name="cancellationToken">Cancellation token of the tenant's loop.</param>
    protected override async Task RunTenantPollLoopAsync(string? tenantId, CancellationToken cancellationToken)
    {
        // The token and the connection belong to this loop, that is to this tenant alone.
        var tokens = new EmailImapOAuthTokenCache(_httpClientFactory, _timeProvider, Logger);
        await using var mailbox = new EmailImapMailboxSession(tokens, Logger);

        var retryDelayMs = EmailAdapterConstants.ImapRetryBaseDelayMs;

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan pause;

            try
            {
                // Tenant context around the visit: the settings, the handler and everything it
                // calls take this tenant's values.
                using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

                var visit = await BeginVisitAsync(mailbox, cancellationToken);

                // The mailbox was signed in to and searched — it is reachable again, so the next
                // refusal starts from the shortest pause.
                retryDelayMs = EmailAdapterConstants.ImapRetryBaseDelayMs;

                await HandleMailsAsync(visit, mailbox, cancellationToken);

                pause = visit.PollInterval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal completion when the tenant loop or the host is stopped.
                break;
            }
            catch (Exception ex)
            {
                // Whatever is left of the connection is dropped: the next visit opens a new one.
                await mailbox.CloseAsync();

                Logger.LogError(
                    ex,
                    "Email IMAP polling of tenant {TenantId} failed. Retrying in {DelayMs} ms",
                    tenantId ?? ChannelTenantContext.DefaultTenantDisplayName,
                    retryDelayMs);

                pause = TimeSpan.FromMilliseconds(retryDelayMs);
                retryDelayMs = Math.Min(
                    retryDelayMs * EmailAdapterConstants.ImapRetryBackoffMultiplier,
                    EmailAdapterConstants.ImapRetryMaxDelayMs);
            }

            await Task.Delay(pause, _timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Opens a visit: resolves the mailbox settings of the tenant, makes sure the mailbox is open
    /// and asks it for the mails worth looking at.
    /// </summary>
    /// <param name="mailbox">The mailbox session of the loop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the visit found.</returns>
    private async Task<MailboxVisit> BeginVisitAsync(
        EmailImapMailboxSession mailbox,
        CancellationToken cancellationToken)
    {
        // The settings are resolved per visit, not once per loop: they are reloadable, and a
        // change has to reach a running loop without a restart.
        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(_resolver, Logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"The Email settings of the tenant could not be resolved. Error: {credentialsResult.Error.Code}.");
        }

        var credentials = credentialsResult.Value;
        var inbound = credentials.Inbound;

        if (inbound.Provider is not EmailInboundProvider.ImapPolling)
        {
            throw new InvalidOperationException(
                $"The inbound provider of the tenant is {inbound.Provider}, so there is no mailbox to read.");
        }

        // Values resolved for a tenant never pass the start-up validation, so the same rule judges
        // them here. Its messages name configuration addresses only, never a secret.
        var violations = EmailOptionsValidator.ImapPollingViolations(inbound);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "The mailbox settings of the tenant cannot be used. " + string.Join(' ', violations));
        }

        var folder = await mailbox.OpenFolderAsync(inbound.Imap, cancellationToken);

        // The token TTL is the age up to which a mail is taken at all: an older one is left as it is.
        var tokenTtl = EmailCredentialsResolver.ResolveTokenTtl(credentials, _options.CurrentValue.TokenTtl);
        var windowStart = _timeProvider.GetUtcNow() - tokenTtl;

        // The server compares dates only, and in the zone the mail was stored with, so its filter
        // is a coarse one — a day earlier than the window, to lose nothing at midnight. The window
        // itself is applied to the exact date of each mail in HandleMailsAsync.
        var notBefore = windowStart.UtcDateTime.AddDays(-1);
        var unread = await folder.SearchAsync(
            SearchQuery.NotSeen.And(SearchQuery.DeliveredAfter(notBefore)),
            cancellationToken);

        return new MailboxVisit(
            inbound,
            folder,
            unread,
            windowStart,
            TimeSpan.FromSeconds(inbound.Imap.PollIntervalSeconds));
    }

    /// <summary>
    /// Handles the mails of a visit that are inside the window, in the order the mailbox received them.
    /// </summary>
    /// <param name="visit">What the visit found.</param>
    /// <param name="mailbox">The mailbox session of the loop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task HandleMailsAsync(
        MailboxVisit visit,
        EmailImapMailboxSession mailbox,
        CancellationToken cancellationToken)
    {
        if (visit.Unread.Count is 0)
        {
            return;
        }

        var facts = await visit.Folder.FetchAsync(visit.Unread, MailFacts, cancellationToken);
        var mails = new List<MailFact>(facts.Count);

        foreach (var fact in facts)
        {
            // A mail the server stated no date or no size for can be placed against neither the
            // window nor the size limit; it is left as it is. So is a mail older than the window:
            // it is neither read nor marked.
            if (fact.InternalDate is { } receivedAt && fact.Size is { } size && receivedAt >= visit.WindowStart)
            {
                mails.Add(new MailFact(fact.UniqueId, receivedAt, size));
            }
        }

        foreach (var mail in mails.OrderBy(static candidate => candidate.ReceivedAt).ThenBy(static candidate => candidate.Uid.Id))
        {
            try
            {
                await HandleMailAsync(visit, mail, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && mailbox.IsConnected)
            {
                // The failure is this mail's own: it stays unread and is taken again on a later
                // visit, and the mails after it are still handled. A failure that took the
                // connection down is not caught here — it ends the visit and is retried as one.
                Logger.LogError(
                    ex,
                    "Email IMAP polling: mail {Uid} of folder {Folder} could not be handled and stays unread",
                    mail.Uid.Id,
                    visit.Folder.FullName);
            }
        }
    }

    /// <summary>
    /// Handles one mail: reads it, hands it to the shared handler of inbound mail and answers the
    /// outcome for this transport.
    /// </summary>
    /// <param name="visit">The visit the mail belongs to.</param>
    /// <param name="mail">What is known about the mail before it is read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task HandleMailAsync(MailboxVisit visit, MailFact mail, CancellationToken cancellationToken)
    {
        var folder = visit.Folder;

        if (mail.Size > EmailAdapterConstants.MaxInboundBodyBytes)
        {
            // The same limit the webhook holds a mail to. The mail is not loaded at all, and it is
            // marked so that it is not weighed again on every visit.
            Logger.LogWarning(
                "Email IMAP polling: mail {Uid} of folder {Folder} is {Size} bytes, over the {Limit} byte limit; marked as read without being loaded",
                mail.Uid.Id,
                folder.FullName,
                mail.Size,
                EmailAdapterConstants.MaxInboundBodyBytes);

            await folder.AddFlagsAsync(mail.Uid, MessageFlags.Seen, true, cancellationToken);
            return;
        }

        // Reading a mail does not mark it: the flag is set below, by the outcome only.
        using var content = await folder.GetMessageAsync(mail.Uid, cancellationToken);

        // A mail without a Message-ID is deduplicated by where it lies. A UID is unique only within
        // one mailbox and one UIDVALIDITY of its folder, so both are part of the identifier.
        var message = EmailImapMessageReader.Read(
            content,
            $"{TenantKey.Segment(ChannelTenantContext.CurrentTenantId)}:{folder.FullName}:{folder.UidValidity}:{mail.Uid.Id}",
            mail.ReceivedAt,
            visit.Inbound);

        await using var scope = _scopeFactory.CreateAsyncScope();

        var processor = scope.ServiceProvider.GetService<IEmailInboundProcessor>()
            ?? throw new InvalidOperationException(
                "No inbound processor is registered, so the mail cannot be verified. "
                + "The channel composition is fixed at startup; restart the host to serve inbound mail.");

        var outcome = await EmailInboundMailPipeline.HandleAsync(
            scope.ServiceProvider,
            processor,
            visit.Inbound,
            message,
            cancellationToken);

        switch (outcome)
        {
            case EmailInboundMailOutcome.Completed:
            case EmailInboundMailOutcome.Rejected:
                // Handled for good either way: nothing would change on a second reading.
                await folder.AddFlagsAsync(mail.Uid, MessageFlags.Seen, true, cancellationToken);
                break;

            case EmailInboundMailOutcome.RetryLater:
                Logger.LogInformation(
                    "Email IMAP polling: mail {Uid} of folder {Folder} is left unread to be handled again on a later visit",
                    mail.Uid.Id,
                    folder.FullName);
                break;
        }
    }

    /// <summary>
    /// What a visit to the mailbox found.
    /// </summary>
    /// <param name="Inbound">Inbound settings of the tenant the visit was made with.</param>
    /// <param name="Folder">The open folder.</param>
    /// <param name="Unread">Unread mails the coarse filter of the server let through.</param>
    /// <param name="WindowStart">The earliest moment of receipt a mail is still handled for.</param>
    /// <param name="PollInterval">The pause before the next visit.</param>
    private sealed record MailboxVisit(
        EmailInboundOptions Inbound,
        IMailFolder Folder,
        IList<UniqueId> Unread,
        DateTimeOffset WindowStart,
        TimeSpan PollInterval);

    /// <summary>
    /// What is known about a mail before it is read.
    /// </summary>
    /// <param name="Uid">Identifier of the mail in the folder.</param>
    /// <param name="ReceivedAt">The moment the mailbox received the mail.</param>
    /// <param name="Size">Size of the mail in bytes.</param>
    private readonly record struct MailFact(UniqueId Uid, DateTimeOffset ReceivedAt, uint Size);
}
