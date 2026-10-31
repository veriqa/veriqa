// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net.Mail;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;
using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// Default implementation of the inbound email processing provider for Push mode (SPEC-016 §5, §7.2).
/// Extracts the correlation token from the mail, checks it against the correlation store,
/// verifies the sender according to <see cref="EmailVerificationPolicy"/> and builds
/// the result for the channel pipeline. Never trusts the From header alone (SPEC-016 §5.5).
/// </summary>
internal sealed class DefaultEmailInboundProcessor : IEmailInboundProcessor
{
    /// <summary>
    /// Normalization settings served when a read fails before any of them has ever been read
    /// successfully — the declared defaults of the section.
    /// </summary>
    private static readonly EmailNormalizationOptions DefaultNormalization = new EmailOptions().Normalization;

    /// <summary>
    /// Global Email settings — read per operation and only for core-owned fields
    /// (<see cref="EmailOptions.Normalization"/>, core-transport CFG-230). Kept as a monitor rather
    /// than a constructor snapshot so configuration reload takes effect without a restart.
    /// </summary>
    private readonly IOptionsMonitor<EmailOptions> _options;

    /// <summary>
    /// Last normalization settings that were read successfully; null until the first successful read.
    /// </summary>
    private EmailNormalizationOptions? _lastValidNormalization;

    /// <summary>
    /// Canonical layer resolver (SPEC-003 §17.4, SPEC-012 CFG-234): resolves the tenant-owned
    /// <c>Inbound</c> settings per operation.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Push-mode correlation token store.
    /// </summary>
    private readonly IEmailPushCorrelationStore _correlationStore;

    /// <summary>
    /// Transaction event publisher (audit events, EM-032).
    /// </summary>
    private readonly ITransactionEventPublisher _eventPublisher;

    /// <summary>
    /// Transaction service — the source of the tenant the confirmed transaction belongs to, which is
    /// the tenant of the identity record and not the one the inbound request is scoped to (see the
    /// snapshot build in ProcessAsync).
    /// </summary>
    private readonly ITransactionService _transactionService;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<DefaultEmailInboundProcessor> _logger;

    /// <summary>
    /// Clock behind the capture and event timestamps of the inbound path.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an instance of <see cref="DefaultEmailInboundProcessor"/>.
    /// </summary>
    /// <param name="options">Global Email settings (core-owned fields only).</param>
    /// <param name="resolver">Canonical layer resolver (SPEC-003 §17.4).</param>
    /// <param name="correlationStore">Correlation token store.</param>
    /// <param name="eventPublisher">Transaction event publisher.</param>
    /// <param name="transactionService">Transaction service (the tenant of the confirmed transaction).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Time provider.</param>
    public DefaultEmailInboundProcessor(
        IOptionsMonitor<EmailOptions> options,
        IConfigurationResolver resolver,
        IEmailPushCorrelationStore correlationStore,
        ITransactionEventPublisher eventPublisher,
        ITransactionService transactionService,
        ILogger<DefaultEmailInboundProcessor> logger,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _correlationStore = correlationStore ?? throw new ArgumentNullException(nameof(correlationStore));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// The guarded read of the core-owned normalization settings (SPEC-012 CFG-210).
    /// <para>
    /// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> re-binds the section and re-runs its
    /// validator after every reload of a watched configuration source, and this read sits on the live
    /// inbound-webhook path of a contour that has no exception-handler middleware: a deployment-side
    /// edit that breaks <c>Veriqa:Channels:Email</c> would turn an incoming sign-in mail into an unhandled
    /// failure instead of a delivery. The guard is as wide as the read — a rule of the validator
    /// reports an <see cref="OptionsValidationException"/>, while a value that does not convert to its
    /// declared property type fails earlier, inside the binder, as an
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// Normalization decides which identity an address maps to, so the degradation keeps the mapping
    /// the installation already had: the last settings read successfully, and the declared defaults
    /// while none have been.
    /// </para>
    /// </summary>
    /// <returns>The current normalization settings, or the degraded ones. Never throws.</returns>
    private EmailNormalizationOptions ReadNormalization()
    {
        try
        {
            var current = _options.CurrentValue.Normalization;

            // Publish the snapshot only after a successful read: a failed read must leave the previous
            // one in place, and readers on other threads must never observe a torn state.
            Volatile.Write(ref _lastValidNormalization, current);
            return current;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Email settings could not be read; normalizing the address on the last valid settings "
                + "(on the declared defaults when none have been read yet).");

            return Volatile.Read(ref _lastValidNormalization) ?? DefaultNormalization;
        }
    }

