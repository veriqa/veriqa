// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Threading.RateLimiting;

using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// In-memory implementation of a per-user authentication rate limiter.
/// Uses <see cref="PartitionedRateLimiter{T}"/> with the key {channel_type}:{channel_user_id}.
/// Limits the number of CONCURRENTLY ACTIVE partitions via
/// <see cref="RateLimitOptions.UserAuthMaxPartitions"/>: a key not seen for a whole limit window leaves the
/// registry and frees its place, and only a new user arriving while the registry is full of keys active
/// within the window is routed to a shared overflow partition.
/// Single-instance only: counters are in-process and are not shared across nodes.
/// </summary>
internal sealed class UserAuthRateLimiter : IUserAuthRateLimiter, IDisposable
{
    /// <summary>
    /// Overflow partition key used for users beyond the limit.
    /// The NUL character guarantees no collisions with real keys.
    /// </summary>
    private const string OverflowPartitionKey = "\0overflow";

    /// <summary>
    /// Partitioned fixed-window request limiter, built on the FIRST acquisition — that is, on the
    /// asynchronous path. Its permit limit and window are levelled settings, and resolving a levelled
    /// setting is asynchronous; a constructor a container calls synchronously is therefore the wrong
    /// place for it, and the class no longer resolves anything there.
    /// </summary>
    private PartitionedRateLimiter<string>? _limiter;

    /// <summary>
    /// Guard of the one-time construction of the limiter: several requests may reach the first
    /// acquisition at once, and exactly one of them must build it.
    /// </summary>
    private readonly SemaphoreSlim _limiterGate = new(1, 1);

    /// <summary>
    /// Builds the limiter from the effective permit limit and window.
    /// </summary>
    private readonly Func<CancellationToken, Task<PartitionedRateLimiter<string>>> _buildLimiter;

    /// <summary>
    /// Registry of the partitions in use: key → its node in <see cref="_partitionsByLastAccess"/>.
    /// Bounds the number of unique keys it holds. The platform limiter keeps its own cache of the
    /// per-key limiters, which lets go of an evicted key only some seconds after its window resets —
    /// so the limiters held may briefly exceed the registry bound.
    /// </summary>
    private readonly Dictionary<string, LinkedListNode<PartitionEntry>> _partitionKeys =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The registered partitions ordered by their last access, the least recently used first. The order
    /// lets a full registry find its idle keys at the head without a pass over all of them.
    /// </summary>
    private readonly LinkedList<PartitionEntry> _partitionsByLastAccess = new();

    /// <summary>
    /// Guard of <see cref="_partitionKeys"/> and <see cref="_partitionsByLastAccess"/>, which change together.
    /// </summary>
    private readonly Lock _partitionGate = new();

    /// <summary>
    /// Clock of the last access of a partition.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Upper bound on the number of concurrently active partitions.
    /// </summary>
    private readonly int _maxPartitions;

