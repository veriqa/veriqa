// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

// The channel contour has a ClaimCompletionConfigKeys of its own (the phone window), so the keys of the
// email completion are named through an alias rather than by an import of their namespace.
using EmailCompletionKeys = Veriqa.Core.TransactionEngine.Configuration.ClaimCompletionConfigKeys;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// The mail side of the email completion on the infrastructure of the Email adapter (SPEC-041 R12, L50,
/// L51, E61): the capabilities of a tenant, the code and link mails within their limits, the check of a
/// code and the prepared mail of the <c>Push</c> method. The web step asks it through
/// <see cref="IClaimCompletionEmail"/>; this is the one implementation, registered when the adapter is.
/// </summary>
/// <remarks>
/// <para>
/// Every operation runs in the tenant scope of the transaction, so the credentials, the public base URL
/// and the token lifetime it reads are those of the tenant of this sign-in.
/// </para>
/// <para>
/// No operation lets a failure of the store or of the configuration out: each answers with the outcome
/// the port names for it and logs a warning. Neither a code, nor a link token, nor an address in clear
/// text is logged — an address only through <see cref="ChannelIdentityMask.MaskEmail"/>.
/// </para>
/// </remarks>
internal sealed class EmailClaimCompletion : IClaimCompletionEmail
{
    /// <summary>
    /// Kind of the counter of one transaction.
    /// </summary>
    private const string TransactionCounterKind = "tx";

    /// <summary>
    /// Kind of the counter of one channel account.
    /// </summary>
    private const string SubjectCounterKind = "sub";

    /// <summary>
    /// Kind of the counter of one recipient address.
    /// </summary>
    private const string AddressCounterKind = "addr";

    /// <summary>
    /// Kind of the counter of one recipient domain.
    /// </summary>
    private const string DomainCounterKind = "dom";

    /// <summary>
    /// Kind of the counter that holds the minimal interval between the completion mails of one
    /// transaction. A kind of its own: the counter of the transaction lives as long as the transaction
    /// does, this one only as long as the interval, and the sign-in mail keeps an interval counter of its
    /// own — the first completion mail right after a sign-in by mail is not a repeat of that mail.
    /// </summary>
    private const string IntervalCounterKind = "claim-interval";

    /// <summary>
    /// Separator between the parts of the scope of a counter.
    /// </summary>
    private const string ScopeSeparator = ":";

    /// <summary>
    /// Global Email settings — the core-owned switches (<c>Enabled</c>, <c>PullEnabled</c>,
    /// <c>PushEnabled</c>), the normalization and the core default of the token lifetime.
    /// </summary>
    private readonly IOptionsMonitor<EmailOptions> _options;

    /// <summary>
    /// Canonical layer resolver: <c>channels_enabled</c>, the tenant credentials and the limits.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Store of the codes, the links, the chosen methods and the mail counters.
    /// </summary>
    private readonly IEmailClaimCompletionStore _store;

    /// <summary>
    /// Correlation store of the Push mode — the <c>Push</c> method correlates its mail the same way.
    /// </summary>
    private readonly IEmailPushCorrelationStore _correlations;

    /// <summary>
    /// The delivery provider; null — the host registered none (the mails cannot be sent).
    /// </summary>
    private readonly IEmailOutboundSender? _sender;

    /// <summary>
    /// Localizer of the subject and the body of the prepared <c>Push</c> mail.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Clock of the expiries.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<EmailClaimCompletion> _logger;

