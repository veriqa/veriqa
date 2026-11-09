// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The store of prompt references as the answers to the events of ONE channel see it: those answers
/// take only a reference of that channel, whatever the channel (SPEC-003 CA-244): every take becomes a
/// take of this channel's reference and every read returns only this channel's reference, so a
/// reference another channel stored for the transaction stays in the store and the answer carries
/// none. The processing of an event on a path that asks in the chat hands it to the handlers in place
/// of the store itself; the handlers of the events do not change.
/// </summary>
internal sealed class OwnChannelPromptMessageStore : IChannelPromptMessageStore
{
    /// <summary>
    /// The store of the host.
    /// </summary>
    private readonly IChannelPromptMessageStore _inner;

    /// <summary>
    /// Channel type the events are answered through.
    /// </summary>
    private readonly string _channelType;

    /// <summary>
    /// Narrows the store of the host to the references of one channel.
    /// </summary>
    /// <param name="inner">The store of the host.</param>
    /// <param name="channelType">Channel type the events are answered through.</param>
    public OwnChannelPromptMessageStore(IChannelPromptMessageStore inner, string channelType)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        _inner = inner;
        _channelType = channelType;
    }

    /// <inheritdoc />
    public Task SaveAsync(
        TransactionId transactionId,
        ChannelPromptMessageRef reference,
        CancellationToken cancellationToken = default) =>
        _inner.SaveAsync(transactionId, reference, cancellationToken);

    /// <summary>
    /// Reads the stored coordinates of the transaction only when this channel stored them; the
    /// coordinates stay in the store either way.
    /// </summary>
    /// <param name="transactionId">Transaction to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The coordinates this channel stored, or null.</returns>
    public async Task<ChannelPromptMessageRef?> FindAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        var reference = await _inner.FindAsync(transactionId, cancellationToken);

        return reference is not null && string.Equals(reference.ChannelType, _channelType, StringComparison.Ordinal)
            ? reference
            : null;
    }

    /// <summary>
    /// Takes the stored coordinates of the transaction only when this channel stored them.
    /// </summary>
    /// <param name="transactionId">Transaction to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The coordinates this channel stored, or null.</returns>
    public Task<ChannelPromptMessageRef?> TakeAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken = default) =>
        _inner.TakeForChannelAsync(transactionId, _channelType, cancellationToken);

    /// <inheritdoc />
    public Task<ChannelPromptMessageRef?> TakeForChannelAsync(
        TransactionId transactionId,
        string channelType,
        CancellationToken cancellationToken = default) =>
        _inner.TakeForChannelAsync(transactionId, channelType, cancellationToken);

    /// <inheritdoc />
    public Task SetPhoneRequestMessageAsync(
        TransactionId transactionId,
        string channelType,
        ChannelMessageRef requestMessage,
        CancellationToken cancellationToken = default) =>
        _inner.SetPhoneRequestMessageAsync(transactionId, channelType, requestMessage, cancellationToken);
}
