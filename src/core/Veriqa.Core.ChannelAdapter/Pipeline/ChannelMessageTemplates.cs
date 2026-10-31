// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The core implementation of <see cref="IChannelMessageTemplates"/>: one instance serves every
/// channel. It resolves through <see cref="IMessageTemplateAccessor"/> — the single point a message of
/// the channel contour is resolved at — and holds no channel of its own: the channel is read off the
/// adapter on each call, the surface is always the in-channel one, and the ownership levels, the
/// locale, the time zone and the application name come from the transaction. A call for a link request
/// names the purpose of the link as well.
/// </summary>
internal sealed class ChannelMessageTemplates : IChannelMessageTemplates
{
    /// <summary>
    /// Text of the failure returned when no template could be resolved.
    /// </summary>
    private const string NotResolvedMessage = "The template of the channel message could not be resolved";

    /// <summary>
    /// The single point a message of the channel contour is resolved at.
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// Transaction store — singleton in every contour (InMemory/EfCore/Redis), so constructor injection
    /// is safe here (no captive dependency).
    /// </summary>
    private readonly ITransactionStore _transactionStore;

    /// <summary>
    /// Metrics of the channel adapter component.
    /// </summary>
    private readonly ChannelAdapterMetrics _metrics;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ChannelMessageTemplates> _logger;

    /// <summary>
    /// Creates the accessor of channel message templates.
    /// </summary>
    /// <param name="messageTemplates">The single point a message of the channel contour is resolved at.</param>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="metrics">Metrics of the channel adapter component.</param>
    /// <param name="logger">Logger.</param>
    public ChannelMessageTemplates(
        IMessageTemplateAccessor messageTemplates,
        ITransactionStore transactionStore,
        ChannelAdapterMetrics metrics,
        ILogger<ChannelMessageTemplates> logger)
    {
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<Result<ResolvedChannelMessage>> ResolveAsync(
        IChannelAdapter adapter,
        string kind,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrEmpty(kind);

        return ResolveCoreAsync(adapter, kind, transactionId, linkPurpose: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ResolvedChannelMessage>> ResolveAsync(
        IChannelAdapter adapter,
        string kind,
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(request);

        // An undefined purpose is a programming error of the caller, so it is thrown before anything is
        // resolved rather than returned as a failure the adapter would treat as a missing template.
        var linkPurpose = LinkPurposeOf(request.Purpose);

        return ResolveCoreAsync(adapter, kind, request.TransactionId, linkPurpose, cancellationToken);
    }

    /// <summary>
    /// The value of the <c>link_purpose</c> dimension a purpose of a link is stated at in configuration.
    /// </summary>
    /// <param name="purpose">Purpose of the link.</param>
    /// <returns>The dimension value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The purpose is not a defined value.</exception>
    private static string LinkPurposeOf(DeepLinkPurpose purpose) => purpose switch
    {
        DeepLinkPurpose.Button => MessageLinkPurposes.Button,
        DeepLinkPurpose.HopQr => MessageLinkPurposes.HopQr,
        DeepLinkPurpose.DirectQr => MessageLinkPurposes.DirectQr,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown deep link purpose.")
    };

    /// <summary>
    /// Resolves the template of a channel message for a transaction: the transaction gives the ownership
    /// levels and the rendering context, a transaction the store does not have resolves over the core
    /// level, and any failure is logged, counted and returned as one.
    /// </summary>
    /// <param name="adapter">The calling adapter.</param>
    /// <param name="kind">Message kind identifier.</param>
    /// <param name="transactionId">Transaction the message belongs to.</param>
    /// <param name="linkPurpose">Value of the <c>link_purpose</c> dimension, or null when the caller names
    /// no link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved template, or a failure.</returns>
    private async Task<Result<ResolvedChannelMessage>> ResolveCoreAsync(
        IChannelAdapter adapter,
        string kind,
        TransactionId transactionId,
        string? linkPurpose,
        CancellationToken cancellationToken)
    {
        var channelType = adapter.ChannelType;

        try
        {
            var transaction = await _transactionStore.GetByIdAsync(transactionId, cancellationToken);

            // A transaction the store does not have resolves over the core level with no context —
            // what the deep-link prefill has always done for it; it is not a failure.
            var ownership = transaction is null
                ? ResolutionContext.Core
                : TransactionResolutionContext.For(transaction);

            var message = linkPurpose is null
                ? await _messageTemplates.RequireAsync(
                    kind,
                    MessageSurfaces.InChannel,
                    channelType,
                    ownership,
                    cancellationToken)
                : await _messageTemplates.RequireAsync(
                    ConfigDimensionValues.Of(
                        (MessageTemplateConfigKeys.KindDimensionName, kind),
                        (MessageTemplateConfigKeys.SurfaceDimensionName, MessageSurfaces.InChannel),
                        (MessageTemplateConfigKeys.ChannelDimensionName, channelType),
                        (MessageTemplateConfigKeys.LinkPurposeDimensionName, linkPurpose)),
                    ownership,
                    cancellationToken);

            return Result<ResolvedChannelMessage>.Success(new ResolvedChannelMessage(
                message,
                transaction?.GetUiLocale(),
                transaction?.GetUiTimeZone(),
                transaction?.InitiatorContextSnapshot?.ClientApplicationName ?? transaction?.GetApplicationName())
            {
                BrandName = transaction?.GetBrandName()
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal request cancellation — propagate, this is not a resolution failure
            throw;
        }
        catch (Exception exception)
        {
            // Nothing resolved (InvalidOperationException of the accessor), or the store or the
            // resolver failed: either way the adapter has no template and sends nothing.
            _logger.LogError(
                exception,
                "Failed to resolve the template of a channel message. Kind: {MessageKind}, "
                + "ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                kind,
                channelType,
                transactionId.ToString());

            _metrics.RecordTemplateResolutionFailure(channelType, kind);

            return Result<ResolvedChannelMessage>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                NotResolvedMessage);
        }
    }
}
