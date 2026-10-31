// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;
using Veriqa.Core.AuthServer.ClaimCompletion;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Opens the page of the claims completion on the device that confirmed the sign-in for the confirmation page
/// of the mail (SPEC-041 L52, N78): issues the form token, tells the sign-in page the form is open there
/// (L77) and answers with the page the link of the channel opens (<see cref="ClaimCompletionFormPageEndpoint"/>).
/// </summary>
/// <remarks>
/// The token is issued only while the web step is on this surface and no token was issued before: the
/// fingerprint is written by the conditional write of the store, so another holder of the same letter gets no
/// token. Scoped: the responder it answers with reads the channel adapters, whose lifetime a host may narrow.
/// </remarks>
internal sealed class ClaimCompletionCrossDevicePage : IClaimCompletionCrossDevicePage
{
    /// <summary>
    /// Transaction service — the transaction is read through it.
    /// </summary>
    private readonly ITransactionService _transactionService;

    /// <summary>
    /// Store the fingerprint of the form token is written into.
    /// </summary>
    private readonly ITransactionStore _store;

    /// <summary>
    /// The one home of the answers of the page of the completion.
    /// </summary>
    private readonly ClaimCompletionPageResponder _pages;

    /// <summary>
    /// Publisher of the channel status.
    /// </summary>
    private readonly ITransactionEventPublisher _eventPublisher;

    /// <summary>
    /// Clock the moment of the channel status is taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Veriqa options — the configured default language.
    /// </summary>
    private readonly IOptions<VeriqaOptions> _veriqaOptions;

    /// <summary>
    /// Registry of the supported page languages.
    /// </summary>
    private readonly IAuthPageLanguageRegistry _languageRegistry;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ClaimCompletionCrossDevicePage> _logger;

    /// <summary>
    /// Creates the opener of the page.
    /// </summary>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="store">Store the fingerprint is written into.</param>
    /// <param name="pages">Responder of the page of the completion.</param>
    /// <param name="eventPublisher">Publisher of transaction events.</param>
    /// <param name="timeProvider">Clock of the channel status.</param>
    /// <param name="veriqaOptions">Veriqa options.</param>
    /// <param name="languageRegistry">Registry of the supported page languages.</param>
    /// <param name="logger">Logger.</param>
    public ClaimCompletionCrossDevicePage(
        ITransactionService transactionService,
        ITransactionStore store,
        ClaimCompletionPageResponder pages,
        ITransactionEventPublisher eventPublisher,
        TimeProvider timeProvider,
        IOptions<VeriqaOptions> veriqaOptions,
        IAuthPageLanguageRegistry languageRegistry,
        ILogger<ClaimCompletionCrossDevicePage> logger)
    {
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _veriqaOptions = veriqaOptions ?? throw new ArgumentNullException(nameof(veriqaOptions));
        _languageRegistry = languageRegistry ?? throw new ArgumentNullException(nameof(languageRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IResult?> TryOpenAsync(HttpContext httpContext, TransactionId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (await TryIssueTokenAsync(id, cancellationToken) is not { } issued)
        {
            return null;
        }

        var requestLanguage = AuthorizeCallbackEndpoint.DetectCallbackLanguage(
            httpContext, _veriqaOptions, _languageRegistry);

        return await _pages.RespondCrossDeviceAsync(httpContext, issued.Transaction, issued.Token, requestLanguage);
    }

    /// <inheritdoc />
    public async Task<string?> TryIssueFormPathAsync(HttpContext httpContext, TransactionId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return await TryIssueTokenAsync(id, cancellationToken) is { } issued
            ? $"{httpContext.Request.PathBase}{ClaimCompletionFormPageEndpoint.BuildPath(SessionIdMapper.ToSessionId(id), issued.Token)}"
            : null;
    }

    /// <summary>
    /// Issues the form token of the transaction where the right of the token would hold (SPEC-041 N78): the web
    /// step shown on this surface, before any token. The write is conditional, so of two requests for the same
    /// transaction only one gets a token; the other is told none was issued. A token issued tells the sign-in page
    /// the form is open on the phone (L77).
    /// </summary>
    /// <param name="id">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction as it was read and the issued token; null — no token was issued.</returns>
    private async Task<(Transaction Transaction, string Token)?> TryIssueTokenAsync(TransactionId id, CancellationToken cancellationToken)
    {
        var read = await _transactionService.GetTransactionAsync(id, cancellationToken);
        if (read.IsFailure
            || read.Value is not { ChannelIdentitySnapshot: { } snapshot } transaction
            || transaction.GetCurrentClaimCompletionStep() is not ClaimCompletionStep.WebStep
            || transaction.ClaimCompletion is not { Surface: WebStepSurface.CrossDevicePage, FormTokenHash: null })
        {
            return null;
        }

        var (token, hash) = ClaimFormToken.Issue();

        if (!await _store.TrySetClaimFormTokenHashAsync(id, hash, cancellationToken))
        {
            _logger.LogDebug(
                "No form token issued for the page of the mail: one was issued already, or the transaction left the claims completion. TransactionId: {TransactionId}",
                id.ToString());
            return null;
        }

        await ClaimFormChannelStatus.PublishFormOnPhoneAsync(
            _eventPublisher, _timeProvider, transaction, snapshot.ChannelType, _logger);

        return (transaction, token);
    }
}
