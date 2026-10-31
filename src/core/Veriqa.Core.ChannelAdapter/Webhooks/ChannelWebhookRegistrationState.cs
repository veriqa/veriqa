// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;

namespace Veriqa.Core.ChannelAdapter.Webhooks;

/// <summary>
/// Which channels run in the Webhook mode and still have no webhook registered at the platform. Written
/// by <see cref="ChannelWebhookRegistrationService"/>, read by the channel health check: a channel whose
/// webhook is not registered receives no updates, whatever its platform answers to a health probe.
/// </summary>
internal sealed class ChannelWebhookRegistrationState
{
    /// <summary>
    /// Channel types whose webhook registration has not succeeded yet. The value is unused.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks the webhook of the channel as not registered.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    public void MarkPending(string channelType) => _pending[channelType] = true;

    /// <summary>
    /// Marks the webhook of the channel as registered.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    public void MarkRegistered(string channelType) => _pending.TryRemove(channelType, out _);

    /// <summary>
    /// Tells whether the channel runs in the Webhook mode and its webhook is not registered yet.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns><c>true</c> while the registration of the channel's webhook has not succeeded.</returns>
    public bool IsPending(string channelType) => _pending.ContainsKey(channelType);
}
