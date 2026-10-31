// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Veriqa.Core.ChannelAdapter.Webhooks;

/// <summary>
/// Registers the webhook of a channel's default-tenant bot at the platform in the background and removes
/// it on shutdown. The mechanics are shared by the channels that register their webhook themselves; only
/// the platform calls differ (<see cref="RegisterAsync"/>, <see cref="UnregisterAsync"/>).
/// </summary>
/// <remarks>
/// The first attempt waits until the host has started and listens: a platform may check the address it is
/// handed by calling it before it answers (Viber does), and none can deliver to a host that does not listen
/// yet. The registration never blocks the start of the host and never takes it down: an unreachable platform
/// would otherwise keep the issuer — and with it the sign-in in every other channel — down for as long as
/// the platform is. Each attempt is bounded by its own timeout instead of the 100 seconds an
/// <see cref="HttpClient"/> waits by default; a failed attempt is logged and retried with an exponential
/// backoff until it succeeds or the host stops. Until then the channel health check reports the channel
/// as unhealthy through <see cref="ChannelWebhookRegistrationState"/>.
/// </remarks>
internal abstract class ChannelWebhookRegistrationService : BackgroundService
{
    /// <summary>
    /// How long a single platform call — a registration attempt or the removal on shutdown — may take.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Pause after the first failed attempt; doubled after every next failure.
    /// </summary>
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upper bound of the pause between attempts.
    /// </summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Registration state the channel health check reads.
    /// </summary>
    private readonly ChannelWebhookRegistrationState _state;

    /// <summary>
    /// Clock the pauses between attempts run on.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Lifetime of the host; the first attempt waits for its start.
    /// </summary>
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Set once the platform accepted the webhook; only then is it removed on shutdown.
    /// </summary>
    private volatile bool _registered;

    /// <summary>
    /// Creates the base registration service.
    /// </summary>
    /// <param name="state">Registration state the channel health check reads.</param>
    /// <param name="timeProvider">Clock the pauses between attempts run on.</param>
    /// <param name="lifetime">Lifetime of the host; the first attempt waits for its start.</param>
    /// <param name="logger">Logger.</param>
    protected ChannelWebhookRegistrationService(
        ChannelWebhookRegistrationState state,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Channel type the webhook belongs to.
    /// </summary>
    protected abstract string ChannelType { get; }

    /// <summary>
    /// Whether the channel receives its updates through the webhook; otherwise nothing is registered.
    /// </summary>
    protected abstract bool IsWebhookMode { get; }

    /// <summary>
    /// Full webhook address, for the log.
    /// </summary>
    protected abstract string WebhookUrl { get; }

    /// <summary>
    /// Stops the registration attempts and removes the webhook if the platform accepted it. A failure of
    /// the removal is logged and does not break the shutdown of the host.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token of the host shutdown.</param>
    /// <returns>A task that completes when the removal has finished.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (!_registered)
        {
            return;
        }

        // Not linked to the shutdown token: it may already be cancelled, and the removal still deserves
        // its own attempt — bounded, so an unreachable platform does not hold the shutdown.
        using var attempt = new CancellationTokenSource(AttemptTimeout);

        try
        {
            await UnregisterAsync(attempt.Token);

            _logger.LogInformation("{ChannelType} webhook removed: {WebhookUrl}", ChannelType, WebhookUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to remove the {ChannelType} webhook on service shutdown: {WebhookUrl}",
                ChannelType,
                WebhookUrl);
        }
    }

    /// <summary>
    /// Registers the webhook at the platform.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token of the attempt.</param>
    /// <returns>A task that completes when the platform has accepted the webhook.</returns>
    protected abstract Task RegisterAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes the registered webhook from the platform.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token of the removal.</param>
    /// <returns>A task that completes when the platform has removed the webhook.</returns>
    protected abstract Task UnregisterAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the host to start, then attempts the registration until it succeeds or the host stops,
    /// pausing between attempts with an exponential backoff. Nothing escapes: an exception leaving a
    /// background service stops the host.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token of the host shutdown.</param>
    /// <returns>A task that completes once the webhook is registered or the host stops.</returns>
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsWebhookMode)
        {
            _logger.LogInformation(
                "{ChannelType} does not run in the Webhook mode, webhook registration skipped",
                ChannelType);

            return;
        }

        _state.MarkPending(ChannelType);

        if (!await WaitForHostStartAsync(stoppingToken))
        {
            return;
        }

        var retryDelay = InitialRetryDelay;

        for (var attempt = 1; ; attempt++)
        {
            if (await TryRegisterAsync(attempt, retryDelay, stoppingToken))
            {
                return;
            }

            try
            {
                await Task.Delay(retryDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryDelay = retryDelay * 2 < MaxRetryDelay ? retryDelay * 2 : MaxRetryDelay;
        }
    }

    /// <summary>
    /// Waits until the host has started, or until it stops.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token of the host shutdown.</param>
    /// <returns><c>true</c> once the host has started; <c>false</c> when it stops first.</returns>
    private async Task<bool> WaitForHostStartAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Fires at once when the host has already started.
        await using var registration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());

        try
        {
            await started.Task.WaitAsync(stoppingToken);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes one registration attempt under <see cref="AttemptTimeout"/>.
    /// </summary>
    /// <param name="attempt">Number of the attempt, from one.</param>
    /// <param name="retryDelay">Pause before the next attempt, for the log.</param>
    /// <param name="stoppingToken">Cancellation token of the host shutdown.</param>
    /// <returns><c>true</c> when the platform accepted the webhook.</returns>
    private async Task<bool> TryRegisterAsync(int attempt, TimeSpan retryDelay, CancellationToken stoppingToken)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attemptCancellation.CancelAfter(AttemptTimeout);

        try
        {
            await RegisterAsync(attemptCancellation.Token);

            _registered = true;
            _state.MarkRegistered(ChannelType);

            _logger.LogInformation(
                "{ChannelType} webhook registered: {WebhookUrl} (attempt {Attempt})",
                ChannelType,
                WebhookUrl,
                attempt);

            return true;
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping: whatever the call ended with, it is not a failure of the platform.
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "{ChannelType} webhook registration attempt {Attempt} did not complete within {AttemptTimeoutSeconds} seconds: {WebhookUrl}. The channel stays unhealthy; next attempt in {RetryDelaySeconds} seconds",
                ChannelType,
                attempt,
                AttemptTimeout.TotalSeconds,
                WebhookUrl,
                retryDelay.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{ChannelType} webhook registration attempt {Attempt} failed with {ExceptionType}: {WebhookUrl}. The channel stays unhealthy; next attempt in {RetryDelaySeconds} seconds",
                ChannelType,
                attempt,
                ex.GetType().Name,
                WebhookUrl,
                retryDelay.TotalSeconds);
        }

        return false;
    }
}
