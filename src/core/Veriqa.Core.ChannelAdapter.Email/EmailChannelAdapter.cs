// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// Email channel adapter (SPEC-016).
/// Supports the Pull mode (magic link) and the Push mode (the one-tap email).
/// Email login and confirmation are delivered through the dedicated email endpoints
/// (EmailAuthEndpoints/EmailPushEndpoints); the push/webhook SPI methods below are deliberate
/// stubs because they are not on the Email delivery path. It also answers
/// <see cref="IEmailPullAvailability"/>, so that the sign-in window offers the address form only where the
/// Pull start serves it, <see cref="IEmailPullMethods"/>, so that the form names its button by what the mail
/// carries and asks for the code of a mail that carries one, and <see cref="IEmailPushAvailability"/>, so that
/// the report of the start knows whether a hop URL of the channel is issued at all.
/// </summary>
internal sealed class EmailChannelAdapter : IChannelAdapter, IEmailPullAvailability, IEmailPullMethods, IEmailPushAvailability
{
    /// <summary>
    /// Global Email settings — read per operation and only for core-owned fields: the
    /// <c>Enabled</c> axis (channels_enabled, §17.7) and core-transport (CFG-230).
    /// A monitor rather than a constructor snapshot, so configuration reload takes effect.
    /// </summary>
    private readonly IOptionsMonitor<EmailOptions> _options;

    /// <summary>
    /// Canonical layer resolver (SPEC-003 §17.4, SPEC-012 CFG-234): resolves the tenant-owned
    /// <c>PublicBaseUrl</c> per operation.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// The one reading of the sign-in modes: the ceiling of the core level narrowed by the tenant and the
    /// application of the transaction, with its preferred mode. Singleton, like this adapter.
    /// </summary>
    private readonly IEmailSignInModes _signInModes;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<EmailChannelAdapter> _logger;

    /// <summary>
    /// Push-mode correlation token store (SPEC-016 §5): the deep link of the Push mode registers the
    /// correlation token its mail carries. Singleton in every contour — no captive dependency in this
    /// singleton adapter.
    /// </summary>
    private readonly IEmailPushCorrelationStore _pushCorrelationStore;

    /// <summary>
    /// Reader of the deep-link prefill context (SPEC-003 §10.4): supplies the ownership levels the faults
    /// of the Push mail are reported by and the expiry of the transaction its correlation token lives
    /// until (the request of GetDeepLinkAsync carries the transaction identifier and the purpose of the
    /// link, not the stored transaction). Singleton, like this adapter — no captive dependency.
    /// </summary>
    private readonly DeepLinkPrefillContextReader _prefillContextReader;

    /// <summary>
    /// Localizer of channel messages into the recipient's language (SPEC-017 §7.2, ICC-050): the renderer
    /// resolves the Natural Keys of the prefilled Push mail through it, falling back to the base text.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// The accessor this adapter takes the templates of its channel messages from: the core resolves
    /// them through the canonical resolver (SPEC-036 TPL-001). Singleton, like this adapter.
    /// </summary>
    private readonly IChannelMessageTemplates _messageTemplates;

    /// <summary>
    /// Clock behind the health-check stamp and the correlation token TTL.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// A fault of the Push sign-in mail that is a state of the configuration, not an event of a request.
    /// </summary>
    private enum PushMailConfigurationFault
    {
        /// <summary>
        /// The inbound address gives the mail no recipient: it is not set, or has no domain to put the
        /// token before.
        /// </summary>
        NoRecipient,

        /// <summary>
        /// Neither the recipient address nor the text of the mail carries the correlation token.
        /// </summary>
        NoCorrelationToken
    }

    /// <summary>
    /// Configuration faults of the Push sign-in mail already reported, by ownership level. The mail is
    /// built on every render of the sign-in page and on every presentation of a hop URL, so the fault of
    /// one configuration is reported loudly once per level and at Debug afterwards. The key is the level,
    /// not the process: the wording of the mail and the inbound address are resolved per tenant,
    /// application and ui_config record, and the fault of one must not hide that of another.
    /// </summary>
    private readonly ConcurrentDictionary<
        (PushMailConfigurationFault Fault, string? TenantId, string? ApplicationId, string? UiConfigSelector), byte>
        _pushMailFaultReported = new();