    /// <inheritdoc />
    public async Task<ChannelInboundResult> ProcessInboundEmailAsync(
        InboundEmailMessage message,
        CancellationToken cancellationToken)
    {
        // The method extracts the correlation token, verifies the sender and builds the pipeline result

        // Inbound.* belongs to the tenant-credential group — resolve it per operation through the
        // seam (CA-162). Missing credentials: the mail is unrelated, no exception is thrown.
        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogWarning(
                "Push inbound: failed to resolve the tenant Email credentials. Error: {ErrorCode}",
                credentialsResult.Error.Code);
            return new ChannelUnrelatedResult();
        }

        var inbound = credentialsResult.Value.Inbound;
        var now = _timeProvider.GetUtcNow();

        // Extract the correlation token from the mail (address paths → subject → body)
        var token = await ExtractCorrelationTokenAsync(message, inbound, _correlationStore, now, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Push inbound: correlation token not found in the incoming mail.");
            return new ChannelUnrelatedResult();
        }

        // Check the correlation token against the store. The text paths of the extraction have asked this
        // very question already — it is how they tell the token from anything else shaped like one — but
        // the address paths have not, and the record itself is needed below whichever path answered.
        var correlation = await _correlationStore.GetAsync(token, cancellationToken);
        if (correlation is null || !IsLive(correlation, now))
        {
            _logger.LogWarning(
                "Push inbound: correlation token is missing, expired or already used.");
            return new ChannelUnrelatedResult();
        }

        // The store keeps the identifier as a string, so it is parsed here — right next to the token
        // check. An unparseable value is a corrupted store record rather than a user event: the same
        // branch as a missing token, and the mail confirms nothing (SPEC-003 §6.3).
        if (!TransactionId.TryParse(correlation.TransactionId, out var transactionId))
        {
            _logger.LogWarning(
                "Push inbound: the correlation record carries an unparseable transaction identifier "
                + "for the {ChannelType} channel.",
                ChannelTypes.Email);
            return new ChannelUnrelatedResult();
        }

        // Resolve the verified sender according to the verification policy (SPEC-016 §5.5)
        if (!TryResolveVerifiedSender(message, inbound, out var verifiedSender))
        {
            _logger.LogWarning(
                "Push inbound: sender verification failed (policy {Policy}). Sender: {MaskedSender}",
                inbound.VerificationPolicy,
                ChannelIdentityMask.MaskEmail(verifiedSender));

            // Audit event email_sender_verification_failed (EM-032):
            // the transaction is not confirmed, the rejection is recorded for audit consumers
            await PublishSenderVerificationFailedAsync(
                transactionId,
                inbound.VerificationPolicy,
                verifiedSender,
                cancellationToken);

            return new ChannelUnrelatedResult();
        }

        // Normalization stays core-owned (CFG-230): address canonicalization is identity-matching
        // semantics, uniform for the installation — a per-tenant divergence would fragment identities.
        var normalization = ReadNormalization();
        var normalizedSender = EmailAddressHelper.Normalize(verifiedSender, normalization);

        // The sender alias goes to Username → preferred_username, and only when the header actually carries
        // one: with no alias the field keeps the verified local part instead of losing data (SPEC-016 §6.3).
        // DisplayName → name stays the full verified address: name reads as an established fact next to
        // email_verified, and the alias is not verified by SPF/DKIM/DMARC.
        var senderDisplayName = ResolveSenderDisplayName(message, verifiedSender, normalization);

