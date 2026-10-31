// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// Base per-tenant polling supervisor (TASK-051).
/// Two-level scheme: the supervisor obtains the set of active polling tenants from
/// <see cref="IPollingTenantSource"/> and starts an independent long-poll loop for each one
/// (<see cref="RunTenantPollLoopAsync"/>) with its own bot client and its own polling position.
/// The only "polling is active" indicator is the set from the source (an empty set ⇒ no loops,
/// waiting for cancellation/rescan; no duplicating <c>UpdateMode</c> gate in the supervisor).
/// Reaction to set changes without a restart — periodic rescanning + set delta
///. The <c>tenantId → (Task, CTS)</c> registry is mutated ONLY from the supervisor
/// thread (sole owner — no concurrent access). The supervisor mechanics are shared by Telegram and
/// MAX; only the loop body differs (<see cref="RunTenantPollLoopAsync"/>: offset/marker, client) —
/// hence it is extracted into a base type (core-rules §5, no over-engineering).
/// </summary>
internal abstract class PerTenantPollingSupervisor : BackgroundService
{
    /// <summary>
    /// Registry key sentinel for the default implicit tenant (<c>null</c>): the dictionary does not store a null key.
    /// </summary>
    private const string DefaultTenantKey = "\0__default__";

    /// <summary>
    /// Source of active polling tenants (seam TASK-051).
    /// </summary>
    private readonly IPollingTenantSource _tenantSource;

    /// <summary>
    /// Registry of active per-tenant loops keyed by tenant. Owned only by the supervisor thread.
    /// </summary>
    private readonly Dictionary<string, TenantLoop> _loops = new(StringComparer.Ordinal);

