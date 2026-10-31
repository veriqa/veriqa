// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Webhooks;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.Diagnostics;

/// <summary>
/// Health check reporting the state of every registered channel adapter. It asks each adapter the
/// question its own contract already answers — <see cref="IChannelAdapter.GetHealthAsync"/> — and
/// turns the answers into a single report that names every channel separately.
/// </summary>
/// <remarks>
/// <para>
/// The status this check produces never rises above <see cref="HealthStatus.Degraded"/>. An outage
/// on the channel platform's side says nothing about this replica, while the general health
/// endpoint aggregates every check without a tag filter and maps the failing status to HTTP 503 —
/// an unreachable Telegram would take a perfectly working instance out of rotation. Keeping the
/// ceiling inside the check makes that hold by construction, whichever endpoint the orchestrator
/// happens to probe.
/// </para>
/// <para>
/// The adapters are never probed from the request that reads the report. The same general health
/// endpoint is anonymous and exempt from rate limiting, so probing inline would tie two things to
/// the rate of incoming requests: the number of live calls made with the channel's production
/// credentials (an anonymous loop over the endpoint would walk a bot token into the platform's
/// 429 and take down the delivery of login codes), and the response time of the endpoint itself
/// (a channel that never answers would add its whole probe limit to a probe an orchestrator runs
/// with a shorter deadline, and a healthy replica would be restarted). Instead the check serves the
/// last outcome of every channel, and a read finding the last probe of a channel older than that
/// channel's interval (<see cref="ChannelHealthConfigKeys.HealthProbeInterval"/>) starts the next
/// probe of that channel alone in the background. The channels are probed independently of one
/// another: a platform is called at most once per its interval per replica, whoever asks and however
/// often — and, past the probes taken at host start-up, not at all while nobody asks: a read is the
/// only thing that moves a probe. The effective refresh interval of a channel is therefore the longer
/// of its interval and the interval the report is actually read at.
/// </para>
/// <para>
/// A probe is waited for no longer than the outer limit
/// (<see cref="ChannelHealthConfigKeys.ChannelProbeLimit"/>), on the system timer, and the limit holds
/// even against an adapter that ignores the cancellation it is handed. How long the channel is given
/// to answer inside that limit is the adapter's own business (SPEC-042 L44 for an HTTP channel). A
/// call left behind at the limit is never duplicated: the channel is not probed again until that call
/// has finished, and it is reported as timed out until then.
/// </para>
/// <para>
/// Which channel is down is answered by the per-channel report in
/// <see cref="HealthCheckResult.Data"/>; why it is down — by a warning in the log. The reason an
/// adapter hands back is free text (an exception message among other things), and free text from
/// an HTTP client may carry a URL with a token, so it never reaches an endpoint an operator reads
/// (SPEC-003 §18.3, CA-121).
/// </para>
/// <para>
/// A channel that runs in the Webhook mode and has no webhook registered yet is reported as unhealthy
/// whatever its probe answered: its platform may well answer, but the channel receives no updates. The
/// registration runs in the background with retries, and its failures are logged by the registration
/// itself.
/// </para>
/// </remarks>
internal sealed class ChannelAdapterHealthCheck : IHealthCheck, IHostedService, IDisposable
{
    /// <summary>
    /// Report value of a channel that answered it is healthy.
    /// </summary>
    private const string HealthyValue = "healthy";

    /// <summary>
    /// Report value of a channel that answered it is not healthy, or failed to answer at all.
    /// </summary>
    private const string UnhealthyValue = "unhealthy";

    /// <summary>
    /// Report value of a channel that did not answer within the probe limit.
    /// </summary>
    private const string TimeoutValue = "timeout";

    /// <summary>
    /// Suffix of the per-channel report key carrying the probe response time in milliseconds.
    /// </summary>
    private const string ResponseTimeKeySuffix = ".response_time_ms";

    /// <summary>
    /// Probe state of every registered channel adapter; empty on a deployment with no channel enabled.
    /// </summary>
    private readonly ChannelProbeState[] _channels;

