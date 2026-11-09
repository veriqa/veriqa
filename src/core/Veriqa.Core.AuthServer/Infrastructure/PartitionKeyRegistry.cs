// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// Bound on the partitions of a partitioned rate limiter whose key comes from the request: it holds the
/// keys active within the limit window, up to a configured number. A key not seen for a whole window
/// leaves the registry and frees its place, and only a new key arriving while the registry is full of
/// keys active within the window is routed to the shared <see cref="OverflowPartitionKey"/> — so rotating
/// keys neither escapes the limit nor grows the memory without bound.
/// </summary>
internal sealed class PartitionKeyRegistry
{
    /// <summary>
    /// Key of the shared partition of the keys beyond the bound. The NUL character guarantees no
    /// collision with a real key.
    /// </summary>
    internal const string OverflowPartitionKey = "\0overflow";

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
    /// Creates the registry.
    /// </summary>
    /// <param name="maxPartitions">Upper bound on the number of concurrently active partitions.</param>
    /// <param name="timeProvider">Clock of the last access of a partition.</param>
    public PartitionKeyRegistry(int maxPartitions, TimeProvider timeProvider)
    {
        _maxPartitions = maxPartitions;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Returns the partition key for the given key and records the access.
    /// A known key is moved to the tail of the access order. A new key is registered while there is room;
    /// when the registry is full, the keys idle for at least <paramref name="idleThreshold"/> are evicted
    /// first, in this same call, and only if none was idle does the new key fall into the shared
    /// <see cref="OverflowPartitionKey"/>.
    /// </summary>
    /// <param name="key">Original key.</param>
    /// <param name="idleThreshold">Idle time after which a key leaves the registry — the limit window.</param>
    /// <returns>Partition key for the limiter.</returns>
    public string Resolve(string key, TimeSpan idleThreshold)
    {
        var now = _timeProvider.GetTimestamp();

        lock (_partitionGate)
        {
            // A known key: refresh its last access and move it to the tail of the order
            if (_partitionKeys.TryGetValue(key, out var known))
            {
                known.Value = known.Value with { LastAccessTimestamp = now };
                _partitionsByLastAccess.Remove(known);
                _partitionsByLastAccess.AddLast(known);
                return key;
            }

            // Registry full: free the places of the idle keys before deciding on the new one
            if (_partitionKeys.Count >= _maxPartitions)
            {
                EvictIdlePartitions(now, idleThreshold);
            }

            // Still full of keys active within the window: route the new key to overflow
            if (_partitionKeys.Count >= _maxPartitions)
            {
                return OverflowPartitionKey;
            }

            _partitionKeys.Add(key, _partitionsByLastAccess.AddLast(new PartitionEntry(key, now)));
            return key;
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