    /// <summary>
    /// Supervisor logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the base per-tenant supervisor.
    /// </summary>
    /// <param name="tenantSource">Source of active polling tenants.</param>
    /// <param name="logger">Logger.</param>
    protected PerTenantPollingSupervisor(IPollingTenantSource tenantSource, ILogger logger)
    {
        _tenantSource = tenantSource ?? throw new ArgumentNullException(nameof(tenantSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Supervisor's channel type (<c>ChannelTypes.Telegram</c> / <c>ChannelTypes.Max</c>).
    /// </summary>
    protected abstract string ChannelType { get; }

    /// <summary>
    /// Base retry delay when the source fails during initial start (milliseconds).
    /// </summary>
    protected abstract int RetryBaseDelayMilliseconds { get; }

    /// <summary>
    /// Body of one per-tenant long-poll loop: obtain the client from the factory by
    /// <c>(ChannelType, tenantId)</c>, keep its own polling position (offset/marker),
    /// process updates in the tenant scope (<see cref="ChannelTenantContext.BeginScope"/>).
    /// Local retry on a network failure — inside the implementation (one tenant's failure does not bring down the others).
    /// </summary>
    /// <param name="tenantId">Tenant identifier (<c>null</c> — the default implicit tenant).</param>
    /// <param name="cancellationToken">Cancellation token of the tenant's loop.</param>
    protected abstract Task RunTenantPollLoopAsync(string? tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Supervisor logger (for loop implementations).
    /// </summary>
    protected ILogger Logger => _logger;

    /// <summary>
    /// Supervisor: initial start of the active tenants' loops + periodic rescanning of the set
    /// with delta application, until <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token for host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Per-tenant polling supervisor of channel {ChannelType} started", ChannelType);

        try
        {
            // Initial start: wait for the source to be available (Failure ⇒ retry after RetryBaseDelay).
            await InitialScanAsync(stoppingToken);

            // Dynamics without a restart: periodic rescanning + set delta.
            await RescanLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal completion on host shutdown.
        }
        finally
        {
            // Stop all per-tenant loops, releasing resources (CancellationToken.None).
            await StopAllLoopsAsync();
        }

        _logger.LogInformation("Per-tenant polling supervisor of channel {ChannelType} stopped", ChannelType);
    }

    /// <summary>
    /// Initial source query: retries on Failure until success or cancellation. On success applies
    /// the delta to the empty registry (starts the active tenants' loops). An empty set is valid
    /// (no loops) and is not an error.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token.</param>
    private async Task InitialScanAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = await _tenantSource.GetActivePollingTenantsAsync(ChannelType, stoppingToken);

            if (snapshot.IsSuccess)
            {
                if (snapshot.Value.Count == 0)
                {
                    _logger.LogInformation(
                        "No active polling tenants for channel {ChannelType} — no loops started",
                        ChannelType);
                }

                ApplyDelta(snapshot.Value, stoppingToken);
                return;
            }

            LogSourceFailure(snapshot.Error, "initial start");

            // Retry the initial start — after the channel's base delay (start phase, not rescan).
            await Task.Delay(RetryBaseDelayMilliseconds, stoppingToken);
        }
    }

    /// <summary>
    /// Periodic tenant set rescanning: every <see cref="PollingSupervisorConstants.RescanIntervalSeconds"/>
    /// re-queries the source and applies the delta (start new ones, stop vanished ones).
    /// A source Failure during rescan does NOT tear down the already running loops — keep the current set,
    /// WARNING, retry on the next tick.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token.</param>
    private async Task RescanLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(PollingSupervisorConstants.RescanIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);

            var snapshot = await _tenantSource.GetActivePollingTenantsAsync(ChannelType, stoppingToken);

            if (snapshot.IsFailure)
            {
                // Temporary source unavailability must not bring down the working loops.
                LogSourceFailure(snapshot.Error, "rescan");
                continue;
            }

            await ApplyDeltaWithStopAsync(snapshot.Value, stoppingToken);
        }
    }

    /// <summary>
    /// Applies the set delta WITHOUT stopping vanished loops (used at initial start, where the
    /// registry is known to be empty): for every tenant from the snapshot missing from the registry — start a loop.
    /// The snapshot is reduced to Set semantics by tenant key (dedupes duplicates from the source).
    /// </summary>
    /// <param name="snapshot">Snapshot of the active tenant set.</param>
    /// <param name="stoppingToken">Supervisor cancellation token (parent of the loops' linked tokens).</param>
    private void ApplyDelta(IReadOnlyCollection<string?> snapshot, CancellationToken stoppingToken)
    {
        var desired = BuildDesiredKeys(snapshot);

        foreach (var (key, tenantId) in desired)
        {
            if (!_loops.ContainsKey(key))
            {
                StartLoop(key, tenantId, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Applies the full set delta (rescan): start new tenants + stop vanished ones.
    /// Registry mutation happens only here, from the supervisor thread (sole owner).
    /// </summary>
    /// <param name="snapshot">Snapshot of the active tenant set.</param>
    /// <param name="stoppingToken">Supervisor cancellation token.</param>
    private async Task ApplyDeltaWithStopAsync(IReadOnlyCollection<string?> snapshot, CancellationToken stoppingToken)
    {
        var desired = BuildDesiredKeys(snapshot);

        // New tenants (in the snapshot, not in the registry) — start loops.
        foreach (var (key, tenantId) in desired)
        {
            if (!_loops.ContainsKey(key))
            {
                StartLoop(key, tenantId, stoppingToken);
            }
        }

        // Vanished tenants (in the registry, not in the snapshot) — cancel and await completion.
        var stale = _loops.Keys.Where(existing => !desired.ContainsKey(existing)).ToList();
        foreach (var key in stale)
        {
            await StopLoopAsync(key);
        }
    }

    /// <summary>
    /// Builds the desired <c>registry key → tenantId</c> dictionary from the snapshot, deduplicating duplicates (Set semantics).
    /// </summary>
    /// <param name="snapshot">Snapshot of the active tenant set.</param>
    /// <returns>Dictionary without duplicates by tenant key.</returns>
    private static Dictionary<string, string?> BuildDesiredKeys(IReadOnlyCollection<string?> snapshot)
    {
        var desired = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tenantId in snapshot)
        {
            desired[ToKey(tenantId)] = tenantId;
        }

        return desired;
    }

    /// <summary>
    /// Starts a per-tenant loop: a linked CTS from <paramref name="stoppingToken"/> + the loop Task.
    /// Registers the pair in the registry. Idempotency by key is guaranteed by the caller (the
    /// <c>ContainsKey</c> check before starting).
    /// </summary>
    /// <param name="key">Tenant registry key.</param>
    /// <param name="tenantId">Tenant identifier (<c>null</c> — default).</param>
    /// <param name="stoppingToken">Parent supervisor cancellation token.</param>
    private void StartLoop(string key, string? tenantId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // The loop body is implemented by the channel; one tenant's failure must not bring down the supervisor — catch here.
        var task = RunTenantLoopGuardedAsync(tenantId, cts.Token);

        _loops[key] = new TenantLoop(task, cts);

        _logger.LogInformation(
            "Polling loop of channel {ChannelType} started for tenant {TenantId}",
            ChannelType,
            tenantId ?? ChannelTenantContext.DefaultTenantDisplayName);
    }

    /// <summary>
    /// Tenant loop wrapper: guarantees that an unhandled exception of a specific loop does not
    /// "leak" into the supervisor's <c>Task.WhenAll</c> and does not bring down the other loops (failure isolation).
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token of the tenant's loop.</param>
    private async Task RunTenantLoopGuardedAsync(string? tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await RunTenantPollLoopAsync(tenantId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal loop completion on cancellation (tenant stop or host shutdown).
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Polling loop of channel {ChannelType} for tenant {TenantId} finished with an error",
                ChannelType,
                tenantId ?? ChannelTenantContext.DefaultTenantDisplayName);
        }
    }

    /// <summary>
    /// Stops the tenant's loop: cancels its CTS, awaits the task's completion, releases the CTS.
    /// Removes the entry from the registry. CTS cleanup is guaranteed (in <c>finally</c>).
    /// </summary>
    /// <param name="key">Tenant registry key.</param>
    private async Task StopLoopAsync(string key)
    {
        if (!_loops.TryGetValue(key, out var loop))
        {
            return;
        }

        _loops.Remove(key);

        try
        {
            await loop.CancelAndAwaitAsync();
        }
        finally
        {
            loop.Dispose();
        }

        _logger.LogInformation(
            "Polling loop of channel {ChannelType} stopped (tenant key removed from the set)",
            ChannelType);
    }

    /// <summary>
    /// Stops all per-tenant loops when the supervisor shuts down: cancels the CTS of all loops and
    /// awaits their completion (<c>Task.WhenAll</c>), then releases resources. Runs with
    /// <see cref="CancellationToken.None"/> (cleanup executes even when the host is cancelled, csharp-rules §2).
    /// </summary>
    private async Task StopAllLoopsAsync()
    {
        if (_loops.Count == 0)
        {
            return;
        }

        var loops = _loops.Values.ToList();
        _loops.Clear();

        // Cancel all loops, then await their joint completion (deadlock is impossible — the loops
        // are independent, cooperative cancellation). Cleanup is guaranteed in finally.
        try
        {
            foreach (var loop in loops)
            {
                await loop.CancelAsync();
            }

            // The loop tasks are the supervisor's own work (started in StartLoop); awaiting their
            // joint completion is safe (VSTHRD003 does not apply — not "foreign" Tasks).
#pragma warning disable VSTHRD003
            await Task.WhenAll(loops.Select(static loop => loop.Task));
#pragma warning restore VSTHRD003
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error while stopping polling loops of channel {ChannelType}",
                ChannelType);
        }
        finally
        {
            foreach (var loop in loops)
            {
                loop.Dispose();
            }
        }
    }

    /// <summary>
    /// Logs a tenant source failure without sensitive data (only the error code/message).
    /// </summary>
    /// <param name="error">Source error.</param>
    /// <param name="phase">Phase (initial start / rescan) — for diagnostics.</param>
    private void LogSourceFailure(TransactionError error, string phase)
    {
        _logger.LogWarning(
            "Polling tenant source of channel {ChannelType} is unavailable ({Phase}): {ErrorCode} — {ErrorMessage}",
            ChannelType,
            phase,
            error.Code,
            error.Message);
    }

    /// <summary>
    /// Registry key for the tenant identifier (<c>null</c> ⇒ the default tenant sentinel).
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <returns>Non-null dictionary key.</returns>
    private static string ToKey(string? tenantId) => tenantId ?? DefaultTenantKey;

    /// <summary>
    /// Registry entry: the tenant loop's task + its cancellation source. Owned by the supervisor.
    /// </summary>
    private sealed class TenantLoop : IDisposable
    {
        /// <summary>
        /// Loop cancellation source (linked from the supervisor's stoppingToken).
        /// </summary>
        private readonly CancellationTokenSource _cts;

        /// <summary>
        /// Creates a tenant loop registry entry.
        /// </summary>
        /// <param name="task">Tenant loop task.</param>
        /// <param name="cts">Loop cancellation source.</param>
        public TenantLoop(Task task, CancellationTokenSource cts)
        {
            Task = task;
            _cts = cts;
        }

        /// <summary>
        /// Per-tenant loop task.
        /// </summary>
        public Task Task { get; }

        /// <summary>
        /// Requests loop cancellation (without awaiting completion).
        /// </summary>
        /// <returns>Task representing completion of the cancellation request.</returns>
        public Task CancelAsync() => _cts.CancelAsync();

        /// <summary>
        /// Cancels the loop and awaits its completion (cooperative cancellation, absorbed normally by the wrapper).
        /// </summary>
        public async Task CancelAndAwaitAsync()
        {
            await _cts.CancelAsync();

            // The loop task is the supervisor's own work (started in StartLoop), awaiting is safe;
            // VSTHRD003 does not apply (not a "foreign" Task), cancellation absorption — in RunTenantLoopGuardedAsync.
#pragma warning disable VSTHRD003
            await Task;
#pragma warning restore VSTHRD003
        }

        /// <inheritdoc />
        public void Dispose() => _cts.Dispose();
    }
}