    /// <summary>
    /// Canonical resolver the probe limit and the probe interval of a channel are read through.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Clock of the channel contour, used to measure the probes this check times itself and to age
    /// the outcomes it serves. The probe limit does not run on it: it runs on the system timer.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger the reason of an unhealthy channel is written to.
    /// </summary>
    private readonly ILogger<ChannelAdapterHealthCheck> _logger;

    /// <summary>
    /// Which channels still have no webhook registered.
    /// </summary>
    private readonly ChannelWebhookRegistrationState _webhooks;

    /// <summary>
    /// Cancelled when the host stops, so that a probe in flight does not outlive it.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Guards the mutable state of every <see cref="ChannelProbeState"/>: only one probe of a channel
    /// runs at a time, however many requests find its outcome stale at once.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Creates the channel health check.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="resolver">Canonical resolver of the probe settings.</param>
    /// <param name="timeProvider">Clock of the channel contour.</param>
    /// <param name="logger">Logger for the reason of an unhealthy channel.</param>
    /// <param name="webhooks">Which channels still have no webhook registered.</param>
    public ChannelAdapterHealthCheck(
        IEnumerable<IChannelAdapter> adapters,
        IConfigurationResolver resolver,
        TimeProvider timeProvider,
        ILogger<ChannelAdapterHealthCheck> logger,
        ChannelWebhookRegistrationState webhooks)
    {
        _channels = adapters.Select(adapter => new ChannelProbeState(adapter)).ToArray();
        _resolver = resolver;
        _timeProvider = timeProvider;
        _logger = logger;
        _webhooks = webhooks;
    }

    /// <summary>
    /// Reports the channel states from the last outcome of every channel, starting in the background
    /// a new probe of each channel whose outcome has aged out. Nothing here waits for a channel: the
    /// report is read by an anonymous endpoint whose latency and call rate must not follow the request
    /// rate.
    /// </summary>
    /// <param name="context">Health check context supplied by the health check service.</param>
    /// <param name="cancellationToken">
    /// Cancellation token of the probe. Unused: the report is built from memory and completes
    /// synchronously, so there is nothing to cancel.
    /// </param>
    /// <returns>Report of the channel states.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var probes = new ChannelProbe[_channels.Length];

        lock (_gate)
        {
            for (var index = 0; index < _channels.Length; index++)
            {
                var channel = _channels[index];

                StartProbeIfDue(channel);

                // Until the first probe of a channel completes, the channel counts as one that has not
                // answered — the very meaning of the unhealthy value here. Calling a channel healthy
                // before anything asked it is the one answer this check must never give; the window is
                // the first probe of the process, and that probe is already started at host start-up.
                var probe = channel.Snapshot?.Probe ?? NotAnsweredYet(channel.Adapter);

                // Read at report time rather than at probe time: a registration that succeeds between
                // two probes shows up at once.
                probes[index] = _webhooks.IsPending(channel.Adapter.ChannelType)
                    ? probe with { Outcome = ChannelProbeOutcome.Unhealthy }
                    : probe;
            }
        }

