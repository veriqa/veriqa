// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;
using Veriqa.Core.ChannelAdapter.WhatsApp.Configuration;
using Veriqa.Core.ChannelAdapter.WhatsApp.Constants;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.WhatsApp;

/// <summary>
/// WhatsApp channel adapter (SPEC-003 §10).
/// There is a single channel adapter — the channel logic (handling inbound events, identity, deep link) is shared.
/// The message delivery provider (a shipped one — Meta Cloud API or Twilio — or one supplied by the host)
/// is injected via <see cref="IWhatsAppProvider"/>; it also owns the provider-specific halves of the inbound
/// path — the authenticity check of its webhook and the format of its body (SPEC-003 §10.2).
/// </summary>
internal sealed partial class WhatsAppChannelAdapter : IChannelAdapter
{
    /// <summary>
    /// WhatsApp message delivery provider.
    /// </summary>
    private readonly IWhatsAppProvider _provider;

    /// <summary>
    /// Per-tenant channel credential provider (seam CA-165). Tenant-credential fields (the business number
    /// of the deep link) are resolved per operation by the ambient tenant, never captured
    /// in the constructor — self-hosted (tenant=null) stays the default N=1 path (1:1 behavior).
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<WhatsAppChannelAdapter> _logger;

    /// <summary>
    /// Localizer of channel messages into the recipient's language (SPEC-017 §7.2).
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// The accessor this adapter takes the templates of its channel messages from: the core resolves
    /// them through the canonical resolver (SPEC-036 TPL-001). Singleton, like this adapter.
    /// </summary>
    private readonly IChannelMessageTemplates _messageTemplates;