    /// <summary>
    /// Creates an instance of <see cref="EmailChannelAdapter"/>.
    /// </summary>
    /// <param name="options">Global Email settings (core-owned fields only).</param>
    /// <param name="resolver">Canonical layer resolver (SPEC-003 §17.4).</param>
    /// <param name="signInModes">The reading of the sign-in modes of a transaction.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="pushCorrelationStore">Push-mode correlation token store (token of the Push mail).</param>
    /// <param name="prefillContextReader">Reader of the deep-link prefill context (ownership and expiry source).</param>
    /// <param name="promptLocalizer">Channel message localizer (subject and body of the Push mail).</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="timeProvider">Time provider.</param>
    public EmailChannelAdapter(
        IOptionsMonitor<EmailOptions> options,
        IConfigurationResolver resolver,
        IEmailSignInModes signInModes,
        ILogger<EmailChannelAdapter> logger,
        IEmailPushCorrelationStore pushCorrelationStore,
        DeepLinkPrefillContextReader prefillContextReader,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelMessageTemplates messageTemplates,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _signInModes = signInModes ?? throw new ArgumentNullException(nameof(signInModes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pushCorrelationStore = pushCorrelationStore ?? throw new ArgumentNullException(nameof(pushCorrelationStore));
        _prefillContextReader = prefillContextReader ?? throw new ArgumentNullException(nameof(prefillContextReader));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public string ChannelType => ChannelTypes.Email;

    /// <summary>
    /// Facts Email declares about itself (SPEC-003 §6.1, SPEC-012 §4.4.1): a letter carries no way to
    /// answer a question, so the channel cannot confirm inside itself (the fact stays at its safe
    /// default) and the core asks on its own page; a sent letter cannot be updated; and the channel
    /// shows no terminal status of its own — the confirm page does that. Declaring the last fact
    /// replaces the former pair of no-op "successes", so the core simply does not call what this
    /// channel cannot do. Truly immutable sets.
    /// </summary>
    private static readonly ChannelCapabilities DeclaredCapabilities = new()
    {
        SupportedMessageKinds = new[] { ChannelMessageKind.PlainText }.ToFrozenSet(),
        SupportsMessageUpdate = false,
        DeliversOutcomeNotice = false,
        ProvidesRecipientLocale = false
    };

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => DeclaredCapabilities;

    /// <inheritdoc />
    /// <remarks>
    /// Not applicable to the Email channel: inbound handling does not run through this SPI method.
    /// In Pull mode a confirmation arrives as an action token via the /auth/email/confirm endpoint;
    /// in Push mode an inbound email is handled by the dedicated email push endpoints
    /// (EmailPushEndpoints). This method is a deliberate stub and returns Failure on a direct call.
    /// </remarks>
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // Deliberate stub: inbound handling is not routed through this SPI for the Email channel.
        _logger.LogWarning(
            "[EmailChannelAdapter] ProcessInboundEventAsync is not applicable for the Email channel; " +
            "inbound handling is served by the dedicated email endpoints.");

        return Task.FromResult(
            Result<ChannelInboundResult>.Failure(
                EmailAdapterConstants.ErrorCodeModeDisabled,
                "Inbound event processing is not applicable for the Email channel."));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Email is a link-based channel and does **not push** an interactive prompt, so this SPI method
    /// is not on the critical path for Email: the confirmation orchestrator (`ConfirmationPromptOrchestrator`)
    /// calls `SendConfirmationPromptAsync` only for messengers (Telegram/WhatsApp/Max).
    /// Login confirmation over Email is delivered by a magic-link email via `EmailAuthEndpoints`
    /// (Pull mode, SPEC-016 §4), and the initiator context (`ConfirmationPromptContext`) is rendered
    /// exactly there — in the email body (SPEC-017 ICC-043), not by this method. The method is a
    /// deliberate stub: on an erroneous direct call it returns Failure so as not to mask
    /// the wrong delivery path.
    /// </remarks>
    public Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        // Deliberate stub: Email does not push a prompt (delivery is by email via EmailAuthEndpoints).
        // A direct call of this SPI for Email is not intended — signal Failure.
        _logger.LogWarning(
            "[EmailChannelAdapter] SendConfirmationPromptAsync called for the Email channel, which " +
            "delivers the confirmation as a magic-link email, not a pushed prompt. " +
            "TransactionId={TransactionId}, ChannelUserId=[MASKED].",
            transactionId);

        return Task.FromResult(
            Result<ChannelMessageRef?>.Failure(
                EmailAdapterConstants.ErrorCodeModeDisabled,
                "The Email channel delivers the confirmation by mail (magic link); pushed prompts are not supported."));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sending arbitrary messages by email is not supported in the current scope.
    /// </remarks>
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        // The Email channel is not intended for sending arbitrary messages
        return Task.FromResult(
            Result<ChannelMessageRef?>.Failure(
                EmailAdapterConstants.ErrorCodeModeDisabled,
                "Arbitrary messages via the Email channel are not supported."));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Not applicable to the Email channel: the Push-mode inbound webhook validates its secret inline
    /// in the dedicated email push endpoints (EmailPushEndpoints, CA-033), not through this SPI method.
    /// This method is a deliberate stub and returns Failure on a direct call.
    /// </remarks>
    public Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // Deliberate stub: the Email push webhook validates its secret inline in the email push endpoints.
        _logger.LogWarning(
            "[EmailChannelAdapter] ValidateWebhookAsync is not applicable for the Email channel; " +
            "the push webhook secret is validated by the dedicated email endpoints.");

        return Task.FromResult(
            Result<bool>.Failure(
                EmailAdapterConstants.ErrorCodeModeDisabled,
                "Webhook validation is not applicable for the Email channel."));
    }

    /// <inheritdoc />
    public async Task<Result<ChannelHealthStatus>> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        // Check the adapter's basic configuration
        await Task.CompletedTask;

        var options = _options.CurrentValue;

        // If the adapter is disabled — report it explicitly
        if (!options.Enabled)
        {
            return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
                IsHealthy: false,
                ChannelType: ChannelTypes.Email,
                ResponseTime: TimeSpan.Zero,
                Details: "The Email adapter is disabled in the configuration.",
                CheckedAt: _timeProvider.GetUtcNow()));
        }

