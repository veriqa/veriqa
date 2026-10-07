// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// Email channel adapter (SPEC-016).
/// Supports the Pull mode (magic link) and the Push mode (the one-tap email).
/// Email login and confirmation are delivered through the dedicated email endpoints
/// (EmailAuthEndpoints/EmailPushEndpoints); the push/webhook SPI methods below are deliberate
/// stubs because they are not on the Email delivery path.
/// </summary>
internal sealed class EmailChannelAdapter : IChannelAdapter
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
    /// Logger.
    /// </summary>
    private readonly ILogger<EmailChannelAdapter> _logger;

    /// <summary>
    /// Push-mode correlation token store (SPEC-016 §5). Used only on the direct-mailto deep-link path to
    /// mint a token at QR-generation time (the compose-helper path mints lazily on the compose page).
    /// Singleton in every contour — no captive dependency in this singleton adapter.
    /// </summary>
    private readonly IEmailPushCorrelationStore _pushCorrelationStore;

    /// <summary>
    /// Reader of the deep-link prefill context (SPEC-003 §10.4): supplies the sign-in page locale for the
    /// direct-mailto subject/body (GetDeepLinkAsync receives only a transaction id). Singleton, like this
    /// adapter — no captive dependency.
    /// </summary>
    private readonly DeepLinkPrefillContextReader _prefillContextReader;

    /// <summary>
    /// Localizer of channel messages into the recipient's language (SPEC-017 §7.2, ICC-050): the renderer
    /// resolves the Natural Keys of the direct-mailto prefill through it, falling back to the base text.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// The single point at which the render points of this adapter obtain a resolved message —
    /// through the canonical resolver (SPEC-036 TPL-001). Singleton, like this adapter.
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// Clock behind the health-check stamp and the correlation token TTL.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an instance of <see cref="EmailChannelAdapter"/>.
    /// </summary>
    /// <param name="options">Global Email settings (core-owned fields only).</param>
    /// <param name="resolver">Canonical layer resolver (SPEC-003 §17.4).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="pushCorrelationStore">Push-mode correlation token store (direct-mailto minting).</param>
    /// <param name="prefillContextReader">Reader of the deep-link prefill context (locale source).</param>
    /// <param name="promptLocalizer">Channel message localizer (direct-mailto subject/body).</param>
    /// <param name="messageTemplates">Accessor of the resolved messages of this adapter.</param>
    /// <param name="timeProvider">Time provider.</param>
    public EmailChannelAdapter(
        IOptionsMonitor<EmailOptions> options,
        IConfigurationResolver resolver,
        ILogger<EmailChannelAdapter> logger,
        IEmailPushCorrelationStore pushCorrelationStore,
        DeepLinkPrefillContextReader prefillContextReader,
        IConfirmationPromptLocalizer promptLocalizer,
        IMessageTemplateAccessor messageTemplates,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
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
    /// Push mode: returns the absolute URL of the compose-helper page for QR code generation.
    /// The authentication page shows the QR and the "Open mail client" button.
    /// Pull mode: returns the relative start path (/auth/email/start).
    /// The authentication page recognizes the relative path and renders the email input form inline.
    /// Push takes priority when enabled and PreferredMode=Push (the default) or Pull is disabled.
    /// </remarks>
    public async Task<Result<string>> GetDeepLinkAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        // The method returns the URL for the QR/button: a direct mailto (Push direct-mailto),
        // an absolute compose URL (Push compose), or a relative path (Pull start).

        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            return Result<string>.Failure(
                EmailAdapterConstants.ErrorCodeModeDisabled,
                "The Email adapter is disabled.");
        }

        // PublicBaseUrl is a tenant-credential field — resolve it per operation through the seam
        // (CA-162). Unresolvable credentials mean no absolute compose URL can be built: fall back
        // to the same branch as an unset PublicBaseUrl (the relative Pull path), not an exception.
        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        var publicBaseUrl = credentialsResult.IsSuccess
            ? credentialsResult.Value.PublicBaseUrl
            : string.Empty;

        if (credentialsResult.IsFailure)
        {
            _logger.LogWarning(
                "Failed to resolve the tenant Email credentials for the deep link; "
                + "falling back to the relative Pull path. Error: {ErrorCode}",
                credentialsResult.Error.Code);
        }

        // Push mode takes priority when enabled, PublicBaseUrl is set, and PreferredMode=Push (or Pull is unavailable)
        var canUsePush = options.PushEnabled && !string.IsNullOrEmpty(publicBaseUrl);
        if (canUsePush && (options.PreferredMode is EmailMode.Push || !options.PullEnabled))
        {
            // Direct-mailto Push (SPEC-016 §5.3): the QR carries a self-contained mailto with the
            // correlation token as the recipient local part — no hosted compose page. Enabled by
            // Inbound.TokenInLocalPart. On a misconfigured inbound address it falls back to the compose URL.
            // canUsePush already implies IsSuccess (non-empty PublicBaseUrl); the explicit check keeps
            // the read off a failed result, where Result.Value throws.
            if (credentialsResult.IsSuccess && credentialsResult.Value.Inbound.TokenInLocalPart)
            {
                var mailto = await TryBuildDirectMailtoAsync(
                    transactionId, credentialsResult.Value.Inbound, options, credentialsResult.Value, cancellationToken);
                if (mailto is not null)
                {
                    return Result<string>.Success(mailto);
                }
            }

            var composeUrl = $"{publicBaseUrl.TrimEnd('/')}{EmailAdapterConstants.PushComposePath}"
                + $"?{EmailAdapterConstants.SessionIdQueryParam}={transactionId}";
            return Result<string>.Success(composeUrl);
        }

        if (!options.PullEnabled)
        {
            return Result<string>.Failure(
                EmailAdapterConstants.ErrorCodeModeDisabled,
                "Email Pull mode and Push mode are both disabled or not configured.");
        }

        // Pull mode: the authentication page recognizes the relative path and renders the form inline
        return Result<string>.Success(EmailAdapterConstants.PullStartPath);
    }

    /// <summary>
    /// Builds a direct-mailto Push deep link (SPEC-016 §5.3): mints a single-use correlation token, embeds
    /// it as the recipient local part (<c>{token}@{domain}</c>, delivered via a catch-all mailbox), and
    /// returns a self-contained <c>mailto:</c> with a short subject/body.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="inbound">Resolved tenant inbound settings (domain source).</param>
    /// <param name="options">Global Email settings (token TTL default).</param>
    /// <param name="credentials">Resolved tenant credentials (token TTL override).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mailto deep link, or null when the inbound address has no usable domain
    /// (the caller then falls back to the compose-helper URL).</returns>
    private async Task<string?> TryBuildDirectMailtoAsync(
        TransactionId transactionId,
        EmailInboundOptions inbound,
        EmailOptions options,
        EmailTenantCredentials credentials,
        CancellationToken cancellationToken)
    {
        var domain = ExtractDomain(inbound.InboundAddress);
        if (domain is null)
        {
            _logger.LogWarning(
                "Direct-mailto Push is enabled but InboundAddress has no usable domain; "
                + "falling back to the compose-helper page.");
            return null;
        }

        // Mint a single-use correlation token bound to the transaction (EM-022: the raw transaction_id is
        // not exposed in the address). At this call site (auth-page render) the transaction is freshly
        // Pending with Email allowed, so no re-validation is needed here — unlike the compose page, which
        // runs as a separate later request.
        // The token rides the ADDRESS, whose case a mail client or an inbound provider may change on the
        // way, so it is minted from a lower-case alphabet rather than the mixed-case Base64Url of the
        // compose page (SPEC-016 §5.4).
        var mintedToken = EmailPushTokenGenerator.GenerateLocalPartToken();
        var ttl = EmailCredentialsResolver.ResolveTokenTtl(credentials, options.TokenTtl);
        var correlation = new EmailPushCorrelation(transactionId.ToString(), _timeProvider.GetUtcNow().Add(ttl));

        // Registration is idempotent: this call site runs on every render of the sign-in page, and minting a
        // token per render would leave as many live secrets for one transaction as there were renders. The
        // store returns the token already live for the transaction (if any), and the freshly minted one is
        // simply dropped — the mint has no side effects of its own. The TTL of a reused token is not
        // extended, so a token near the end of its life is replaced by the next render after it expires.
        var token = await _pushCorrelationStore.StoreOrReuseAsync(mintedToken, correlation, cancellationToken);

        // The subject and the body of the prefilled mail are the deep-link prefill message of this
        // channel — the same one the compose page renders, and the same kind WhatsApp prefills its link
        // with (SPEC-036 §4.6). Here the token rides the ADDRESS, so no {token} value is supplied and the
        // ladder degrades to its payload-free variant on its own, without a branch of its own.
        // GetDeepLinkAsync carries only a transaction id, so the locale comes from the stored transaction
        // (same source as WhatsApp v1); an unavailable source degrades to the base language.
        var prefillContext = await _prefillContextReader.ReadAsync(
            transactionId, ChannelTypes.Email, cancellationToken);

        var prefill = MessageTextRenderer.RenderParts(
            await _messageTemplates.RequireAsync(
                MessageKinds.DeeplinkPrefill,
                MessageSurfaces.InChannel,
                ChannelTypes.Email,
                // The wording is resolved over the ownership of the very transaction this link is
                // built for (SPEC-036 TPL-116); an unreadable transaction leaves the core level as
                // the only one there is, which is also where the locale of the mail degrades to.
                prefillContext?.Ownership ?? ResolutionContext.Core,
                cancellationToken),
            new Dictionary<string, object?>(StringComparer.Ordinal),
            prefillContext?.UiLocale,
            prefillContext?.UiTimeZone,
            _promptLocalizer,
            MessageRenderMode.PlainText,
            _logger);

        // A ladder none of whose steps states the plain-text edition leaves the prefilled mail without
        // wording; the address still carries the correlation token, so the link keeps working.
        if (prefill is null)
        {
            _logger.LogError(
                "No step of the {MessageKind} ladder of channel {ChannelType} states the plain-text "
                + "edition; the prefilled mail of the deep link carries no subject and no body.",
                MessageKinds.DeeplinkPrefill,
                ChannelTypes.Email);
        }

        var subject = prefill?.Subject ?? string.Empty;
        var body = prefill?.Body ?? string.Empty;

        // The recipient stays a literal addr-spec ({base32-token}@{domain}) — do NOT percent-encode it.
        // Uri.EscapeDataString would encode the "@" to "%40", and many phone QR scanners / mail clients then
        // fail to recognize mailto:{token}%40{domain} as an email and only show the raw string instead of
        // opening the mail client. Both parts are already URL/mailto-safe (base32 token, DNS domain), so
        // no escaping is needed. Only subject/body (free text with spaces) are escaped.
        var recipient = $"{token}@{domain}";
        return $"mailto:{recipient}"
            + $"?subject={Uri.EscapeDataString(subject)}"
            + $"&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>
    /// Extracts the domain part of an email address (after the last <c>@</c>); null when absent.
    /// </summary>
    /// <param name="address">Email address (for example <c>login@veriqa.app</c>).</param>
    /// <returns>The domain (for example <c>veriqa.app</c>), or null.</returns>
    private static string? ExtractDomain(string? address)
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