    /// <summary>
    /// Clock behind the health-check stamps and the capture timestamps of the adapter.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an instance of <see cref="WhatsAppChannelAdapter"/>.
    /// </summary>
    /// <param name="provider">Message delivery provider.</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="timeProvider">Time provider.</param>
    public WhatsAppChannelAdapter(
        IWhatsAppProvider provider,
        IConfigurationResolver resolver,
        ILogger<WhatsAppChannelAdapter> logger,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelMessageTemplates messageTemplates,
        TimeProvider timeProvider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public string ChannelType => ChannelTypes.WhatsApp;

    /// <summary>
    /// Facts WhatsApp declares about itself (SPEC-003 §6.1, SPEC-012 §4.4.1): the channel supports
    /// interactive reply buttons, so it can ask for and receive a confirmation inside itself, like
    /// Telegram/MAX.
    /// </summary>
    /// <remarks>
    /// Interactive messages are paid, but that is not expressed as a confirmation surface: a channel
    /// declaring the web page instead makes the page ask the user to re-confirm a transaction the
    /// channel has already completed, on a different device than the one they have just used. Cost is
    /// an argument about whether a tenant is ALLOWED to send a button, not about whether
    /// the channel CAN confirm — the permission layer, expressed as a per-tenant channel setting, not
    /// as a channel fact. Operators who want the one-step flow configure
    /// <see cref="LoginConfirmationMode.None"/> explicitly.
    /// <para>
    /// The prompt is always a reply to the message the user has just sent (it is triggered by that
    /// inbound message, <c>AuthStart</c>), so it goes out inside the 24-hour customer service window,
    /// where free-form interactive messages are allowed and no pre-approved template is required.
    /// </para>
    /// <para>
    /// The Cloud API cannot edit a message already delivered to a user, so a reported status — terminal
    /// or not — is shown as a new message; the inbound webhook (messages/contacts) carries no recipient
    /// locale (verified against the official Meta Cloud API webhooks documentation, 2026-07-18), so
    /// consumers fall back to the transaction locale.
    /// </para>
    /// <para>
    /// The user is identified by the business-scoped user ID (BSUID) every inbound message carries, not by
    /// the phone number. The number comes without any action of the user while the platform sends it with
    /// the message: the adapter puts it into <c>ChannelIdentitySnapshot.PhoneNumber</c>, proven by the
    /// platform itself — it is the account the message came from (SPEC-041 L45). A user who hides the
    /// number behind a username sends messages without it; such a message is accepted, and when the relying
    /// party asks for the number the phone step asks for it with a <c>request_contact_info</c> message
    /// addressed by the BSUID — a contact shared by its button is proven too (SPEC-041 R9, L45).
    /// </para>
    /// <para>
    /// A link to the claim completion form is a <c>cta_url</c> message — one button that opens the link
    /// (SPEC-041 C76). After a completed sign-in the chat can be sent a welcome message, a menu or a link to
    /// continue with (SPEC-012 §4.9): a link is a <c>cta_url</c> message as well; a menu draws its callback
    /// items as reply buttons (up to three) or as the rows of a list message (up to ten), and leaves its
    /// link items out — neither form has link buttons. Only the Meta Cloud API provider sends these forms;
    /// through Twilio the send answers a refusal, as for an undelivered link.
    /// </para>
    /// </remarks>
    private static readonly ChannelCapabilities DeclaredCapabilities = new()
    {
        SupportsInChannelConfirmation = true,
        SupportedMessageKinds = new[]
        {
            ChannelMessageKind.PlainText,
            ChannelMessageKind.PhoneNumberRequest,
            ChannelMessageKind.ClaimFormLink,
            ChannelMessageKind.ActionButtons
        }.ToFrozenSet(),
        SupportsMessageUpdate = false,
        DeliversOutcomeNotice = true,
        ProvidesRecipientLocale = false,
        RendersEmoji = true,
        PhoneNumber = new PhoneNumberCapability
        {
            Acquisition = PhoneNumberAcquisition.Automatic | PhoneNumberAcquisition.OnRequest,
            Proof = PhoneNumberProof.Proven
        },
        SupportedCompletionBehaviorActions = new[]
        {
            CompletionBehaviorAction.None,
            CompletionBehaviorAction.WelcomeMessage,
            CompletionBehaviorAction.Menu,
            CompletionBehaviorAction.Redirect
        }.ToFrozenSet(),
        DefaultCompletionBehaviorAction = CompletionBehaviorAction.None
    };

    /// <summary>
    /// What the one button of a <c>cta_url</c> message allows: a link with a caption within the limit of the
    /// form. The platform names no limit of the address.
    /// </summary>
    private static readonly ActionButtonLimits CtaUrlLimits = new()
    {
        MaxButtons = WhatsAppAdapterConstants.CtaUrlMaxButtons,
        MaxLabelLength = WhatsAppAdapterConstants.CtaUrlButtonLabelMaxLength
    };

    /// <summary>
    /// The first pass over the items of a menu, before its form is chosen: callback items only, at most as
    /// many as the larger form — a list — holds. The limits of the caption and the identifier depend on the
    /// form, so they are applied by the second pass.
    /// </summary>
    private static readonly ActionButtonLimits MenuCandidateLimits = new()
    {
        DrawsLinks = false,
        MaxButtons = WhatsAppAdapterConstants.ListMaxRows
    };

    /// <summary>
    /// The second pass over a menu drawn as reply buttons: the limits of the identifier and the caption of a
    /// reply button.
    /// </summary>
    private static readonly ActionButtonLimits ReplyButtonLimits = new()
    {
        DrawsLinks = false,
        MaxLabelLength = WhatsAppAdapterConstants.ReplyButtonTitleMaxLength,
        MaxCallbackDataLength = WhatsAppAdapterConstants.ReplyButtonIdMaxLength
    };

    /// <summary>
    /// The second pass over a menu drawn as a list: the limits of the identifier and the title of a row.
    /// </summary>
    private static readonly ActionButtonLimits ListRowLimits = new()
    {
        DrawsLinks = false,
        MaxLabelLength = WhatsAppAdapterConstants.ListRowTitleMaxLength,
        MaxCallbackDataLength = WhatsAppAdapterConstants.ListRowIdMaxLength
    };

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => DeclaredCapabilities;

    /// <inheritdoc />
    /// <remarks>
    /// Besides the markers of Veriqa buttons and deep links, a shared contact card is claimed: it answers
    /// the phone number request of claim completion and carries no marker of its own. Only the Meta Cloud
    /// API provider can ask for the contact, so the marker is of its wire format.
    /// </remarks>
    public bool OwnsInboundEvent(ChannelInboundRequest request)
    {
        var body = request.Body.Span;

        return CallbackDataPrefixes.ContainsAnyMarker(body)
            || body.IndexOf(MetaCloudApiConstants.ContactsMessageMarker) >= 0;
    }

    /// <inheritdoc />
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method maps the first relevant message of the inbound webhook to an auth event.
        // The wire format is the provider's: it reads the body into neutral messages, and the channel logic
        // below is the same for every provider. The first message that yields a result wins; none — the
        // event is unrelated and carries the provider's parsed payload.
        var inboundEvent = _provider.ReadInboundEvent(request);

        foreach (var message in inboundEvent.Messages)
        {
            var mappedResult = TryMapMessageToInboundResult(message, inboundEvent.Raw);
            if (mappedResult is not null)
            {
                return Task.FromResult(mappedResult);
            }
        }

        return Task.FromResult(Result<ChannelInboundResult>.Success(
            new ChannelUnrelatedResult { RawEvent = inboundEvent.Raw }));
    }

    /// <inheritdoc />
    public async Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        // The method sends an interactive message with confirm/decline buttons via the provider.
        // The recipient locale travels with the context and nowhere else (SPEC-017 §7.2).
        // The recipient is the channel user identifier as is — the BSUID the platform addresses the user by.
        var recipientLocale = promptContext.RecipientLocale;

        // Render the text from the confirmation context (SPEC-017 §7.2) in the recipient's language;
        // on a context without details — the default text (ICC-042), also localized by the passed
        // locale (ICC-050). The template is resolved by the core, which takes the address and the
        // owners itself — the in-channel surface, this channel and the levels the transaction states —
        // so a variant declared by a tenant, an application or a ui_config record answers instead of
        // the shipped one (SPEC-036 TPL-116); a template it cannot resolve sends nothing.
        var promptTemplate = await _messageTemplates.ResolveAsync(
            this, MessageKinds.ConfirmationPrompt, transactionId, cancellationToken);
        if (promptTemplate.IsFailure)
        {
            return Result<ChannelMessageRef?>.Failure(promptTemplate.Error);
        }

