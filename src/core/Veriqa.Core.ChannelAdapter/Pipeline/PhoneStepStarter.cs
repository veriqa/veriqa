// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Starts the phone step of claim completion (SPEC-041 L44): asks the channel of the transaction for
/// the number in the chat of the user of its channel snapshot, and ends the wait of an <c>Optional</c>
/// number when its window runs out (C74, L73).
/// </summary>
/// <remarks>
/// A singleton over singletons, the channel adapters included: the engine resolves it from a scope of its
/// own, and what the end of the window needs of the scoped world — the processing of a skip with the
/// transaction service behind it — is taken from a scope opened when the window runs out, since the scope
/// of the start is long gone by then. The transaction service
/// is never taken in the constructor: the service owns the call that starts the step.
/// </remarks>
internal sealed class PhoneStepStarter : IClaimCompletionStepStarter
{
    /// <summary>
    /// Registered channel adapters.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Record of the last phone number request sent into each chat.
    /// </summary>
    private readonly IPhoneRequestCorrelationStore _correlationStore;

    /// <summary>
    /// Builder of the phone number request.
    /// </summary>
    private readonly PhoneNumberRequestBuilder _requestBuilder;

    /// <summary>
    /// The seam the request is delivered into the chat through.
    /// </summary>
    private readonly IClaimCompletionChannelDelivery _delivery;

    /// <summary>
    /// Resolver of the window of an <c>Optional</c> number.
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// Clock the window and the deadline of the transaction are read against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Factory of the scope the end of the window runs in.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<PhoneStepStarter> _logger;

    /// <summary>
    /// Creates the starter of the phone step.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="correlationStore">Record of the last phone number request sent into each chat.</param>
    /// <param name="requestBuilder">Builder of the phone number request.</param>
    /// <param name="delivery">The seam the request is delivered into the chat through.</param>
    /// <param name="configurationResolver">Resolver of the window of an <c>Optional</c> number.</param>
    /// <param name="timeProvider">Clock the window and the deadline are read against.</param>
    /// <param name="scopeFactory">Factory of the scope the end of the window runs in.</param>
    /// <param name="logger">Logger.</param>
    public PhoneStepStarter(
        IEnumerable<IChannelAdapter> adapters,
        IPhoneRequestCorrelationStore correlationStore,
        PhoneNumberRequestBuilder requestBuilder,
        IClaimCompletionChannelDelivery delivery,
        IConfigurationResolver configurationResolver,
        TimeProvider timeProvider,
        IServiceScopeFactory scopeFactory,
        ILogger<PhoneStepStarter> logger)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _correlationStore = correlationStore ?? throw new ArgumentNullException(nameof(correlationStore));
        _requestBuilder = requestBuilder ?? throw new ArgumentNullException(nameof(requestBuilder));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _configurationResolver = configurationResolver ?? throw new ArgumentNullException(nameof(configurationResolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ClaimCompletionStep Step => ClaimCompletionStep.Phone;

    /// <inheritdoc />
    /// <remarks>
    /// The record tying the chat to the transaction is written before the request is sent, so an answer
    /// quicker than the return of this call still finds it; the delivery with its retries and the wait
    /// of an <c>Optional</c> number run in the background. The whole start runs under the tenant of the
    /// channel snapshot: after "Yes" on the web confirmation page nothing else states it, and the request
    /// has to go out through the bot of that tenant.
    /// </remarks>
    public async Task StartAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction.ChannelIdentitySnapshot is not { } snapshot)
        {
            _logger.LogWarning(
                "The phone step began on a transaction without a channel snapshot — nothing is requested. TransactionId: {TransactionId}",
                transaction.Id.ToString());
            return;
        }

        using var tenantScope = ChannelTenantContext.BeginScope(snapshot.TenantId);

        var request = await _requestBuilder.BuildAsync(transaction, cancellationToken);
        if (request is null)
        {
            _logger.LogWarning(
                "The phone step began without a phone number to request — nothing is requested. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transaction.Id.ToString(),
                snapshot.ChannelType);
            return;
        }

        await _correlationStore.SaveAsync(request.Correlation, cancellationToken);

        // SPEC-041 L73: only an Optional number is ever waited out; a Required one waits for the user or
        // for the deadline of the transaction (E59). An undelivered request waits the same way (L44).
        var skipsAfterWindow = request.Correlation.SecondaryAction is PhoneStepAction.Skip;

        _delivery.DeliverInBackground(
            transaction,
            request.Message,
            skipsAfterWindow ? (_, stopping) => SkipAfterWindowAsync(transaction, snapshot, stopping) : null);

        _logger.LogInformation(
            "Phone number requested in the channel. TransactionId: {TransactionId}, ChannelType: {ChannelType}, SecondaryAction: {SecondaryAction}",
            transaction.Id.ToString(),
            snapshot.ChannelType,
            request.Correlation.SecondaryAction);
    }

    /// <summary>
    /// Waits out the window of an <c>Optional</c> number and skips it, when the transaction still waits
    /// for it — through the processing a press of "Skip" gets, so the chat is answered the same way.
    /// </summary>
    /// <param name="transaction">Transaction whose number was requested.</param>
    /// <param name="snapshot">Its channel snapshot.</param>
    /// <param name="stopping">Token of the stopping host.</param>
    /// <returns>The completed wait.</returns>
    private async Task SkipAfterWindowAsync(
        Transaction transaction,
        ChannelIdentitySnapshot snapshot,
        CancellationToken stopping)
    {
        // The background operation carries no ambient state of the request that started it, so the
        // tenant of the snapshot is stated again: the window is resolved for it, and the answer in the
        // chat goes out through its bot.
        using var tenantScope = ChannelTenantContext.BeginScope(snapshot.TenantId);

        var window = await WindowOfAsync(transaction, stopping);
        if (window > TimeSpan.Zero)
        {
            await Task.Delay(window, _timeProvider, stopping);
        }

        var adapter = ChannelWebhookPipeline.FindAdapter(_adapters, snapshot.ChannelType);
        if (adapter is null)
        {
            _logger.LogWarning(
                "No adapter serves the channel of the phone step — its window ends without a skip. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transaction.Id.ToString(),
                snapshot.ChannelType);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<ChannelInboundProcessor>()
            .SkipPhoneAfterWindowAsync(transaction, adapter, stopping);
    }

    /// <summary>
    /// The window of an <c>Optional</c> number (SPEC-041 C74): the value the levels state for the
    /// transaction, bounded so that the skip and the finalization after it are done before the deadline
    /// of the transaction.
    /// </summary>
    /// <param name="transaction">Transaction whose number was requested.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The window, never negative.</returns>
    private async Task<TimeSpan> WindowOfAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        var configured = (await _configurationResolver.ResolveAsync(
            ClaimCompletionConfigKeys.PhoneOptionalWindow,
            TransactionResolutionContext.For(transaction),
            ConfigDimensionValues.None,
            cancellationToken)).Value;

        var remaining = transaction.ExpiresAt
            - _timeProvider.GetUtcNow()
            - ClaimCompletionPhoneStepConstants.FinalizationReserve;

        var window = configured < remaining ? configured : remaining;

        return window > TimeSpan.Zero ? window : TimeSpan.Zero;
    }
}