        // Tenant of the transaction this mail confirms — read from the transaction and NOT from the
        // ambient scope, even though the inbound route now opens one. The two answer different
        // questions: the scope says whose inbound mailbox received the mail (the route segment), the
        // transaction says whose sign-in it completes, and the identity record belongs to the latter.
        // They coincide in every correct configuration and are told apart deliberately, so that a mail
        // arriving on one tenant's mailbox never writes an identity under that tenant for somebody
        // else's transaction.
        // A failed read degrades to the default tenant rather than dropping the mail: the pipeline
        // reads the transaction again and reports the real outcome, and the tenant only separates
        // identity records and rate-limit budgets — it is not an access boundary.
        var tenantTxResult = await _transactionService.GetTransactionAsync(
            transactionId, cancellationToken);
        var tenantId = tenantTxResult.IsSuccess ? tenantTxResult.Value.GetTenantId() : null;

        // Build a ChannelIdentitySnapshot from the verified sender (SPEC-016 §6.1, §6.3)
        var snapshot = new ChannelIdentitySnapshot
        {
            TenantId = tenantId,
            ChannelType = ChannelTypes.Email,
            IsBot = false,
            ChannelUserId = normalizedSender,
            DisplayName = normalizedSender,
            Username = senderDisplayName ?? EmailAddressHelper.GetLocalPart(normalizedSender),
            Email = normalizedSender,
            // Push proves control over the mailbox via the verified sender
            EmailVerified = true,
            CapturedAt = _timeProvider.GetUtcNow(),
            AdapterVersion = EmailAdapterConstants.AdapterVersion,
            RawMetadata = BuildRawMetadata(message, inbound),
            AdditionalClaims = EmailAuthEndpoints.BuildEmailAdditionalClaims(EmailAdapterConstants.ModePush)
        };

        _logger.LogInformation(
            "Push inbound: mail verified successfully. Sender: {MaskedSender}, TransactionId: {TransactionId}",
            ChannelIdentityMask.MaskEmail(normalizedSender),
            correlation.TransactionId);