        var messageText = ConfirmationPromptRenderer.Render(
            promptContext,
            MessageTemplateNaturalKeys.PromptDefault,
            promptTemplate.Value.Message,
            _promptLocalizer,
            _logger);

        var transactionIdString = transactionId.ToString();
        var confirmReplyId = CallbackDataPrefixes.Confirm + transactionIdString;
        var declineReplyId = CallbackDataPrefixes.Decline + transactionIdString;

        // The button captions are Natural Keys localized into the recipient's language (ICC-050);
        // a missing translation falls back to the key's base text. The recipient locale comes from the
        // transaction fallback of the chain (WhatsApp itself does not supply a snapshot locale).
        var confirmButtonText = _promptLocalizer.ResolveOrBaseText(
            WhatsAppAdapterConstants.ConfirmButtonText, recipientLocale);
        var declineButtonText = _promptLocalizer.ResolveOrBaseText(
            WhatsAppAdapterConstants.DeclineButtonText, recipientLocale);

        var sent = await _provider.SendInteractiveButtonsMessageAsync(
            channelUserId,
            messageText,
            confirmReplyId,
            confirmButtonText,
            declineReplyId,
            declineButtonText,
            cancellationToken);

        if (!sent)
        {
            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "Error sending the WhatsApp message.");
        }

        // The Cloud API gives no addressable handle for editing a delivered message, so the prompt
        // is delivered but not referenceable — a success without a reference, never a failure.
        return Result<ChannelMessageRef?>.Success(null);
    }

    /// <inheritdoc />
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        if (message.PhoneNumberRequest is { } phoneRequest)
        {
            return SendContactRequestAsync(message.ChannelUserId, message.Text, phoneRequest, cancellationToken);
        }

        // A link to the claim completion form is the one link button of a cta_url message (SPEC-041 C76).
        // It goes the way the buttons of a post-sign-in action go, except that a link is never left out
        // for being a link.
        if (message.ClaimFormLink is { } formLink)
        {
            var formLinkButton = new ChannelActionButton { Label = formLink.ButtonLabel, Url = formLink.Url };

            return SendLinkButtonAsync(
                message.ChannelUserId,
                message.Text,
                new ActionButtonsContent { Buttons = [formLinkButton] },
                cancellationToken);
        }

        if (message.ActionButtons is { } actionButtons)
        {
            return IsSingleLink(actionButtons)
                ? SendLinkButtonAsync(message.ChannelUserId, message.Text, actionButtons, cancellationToken)
                : SendReplyOptionsAsync(message.ChannelUserId, message.Text, actionButtons, cancellationToken);
        }

        return SendTextAsync(message.ChannelUserId, message.Text, cancellationToken);
    }

    /// <summary>
    /// Tells whether the buttons of a message are one link and nothing else — the redirect after a sign-in,
    /// or a menu of one link item, which the contract of the message cannot tell apart. Such a message is
    /// drawn as a <c>cta_url</c> message; any other is a menu of callback items.
    /// </summary>
    /// <param name="content">Buttons of the message.</param>
    /// <returns>true — one button, carrying a link and no callback data.</returns>
    private static bool IsSingleLink(ActionButtonsContent content) =>
        content.Buttons is [{ Url.Length: > 0, CallbackData: null or "" }];

    /// <summary>
    /// Sends the text with one link button under it as a <c>cta_url</c> message (SPEC-041 C76, SPEC-012
    /// §4.9): the body and the caption of the button are fitted into the limits of the form by the shared
    /// selection. A button that cannot be drawn leaves nothing to send.
    /// </summary>
    /// <param name="channelUserId">Recipient within the channel — the BSUID, passed to the provider as is.</param>
    /// <param name="text">Text above the button.</param>
    /// <param name="content">The one link button.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success without a reference, or a failure — also when the provider does not send the form.</returns>
    private async Task<Result<ChannelMessageRef?>> SendLinkButtonAsync(
        string channelUserId,
        string text,
        ActionButtonsContent content,
        CancellationToken cancellationToken)
    {
        var drawable = ActionButtonSelection.SelectDrawable(content, CtaUrlLimits, ChannelType, _logger);
        if (drawable.Count == 0)
        {
            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "The link button of the message cannot be drawn in WhatsApp.");
        }

        var button = drawable[0];
        var bodyText = ActionButtonSelection.FitText(
            text,
            WhatsAppAdapterConstants.CtaUrlBodyMaxLength,
            ChannelType,
            _logger);

        // The address may carry a claim form token or a link handle (SPEC-041 N78, SPEC-003 §4.9.3): it is
        // handed to the provider and is not logged here.
        var sent = await _provider.SendLinkButtonMessageAsync(
            channelUserId,
            bodyText,
            button.Label,
            button.Url!,
            cancellationToken);

        return SentOrFailure(sent);
    }

    /// <summary>
    /// Sends the callback items of a post-sign-in menu (SPEC-012 §4.9) as reply buttons — up to three — or as
    /// the rows of a list message — up to ten. Link items are left out: neither form has link buttons. The
    /// form is chosen by the number of items left after the shared selection; then the limits of that
    /// form — the identifier and the caption — are applied, and an item left out by them, or as a repeat
    /// of an earlier reply button, does not change the form.
    /// </summary>
    /// <param name="channelUserId">Recipient within the channel — the BSUID, passed to the provider as is.</param>
    /// <param name="text">Text above the buttons.</param>
    /// <param name="content">Buttons of the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success without a reference, or a failure — also when no item can be drawn or the provider
    /// does not send the form.</returns>
    private async Task<Result<ChannelMessageRef?>> SendReplyOptionsAsync(
        string channelUserId,
        string text,
        ActionButtonsContent content,
        CancellationToken cancellationToken)
    {
        var candidates = ActionButtonSelection.SelectDrawable(content, MenuCandidateLimits, ChannelType, _logger);

        var asReplyButtons = candidates.Count <= WhatsAppAdapterConstants.ReplyButtonsMaxButtons;
        var drawable = ActionButtonSelection.SelectDrawable(
            new ActionButtonsContent { Buttons = candidates },
            asReplyButtons ? ReplyButtonLimits : ListRowLimits,
            ChannelType,
            _logger);

        if (drawable.Count == 0)
        {
            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "None of the action buttons of the message can be drawn in WhatsApp.");
        }

        var options = drawable
            .Select(button => new WhatsAppReplyOption(button.CallbackData!, button.Label))
            .ToArray();

        bool sent;
        if (asReplyButtons)
        {
            var bodyText = ActionButtonSelection.FitText(
                text,
                WhatsAppAdapterConstants.ReplyButtonsBodyMaxLength,
                ChannelType,
                _logger);

            sent = await _provider.SendReplyButtonsMessageAsync(
                channelUserId,
                bodyText,
                DropRepeatedReplyButtons(options),
                cancellationToken);
        }
        else
        {
            var listButtonText = ActionButtonSelection.FitLabel(
                content.ChooserLabel ?? WhatsAppAdapterConstants.ChooserLabelBaseText,
                WhatsAppAdapterConstants.ListButtonTextMaxLength,
                ChannelType,
                _logger);

            var bodyText = ActionButtonSelection.FitText(
                text,
                WhatsAppAdapterConstants.ListBodyMaxLength,
                ChannelType,
                _logger);

            sent = await _provider.SendListMessageAsync(
                channelUserId,
                bodyText,
                listButtonText,
                options,
                cancellationToken);
        }

        return SentOrFailure(sent);
    }

    /// <summary>
    /// Leaves out a reply button whose identifier or caption repeats an earlier button of the message:
    /// the platform requires both to be unique among the buttons of a reply buttons message (Meta, WhatsApp
    /// Cloud API, "Interactive reply buttons messages", checked 2026-10-24) and refuses the whole message
    /// otherwise. A repeat may come from the operator's menu or from fitting two captions with a common
    /// beginning into the limit. The first button of a repeat is kept; the warning counts the dropped ones
    /// and names nothing they carry.
    /// </summary>
    /// <param name="options">Reply buttons, identifiers and captions already fitted.</param>
    /// <returns>The buttons without repeats, in their order.</returns>
    private WhatsAppReplyOption[] DropRepeatedReplyButtons(WhatsAppReplyOption[] options)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<WhatsAppReplyOption>(options.Length);

        foreach (var option in options)
        {
            if (ids.Contains(option.Id) || titles.Contains(option.Title))
            {
                continue;
            }

            ids.Add(option.Id);
            titles.Add(option.Title);
            kept.Add(option);
        }

        var droppedCount = options.Length - kept.Count;
        if (droppedCount > 0)
        {
            _logger.LogWarning(
                "{DroppedCount} reply button(s) are not drawn in {ChannelType}: the identifier or the caption repeats an earlier button, and the platform requires both to be unique",
                droppedCount,
                ChannelType);
        }

        return kept.ToArray();
    }

    /// <summary>
    /// Turns the answer of the provider for an interactive message into the result of the send.
    /// </summary>
    /// <param name="sent">Whether the provider sent the message.</param>
    /// <returns>A success without a reference (WhatsApp has no addressable edit), or a failure.</returns>
    private static Result<ChannelMessageRef?> SentOrFailure(bool sent) =>
        sent
            ? Result<ChannelMessageRef?>.Success(null)
            : Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "The WhatsApp interactive message was not sent.");

    /// <summary>
    /// Asks the user to share their number through the provider (SPEC-041 R9): the request contact info
    /// message and the reply button of the secondary action, whose id names the transaction (L73).
    /// </summary>
    /// <param name="channelUserId">Recipient within the channel — the BSUID, passed to the provider as is.</param>
    /// <param name="text">Text of the request.</param>
    /// <param name="phoneRequest">Content of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success without a reference, or a failure — also when the provider does not support the
    /// request (E64).</returns>
    private async Task<Result<ChannelMessageRef?>> SendContactRequestAsync(
        string channelUserId,
        string text,
        PhoneNumberRequestContent phoneRequest,
        CancellationToken cancellationToken)
    {
        var secondaryButtonId = PhoneStepButtonPayload.Of(phoneRequest.SecondaryAction, phoneRequest.TransactionId);

        var sent = await _provider.SendContactRequestAsync(
            channelUserId,
            text,
            secondaryButtonId,
            phoneRequest.SecondaryButtonLabel,
            cancellationToken);

        if (!sent)
        {
            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "The WhatsApp contact request was not sent.");
        }

        return Result<ChannelMessageRef?>.Success(null);
    }

    /// <summary>
    /// Sends a plain text message to the user via the provider.
    /// </summary>
    /// <param name="channelUserId">Recipient within the channel — the BSUID, passed to the provider as is.</param>
    /// <param name="text">Message text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success without a reference (WhatsApp has no addressable edit), or a failure.</returns>
    private async Task<Result<ChannelMessageRef?>> SendTextAsync(
        string channelUserId,
        string text,
        CancellationToken cancellationToken)
    {
        var sent = await _provider.SendTextMessageAsync(channelUserId, text, cancellationToken);
        if (!sent)
        {
            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "Error sending the WhatsApp message.");
        }

        return Result<ChannelMessageRef?>.Success(null);
    }

    /// <inheritdoc />
    public Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // Authenticity is proven by the provider's own model (a GET handshake, a signature) — the adapter
        // has nothing of its own to add to it.
        return _provider.ValidateWebhookAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        // The method delegates the health check to the provider.
        var startedAt = _timeProvider.GetUtcNow();

        var (isHealthy, details) = await _provider.CheckHealthAsync(cancellationToken);
        var elapsed = _timeProvider.GetUtcNow() - startedAt;

        return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
            IsHealthy: isHealthy,
            ChannelType: ChannelTypes.WhatsApp,
            ResponseTime: elapsed,
            Details: details,
            CheckedAt: _timeProvider.GetUtcNow()));
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The method builds a WhatsApp click-to-chat deep link to start the auth flow.
        // The deep link is the same for all providers — it is addressed to WhatsApp directly.
        // One credential resolve per operation (CA-171): the business number is a tenant-credential field.
        var credentialsResult = await WhatsAppCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError("WhatsApp credentials are unavailable for the deep link.");
            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "WhatsApp credentials are unavailable for the deep link.");
        }

        var phoneDigits = TryNormalizeBusinessPhoneToDeepLinkDigits(credentialsResult.Value.BusinessPhoneNumber);
        if (phoneDigits is null)
        {
            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "BusinessPhoneNumber is misconfigured for the WhatsApp deep link.");
        }

        // Render the formatted prefill text (SPEC-003 §10.4): a title with the application name (best-effort),
        // a one-line instruction, and the sign-in code on its own last line. The template, the application
        // name and the page language come from the core accessor of channel message templates; a transaction
        // that names no application or language degrades to the minimal variant in the base language, and a
        // template the core cannot resolve leaves the sign-in code alone. The template is the one of the
        // purpose of the link where the deployment states one, and the template of the channel otherwise.
        var prefillText = await RenderPrefillTextAsync(request, cancellationToken);

        var encodedText = Uri.EscapeDataString(prefillText);
        var deepLink = string.Concat(
            WhatsAppAdapterConstants.DeepLinkBaseUrl,
            phoneDigits,
            "?text=",
            encodedText);

        return Result<string>.Success(deepLink);
    }

    /// <summary>
    /// Renders the formatted deep-link prefill text (SPEC-003 §10.4).
    /// </summary>
    /// <param name="request">The request for the link: the transaction and the purpose of the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The multi-line prefill text (never null; at minimum the sign-in code alone).</returns>
    private async Task<string> RenderPrefillTextAsync(DeepLinkRequest request, CancellationToken cancellationToken)
    {
        // The code is the guaranteed slot — it is the sign-in payload and is always present. The parser on
        // the inbound side extracts it from anywhere in the message text (see TryExtractAuthTransactionId).
        var code = string.Concat(WhatsAppAdapterConstants.DeepLinkPrefix, request.TransactionId.ToString());

        // The wording is resolved over the ownership of the very transaction this deep link is built for
        // (SPEC-036 TPL-116); a transaction the store does not have leaves the core level as the only one
        // there is, which is also where the locale of the prefill degrades to.
        var prefillTemplate = await _messageTemplates.ResolveAsync(
            this, MessageKinds.DeeplinkPrefill, request, cancellationToken);
        if (prefillTemplate.IsFailure)
        {
            // The core has already logged the failure; the code still travels — it is the sign-in
            // payload, and the inbound parser finds it anywhere in the text.
            return code;
        }

        var resolved = prefillTemplate.Value;

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SlotNames.Code] = code,
        };

        // {app} present → the full variant with the title line; absent → the minimal variant (TPL-031).
        var applicationName = resolved.ApplicationName;
        if (!string.IsNullOrWhiteSpace(applicationName))
        {
            values[SlotNames.App] = applicationName;
        }

        TransactionSlotValues.AddBrandName(values, resolved.BrandName);

        var prefill = MessageTextRenderer.Render(
            resolved.Message,
            values,
            resolved.Locale,
            resolved.TimeZone,
            _promptLocalizer,
            MessageRenderMode.PlainText,
            _logger);

        if (prefill is null)
        {
            // A prefilled message this channel can send is plain text and nothing else, so a ladder none
            // of whose steps states the plain-text edition leaves it without wording. The code still
            // travels — it is the sign-in payload and the inbound parser finds it anywhere in the text —
            // but the deployment is told, because the wording it wrote reaches nobody.
            _logger.LogError(
                "No step of the {MessageKind} ladder of channel {ChannelType} states the plain-text "
                + "edition; the prefilled message carries the sign-in code alone.",
                MessageKinds.DeeplinkPrefill,
                ChannelTypes.WhatsApp);
        }

        return prefill ?? code;
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        // WhatsApp has neither callback answers nor an editable delivered message: the only way to
        // show the reported state — terminal or the claims completion in progress — is a new message,
        // and the buttons of the question stay where they are. Nor can a delivered phone number
        // request be recalled. The status text is already localized by the core, so an unknown future
        // outcome value changes nothing here. A ReplacePrompt intent therefore goes unhonoured by this
        // channel as it ships today — there is nothing delivered to replace — and that is not a
        // refusal: the receipt still reaches the user and the result is unchanged.
        var sendResult = await SendTextAsync(notice.ChannelUserId, notice.StatusText, cancellationToken);

        return sendResult.IsSuccess
            ? Result<bool>.Success(true)
            : Result<bool>.Failure(sendResult.Error);
    }

    /// <summary>
    /// Maps an inbound WhatsApp message to a <see cref="ChannelInboundResult"/>.
    /// </summary>
    /// <param name="message">Inbound message in the provider-neutral form.</param>
    /// <param name="rawEvent">Provider's parsed payload, carried to the result.</param>
    /// <returns>The message processing result, or null to skip.</returns>
    private Result<ChannelInboundResult>? TryMapMessageToInboundResult(
        WhatsAppInboundMessage message,
        object? rawEvent)
    {
        // The method determines the intent by the button/text payload: a pressed button carries its payload,
        // a text may carry the sign-in code; anything else is skipped. The sender is its business-scoped user
        // ID: without a valid one nobody can be named, so the message fails the identity.
        var senderUserId = message.SenderUserId?.Trim();
        if (senderUserId is null || !BusinessScopedUserIdRegex().IsMatch(senderUserId))
        {
            return Result<ChannelInboundResult>.Failure(
                ChannelAdapterErrorCodes.IdentityExtractionFailed,
                "Invalid sender business-scoped user ID in the WhatsApp message.");
        }

        var snapshot = BuildIdentitySnapshot(senderUserId, message, _timeProvider.GetUtcNow());

        if (message.SharedContact is { } sharedContact)
        {
            return MapSharedContact(sharedContact, snapshot, rawEvent);
        }

        if (message.ButtonPayload is null)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // The sign-in code may now sit anywhere in a formatted prefill message (SPEC-003 §10.4), not
            // only at the start — so we scan for the auth_ token instead of matching the whole text.
            // The bare-payload format (text == "auth_{id}") stays a special case: backward compatible.
            var transactionId = TryExtractAuthTransactionId(text);
            if (transactionId is null)
            {
                return null;
            }

            return Result<ChannelInboundResult>.Success(new ChannelAuthStartResult
            {
                TransactionId = transactionId.Value,
                Identity = snapshot,
                RawEvent = rawEvent
            });
        }

        var callbackPayload = message.ButtonPayload;
        if (string.IsNullOrWhiteSpace(callbackPayload))
        {
            return null;
        }

        if (callbackPayload.StartsWith(CallbackDataPrefixes.Confirm, StringComparison.Ordinal))
        {
            var rawTransactionId = callbackPayload[CallbackDataPrefixes.Confirm.Length..];
            if (!TryParseEventTransactionId(rawTransactionId, out var transactionId))
            {
                return null;
            }

            return Result<ChannelInboundResult>.Success(new ChannelAuthConfirmResult
            {
                TransactionId = transactionId,
                Identity = snapshot,
                RawEvent = rawEvent
            });
        }

        if (callbackPayload.StartsWith(CallbackDataPrefixes.Decline, StringComparison.Ordinal))
        {
            var rawTransactionId = callbackPayload[CallbackDataPrefixes.Decline.Length..];
            if (!TryParseEventTransactionId(rawTransactionId, out var transactionId))
            {
                return null;
            }

            return Result<ChannelInboundResult>.Success(new ChannelAuthDeclineResult
            {
                TransactionId = transactionId,
                Identity = snapshot,
                RawEvent = rawEvent
            });
        }

        // A pressed callback item of the post-sign-in menu (SPEC-012 §4.9) — a reply button or a row of a
        // list. The platform waits for no answer to the press. A payload of the marker alone names no item
        // and falls through to the unrelated end, like any other payload that is none of ours.
        if (MenuItemPayload.TryRead(callbackPayload, out var menuItemValue))
        {
            return Result<ChannelInboundResult>.Success(new ChannelMenuItemPressedResult
            {
                Identity = snapshot,
                Value = menuItemValue,
                PressId = string.IsNullOrEmpty(message.MessageId) ? null : message.MessageId,
                RawEvent = rawEvent
            });
        }

        // The reply button of the secondary action of the phone step (SPEC-041 L73)
        if (PhoneStepButtonPayload.TryRead(callbackPayload, out var phoneStepAction, out var rawPhoneStepTransactionId))
        {
            if (!TryParseEventTransactionId(rawPhoneStepTransactionId, out var transactionId))
            {
                return null;
            }

            return Result<ChannelInboundResult>.Success(new ChannelPhoneStepActionResult
            {
                TransactionId = transactionId,
                Identity = snapshot,
                Action = phoneStepAction,
                RawEvent = rawEvent
            });
        }

        return null;
    }

    /// <summary>
    /// Maps a shared contact card to the shared phone number (SPEC-041 L45, L46). A card shared by the
    /// request contact info button (<c>origin = contact_request</c>) is the number of the sender's own
    /// account; any other card — one sent by hand — is reported as rejected, so the phone step can ask
    /// again.
    /// </summary>
    /// <param name="contact">The shared contact card.</param>
    /// <param name="snapshot">Identity snapshot of the sender.</param>
    /// <param name="rawEvent">Provider's parsed payload, carried to the result.</param>
    /// <returns>The shared phone number result.</returns>
    private Result<ChannelInboundResult> MapSharedContact(
        WhatsAppSharedContact contact,
        ChannelIdentitySnapshot snapshot,
        object? rawEvent)
    {
        var ownershipRejected = string.IsNullOrWhiteSpace(contact.PhoneNumber) || !contact.SharedByRequest;

        // The number is handed on in E.164 by the one rule of the core (SPEC-041 L47, SPEC-003 CA-009):
        // a card carries the number as its owner wrote it. A number with no E.164 form counts as absent;
        // it does not touch the ownership, which the origin of the card decides.
        var phoneNumber = E164PhoneNumber.TryNormalize(
            contact.PhoneNumber,
            out var normalizedPhone)
                ? normalizedPhone
                : string.Empty;

        if (ownershipRejected)
        {
            _logger.LogWarning(
                "WhatsApp contact is not proven to belong to the sender {ChannelUserIdHash}: Origin = {Origin}",
                LogMasking.Fingerprint(snapshot.ChannelUserId),
                contact.Origin);
        }

        return Result<ChannelInboundResult>.Success(new ChannelPhoneSharedResult
        {
            PhoneNumber = phoneNumber,
            Identity = snapshot,
            OwnershipRejected = ownershipRejected,
            RawEvent = rawEvent
        });
    }

    /// <summary>
    /// Parses the transaction identifier carried by a channel event. An identifier that does not
    /// parse means the event is not ours: the caller answers with an irrelevant-event result instead
    /// of handing a broken value to the core (SPEC-003 §6.3).
    /// </summary>
    /// <param name="rawTransactionId">Raw identifier as it arrived in the event.</param>
    /// <param name="transactionId">Parsed identifier on success.</param>
    /// <returns>true — the identifier parsed and the intent can be constructed.</returns>
    private bool TryParseEventTransactionId(
        string? rawTransactionId,
        out TransactionId transactionId)
    {
        if (TransactionId.TryParse(rawTransactionId, out transactionId))
        {
            return true;
        }

        _logger.LogWarning(
            "Unparseable transaction identifier in a {ChannelType} event: {RawId}. "
            + "The event is treated as irrelevant",
            ChannelTypes.WhatsApp,
            rawTransactionId);

        return false;
    }

    /// <summary>
    /// Extracts the sign-in transaction id from an inbound text message: finds the <c>auth_</c> token
    /// followed by a valid fixed-length Base62 transaction id anywhere in the text (SPEC-003 §10.4).
    /// </summary>
    /// <remarks>
    /// The formatted prefill (SPEC-003 §10.4) puts the code on its own line, no longer at the very start,
    /// so a <c>StartsWith</c> match would break it. The id length and alphabet are fixed
    /// (<see cref="TransactionId.TryParse(string?, out TransactionId)"/>), which rules out false positives from surrounding text. The
    /// legacy bare-payload text (<c>auth_{id}</c> only) is matched by the same scan — backward compatible.
    /// </remarks>
    /// <param name="text">Inbound message text (already trimmed).</param>
    /// <returns>The transaction id, or null when no valid token is present.</returns>
    private static TransactionId? TryExtractAuthTransactionId(string text)
    {
        var prefix = WhatsAppAdapterConstants.DeepLinkPrefix;
        var searchStart = 0;

        while (true)
        {
            var prefixIndex = text.IndexOf(prefix, searchStart, StringComparison.Ordinal);
            if (prefixIndex < 0)
            {
                return null;
            }

            var idStart = prefixIndex + prefix.Length;
            if (idStart + TransactionId.StringLength <= text.Length)
            {
                var candidate = text.Substring(idStart, TransactionId.StringLength);
                if (TransactionId.TryParse(candidate, out var parsed))
                {
                    return parsed;
                }
            }

            // Overlapping occurrences are possible (e.g. "auth_auth_…"); advance by one to not miss them.
            searchStart = prefixIndex + 1;
        }
    }

    /// <summary>
    /// Builds an identity snapshot for a WhatsApp user.
    /// </summary>
    /// <param name="channelUserId">Business-scoped user ID (BSUID) of the sender, already validated.</param>
    /// <param name="message">Source inbound message.</param>
    /// <param name="capturedAt">Capture moment (UTC), supplied by the caller's time provider.</param>
    /// <returns>Channel identity snapshot.</returns>
    private static ChannelIdentitySnapshot BuildIdentitySnapshot(
        string channelUserId,
        WhatsAppInboundMessage message,
        DateTimeOffset capturedAt)
    {
        // The method builds a snapshot identified by the BSUID. The number is an optional attribute: present
        // only when the platform sent it, and then proven; the username is a hint for the relying party
        // (preferred_username), never the identity. The display name is the first of profile name, username,
        // number and BSUID that is there.
        var rawMetadata = BuildRawMetadata(message);
        var phoneNumber = TryNormalizeSenderPhone(message.SenderPhone);
        var username = TrimOrNull(message.SenderUsername);
        var profileName = TrimOrNull(message.SenderDisplayName);

        return new ChannelIdentitySnapshot
        {
            // Tenant of the inbound request, taken from the ambient scope the host opened for it —
            // the {tenant} segment of the webhook route or the polling iteration of that tenant.
            // The identity of a channel user belongs to the tenant whose channel received the event,
            // so the scope is the authoritative source here and the transaction is not consulted.
            TenantId = ChannelTenantContext.CurrentTenantId,
            ChannelType = ChannelTypes.WhatsApp,
            IsBot = false,
            ChannelUserId = channelUserId,
            DisplayName = profileName ?? username ?? phoneNumber ?? channelUserId,
            FirstName = profileName,
            Username = username,
            PhoneNumber = phoneNumber,
            // The number is the account the message came from — proven by the platform (SPEC-041 L45). Without
            // a number there is nothing to state, and the phone step asks for it when it is needed (R9).
            PhoneNumberVerified = phoneNumber is null ? null : true,
            CapturedAt = capturedAt,
            AdapterVersion = WhatsAppAdapterConstants.AdapterVersion,
            RawMetadata = rawMetadata
        };
    }

    /// <summary>
    /// Builds raw metadata with a size limit.
    /// </summary>
    /// <param name="message">Source inbound message.</param>
    /// <returns>JSON metadata, or null if the limit is exceeded.</returns>
    private static JsonElement? BuildRawMetadata(WhatsAppInboundMessage message)
    {
        // The method stores the message's minimal technical fields without PII secrets.
        var metadata = new
        {
            whatsapp_message_id = message.MessageId,
            message_type = message.MessageType,
            timestamp = message.Timestamp
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
        if (bytes.Length > WhatsAppAdapterConstants.MaxRawMetadataSize)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(metadata);
    }

    /// <summary>
    /// Normalizes a phone number to E.164 format with a leading <c>+</c>. The one rule for a number of the
    /// channel — the sender's and the business number, which a provider sends from, alike.
    /// </summary>
    /// <param name="rawPhone">Source number.</param>
    /// <returns>The normalized number, or null if the format is invalid.</returns>
    internal static string? TryNormalizePhoneToE164(string? rawPhone)
    {
        // The method strips spaces/separators from the number and converts it to the +{digits} format.
        if (string.IsNullOrWhiteSpace(rawPhone))
        {
            return null;
        }

        var trimmed = rawPhone.Trim();

        // SPEC-003: channel_user_id cannot contain the ':' character.
        // We check the source string before filtering characters.
        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var builder = new StringBuilder(trimmed.Length);

        foreach (var ch in trimmed)
        {
            if (char.IsDigit(ch))
            {
                builder.Append(ch);
            }
        }

        if (builder.Length is < 8 or > 15)
        {
            return null;
        }

        return "+" + builder;
    }

    /// <summary>
    /// Normalizes the sender's number the platform sent to E.164. A value with a Latin letter is not a number
    /// but a business-scoped user ID (plain or parent) a provider put into the number field — Twilio does so
    /// for a user without a number; it is dropped before the normalization, which keeps only the digits and
    /// would turn <c>US.12345678</c> into <c>+12345678</c>.
    /// </summary>
    /// <param name="senderPhone">Sender number as received; null when the platform did not send it.</param>
    /// <returns>The number in E.164, or null when there is no valid number.</returns>
    private static string? TryNormalizeSenderPhone(string? senderPhone)
    {
        if (senderPhone is null || senderPhone.Any(char.IsAsciiLetter))
        {
            return null;
        }

        return TryNormalizePhoneToE164(senderPhone);
    }

    /// <summary>
    /// Trims a value of the sender's profile.
    /// </summary>
    /// <param name="value">Value as received.</param>
    /// <returns>The trimmed value, or null when it is absent or blank.</returns>
    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Normalizes a business number for a deep link of the <c>wa.me/{digits}</c> format.
    /// </summary>
    /// <param name="rawPhone">Source number.</param>
    /// <returns>The digits-only number, or null if the format is invalid.</returns>
    private static string? TryNormalizeBusinessPhoneToDeepLinkDigits(string? rawPhone)
    {
        // The method converts the number to a digits-only form for use in wa.me.
        var e164 = TryNormalizePhoneToE164(rawPhone);
        if (e164 is null)
        {
            return null;
        }

        var digits = e164.TrimStart('+');
        return digits.Length is < 8 or > 15 ? null : digits;
    }

    /// <summary>
    /// Compiled matcher of the business-scoped user ID format.
    /// </summary>
    [GeneratedRegex(WhatsAppAdapterConstants.BusinessScopedUserIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex BusinessScopedUserIdRegex();
}