    /// <summary>
    /// Creates an instance of <see cref="UserAuthRateLimiter"/> with parameters from configuration.
    /// </summary>
    /// <param name="options">Rate limiting settings.</param>
    /// <param name="resolver">Canonical multi-level configuration resolver.</param>
    /// <param name="timeProvider">Clock of the last access of a partition.</param>
    public UserAuthRateLimiter(
        IOptions<RateLimitOptions> options,
        IConfigurationResolver resolver,
        TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;

        // The machinery (partitions/queue) stays in the core and is NOT split across levels (CFG-230):
        // QueueLimit/MaxPartitions are read directly from config.
        var config = options.Value;
        var queueLimit = config.UserAuthQueueLimit;
        _maxPartitions = config.UserAuthMaxPartitions;

        // Re-leveling of the limit VALUE and WINDOW through the single asynchronous resolver (CFG-223,
        // CFG-235). The limiter is built once, on the first acquisition: that is an asynchronous path,
        // so the resolution has a seam to happen on and needs neither a blocking wait nor a second
        // entry of resolution. Runtime configuration changes are not applied afterwards (acceptable:
        // a rate limit is not a hot-path configuration).
        _buildLimiter = async cancellationToken =>
        {
            var permitLimit = (await resolver.ResolveAsync(
                RateLimitConfigKeys.UserAuthPermitLimit,
                ResolutionContext.Core,
                ConfigDimensionValues.None,
                cancellationToken)).Value;

            var windowSeconds = (await resolver.ResolveAsync(
                RateLimitConfigKeys.UserAuthWindowSeconds,
                ResolutionContext.Core,
                ConfigDimensionValues.None,
                cancellationToken)).Value;

            // The idle threshold of the partition registry is this same window: a key idle for a whole
            // window has had its window reset, so leaving the registry costs it nothing.
            var window = TimeSpan.FromSeconds(windowSeconds);

            return PartitionedRateLimiter.Create<string, string>(
                resourceId => RateLimitPartition.GetFixedWindowLimiter(
                    ResolvePartitionKey(resourceId, window),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = window,
                        QueueLimit = queueLimit
                    }));
        };
    }

    /// <summary>
    /// Returns the limiter, building it on the first acquisition. This is the asynchronous startup
    /// platform of this class: the permit limit and the window are levelled settings, and resolving
    /// them belongs on an awaitable path rather than in a constructor.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The partitioned limiter.</returns>
    private async ValueTask<PartitionedRateLimiter<string>> GetLimiterAsync(CancellationToken cancellationToken)
    {
        var built = Volatile.Read(ref _limiter);
        if (built is not null)
        {
            return built;
        }

        await _limiterGate.WaitAsync(cancellationToken);
        try
        {
            built = Volatile.Read(ref _limiter);
            if (built is null)
            {
                built = await _buildLimiter(cancellationToken);
                Volatile.Write(ref _limiter, built);
            }

            return built;
        }
        finally
        {
            _limiterGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<UserAuthRateLimitResult> TryAcquireAsync(
        string userKey,
        CancellationToken cancellationToken = default)
    {
        // Try to acquire 1 permit from the user's partition.
        // The lease is released immediately (allow/deny); window state is managed by the limiter.
        var limiter = await GetLimiterAsync(cancellationToken);
        using var lease = await limiter.AcquireAsync(userKey, 1, cancellationToken);

        if (lease.IsAcquired)
        {
            return UserAuthRateLimitResult.Allowed;
        }

        // Get the actual wait time from the limiter metadata
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter);

        return UserAuthRateLimitResult.Rejected(retryAfter);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to dispose until the limiter has actually been built: a host that never took a
        // permit never created one.
        Volatile.Read(ref _limiter)?.Dispose();
        _limiterGate.Dispose();
    }

    /// <summary>
    /// Returns the partition key for the given userKey and records the access.
    /// A known key is moved to the tail of the access order. A new key is registered while there is room;
    /// when the registry is full, the keys idle for at least <paramref name="idleThreshold"/> are evicted
    /// first, in this same call, and only if none was idle does the new user fall into the shared
    /// <see cref="OverflowPartitionKey"/>.
    /// </summary>
    /// <param name="userKey">Original user key.</param>
    /// <param name="idleThreshold">Idle time after which a key leaves the registry — the limit window.</param>
    /// <returns>Partition key for the limiter.</returns>
    private string ResolvePartitionKey(string userKey, TimeSpan idleThreshold)
    {
        var now = _timeProvider.GetTimestamp();

        lock (_partitionGate)
        {
            // A known key: refresh its last access and move it to the tail of the order
            if (_partitionKeys.TryGetValue(userKey, out var known))
            {
                known.Value = known.Value with { LastAccessTimestamp = now };
                _partitionsByLastAccess.Remove(known);
                _partitionsByLastAccess.AddLast(known);
                return userKey;
            }

            // Registry full: free the places of the idle keys before deciding on the new one
            if (_partitionKeys.Count >= _maxPartitions)
            {
                EvictIdlePartitions(now, idleThreshold);
            }

            // Still full of keys active within the window: route the new user to overflow
            if (_partitionKeys.Count >= _maxPartitions)
            {
                return OverflowPartitionKey;
            }

            _partitionKeys.Add(userKey, _partitionsByLastAccess.AddLast(new PartitionEntry(userKey, now)));
            return userKey;
        }
    }

    /// <summary>
    /// Removes from the registry the keys idle for at least <paramref name="idleThreshold"/>. The access
    /// order keeps the least recently used key at the head, so the walk stops at the first active key:
    /// a registry full of active keys costs one check, and each evicted key is paid for once.
    /// Must be called under <see cref="_partitionGate"/>.
    /// </summary>
    /// <param name="now">Timestamp of the current access.</param>
    /// <param name="idleThreshold">Idle time after which a key leaves the registry.</param>
    private void EvictIdlePartitions(long now, TimeSpan idleThreshold)
    {
        while (_partitionsByLastAccess.First is { } oldest
            && _timeProvider.GetElapsedTime(oldest.Value.LastAccessTimestamp, now) >= idleThreshold)
        {
            _partitionsByLastAccess.RemoveFirst();
            _partitionKeys.Remove(oldest.Value.Key);
        }
    }

    /// <summary>
    /// A registered partition and the timestamp of its last access.
    /// </summary>
    /// <param name="Key">Partition key.</param>
    /// <param name="LastAccessTimestamp">Timestamp of the last access, from <see cref="_timeProvider"/>.</param>
    private readonly record struct PartitionEntry(string Key, long LastAccessTimestamp);
}