        return Task.FromResult(BuildReport(probes));
    }

    /// <summary>
    /// Takes the first probe of every channel at host start-up, so that the first reader of the report
    /// already finds channel states rather than the not-answered-yet placeholder.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token of the host start-up.</param>
    /// <returns>Completed task: the probes run in the background.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var channel in _channels)
            {
                StartProbeIfDue(channel);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the probes in flight and waits for them, so that no probe outlives the host — but no
    /// longer than the shutdown itself is allowed to take.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token of the host shutdown.</param>
    /// <returns>
    /// Task that completes once the probes in flight have finished, or once the shutdown deadline has
    /// passed — whichever comes first.
    /// </returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();

        Task[] probes;

        lock (_gate)
        {
            probes = _channels
                .Select(channel => channel.ProbeTask)
                .OfType<Task>()
                .ToArray();
        }

        try
        {
            // The wait observes the shutdown deadline of the host. A probe stops waiting for its adapter
            // on the cancellation the line above causes, even when the adapter ignores the token; the
            // probes swallow that cancellation and log their own faults.
            await Task.WhenAll(probes).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown ran out of its own deadline first. The probes are left behind rather than waited
            // out: a probe cancelled by the shutdown publishes nothing.
            _logger.LogDebug("Channel health probes outlived the shutdown deadline and were left behind");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    /// <summary>
    /// Turns the outcomes of the channels into the health report.
    /// </summary>
    /// <param name="probes">Outcomes, one per registered channel.</param>
    /// <returns>Report of the channel states.</returns>
    private static HealthCheckResult BuildReport(ChannelProbe[] probes)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var healthy = 0;

        foreach (var probe in probes)
        {
            // The report value comes from the closed set of constants above and never from a string
            // the adapter handed back — that is what keeps a reason out of the response body.
            switch (probe.Outcome)
            {
                case ChannelProbeOutcome.Healthy:
                    data[probe.ChannelType] = HealthyValue;
                    healthy++;
                    break;

                case ChannelProbeOutcome.Timeout:
                    data[probe.ChannelType] = TimeoutValue;
                    break;

                default:
                    data[probe.ChannelType] = UnhealthyValue;
                    break;
            }

            data[probe.ChannelType + ResponseTimeKeySuffix] = probe.ResponseTimeMs;
        }

        var description = $"{healthy}/{probes.Length} channels healthy";

        // Every enabled channel healthy — Healthy; anything else, the case of no healthy channel at
        // all included, — Degraded. The two are told apart by the per-channel report rather than by
        // the status: the ceiling above forbids anything worse.
        // No channel enabled at all is a healthy "0/0": that deployment is already covered by the
        // start-up warning of the channel contour (CA-110) and needs no second signal.
        return healthy == probes.Length
            ? HealthCheckResult.Healthy(description, data)
            : HealthCheckResult.Degraded(description, data: data);
    }

    /// <summary>
    /// Placeholder outcome used before the first real probe of a channel completes: the channel is
    /// reported as one that has not answered.
    /// </summary>
    /// <param name="adapter">Adapter of the channel.</param>
    /// <returns>Outcome of the channel, not healthy.</returns>
    private static ChannelProbe NotAnsweredYet(IChannelAdapter adapter) =>
        new(adapter.ChannelType, ChannelProbeOutcome.Unhealthy, ResponseTimeMs: 0);

    /// <summary>
    /// Starts a probe of the channel unless one is already running, the adapter call of the previous
    /// probe has not finished yet, the last outcome is still younger than the channel's interval or the
    /// host is stopping. Called under <see cref="_gate"/>.
    /// </summary>
    /// <param name="channel">Channel to probe.</param>
    private void StartProbeIfDue(ChannelProbeState channel)
    {
        // A call left behind at the limit is still a call to the platform: a second one beside it would
        // double the load on a channel that is already not answering.
        if (_stopping.IsCancellationRequested
            || channel.ProbeTask is { IsCompleted: false }
            || channel.AdapterCall is { IsCompleted: false })
        {
            return;
        }

        if (channel.Snapshot is { } snapshot && _timeProvider.GetElapsedTime(snapshot.TakenAt) < snapshot.Interval)
        {
            return;
        }

        // Detached from whatever asked for it: the caller is a request that must not wait for a
        // channel, and its own cancellation token dies with the response.
        channel.ProbeTask = Task.Run(() => RefreshAsync(channel), CancellationToken.None);
    }

    /// <summary>
    /// Probes a single channel under the probe limit and publishes the outcome as its new snapshot,
    /// together with the interval the next probe of the channel is due after.
    /// </summary>
    /// <param name="channel">Channel to probe.</param>
    /// <returns>Task that completes once the probe has finished.</returns>
    private async Task RefreshAsync(ChannelProbeState channel)
    {
        try
        {
            var stopping = _stopping.Token;

            // Resolved on every probe rather than once: the resolver answers from its own cache, and a
            // probe runs at most once per interval.
            var interval = await _resolver.ResolveAsync(
                ChannelHealthConfigKeys.HealthProbeInterval,
                ResolutionContext.Core,
                channel.Dimension,
                stopping);

            var limit = await _resolver.ResolveAsync(
                ChannelHealthConfigKeys.ChannelProbeLimit,
                ResolutionContext.Core,
                ConfigDimensionValues.None,
                stopping);

            var probe = await ProbeAsync(channel, limit.Value, stopping);

            lock (_gate)
            {
                // A probe the shutdown overtook publishes nothing — the previous outcome is left as it
                // is: a channel whose answer was not waited for is not a channel down.
                if (!stopping.IsCancellationRequested)
                {
                    channel.Snapshot = new ChannelHealthSnapshot(probe, _timeProvider.GetTimestamp(), interval.Value);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug("Channel {ChannelType} health probe abandoned: the host is stopping", channel.Adapter.ChannelType);
        }
        catch (Exception exception)
        {
            // Resolving the probe settings is outside the safety net of the adapter call; a failure there
            // leaves the previous outcome in place, and only the exception type reaches the log.
            _logger.LogWarning(
                "Channel {ChannelType} health probe failed with {ExceptionType}",
                channel.Adapter.ChannelType,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Probes a single adapter under the probe limit and classifies the answer. The wait is bounded on
    /// its own: it ends when the limit passes or the host stops, whether or not the adapter observes the
    /// cancellation it is handed. Cancellation by the shutdown is not classified — it is propagated, so
    /// a cancelled probe publishes nothing.
    /// </summary>
    /// <param name="channel">Channel to probe.</param>
    /// <param name="limit">Outer limit of the probe, on the system timer.</param>
    /// <param name="stopping">Cancellation token of the host shutdown.</param>
    /// <returns>Outcome of the single channel.</returns>
    private async Task<ChannelProbe> ProbeAsync(
        ChannelProbeState channel,
        TimeSpan limit,
        CancellationToken stopping)
    {
        var adapter = channel.Adapter;
        var startedAt = _timeProvider.GetTimestamp();

        // The adapter is told to give up when the limit passes. The source lives as long as the call
        // does rather than as long as this wait: a call left behind at the limit may still hold its token.
        var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        callCancellation.CancelAfter(limit);
        var callToken = callCancellation.Token;

        // Started on the thread pool: an adapter blocking before its first await would otherwise hold
        // this very wait, and the limit would not hold.
        var call = Task.Run(() => adapter.GetHealthAsync(callToken), CancellationToken.None);

        _ = call.ContinueWith(
            static (_, source) => ((CancellationTokenSource)source!).Dispose(),
            callCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        lock (_gate)
        {
            channel.AdapterCall = call;
        }

        try
        {
            var health = await call.WaitAsync(limit, stopping);

            return Classify(adapter, health, startedAt);
        }
        catch (TimeoutException) when (!call.IsFaulted)
        {
            // The limit passed. A timeout the adapter threw itself faults the call and is classified by
            // the safety net below, with its type.
            return TimedOut(adapter, limit, startedAt);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            // Not the shutdown: the adapter gave up on the cancellation it was handed at the limit, or
            // on a timeout of its own inside the limit.
            return TimedOut(adapter, limit, startedAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Safety net for a third-party adapter: the shipped ones catch their own exceptions and
            // answer with an unhealthy status instead. Only the exception type reaches the log.
            _logger.LogWarning(
                "Channel {ChannelType} health probe threw {ExceptionType}",
                adapter.ChannelType,
                exception.GetType().Name);

            return new ChannelProbe(adapter.ChannelType, ChannelProbeOutcome.Unhealthy, Elapsed(startedAt));
        }
    }

    /// <summary>
    /// Classifies the answer an adapter gave within the limit.
    /// </summary>
    /// <param name="adapter">Adapter that answered.</param>
    /// <param name="health">Its answer.</param>
    /// <param name="startedAt">Timestamp taken before the probe.</param>
    /// <returns>Outcome of the single channel.</returns>
    private ChannelProbe Classify(IChannelAdapter adapter, Result<ChannelHealthStatus> health, long startedAt)
    {
        if (health.IsFailure)
        {
            _logger.LogWarning("Channel {ChannelType} health probe failed with error code {ErrorCode}", adapter.ChannelType, health.Error.Code);

            return new ChannelProbe(adapter.ChannelType, ChannelProbeOutcome.Unhealthy, Elapsed(startedAt));
        }

        var status = health.Value;
        var responseTimeMs = (long)status.ResponseTime.TotalMilliseconds;

        if (!status.IsHealthy)
        {
            _logger.LogWarning("Channel {ChannelType} reported itself unhealthy: {Reason}", adapter.ChannelType, status.Details);

            return new ChannelProbe(adapter.ChannelType, ChannelProbeOutcome.Unhealthy, responseTimeMs);
        }

        return new ChannelProbe(adapter.ChannelType, ChannelProbeOutcome.Healthy, responseTimeMs);
    }

    /// <summary>
    /// Outcome of a channel that did not answer within the probe limit.
    /// </summary>
    /// <param name="adapter">Adapter that did not answer.</param>
    /// <param name="limit">The limit in effect.</param>
    /// <param name="startedAt">Timestamp taken before the probe.</param>
    /// <returns>Timed-out outcome of the single channel.</returns>
    private ChannelProbe TimedOut(IChannelAdapter adapter, TimeSpan limit, long startedAt)
    {
        _logger.LogWarning(
            "Channel {ChannelType} health probe did not answer within the probe limit of {ProbeLimitSeconds} seconds",
            adapter.ChannelType,
            limit.TotalSeconds);

        return new ChannelProbe(adapter.ChannelType, ChannelProbeOutcome.Timeout, Elapsed(startedAt));
    }

    /// <summary>
    /// Milliseconds elapsed since the timestamp, for the branches where no adapter-reported
    /// response time exists.
    /// </summary>
    /// <param name="startedAt">Timestamp taken before the probe.</param>
    /// <returns>Elapsed time in whole milliseconds.</returns>
    private long Elapsed(long startedAt) =>
        (long)_timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;

    /// <summary>
    /// How a single channel answered its probe.
    /// </summary>
    private enum ChannelProbeOutcome
    {
        /// <summary>The channel answered it is healthy.</summary>
        Healthy,

        /// <summary>The channel answered it is not healthy, or failed to answer.</summary>
        Unhealthy,

        /// <summary>The channel did not answer within the probe limit.</summary>
        Timeout
    }

    /// <summary>
    /// Outcome of a single channel probe.
    /// </summary>
    /// <param name="ChannelType">Channel type the outcome belongs to.</param>
    /// <param name="Outcome">How the channel answered.</param>
    /// <param name="ResponseTimeMs">Response time in whole milliseconds.</param>
    private readonly record struct ChannelProbe(
        string ChannelType,
        ChannelProbeOutcome Outcome,
        long ResponseTimeMs);

    /// <summary>
    /// A completed probe of one channel together with the moment it finished and the interval the next
    /// probe of the channel is due after.
    /// </summary>
    /// <param name="Probe">Outcome of the probe.</param>
    /// <param name="TakenAt">Timestamp of the clock taken when the probe finished.</param>
    /// <param name="Interval">Probe interval of the channel in effect when the probe was taken.</param>
    private sealed record ChannelHealthSnapshot(ChannelProbe Probe, long TakenAt, TimeSpan Interval);

    /// <summary>
    /// Probe state of one channel. Every member but the adapter and its dimension is read and written
    /// under <see cref="_gate"/>.
    /// </summary>
    private sealed class ChannelProbeState
    {
        /// <summary>
        /// Creates the probe state of a channel that has not been probed yet.
        /// </summary>
        /// <param name="adapter">Adapter of the channel.</param>
        public ChannelProbeState(IChannelAdapter adapter)
        {
            Adapter = adapter;
            Dimension = ConfigDimensionValues.Of((ConfigDimensionNames.Channel, adapter.ChannelType));
        }

        /// <summary>
        /// Adapter of the channel.
        /// </summary>
        public IChannelAdapter Adapter { get; }

        /// <summary>
        /// Channel dimension the probe interval of this channel is resolved for.
        /// </summary>
        public ConfigDimensionValues Dimension { get; }

        /// <summary>
        /// Last published outcome; null until the first probe completes.
        /// </summary>
        public ChannelHealthSnapshot? Snapshot { get; set; }

        /// <summary>
        /// The probe in flight or the last one finished; null until the first one starts.
        /// </summary>
        public Task? ProbeTask { get; set; }

        /// <summary>
        /// Adapter call of the last probe. It may outlive its probe: the probe stops waiting at the
        /// limit, and an adapter that ignores its token goes on regardless.
        /// </summary>
        public Task? AdapterCall { get; set; }
    }
}