        // The adapter is activated with the Pull mode
        var details = $"Email adapter activated. Pull mode: {(options.PullEnabled ? "enabled" : "disabled")}" +
                      $", Push mode: {(options.PushEnabled ? "enabled" : "disabled")}" +
                      $", PreferredMode: {options.PreferredMode}";
        return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
            IsHealthy: true,
            ChannelType: ChannelTypes.Email,
            ResponseTime: TimeSpan.Zero,
            Details: details,
            CheckedAt: _timeProvider.GetUtcNow()));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Push mode: returns the <c>mailto:</c> of the sign-in mail (SPEC-016 §5.3) — the QR code and the
    /// "Open mail client" button of the authentication page carry the mail itself. A mail that could not
    /// be built leaves the Pull target below when Pull is in the effective set, and a failure only when it
    /// is not.
    /// Pull mode: returns the relative start path (/auth/email/start).
    /// The authentication page recognizes the relative path and renders the email input form inline.
    /// The modes are those of the effective set of the transaction — the core ceiling narrowed by its
    /// tenant and its application: Push is taken when it is in the set, the tenant states its
    /// <c>PublicBaseUrl</c>, and the preferred mode is Push or Pull is not in the set.
    /// </remarks>
    public async Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            return Result<string>.Failure(
                EmailAdapterConstants.ErrorCodeModeDisabled,
                "The Email adapter is disabled.");
        }

        // The request of GetDeepLinkAsync names the transaction, not its state, so the ownership levels come
        // from the stored transaction, read once and before the mode is decided: the sign-in modes are
        // chosen by its tenant and its application, and the mail below is built over the same levels. An
        // unreadable transaction leaves the core level as the only one there is.
        var prefillContext = await _prefillContextReader.ReadAsync(
            request.TransactionId, ChannelTypes.Email, cancellationToken);
        var modes = await _signInModes.ResolveAsync(
            prefillContext?.Ownership ?? ResolutionContext.Core, cancellationToken);

        if (await ResolvePushSignInCredentialsAsync(modes, cancellationToken) is { } pushCredentials)
        {
            // The target of the Push mode is the mail itself, whatever Inbound.TokenInLocalPart says: the
            // setting chooses the form of the recipient address only.
            var mailto = await TryBuildPushMailtoAsync(
                request, prefillContext, modes, options, pushCredentials, cancellationToken);

            if (mailto is not null)
            {
                return Result<string>.Success(mailto);
            }

            // A mail that was not built takes only the mail away: where Pull is served the channel keeps
            // its address form, and the target is the Pull one. Without Pull the channel has no target.
            // Why it was not built — and what the channel does instead — is already in the journal, by
            // the one entry the build wrote.
            if (!modes.Pull)
            {
                return Result<string>.Failure(
                    ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                    "The Email Push sign-in mail was not built and Pull mode is not offered to the transaction.");
            }
        }
        else if (!modes.Pull)
        {
            return Result<string>.Failure(
                EmailAdapterConstants.ErrorCodeModeDisabled,
                "Neither Email Pull mode nor Push mode is offered to the transaction, or Push is not configured.");
        }

        // Pull mode: the authentication page recognizes the relative path and renders the form inline
        return Result<string>.Success(EmailAdapterConstants.PullStartPath);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The same effective set the Pull start checks before serving the form, and the one
    /// <see cref="GetDeepLinkAsync"/> decides the target by.
    /// </remarks>
    public async ValueTask<bool> IsPullAvailableAsync(ResolutionContext context, CancellationToken cancellationToken) =>
        (await _signInModes.ResolveAsync(context, cancellationToken)).Pull;

    /// <inheritdoc />
    /// <remarks>
    /// The same effective set the Pull start chooses the kind of its mail by.
    /// </remarks>
    public async ValueTask<IReadOnlyList<EmailPullMethod>> GetPullMethodsAsync(
        ResolutionContext context,
        CancellationToken cancellationToken) =>
        (await _signInModes.ResolveAsync(context, cancellationToken)).PullMethods;

    /// <inheritdoc />
    /// <remarks>
    /// The same decision <see cref="GetDeepLinkAsync"/> takes the Push target by
    /// (<see cref="SelectPushSignInCredentials"/>). The sign-in modes are those of the given context; the
    /// tenant credentials are those of the tenant scope of the call: asked under the default implicit
    /// tenant, they are those of the core level. Credentials that do not resolve are the answer "not Push"
    /// and are not reported here: this is a question about the mode, not a sign-in that lost its Push
    /// target.
    /// </remarks>
    public async ValueTask<bool> IsPushEffectiveAsync(ResolutionContext context, CancellationToken cancellationToken)
    {
        var modes = await _signInModes.ResolveAsync(context, cancellationToken);

        if (!modes.PushIsEffective)
        {
            return false;
        }

        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);

        return credentialsResult.TryGetValue(out var credentials)
            && SelectPushSignInCredentials(modes, credentials) is not null;
    }

    /// <summary>
    /// The Push credentials of a sign-in that is being started: the tenant credentials are resolved, and
    /// the decision of the mode is taken over them (<see cref="SelectPushSignInCredentials"/>).
    /// </summary>
    /// <param name="modes">The effective set of the sign-in modes of the transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved tenant credentials when Push is the effective mode; null — it is not.</returns>
    private async Task<EmailTenantCredentials?> ResolvePushSignInCredentialsAsync(
        EmailSignInModeSet modes,
        CancellationToken cancellationToken)
    {
        // PublicBaseUrl is a tenant-credential field — resolve it per operation through the seam
        // (CA-162). Unresolvable credentials are decided the same as an unset PublicBaseUrl (not Push),
        // not by an exception.
        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogWarning(
                "Failed to resolve the tenant Email credentials; the Email sign-in does not take Push "
                + "and falls back to the relative Pull path. Error: {ErrorCode}",
                credentialsResult.Error.Code);
            return null;
        }

        return SelectPushSignInCredentials(modes, credentialsResult.Value);
    }

    /// <summary>
    /// The one decision of the Email sign-in mode: Push is taken when the effective set takes it
    /// (<see cref="EmailSignInModeSet.PushIsEffective"/> — in the set, and preferred or the only one) and
    /// the tenant states its <c>PublicBaseUrl</c>.
    /// </summary>
    /// <param name="modes">The effective set of the sign-in modes.</param>
    /// <param name="credentials">Resolved tenant credentials.</param>
    /// <returns>The given credentials when Push is the effective mode; null — it is not.</returns>
    private static EmailTenantCredentials? SelectPushSignInCredentials(
        EmailSignInModeSet modes,
        EmailTenantCredentials credentials) =>
        modes.PushIsEffective && !string.IsNullOrEmpty(credentials.PublicBaseUrl)
            ? credentials
            : null;

    /// <summary>
    /// Builds the <c>mailto:</c> of the Push sign-in mail (SPEC-016 §5.3): registers a single-use
    /// correlation token for the transaction and returns a self-contained link with the subject and the
    /// body of the deep-link prefill of this channel. <c>Inbound.TokenInLocalPart</c> chooses where the
    /// token rides: <c>true</c> — it is the recipient local part (<c>{token}@{domain}</c>, delivered via a
    /// catch-all mailbox); <c>false</c> — the recipient is the inbound address (plus-addressed with the
    /// token when that is enabled) and the token stands in its slot of the text of the mail (§5.4).
    /// </summary>
    /// <param name="request">The request for the link: the transaction and the purpose of the link.</param>
    /// <param name="prefillContext">
    /// The stored transaction as the deep link reads it (ownership levels and expiry); null — it could not
    /// be read.
    /// </param>
    /// <param name="modes">The effective set of the sign-in modes (what the channel does without the mail).</param>
    /// <param name="options">Global Email settings (token TTL default).</param>
    /// <param name="credentials">Resolved tenant credentials (inbound settings, token TTL override).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The mailto link, or null when no mail was built: the inbound address gives it no recipient, the
    /// transaction has expired, or the build failed.
    /// </returns>
    private async Task<string?> TryBuildPushMailtoAsync(
        DeepLinkRequest request,
        DeepLinkPrefillContext? prefillContext,
        EmailSignInModeSet modes,
        EmailOptions options,
        EmailTenantCredentials credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            return await BuildPushMailtoAsync(
                request, prefillContext, modes, options, credentials, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only the type of the fault is logged: what a correlation store reports about a record it
            // failed to write may quote the token, and the token addresses the transaction.
            _logger.LogWarning(
                "Building the Push sign-in mailto failed ({ExceptionType}) for transaction {TransactionId}; "
                + "no Push sign-in mailto is built, {Consequence}.",
                ex.GetType().Name,
                request.TransactionId,
                MissingPushMailConsequence(modes));
            return null;
        }
    }

    /// <summary>
    /// The build behind <see cref="TryBuildPushMailtoAsync"/>; a fault of the correlation store or of the
    /// rendering of the mail is thrown to it.
    /// </summary>
    /// <param name="request">The request for the link: the transaction and the purpose of the link.</param>
    /// <param name="prefillContext">
    /// The stored transaction as the deep link reads it (ownership levels and expiry); null — it could not
    /// be read.
    /// </param>
    /// <param name="modes">The effective set of the sign-in modes (what the channel does without the mail).</param>
    /// <param name="options">Global Email settings (token TTL default).</param>
    /// <param name="credentials">Resolved tenant credentials (inbound settings, token TTL override).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mailto link, or null when no mail is to be built.</returns>
    private async Task<string?> BuildPushMailtoAsync(
        DeepLinkRequest request,
        DeepLinkPrefillContext? prefillContext,
        EmailSignInModeSet modes,
        EmailOptions options,
        EmailTenantCredentials credentials,
        CancellationToken cancellationToken)
    {
        var inbound = credentials.Inbound;
        var tokenInLocalPart = inbound.TokenInLocalPart;
        var domain = tokenInLocalPart ? ExtractDomain(inbound.InboundAddress) : null;

        // The ownership levels the faults of the mail are reported by and the expiry are those of the
        // transaction GetDeepLinkAsync has already read; an unreadable transaction leaves the core level
        // as the only one there is. The template, the locale and the time zone of the mail come from the
        // core accessor of channel message templates, read off the same transaction, with the template of
        // the purpose of the link where one is stated.
        var ownership = prefillContext?.Ownership ?? ResolutionContext.Core;

        if (tokenInLocalPart ? domain is null : string.IsNullOrWhiteSpace(inbound.InboundAddress))
        {
            ReportMailWithoutRecipient(ownership, tokenInLocalPart, modes);
            return null;
        }

        // The mail is written from a page that is rendered once and stays on screen for as long as its
        // transaction lives, so the token lives exactly that long, in either form of the address: it
        // neither expires under a person who sends the mail late nor outlives the transaction. The
        // configured token TTL stands in only where the transaction could not be read.
        var now = _timeProvider.GetUtcNow();
        var tokenExpiresAt = prefillContext?.TransactionExpiresAt
            ?? now.Add(EmailCredentialsResolver.ResolveTokenTtl(credentials, options.TokenTtl));

        if (tokenExpiresAt <= now)
        {
            _logger.LogWarning(
                "Transaction {TransactionId} has expired; no Push sign-in mailto is built, {Consequence}.",
                request.TransactionId,
                MissingPushMailConsequence(modes));
            return null;
        }

        // The subject and the body are the deep-link prefill message of this channel — the same kind
        // WhatsApp prefills its link with (SPEC-036 §4.6), resolved over the ownership of the very
        // transaction the link is built for (SPEC-036 TPL-116). It is resolved before the correlation
        // token is registered: a mail whose template cannot be resolved is not built, and no live token
        // is left behind for it. The failure itself is already logged by the core.
        var prefillTemplate = await _messageTemplates.ResolveAsync(
            this, MessageKinds.DeeplinkPrefill, request, cancellationToken);
        if (prefillTemplate.IsFailure)
        {
            _logger.LogWarning(
                "The template of the Push sign-in mail of transaction {TransactionId} could not be resolved; "
                + "no Push sign-in mailto is built, {Consequence}.",
                request.TransactionId,
                MissingPushMailConsequence(modes));
            return null;
        }

        // A single-use correlation token bound to the transaction (EM-022: the raw transaction_id is not
        // exposed in the mail). A token that rides the ADDRESS is minted from a lower-case alphabet, since
        // a mail client or an inbound provider may change the case of an address on the way; one that
        // rides the text of the mail (and the plus segment, when used) takes the URL-safe Base64 form
        // (SPEC-016 §5.4).
        var mintedToken = tokenInLocalPart
            ? EmailPushTokenGenerator.GenerateLocalPartToken()
            : EmailPushTokenGenerator.GenerateUrlSafeToken();
        var correlation = new EmailPushCorrelation(request.TransactionId.ToString(), tokenExpiresAt);

        // Registration is idempotent (SPEC-025 HOP-011a): this call site runs on every render of the
        // sign-in page and on every presentation of a hop URL, and minting a token per call would leave as
        // many live secrets for one transaction as there were calls. The store returns the token already
        // live for the transaction (if any), and the freshly minted one is simply dropped — the mint has
        // no side effects of its own. The expiry of a reused token is not moved. The token is one per
        // transaction whatever the purpose of the link: a mail asked for another purpose reuses it.
        var token = await _pushCorrelationStore.StoreOrReuseAsync(mintedToken, correlation, cancellationToken);

        // A token that rides the address is not supplied as a slot value, and the ladder degrades to its
        // payload-free variant on its own.
        var slots = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!tokenInLocalPart)
        {
            slots[SlotNames.Token] = token;
        }

        // The application and the brand name the mail as the prefill of WhatsApp does; an unknown
        // application leaves its slot without a value, and the ladder answers with a step below.
        if (!string.IsNullOrWhiteSpace(prefillTemplate.Value.ApplicationName))
        {
            slots[SlotNames.App] = prefillTemplate.Value.ApplicationName;
        }

        TransactionSlotValues.AddBrandName(slots, prefillTemplate.Value.BrandName);

        var (subject, body) = RenderPrefill(
            prefillTemplate.Value.Message,
            slots,
            prefillTemplate.Value.Locale,
            prefillTemplate.Value.TimeZone);

        // The recipient stays a literal addr-spec — do NOT percent-encode it. Uri.EscapeDataString would
        // encode the "@" to "%40", and many phone QR scanners / mail clients then fail to recognize
        // mailto:{token}%40{domain} as an email and only show the raw string instead of opening the mail
        // client. Every part is already mailto-safe (base32 or URL-safe Base64 token, DNS domain), and "@"
        // and "+" stand in the address as they are (RFC 6068 §2). Only subject/body (free text with
        // spaces) are escaped.
        var recipient = tokenInLocalPart
            ? $"{token}@{domain}"
            : BuildPushRecipientAddress(inbound, token);

        // With the token in neither the address nor the text (EM-021), a reply to this mail can never be
        // matched to its transaction.
        if (!recipient.Contains(token, StringComparison.Ordinal)
            && !subject.Contains(token, StringComparison.Ordinal)
            && !body.Contains(token, StringComparison.Ordinal))
        {
            ReportMailWithoutToken(ownership);
        }

        return BuildMailto(recipient, subject, body);
    }

    /// <summary>
    /// Reports a Push sign-in mail that carries no correlation token: as an error the first time an
    /// ownership level builds one, at Debug on every later build for the same level.
    /// </summary>
    /// <param name="ownership">Ownership levels the mail was built over.</param>
    private void ReportMailWithoutToken(ResolutionContext ownership)
    {
        if (!IsFirstReport(PushMailConfigurationFault.NoCorrelationToken, ownership))
        {
            _logger.LogDebug(
                "The Push sign-in mailto carries no correlation token (message kind {MessageKind}); "
                + "already reported for this ownership level.",
                MessageKinds.DeeplinkPrefill);
            return;
        }

        _logger.LogError(
            "The Push sign-in mailto carries no correlation token: neither the recipient address nor the "
            + "text of message kind {MessageKind} holds it, so a reply to it can never be matched to its "
            + "transaction. Turn Inbound.UsePlusAddressing on, or state a plain-text step whose wording "
            + "references the token slot. Reported once per ownership level.",
            MessageKinds.DeeplinkPrefill);
    }

    /// <summary>
    /// Reports a Push sign-in mail the inbound address gives no recipient: as a warning the first time
    /// an ownership level meets it, at Debug on every later build for the same level. The address is a
    /// setting, so the condition holds for every sign-in of the level until the configuration changes.
    /// </summary>
    /// <param name="ownership">Ownership levels the mail was to be built over.</param>
    /// <param name="tokenInLocalPart">Whether the token is the local part of the recipient address.</param>
    /// <param name="modes">The effective set of the sign-in modes (what the channel does without the mail).</param>
    private void ReportMailWithoutRecipient(
        ResolutionContext ownership,
        bool tokenInLocalPart,
        EmailSignInModeSet modes)
    {
        var problem = tokenInLocalPart ? "has no usable domain" : "is not set";

        if (!IsFirstReport(PushMailConfigurationFault.NoRecipient, ownership))
        {
            _logger.LogDebug(
                "Push is the effective Email mode but InboundAddress {Problem}; no Push sign-in mailto is "
                + "built, {Consequence}. Already reported for this ownership level.",
                problem,
                MissingPushMailConsequence(modes));
            return;
        }

        _logger.LogWarning(
            "Push is the effective Email mode but InboundAddress {Problem}; no Push sign-in mailto is "
            + "built, {Consequence}. Reported once per ownership level.",
            problem,
            MissingPushMailConsequence(modes));
    }

    /// <summary>
    /// Whether a configuration fault of the Push sign-in mail is met for the first time at an ownership
    /// level; the first answer for a level is the only <c>true</c> one.
    /// </summary>
    /// <param name="fault">The fault.</param>
    /// <param name="ownership">Ownership levels the mail is built over.</param>
    /// <returns>true — the fault was not reported for the level yet.</returns>
    private bool IsFirstReport(PushMailConfigurationFault fault, ResolutionContext ownership) =>
        _pushMailFaultReported.TryAdd(
            (fault, ownership.TenantId, ownership.ApplicationId, ownership.UiConfigSelector), default);

    /// <summary>
    /// What the channel does for a sign-in whose Push mail was not built, as the journal words it beside
    /// the reason: one entry then states both. What it does is decided by the effective set of the
    /// transaction: Pull outside it leaves the channel without a target.
    /// </summary>
    /// <param name="modes">The effective set of the sign-in modes of the transaction.</param>
    /// <returns>The wording.</returns>
    private static string MissingPushMailConsequence(EmailSignInModeSet modes) =>
        modes.Pull
            ? "the channel falls back to the relative Pull path"
            : "the channel has no target, Pull mode being disabled";

    /// <summary>
    /// Assembles a <c>mailto:</c> link of a prefilled mail. The recipient is a literal addr-spec and is not
    /// escaped; only the subject and the body, free text, are percent-encoded.
    /// </summary>
    /// <param name="recipient">The recipient address, already mailto-safe.</param>
    /// <param name="subject">The subject of the mail.</param>
    /// <param name="body">The body of the mail.</param>
    /// <returns>The mailto link.</returns>
    private static string BuildMailto(string recipient, string subject, string body) =>
        $"{Uri.UriSchemeMailto}:{recipient}"
        + $"?subject={Uri.EscapeDataString(subject)}"
        + $"&body={Uri.EscapeDataString(body)}";

    /// <summary>
    /// Renders the deep-link prefill of this channel — the subject and the body of the prefilled mail
    /// (SPEC-036 §4.6) — as plain text, the form a mail client opens a new mail in.
    /// </summary>
    /// <param name="template">The resolved prefill message of this channel.</param>
    /// <param name="slots">Slot values of the message.</param>
    /// <param name="locale">Locale of the mail; null — the base language.</param>
    /// <param name="timeZone">Time zone the moments of the message are shown in; null — UTC.</param>
    /// <returns>The subject and the body; empty when no step of the ladder states the plain-text edition.</returns>
    private (string Subject, string Body) RenderPrefill(
        ResolvedMessage template,
        Dictionary<string, object?> slots,
        string? locale,
        string? timeZone)
    {
        var prefill = MessageTextRenderer.RenderParts(
            template,
            slots,
            locale,
            timeZone,
            _promptLocalizer,
            MessageRenderMode.PlainText,
            _logger);

        // A ladder none of whose steps states the plain-text edition leaves the prefilled mail without
        // wording; the link still opens the mail client.
        if (prefill is null)
        {
            _logger.LogError(
                "No step of the {MessageKind} ladder of channel {ChannelType} states the plain-text "
                + "edition; the prefilled mail of the deep link carries no subject and no body.",
                MessageKinds.DeeplinkPrefill,
                ChannelTypes.Email);
        }

        return (prefill?.Subject ?? string.Empty, prefill?.Body ?? string.Empty);
    }

    /// <summary>
    /// Extracts the domain part of an email address (after the last <c>@</c>); null when absent.
    /// </summary>
    /// <param name="address">Email address (for example <c>login@veriqa.app</c>).</param>
    /// <returns>The domain (for example <c>veriqa.app</c>), or null.</returns>
    internal static string? ExtractDomain(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var trimmed = address.Trim();
        var atIndex = trimmed.LastIndexOf('@');
        return atIndex <= 0 || atIndex == trimmed.Length - 1
            ? null
            : trimmed[(atIndex + 1)..];
    }

    /// <summary>
    /// Builds the Push mode recipient address, taking plus-addressing into account.
    /// </summary>
    /// <param name="inbound">Inbound processing settings.</param>
    /// <param name="correlationToken">The correlation token to insert into the plus segment.</param>
    /// <returns>The recipient address, or an empty string when InboundAddress is unset.</returns>
    internal static string BuildPushRecipientAddress(EmailInboundOptions inbound, string correlationToken)
    {
        // The method inserts the correlation token into the address via plus-addressing, if it is enabled
        var address = inbound.InboundAddress.Trim();

        if (!inbound.UsePlusAddressing)
        {
            return address;
        }

        var atIndex = address.LastIndexOf('@');
        if (atIndex <= 0)
        {
            return address;
        }

        var localPart = address[..atIndex];
        var domain = address[(atIndex + 1)..];

        return $"{localPart}{EmailAdapterConstants.PlusAddressSeparator}{correlationToken}@{domain}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Email channel shows no terminal status of its own — the magic-link confirm page does.
    /// It declares <c>DeliversOutcomeNotice = false</c>, so the core never calls this method; a
    /// direct call answers "deliberately showed nothing" rather than a fake success.
    /// </remarks>
    public Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<bool>.Success(false));
    }
}
