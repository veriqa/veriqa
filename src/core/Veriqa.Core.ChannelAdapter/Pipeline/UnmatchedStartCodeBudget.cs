// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Threading.RateLimiting;

using Microsoft.Extensions.Options;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The budget of start codes that took no record, kept per pair "tenant of the event + channel type": the
/// bound on guessing codes on a path whose source names any sender it likes — the route of a tenant, the
/// polling of a tenant, a registration with an owner — where the limit of starts per channel user does
/// not hold. Only an unmatched code spends it; the check of what is left spends nothing.
/// </summary>
/// <remarks>
/// The counters live in this process: every instance of a deployment keeps its own budget of each pair,
/// as the limiter of starts per channel user does. The pairs are bounded by the tenants the deployment
/// serves and the channels it registers — an event reaches the budget only past the check of the tenant
/// of its route — so no bound on the number of partitions is kept here.
/// <para>
/// The check and the charge are two steps with the take of the code between them, because only a code
/// that took no record is charged and that is known after the take. Events of one pair that pass the check
/// together are all looked up before the first of them is charged, so the budget may be overspent by the
/// number of such events in flight. The overspend stays bounded by the <c>webhook</c> rate limit policy of
/// the route segment and by how many requests run at once.
/// </para>
/// </remarks>
internal sealed class UnmatchedStartCodeBudget : IDisposable
{
    /// <summary>
    /// Separator of the two parts of a partition key. The NUL character occurs in neither a channel type
    /// nor a tenant identifier, so two different pairs never share a key.
    /// </summary>
    private const string KeySeparator = "\0";

    /// <summary>
    /// The fixed-window limiter of every pair.
    /// </summary>
    private readonly PartitionedRateLimiter<string> _limiter;

    /// <summary>
    /// Creates the budget from the settings of the short code.
    /// </summary>
    /// <param name="options">Settings of the short code — the permit limit and the window of the budget.</param>
    public UnmatchedStartCodeBudget(IOptions<ChannelCodeStartOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var permitLimit = options.Value.UnmatchedCodePermitLimit;
        var window = TimeSpan.FromSeconds(options.Value.UnmatchedCodeWindowSeconds);

        _limiter = PartitionedRateLimiter.Create<string, string>(
            key => RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0
                }));
    }

    /// <summary>
    /// Whether the budget of the pair is spent for the current window. Spends nothing.
    /// </summary>
    /// <param name="tenantId">Tenant of the event.</param>
    /// <param name="channelType">Channel type the event arrived through.</param>
    /// <returns>true — no unmatched code is left to the pair until the window ends.</returns>
    public bool IsExhausted(string tenantId, string channelType)
    {
        // A request for zero permits answers whether any is left without taking one.
        using var lease = _limiter.AttemptAcquire(KeyOf(tenantId, channelType), 0);

        return !lease.IsAcquired;
    }

    /// <summary>
    /// Spends one unmatched code of the pair.
    /// </summary>
    /// <param name="tenantId">Tenant of the event.</param>
    /// <param name="channelType">Channel type the event arrived through.</param>
    public void Charge(string tenantId, string channelType)
    {
        using var lease = _limiter.AttemptAcquire(KeyOf(tenantId, channelType), 1);
    }

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();

    /// <summary>
    /// The partition key of a pair.
    /// </summary>
    /// <param name="tenantId">Tenant of the event.</param>
    /// <param name="channelType">Channel type.</param>
    /// <returns>The key.</returns>
    private static string KeyOf(string tenantId, string channelType) =>
        string.Concat(channelType, KeySeparator, tenantId);
}
