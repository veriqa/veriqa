// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Reader of the deep-link prefill context (SPEC-003 §10.4): the pieces of the transaction the Email
/// channel needs beside the template of its prefilled Push mail but cannot see from
/// <c>GetDeepLinkAsync</c>, whose request carries the <see cref="TransactionId"/> and the purpose of the
/// link, not the stored transaction — the ownership levels the faults of the mail are reported by and the
/// expiry its correlation token lives until. The template
/// itself, with the locale, the time zone and the application name, comes from
/// <c>IChannelMessageTemplates</c>.
/// <para>
/// Plain transaction data, no configuration involved: the reader is a shared implementation detail of
/// the channel contour and not a replaceable contract.
/// </para>
/// </summary>
internal sealed class DeepLinkPrefillContextReader
{
    /// <summary>
    /// Transaction store — singleton in every contour (InMemory/EfCore/Redis), so constructor injection
    /// is safe here (no captive dependency).
    /// </summary>
    private readonly ITransactionStore _transactionStore;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<DeepLinkPrefillContextReader> _logger;

    /// <summary>
    /// Creates the prefill context reader.
    /// </summary>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="logger">Logger.</param>
    public DeepLinkPrefillContextReader(
        ITransactionStore transactionStore,
        ILogger<DeepLinkPrefillContextReader> logger)
    {
        _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads the prefill context (ownership levels + expiry) of the transaction.
    /// </summary>
    /// <remarks>
    /// Never throws: an unavailable store is not a reason to block deep-link generation — it degrades to
    /// null (plus a WARNING in the log): the channel then reports over the core level, and its token lives
    /// for the configured TTL.
    /// </remarks>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelType">Channel type (telegram/whatsapp/max/email from <c>ChannelTypes</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The prefill context, or null when no context is available.</returns>
    public async Task<DeepLinkPrefillContext?> ReadAsync(
        TransactionId transactionId,
        string channelType,
        CancellationToken cancellationToken = default)
    {
        // Contract: never throws and never blocks deep-link generation — any failure degrades to null.
        try
        {
            var transaction = await _transactionStore.GetByIdAsync(transactionId, cancellationToken);
            if (transaction is null)
            {
                return null;
            }

            return new DeepLinkPrefillContext(TransactionResolutionContext.For(transaction))
            {
                TransactionExpiresAt = transaction.ExpiresAt
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal request cancellation — propagate, this is not a context-resolution failure
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Unexpected failure reading the deep-link prefill context; using the core level. "
                + "ChannelType: {ChannelType}",
                channelType);

            return null;
        }
    }
}

/// <summary>
/// Deep-link prefill context (SPEC-003 §10.4) of the Email channel: the ownership levels of the
/// transaction and its expiry.
/// </summary>
/// <param name="Ownership">Ownership levels of the transaction (SPEC-036 TPL-116) — the same levels the
/// prefill message is resolved over; the faults of the Push mail are reported once per level.</param>
internal sealed record DeepLinkPrefillContext(ResolutionContext Ownership)
{
    /// <summary>
    /// Expiry of the transaction the link is built for: what a channel registers for the link (a
    /// correlation token of its mail) lives no longer than this moment.
    /// </summary>
    public required DateTimeOffset TransactionExpiresAt { get; init; }
}