    /// <summary>
    /// Creates the mail side of the email completion.
    /// </summary>
    /// <param name="options">Global Email settings.</param>
    /// <param name="resolver">Canonical layer resolver.</param>
    /// <param name="store">Store of the email completion.</param>
    /// <param name="correlations">Correlation store of the Push mode.</param>
    /// <param name="senders">Registrations of the delivery provider; the last one is used, none is allowed.</param>
    /// <param name="localizer">Localizer of the prepared <c>Push</c> mail.</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public EmailClaimCompletion(
        IOptionsMonitor<EmailOptions> options,
        IConfigurationResolver resolver,
        IEmailClaimCompletionStore store,
        IEmailPushCorrelationStore correlations,
        IEnumerable<IEmailOutboundSender> senders,
        IConfirmationPromptLocalizer localizer,
        TimeProvider timeProvider,
        ILogger<EmailClaimCompletion> logger)
    {
        ArgumentNullException.ThrowIfNull(senders);

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _correlations = correlations ?? throw new ArgumentNullException(nameof(correlations));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // With Outbound.Provider = Custom the host registers the sender itself; a host that did not
        // leaves the mails unsendable rather than this service unresolvable.
        _sender = senders.LastOrDefault();
    }

    /// <inheritdoc />
    public async ValueTask<ClaimEmailCapabilities> GetCapabilitiesAsync(string? tenantId, CancellationToken cancellationToken)
    {
        try
        {
            return (await ResolveMailSideAsync(tenantId, cancellationToken))?.Capabilities ?? ClaimEmailCapabilities.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Asked at the start of the host too: a configuration that cannot be read yet answers
            // "nothing can be done" instead of failing the start from here.
            _logger.LogWarning(ex, "The capabilities of the Email adapter for the email completion could not be read; none are assumed.");
            return ClaimEmailCapabilities.None;
        }
    }

    /// <inheritdoc />
    public string? NormalizeAddress(string raw)
    {
        return ClaimEmailAddress.TakeBare(raw) is { } bare
            ? EmailAddressHelper.Normalize(bare, TryReadOptions()?.Normalization)
            : null;
    }

    /// <inheritdoc />
    public async Task<ClaimEmailSendOutcome> SendAsync(
        Transaction transaction,
        ClaimEmailMailContent content,
        string? normalizedAddress,
        string language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!Enum.IsDefined(content))
        {
            throw new ArgumentOutOfRangeException(nameof(content), content, "A completion mail carries a code, a link or both.");
        }

        var tenantId = transaction.GetTenantId();
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

        try
        {
            // The capabilities are asked again at the send: the configuration may have changed since the
            // block was shown, and a mail that cannot be sent must not spend a reservation.
            var mailSide = await ResolveMailSideAsync(tenantId, cancellationToken);
            var sender = _sender;
            if (mailSide is not { Capabilities.CanSend: true } || sender is null
                || (content.CarriesLink() && string.IsNullOrWhiteSpace(mailSide.Credentials.PublicBaseUrl)))
            {
                _logger.LogWarning(
                    "A completion mail was asked for, but the tenant cannot send one. TransactionId: {TransactionId}, Content: {Content}",
                    transaction.Id,
                    content);

                return ClaimEmailSendOutcome.DeliveryFailed;
            }

            var transactionId = transaction.Id.ToString();
            var record = await _store.GetAsync(transactionId, cancellationToken);

            // A repeat goes to the address of the last completion mail. The code and the link are one way
            // for the user, so the address is taken from what this mail carries first, and from the other
            // part only when the list of methods changed while the transaction was open.
            var address = normalizedAddress
                ?? (content.CarriesCode()
                    ? record?.Code?.Address ?? record?.Link?.Address
                    : record?.Link?.Address ?? record?.Code?.Address);

            if (address is null)
            {
                return ClaimEmailSendOutcome.NoAddress;
            }

            // A transaction past its time has no mail left to send, and its counter no window to count in.
            var now = _timeProvider.GetUtcNow();
            if (now >= transaction.ExpiresAt)
            {
                return ClaimEmailSendOutcome.TransactionLimit;
            }

            var limits = await ResolveLimitsAsync(transaction, tenantId, cancellationToken);
            var counters = MailCounters(
                transaction, tenantId, address, limits, mailSide.Options.Normalization, mailSide.Options.MailResendInterval, now);

            // One reservation per mail, whatever the mail carries: a mail with a code and a link is one mail.
            if (await _store.TryReserveMailAsync([.. counters.Select(counter => counter.Counter)], cancellationToken) is { } refused)
            {
                return counters[refused].Refusal;
            }

            // The code and the link of one mail share one lifetime, so the moment the mail names as "valid
            // until" is the expiry of both — and thus the earlier of the two.
            var expiresAt = EarlierOf(
                now.Add(EmailCredentialsResolver.ResolveTokenTtl(mailSide.Credentials, mailSide.Options.TokenTtl)),
                transaction.ExpiresAt);

            var code = content.CarriesCode() ? EmailClaimSecrets.GenerateCode() : null;
            var linkToken = content.CarriesLink() ? EmailPushTokenGenerator.GenerateUrlSafeToken() : null;

            var message = new EmailClaimCompletionMessage(
                address,
                transaction.InitiatorContextSnapshot?.ClientApplicationName
                    ?? transaction.GetApplicationName()
                    ?? EmailAdapterConfigDefaults.DefaultClientName,
                code,
                linkToken is null ? null : BuildLink(mailSide.Credentials.PublicBaseUrl, linkToken),
                expiresAt,
                LocaleOf(language))
            {
                Ownership = transaction.ToResolutionContext(tenantId),
                TransactionType = transaction.Type,
                ActionType = transaction.ConfirmationSnapshot?.ActionType,
                TimeZone = transaction.GetUiTimeZone(),
                BrandName = transaction.GetBrandName()
            };

            var sendResult = await sender.SendClaimCompletionEmailAsync(message, cancellationToken);
            if (!sendResult.Success)
            {
                // The reservation is not returned: a mail the relay refused may still have gone out.
                _logger.LogWarning(
                    "A completion mail was not delivered; a code or link sent before stays live. TransactionId: {TransactionId}, Content: {Content}, ErrorCode: {ErrorCode}",
                    transaction.Id,
                    content,
                    sendResult.ErrorCode);

                return ClaimEmailSendOutcome.DeliveryFailed;
            }

            // The new code and link take effect only now that their mail is out; until these writes the
            // ones before them are the live ones. A mail with both makes both live at once.
            if (code is not null)
            {
                await _store.SetCodeAsync(
                    transactionId,
                    new EmailClaimCode(EmailClaimSecrets.HashOfCode(transactionId, code), address, expiresAt, limits.CodeAttempts),
                    transaction.ExpiresAt,
                    cancellationToken);
            }

            if (linkToken is not null)
            {
                await _store.SetLinkAsync(
                    transactionId,
                    new EmailClaimLink(EmailClaimSecrets.HashOfToken(linkToken), address, expiresAt, Used: false),
                    transaction.ExpiresAt,
                    cancellationToken);
            }

            return ClaimEmailSendOutcome.Sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "A completion mail could not be sent. TransactionId: {TransactionId}, Content: {Content}",
                transaction.Id,
                content);

            return ClaimEmailSendOutcome.DeliveryFailed;
        }
    }

    /// <inheritdoc />
    public async Task<ClaimEmailCodeCheck> CheckCodeAsync(Transaction transaction, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(code);

        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());
        var transactionId = transaction.Id.ToString();

        try
        {
            var check = await EmailCodeCheck.RunAsync(
                transactionId,
                code,
                async ct => (await _store.GetAsync(transactionId, ct))?.Code,
                isAdmitted: null,
                (live, matched, ct) => _store.TrySpendCodeAttemptAsync(transactionId, live.Hash, live.AttemptsLeft, matched, ct),
                _timeProvider,
                _logger,
                cancellationToken);

            return new ClaimEmailCodeCheck(
                check.Status,
                check.Status is ClaimEmailCodeStatus.Matched ? check.Code?.Address : null,
                check.AttemptsLeft);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A completion code could not be checked. TransactionId: {TransactionId}", transaction.Id);
            return new ClaimEmailCodeCheck(ClaimEmailCodeStatus.NoCode, Address: null, AttemptsLeft: 0);
        }
    }

    /// <inheritdoc />
    public async Task<ClaimEmailState?> GetStateAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var tenantId = transaction.GetTenantId();
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

        try
        {
            var record = await _store.GetAsync(transaction.Id.ToString(), cancellationToken) ?? EmailClaimCompletionRecord.Empty;
            var limits = await ResolveLimitsAsync(transaction, tenantId, cancellationToken);

            // The budget is that of the transaction and of the channel account: the limits of an address
            // and of a domain do not exhaust a method, since another address may be entered.
            var subjectKey = SubjectCounterKey(transaction, tenantId);
            var keys = new List<string>(capacity: 2) { TransactionCounterKey(transaction) };
            if (subjectKey is not null)
            {
                keys.Add(subjectKey);
            }

            var counts = await _store.ReadMailCountsAsync(keys, cancellationToken);

            var mailBudgetLeft = counts[0] < limits.PerTransaction && (subjectKey is null || counts[1] < limits.PerSubject);
            var now = _timeProvider.GetUtcNow();

            // One mail carries the code and the link to one address; the two differ only when the list of
            // methods changed while the transaction was open, and then the code is read first.
            return new ClaimEmailState(
                record.SelectedMethod,
                record.Code is { } code ? ChannelIdentityMask.MaskEmail(code.Address) : null,
                record.Code?.IsLive(now) is true,
                record.Link is { } link ? ChannelIdentityMask.MaskEmail(link.Address) : null,
                record.Link?.IsLive(now) is true,
                mailBudgetLeft,
                record.Code?.Address ?? record.Link?.Address);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The state of the email completion could not be read. TransactionId: {TransactionId}", transaction.Id);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SelectMethodAsync(Transaction transaction, EmailCompletionMethod method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

        try
        {
            await _store.SetSelectedMethodAsync(transaction.Id.ToString(), method, transaction.ExpiresAt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The choice is a convenience of the display: lost, the block shows the main method again.
            _logger.LogWarning(ex, "The chosen email method could not be recorded. TransactionId: {TransactionId}", transaction.Id);
        }
    }

    /// <inheritdoc />
    public async Task<bool> HoldAddressAsync(Transaction transaction, ClaimEmailHeldAddress? held, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

        try
        {
            await _store.SetHeldAddressAsync(
                transaction.Id.ToString(),
                held is null ? null : new EmailClaimHeldAddress(held.Address, held.Verified, held.Method),
                transaction.ExpiresAt,
                cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The address of the email completion could not be held. TransactionId: {TransactionId}", transaction.Id);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ClaimEmailHeldRead> GetHeldAddressAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

        try
        {
            return (await _store.GetAsync(transaction.Id.ToString(), cancellationToken))?.Held is { } held
                ? new ClaimEmailHeldRead(Known: true, new ClaimEmailHeldAddress(held.Address, held.Verified, held.Method))
                : ClaimEmailHeldRead.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The held address of the email completion could not be read. TransactionId: {TransactionId}", transaction.Id);
            return ClaimEmailHeldRead.Unknown;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetPushMailtoAsync(Transaction transaction, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var tenantId = transaction.GetTenantId();
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

        try
        {
            if (await ResolveMailSideAsync(tenantId, cancellationToken) is not { Capabilities.CanReceivePush: true, InboundDomain: { } domain } mailSide)
            {
                return null;
            }

            var inbound = mailSide.Credentials.Inbound;
            var token = await LivePushTokenAsync(transaction, mailSide, cancellationToken);
            var locale = LocaleOf(language);
            var subject = _localizer.ResolveOrBaseText(EmailAdapterMailStrings.ClaimPushSubject, locale);
            var body = _localizer.ResolveOrBaseText(EmailAdapterMailStrings.ClaimPushBody, locale);

            // The token travels where the inbound side of this deployment looks for it, as for the Push
            // sign-in: the whole local part of a catch-all address, or else the form of SPEC-016 §5.4 in the
            // plus segment of the address (when it is used) and in the subject and the body (EM-021).
            if (inbound.TokenInLocalPart)
            {
                // A literal addr-spec, not percent-encoded — see the direct mailto of the Push sign-in.
                return $"mailto:{token}@{domain}"
                    + $"?subject={Uri.EscapeDataString(subject)}"
                    + $"&body={Uri.EscapeDataString(body)}";
            }

            var recipient = EmailChannelAdapter.BuildPushRecipientAddress(inbound, token);

            return $"mailto:{Uri.EscapeDataString(recipient)}"
                + $"?subject={Uri.EscapeDataString($"{subject} {token}")}"
                + $"&body={Uri.EscapeDataString($"{body}\n\n{token}")}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The prepared mail of the Push email method could not be built. TransactionId: {TransactionId}", transaction.Id);
            return null;
        }
    }

    /// <summary>
    /// The correlation token of the <c>Push</c> method of a transaction: the kept one while its correlation
    /// lives, a new one otherwise. A correlation of its own, marked for the completion — not the index of
    /// the Push sign-in, which could hand back the sign-in token of the same transaction.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="mailSide">The mail side of the tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token.</returns>
    private async Task<string> LivePushTokenAsync(Transaction transaction, MailSide mailSide, CancellationToken cancellationToken)
    {
        var transactionId = transaction.Id.ToString();
        var now = _timeProvider.GetUtcNow();

        if ((await _store.GetAsync(transactionId, cancellationToken))?.Push is { } kept
            && now < kept.ExpiresAt
            && await _correlations.GetAsync(kept.Token, cancellationToken) is { IsConsumed: false } correlation
            && !correlation.IsExpired(now))
        {
            return kept.Token;
        }

        // The form of the token follows where it travels: the case of an address is not preserved on the
        // way, so a token in the local part is drawn from a lower-case alphabet (SPEC-016 §5.4).
        var token = mailSide.Credentials.Inbound.TokenInLocalPart
            ? EmailPushTokenGenerator.GenerateLocalPartToken()
            : EmailPushTokenGenerator.GenerateUrlSafeToken();

        var expiresAt = EarlierOf(
            now.Add(EmailCredentialsResolver.ResolveTokenTtl(mailSide.Credentials, mailSide.Options.TokenTtl)),
            transaction.ExpiresAt);

        await _correlations.StoreAsync(
            token,
            new EmailPushCorrelation(transactionId, expiresAt) { ForClaimCompletion = true },
            cancellationToken);

        await _store.SetPushTokenAsync(transactionId, new EmailClaimPushToken(token, expiresAt), transaction.ExpiresAt, cancellationToken);

        return token;
    }

    /// <summary>
    /// What the mail infrastructure of a tenant is: the settings, the credentials and what they allow.
    /// </summary>
    /// <param name="tenantId">Tenant; null — the implicit tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mail side; null — the adapter is off, or Email is not a channel of the tenant, or its
    /// credentials do not resolve.</returns>
    private async ValueTask<MailSide?> ResolveMailSideAsync(string? tenantId, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return null;
        }

        using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

        if (!await _resolver.IsChannelEnabledAsync(tenantId, ChannelTypes.Email, cancellationToken))
        {
            return null;
        }

        var credentials = await EmailCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentials.IsFailure)
        {
            return null;
        }

        // Push needs an address to receive the mail at: without one it is not available at all, rather
        // than merely hidden in the block.
        var inboundDomain = EmailChannelAdapter.ExtractDomain(credentials.Value.Inbound.InboundAddress);

        return new MailSide(
            options,
            credentials.Value,
            inboundDomain,
            new ClaimEmailCapabilities(
                CanSend: options.PullEnabled && _sender?.SupportsClaimCompletionEmail is true,
                CanReceivePush: options.PushEnabled && inboundDomain is not null));
    }

    /// <summary>
    /// The limits of the completion mails in the ownership context of a transaction (SPEC-041 E61).
    /// </summary>
    /// <param name="transaction">Transaction.</param>
    /// <param name="tenantId">Tenant of the transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The limits.</returns>
    private async ValueTask<MailLimits> ResolveLimitsAsync(Transaction transaction, string? tenantId, CancellationToken cancellationToken)
    {
        using var scope = new ConfigResolutionScope(transaction.ToResolutionContext(tenantId));

        return new MailLimits(
            CodeAttempts: await ResolveLimitAsync(EmailCompletionKeys.EmailCodeAttempts, scope, cancellationToken),
            PerTransaction: await ResolveLimitAsync(EmailCompletionKeys.EmailMailsPerTransaction, scope, cancellationToken),
            PerSubject: await ResolveLimitAsync(EmailCompletionKeys.EmailMailsPerSubject, scope, cancellationToken),
            PerAddress: await ResolveLimitAsync(EmailCompletionKeys.EmailMailsPerAddress, scope, cancellationToken),
            PerDomain: await ResolveLimitAsync(EmailCompletionKeys.EmailMailsPerDomain, scope, cancellationToken));
    }

    /// <summary>
    /// Resolves one limit.
    /// </summary>
    /// <param name="key">Key of the limit.</param>
    /// <param name="scope">Scope of resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective limit.</returns>
    private async ValueTask<int> ResolveLimitAsync(ConfigKey<int> key, ConfigResolutionScope scope, CancellationToken cancellationToken) =>
        (await _resolver.ResolveAsync(key, scope, ConfigDimensionValues.None, cancellationToken)).Value;

    /// <summary>
    /// The counters one mail is reserved against, each with the outcome its refusal answers with: the
    /// transaction; the channel account across its transactions (when the transaction knows it); the
    /// recipient address; the recipient domain (only while its limit is switched on); the minimal interval
    /// between the mails of the transaction (only while it is switched on). The interval is judged last: a
    /// limit that is used up is the final answer, whereas the interval only asks to wait.
    /// </summary>
    /// <param name="transaction">Transaction.</param>
    /// <param name="tenantId">Tenant of the transaction.</param>
    /// <param name="address">Recipient address.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="normalization">Normalization settings of the comparison form of the address.</param>
    /// <param name="resendInterval">Minimal interval between the mails of one transaction; zero — none.</param>
    /// <param name="now">Current moment.</param>
    /// <returns>The counters with their refusals, in the order the refusals are reported.</returns>
    private static List<(EmailClaimMailCounter Counter, ClaimEmailSendOutcome Refusal)> MailCounters(
        Transaction transaction,
        string? tenantId,
        string address,
        MailLimits limits,
        EmailNormalizationOptions? normalization,
        TimeSpan resendInterval,
        DateTimeOffset now)
    {
        var tenant = tenantId ?? string.Empty;

        // The counter of the transaction lives as long as the transaction does.
        var counters = new List<(EmailClaimMailCounter Counter, ClaimEmailSendOutcome Refusal)>
        {
            (new EmailClaimMailCounter(TransactionCounterKey(transaction), limits.PerTransaction, transaction.ExpiresAt - now),
                ClaimEmailSendOutcome.TransactionLimit),
        };

        if (SubjectCounterKey(transaction, tenantId) is { } subjectKey)
        {
            counters.Add((new EmailClaimMailCounter(subjectKey, limits.PerSubject, EmailClaimMailWindows.Subject),
                ClaimEmailSendOutcome.SubjectLimit));
        }

        counters.Add((
            new EmailClaimMailCounter(
                EmailClaimSecrets.CounterKey(
                    AddressCounterKind,
                    string.Join(ScopeSeparator, tenant, EmailAddressHelper.ToComparisonForm(address, normalization))),
                limits.PerAddress,
                EmailClaimMailWindows.Address),
            ClaimEmailSendOutcome.AddressLimit));

        // Zero switches the limit of the domain off (SPEC-041 E61): no counter, nothing to refuse by.
        if (limits.PerDomain > 0 && EmailChannelAdapter.ExtractDomain(address) is { } domain)
        {
            counters.Add((
                new EmailClaimMailCounter(
                    EmailClaimSecrets.CounterKey(DomainCounterKind, string.Join(ScopeSeparator, tenant, domain.ToLowerInvariant())),
                    limits.PerDomain,
                    EmailClaimMailWindows.Domain),
                ClaimEmailSendOutcome.DomainLimit));
        }

        // The interval between two mails; zero switches it off.
        if (EmailClaimMailCounter.OfResendInterval(IntervalCounterKind, transaction.Id, resendInterval) is { } intervalCounter)
        {
            counters.Add((intervalCounter, ClaimEmailSendOutcome.TooSoon));
        }

        return counters;
    }

    /// <summary>
    /// Key of the mail counter of a transaction.
    /// </summary>
    /// <param name="transaction">Transaction.</param>
    /// <returns>The key.</returns>
    private static string TransactionCounterKey(Transaction transaction) =>
        EmailClaimSecrets.CounterKey(TransactionCounterKind, transaction.Id.ToString());

    /// <summary>
    /// Key of the mail counter of the channel account that confirmed a transaction: the identity of the
    /// channel snapshot, the <c>sub</c> not being resolved before the confirmation completes.
    /// </summary>
    /// <param name="transaction">Transaction.</param>
    /// <param name="tenantId">Tenant of the transaction.</param>
    /// <returns>The key; null — the transaction carries no channel account.</returns>
    private static string? SubjectCounterKey(Transaction transaction, string? tenantId) =>
        transaction.ChannelIdentitySnapshot is { } identity
            ? EmailClaimSecrets.CounterKey(
                SubjectCounterKind,
                string.Join(ScopeSeparator, tenantId ?? string.Empty, identity.ChannelType, identity.ChannelUserId))
            : null;

    /// <summary>
    /// Absolute URL of the confirmation page of a link mail.
    /// </summary>
    /// <param name="publicBaseUrl">Public base URL of the tenant.</param>
    /// <param name="token">Token of the link.</param>
    /// <returns>The URL.</returns>
    private static string BuildLink(string publicBaseUrl, string token) =>
        $"{publicBaseUrl.TrimEnd('/')}{EmailAdapterConstants.ClaimEmailConfirmPath}"
        + $"?{EmailAdapterConstants.TokenQueryParam}={Uri.EscapeDataString(token)}";

    /// <summary>
    /// The earlier of two moments — how a code and a link are held within the life of their transaction, the
    /// code of a sign-in mail as well.
    /// </summary>
    /// <param name="first">A moment.</param>
    /// <param name="second">Another moment.</param>
    /// <returns>The earlier one.</returns>
    internal static DateTimeOffset EarlierOf(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;

    /// <summary>
    /// The locale a language argument states; a blank one states none.
    /// </summary>
    /// <param name="language">Language as given.</param>
    /// <returns>The locale, or null — the base language.</returns>
    private static string? LocaleOf(string language) =>
        string.IsNullOrWhiteSpace(language) ? null : language;

    /// <summary>
    /// The Email settings, or null when a reload left them unreadable.
    /// </summary>
    /// <returns>The settings, or null.</returns>
    private EmailOptions? TryReadOptions()
    {
        try
        {
            return _options.CurrentValue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Email settings could not be read; the address is normalized by the default rule.");
            return null;
        }
    }

    /// <summary>
    /// The mail infrastructure of a tenant.
    /// </summary>
    /// <param name="Options">Global Email settings.</param>
    /// <param name="Credentials">Credentials of the tenant.</param>
    /// <param name="InboundDomain">Domain of the address the tenant receives mail at; null — none.</param>
    /// <param name="Capabilities">What they allow.</param>
    private sealed record MailSide(
        EmailOptions Options,
        EmailTenantCredentials Credentials,
        string? InboundDomain,
        ClaimEmailCapabilities Capabilities);

    /// <summary>
    /// The limits of the completion mails (SPEC-041 E61).
    /// </summary>
    /// <param name="CodeAttempts">Attempts of one code.</param>
    /// <param name="PerTransaction">Mails of one transaction.</param>
    /// <param name="PerSubject">Mails of one channel account within its window.</param>
    /// <param name="PerAddress">Mails to one address within its window.</param>
    /// <param name="PerDomain">Mails to one domain within its window; zero — no limit.</param>
    private sealed record MailLimits(int CodeAttempts, int PerTransaction, int PerSubject, int PerAddress, int PerDomain);
}