        return new ChannelAuthConfirmResult
        {
            TransactionId = transactionId,
            Identity = snapshot
        };
    }

    /// <summary>
    /// Publishes the audit event email_sender_verification_failed (EM-032, EM-066).
    /// Details contain only safe data: the verification policy and a masked email.
    /// A publish failure does not change the mail processing result.
    /// </summary>
    /// <remarks>
    /// The event carries no attribution (SPEC-001 §10.3): the inbound path decides on the correlation
    /// record alone and never holds the transaction, so there is nothing to capture it from. Reading the
    /// store here would put an extra read on the live webhook path for the journal's sake only, so the
    /// gap is left as it is — the audit record then falls back to its closing rule (a system actor and
    /// empty application attributes), which is exactly what the journal states for a publisher without
    /// a transaction.
    /// </remarks>
    /// <param name="transactionId">Transaction identifier from correlation.</param>
    /// <param name="policy">Applied verification policy.</param>
    /// <param name="sender">Sender address (will be masked).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task PublishSenderVerificationFailedAsync(
        TransactionId transactionId,
        EmailVerificationPolicy policy,
        string? sender,
        CancellationToken cancellationToken)
    {
        // The method builds a verification-failure audit event without exposing PII
        try
        {
            await _eventPublisher.PublishAsync(
                new TransactionChannelAuditEvent
                {
                    TransactionId = transactionId,
                    OccurredAt = _timeProvider.GetUtcNow(),
                    ChannelType = ChannelTypes.Email,
                    EventCode = EmailAdapterConstants.ErrorCodeSenderVerificationFailed,
                    Details = $"policy={policy}; sender={ChannelIdentityMask.MaskEmail(sender)}"
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal request cancellation — do not publish the event
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish audit event {EventCode}. TransactionId: {TransactionId}",
                EmailAdapterConstants.ErrorCodeSenderVerificationFailed,
                transactionId);
        }
    }

    /// <summary>
    /// Extracts the correlation token from an incoming mail.
    /// Search order: the recipient address (if the mode puts the token there) → mail subject → mail body.
    /// <para>
    /// <b>The text of the mail is searched for the FORM of the token, not for the wording around it.</b>
    /// That wording is the text of the prefill message a deployment owns and may reword, so a parser
    /// keyed to it would stop correlating the moment the wording changed. The form is the machine
    /// contract instead: exactly <see cref="EmailAdapterConstants.TokenCharLength"/> Base64Url
    /// characters. Every MAXIMAL run of that form is a candidate, and the correlation store — which
    /// already answers "issued, not expired, not used" — says which of them is the token.
    /// </para>
    /// <para>
    /// A run LONGER than the form is not sliced into windows: one Message-ID, tracking parameter or
    /// signature in a quoted reply would otherwise yield dozens of candidates and spend the probe budget
    /// before the real token was reached.
    /// </para>
    /// <para>
    /// The candidates are asked about in ONE call to the store
    /// (<see cref="IEmailPushCorrelationStore.GetManyAsync"/>), so a mail costs one question of the
    /// store per pass whatever its text holds. The early exit of the probe-by-probe form is the price:
    /// a mail with several candidates has all of them read even when the first is the token.
    /// </para>
    /// </summary>
    /// <param name="message">Incoming email message.</param>
    /// <param name="inbound">Inbound processing settings.</param>
    /// <param name="correlationStore">Correlation token store — the authority on which candidate is live.</param>
    /// <param name="now">Current moment, for the expiry half of that answer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The correlation token, or null if not found.</returns>
    public static async ValueTask<string?> ExtractCorrelationTokenAsync(
        InboundEmailMessage message,
        EmailInboundOptions inbound,
        IEmailPushCorrelationStore correlationStore,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(correlationStore);

        // 0. Direct-mailto mode (SPEC-016 §5.3): the whole recipient local part IS the correlation token
        // ({token}@domain, delivered via a catch-all mailbox). Gated on TokenInLocalPart so a fixed local
        // part (e.g. "login") in the other modes is never mistaken for a token.
        // The local part is folded to lower case before the lookup: mail clients and inbound providers
        // are free to change the case of an address on the way, so a token in this position is minted
        // from a lower-case alphabet (EmailPushTokenGenerator.GenerateLocalPartToken) and the fold
        // loses nothing of it (SPEC-016 §5.4).
        if (inbound.TokenInLocalPart && !string.IsNullOrWhiteSpace(message.ToAddress))
        {
            var localPartToken = EmailAddressHelper.GetLocalPart(message.ToAddress.Trim());
            if (!string.IsNullOrEmpty(localPartToken))
            {
                return localPartToken.ToLowerInvariant();
            }
        }

        // 1. Recipient plus-address: login+{token}@domain
        if (inbound.UsePlusAddressing && !string.IsNullOrWhiteSpace(message.ToAddress))
        {
            var fromPlus = ExtractFromPlusAddress(message.ToAddress);
            if (!string.IsNullOrEmpty(fromPlus))
            {
                return fromPlus;
            }
        }

        // 2-3. The text of the mail. The store is asked ONCE for the whole mail rather than once per
        // candidate: the extraction runs twice per mail (the webhook endpoint publishing the received
        // status, and the processing that follows it), and against a store across the network every
        // candidate of every pass would otherwise be a round trip of its own.
        var candidates = TokenCandidates(message);

        if (candidates.Count is 0)
        {
            return null;
        }

        var correlations = await correlationStore.GetManyAsync(candidates, cancellationToken);

        // The order of the candidates is the order of the text — the subject before the body — and the
        // first live one wins.
        foreach (var candidate in candidates)
        {
            if (correlations.TryGetValue(candidate, out var correlation) && IsLive(correlation, now))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The distinct token-shaped candidates one mail offers, in the order its text states them — the
    /// subject before the body, because the subject is short and the token is put there first. A
    /// candidate the mail carries twice is asked about once.
    /// </summary>
    /// <remarks>
    /// The number of candidates is capped at
    /// <see cref="EmailAdapterConstants.MaxCorrelationTokenProbes"/>: the mail comes from outside, and a
    /// long quoted reply may hold any number of Base64Url-looking runs. The cap is what keeps the cost
    /// of one mail bounded: the candidates are asked about in one batch, and the cap is applied before
    /// the store is asked.
    /// </remarks>
    /// <param name="message">Incoming email message.</param>
    /// <returns>The candidates, at most the capped number of them.</returns>
    private static List<string> TokenCandidates(InboundEmailMessage message)
    {
        var candidates = new List<string>(EmailAdapterConstants.MaxCorrelationTokenProbes);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var text in new[] { message.Subject, message.Body })
        {
            foreach (var candidate in TokenShapedRuns(text))
            {
                if (!seen.Add(candidate))
                {
                    continue;
                }

                candidates.Add(candidate);

                if (candidates.Count == EmailAdapterConstants.MaxCorrelationTokenProbes)
                {
                    // The budget is spent: the rest of the mail is left unread rather than asked about.
                    return candidates;
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Whether a correlation record may still confirm a transaction — the one place the answer "issued,
    /// not expired, not used" is spelled out: the candidate filter of the extraction and the check of the
    /// processed mail ask exactly the same question.
    /// </summary>
    /// <param name="correlation">Correlation record read from the store.</param>
    /// <param name="now">Current moment.</param>
    /// <returns><c>true</c> when the record is neither expired nor already used.</returns>
    private static bool IsLive(EmailPushCorrelation correlation, DateTimeOffset now) =>
        !correlation.IsExpired(now) && !correlation.IsConsumed;

    /// <summary>
    /// Maximal runs of token characters whose length is exactly that of a token — the candidates a text
    /// of the mail offers. A run of any other length is not a candidate at all: a shorter one cannot be a
    /// token, and a longer one is something else that merely contains characters of the same alphabet.
    /// </summary>
    /// <param name="text">Subject or body of the mail (null/empty — no candidates).</param>
    /// <returns>The candidate runs, in the order the text states them.</returns>
    private static IEnumerable<string> TokenShapedRuns(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var runStart = 0;

        // The position past the end is visited on purpose: it closes a run that ends at the end of the text.
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && IsTokenCharacter(text[index]))
            {
                continue;
            }

            if (index - runStart == EmailAdapterConstants.TokenCharLength)
            {
                yield return text[runStart..index];
            }

            runStart = index + 1;
        }
    }

    /// <summary>
    /// Whether a character may appear in a token: the Base64Url alphabet without padding
    /// (<c>EmailPushTokenGenerator.GenerateUrlSafeToken</c>).
    /// </summary>
    /// <param name="symbol">Character of the text.</param>
    /// <returns><c>true</c> when the character belongs to the alphabet.</returns>
    private static bool IsTokenCharacter(char symbol) =>
        char.IsAsciiLetterOrDigit(symbol) || symbol is '-' or '_';

    /// <summary>
    /// Extracts the correlation token from the recipient plus-address (login+{token}@domain).
    /// </summary>
    /// <param name="toAddress">Recipient address.</param>
    /// <returns>The token from the plus segment, or null.</returns>
    private static string? ExtractFromPlusAddress(string toAddress)
    {
        // The method isolates the segment between the '+' separator and the '@' character
        var trimmed = toAddress.Trim();
        var atIndex = trimmed.IndexOf('@');
        if (atIndex <= 0)
        {
            return null;
        }

        var localPart = trimmed[..atIndex];
        var plusIndex = localPart.LastIndexOf(EmailAdapterConstants.PlusAddressSeparator);
        if (plusIndex < 0 || plusIndex == localPart.Length - 1)
        {
            return null;
        }

        return localPart[(plusIndex + 1)..];
    }

    /// <summary>
    /// Resolves the verified sender of the mail according to the verification policy (SPEC-016 §5.5).
    /// Never trusts the From header alone without a cryptographic/provider guarantee.
    /// </summary>
    /// <param name="message">Incoming email message.</param>
    /// <param name="inbound">Inbound processing settings.</param>
    /// <param name="verifiedSender">Verified sender email (output).</param>
    /// <returns>true — the sender is verified; otherwise false.</returns>
    private bool TryResolveVerifiedSender(
        InboundEmailMessage message,
        EmailInboundOptions inbound,
        out string verifiedSender)
    {
        // The From address is an identity candidate and a value for diagnostics, but by itself is not
        // proof (SPEC-016 §5.5). For the ProviderVerified policy the source of truth is
        // providerVerifiedSender, so a missing/empty From must not block verification
        // (review feedback). verifiedSender defaults to From so that rejection logs for policies other
        // than ProviderVerified are informative (on the successful ProviderVerified path it is overwritten).
        var fromEmail = ParseEmailAddress(message.FromAddress);
        verifiedSender = fromEmail;

        switch (inbound.VerificationPolicy)
        {
            case EmailVerificationPolicy.DmarcAlignedPass:
                // Requires DMARC pass with alignment of the From domain
                return !string.IsNullOrEmpty(fromEmail) && IsPass(message.DmarcResult);

            case EmailVerificationPolicy.SpfOrDkimPass:
                // An SPF pass or DKIM pass is sufficient
                return !string.IsNullOrEmpty(fromEmail)
                    && (IsPass(message.SpfResult) || IsPass(message.DkimResult));

            case EmailVerificationPolicy.ProviderVerified:
                // Trust the address verified by the inbound provider; the From header is not required
                var providerSender = ParseEmailAddress(message.ProviderVerifiedSender);
                if (string.IsNullOrEmpty(providerSender))
                {
                    return false;
                }

                verifiedSender = providerSender;
                return true;

            case EmailVerificationPolicy.AllowListOnly:
                // Accept only addresses/domains from the allowlist
                return !string.IsNullOrEmpty(fromEmail)
                    && IsInAllowList(fromEmail, inbound.AllowedDomains, ReadNormalization());

            case EmailVerificationPolicy.None:
                // Dev environment only: no cryptographic check (EM-030)
                return !string.IsNullOrEmpty(fromEmail);

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks whether the sender address belongs to the allowlist (by full address or by domain).
    /// </summary>
    /// <remarks>
    /// What leaves this point is a decision, not an address, so both sides are prepared with the
    /// comparison form of <see cref="EmailAddressHelper"/>: the case difference is removed
    /// unconditionally (as it always was here) and provider-specific canonicalization is added only
    /// when it is enabled. An entry without an "@" is a bare domain and the comparison form maps its
    /// alias too, so an allowlist written as <c>["gmail.com"]</c> and one written as
    /// <c>["googlemail.com"]</c> both admit a sender from either domain once the flag is on.
    /// </remarks>
    /// <param name="email">Sender address.</param>
    /// <param name="allowedDomains">List of allowed domains/addresses.</param>
    /// <param name="normalization">Normalization settings (the canonicalization flag).</param>
    /// <returns>true — the address is allowed.</returns>
    private static bool IsInAllowList(
        string email,
        IReadOnlyList<string> allowedDomains,
        EmailNormalizationOptions? normalization)
    {
        // The method compares the address and its domain with the allowlist entries case-insensitively
        if (allowedDomains.Count is 0)
        {
            return false;
        }

        var normalized = EmailAddressHelper.ToComparisonForm(email, normalization);
        var atIndex = normalized.LastIndexOf('@');
        var domain = atIndex >= 0 && atIndex < normalized.Length - 1
            ? normalized[(atIndex + 1)..]
            : string.Empty;

        foreach (var entry in allowedDomains)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var normalizedEntry = EmailAddressHelper.ToComparisonForm(entry, normalization);

            if (string.Equals(normalizedEntry, normalized, StringComparison.Ordinal)
                || string.Equals(normalizedEntry, domain, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the check result equals "pass" (case-insensitively).
    /// </summary>
    /// <param name="result">String SPF/DKIM/DMARC result.</param>
    /// <returns>true — the result equals "pass".</returns>
    private static bool IsPass(string? result)
    {
        return !string.IsNullOrWhiteSpace(result)
            && string.Equals(
                result.Trim(),
                EmailAdapterConstants.VerificationResultPass,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts an email address from the header string (supports the form "Name &lt;mail@domain&gt;").
    /// </summary>
    /// <param name="rawAddress">Raw address value.</param>
    /// <returns>The email address, or an empty string on error.</returns>
    private static string ParseEmailAddress(string? rawAddress)
    {
        // The method uses .NET MailAddress to parse the address
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            return string.Empty;
        }

        try
        {
            return new MailAddress(rawAddress.Trim()).Address;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Resolves the display name of the verified sender for the snapshot (SPEC-016 §6.3).
    /// The alias is taken only from a header whose address equals the verified one: ProviderVerified
    /// installations usually deliver a bare address in providerVerifiedSender while the raw From header
    /// still carries the alias, and that alias may be used only while the provider corroborates the very
    /// same address. An alias from an unrelated header is never attached to the verified identity.
    /// </summary>
    /// <param name="message">Incoming email message.</param>
    /// <param name="verifiedSender">Verified sender address (before normalization).</param>
    /// <param name="normalization">Normalization settings (the canonicalization flag).</param>
    /// <returns>The sanitized display name, or null if no matching header carries one.</returns>
    private static string? ResolveSenderDisplayName(
        InboundEmailMessage message,
        string verifiedSender,
        EmailNormalizationOptions? normalization)
    {
        // Both sides go through the same comparison form as the allowlist check: what leaves this point
        // is a decision, so the case difference is removed unconditionally and canonicalization is
        // added only when it is enabled. With the flag on, "Dmitrii <U.ser+tag@gmail.com>" is
        // recognized as the header of the provider-verified user@gmail.com and its alias is used.
        var comparableVerifiedSender = EmailAddressHelper.ToComparisonForm(verifiedSender, normalization);

        return DisplayNameOfVerifiedAddress(message.ProviderVerifiedSender)
            ?? DisplayNameOfVerifiedAddress(message.FromAddress);

        string? DisplayNameOfVerifiedAddress(string? rawAddress)
        {
            var comparableHeaderAddress = EmailAddressHelper.ToComparisonForm(
                ParseEmailAddress(rawAddress),
                normalization);

            return string.Equals(comparableHeaderAddress, comparableVerifiedSender, StringComparison.Ordinal)
                ? EmailAddressHelper.ExtractDisplayName(rawAddress)
                : null;
        }
    }

    /// <summary>
    /// Builds the snapshot RawMetadata for Push mode subject to the size limit (CA-005).
    /// Contains the mode, inbound provider, message-id and SPF/DKIM/DMARC results.
    /// </summary>
    /// <param name="message">Incoming email message.</param>
    /// <param name="inbound">Inbound processing settings.</param>
    /// <returns>JSON metadata, or null if the limit is exceeded.</returns>
    private static JsonElement? BuildRawMetadata(InboundEmailMessage message, EmailInboundOptions inbound)
    {
        // The method collects safe (non-PII) metadata of the incoming mail
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [EmailAdapterConstants.MetadataKeyMode] = EmailAdapterConstants.ModePush,
            [EmailAdapterConstants.MetadataKeyInboundProvider] = inbound.Provider.ToString(),
            [EmailAdapterConstants.MetadataKeyMessageId] = message.MessageId,
            [EmailAdapterConstants.MetadataKeySpfResult] = message.SpfResult,
            [EmailAdapterConstants.MetadataKeyDkimResult] = message.DkimResult,
            [EmailAdapterConstants.MetadataKeyDmarcResult] = message.DmarcResult
        };

        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);

        // CA-005: if the limit is exceeded, do not include metadata
        if (metadataBytes.Length > EmailAdapterConstants.MaxRawMetadataSize)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(metadata);
    }
}
