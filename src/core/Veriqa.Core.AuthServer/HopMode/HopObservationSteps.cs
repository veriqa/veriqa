// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Steps 4–5 of the resolve endpoint (SPEC-025 §7.2) — the observations of an admitted presentation made
/// by a person: the mark of the first resolve, then, by its outcome, the snapshot of the scanning device
/// and the intermediate status <see cref="HopChannelStatusKeys.Scanned"/>.
/// <para>
/// The mark is the one anchor of the once-only observations (HOP-071a, HOP-080): only the call that wrote
/// it captures the context and hands its browser the browser mark, and "it has already happened" is known
/// from the outcome of that conditional write alone, never from a read of the transaction. Every step is
/// best-effort (HOP-064): a failure is logged, and the answer of the endpoint does not change.
/// </para>
/// <para>
/// The status <see cref="HopChannelStatusKeys.Scanned"/> is not once-only: the call that wrote the mark
/// publishes it, and so does every later resolve of a person, so that a sign-in window that went back to
/// the choice of a way returns to the hop when the same phone scans the code again (HOP-188, HOP-189). The
/// one exception is the call that published <see cref="HopChannelStatusKeys.Reopened"/>: that code brings
/// the window to the hop by itself.
/// </para>
/// <para>
/// A reopening of the link elsewhere (SPEC-041 L93) is decided by the conditional write as well: a later
/// call that finds the first mark taken and no valid browser mark on its request writes the mark of the
/// reopening once; only the call that wrote it publishes <see cref="HopChannelStatusKeys.Reopened"/>.
/// </para>
/// </summary>
internal sealed class HopObservationSteps
{
    /// <summary>
    /// Reason of a skip: the presentation was made by a link-preview agent (SPEC-025 HOP-072).
    /// </summary>
    private const string PreviewSkipReason = "preview";

    /// <summary>
    /// Reason of a skip: the first resolve of the link is already marked (SPEC-025 HOP-071a).
    /// </summary>
    private const string AlreadyResolvedSkipReason = "already_resolved";

    /// <summary>
    /// Name of the step reading the observation settings, for the log entry of its failure.
    /// </summary>
    private const string SettingsStep = "settings";

    /// <summary>
    /// Name of the step writing the first-resolve mark, for the log entry of its failure.
    /// </summary>
    private const string FirstResolveMarkStep = "first_resolve_mark";

    /// <summary>
    /// Name of the step attaching the scanner context, for the log entry of its failure.
    /// </summary>
    private const string ScannerContextStep = "scanner_context_attach";

    /// <summary>
    /// Name of the step handing the browser its mark, for the log entry of its failure.
    /// </summary>
    private const string BrowserMarkStep = "browser_mark_issue";

    /// <summary>
    /// Name of the step writing the mark of a reopening elsewhere, for the log entry of its failure.
    /// </summary>
    private const string RepeatResolveMarkStep = "repeat_resolve_mark";

    /// <summary>
    /// Template of the log entry of a failed step.
    /// </summary>
    private const string StepFailedLogTemplate =
        "A hop observation step failed; the answer of the resolve endpoint is not affected. Step: {Step}, "
        + "TransactionId: {TransactionId}";

    /// <summary>
    /// Store the observations are written to — by its one-time conditional writes.
    /// </summary>
    private readonly ITransactionStore _transactionStore;

    /// <summary>
    /// The reading of the effective hop settings.
    /// </summary>
    private readonly HopModeSettings _hopModeSettings;

    /// <summary>
    /// Collector of the scanning-device context.
    /// </summary>
    private readonly ScannerContextCollector _scannerContextCollector;

    /// <summary>
    /// Publisher of the intermediate statuses of the hop family.
    /// </summary>
    private readonly HopChannelStatusPublisher _statusPublisher;

    /// <summary>
    /// The browser mark of the link.
    /// </summary>
    private readonly HopBrowserMark _browserMark;

    /// <summary>
    /// Clock the moment of the mark is taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HopObservationSteps> _logger;

    /// <summary>
    /// Creates the steps.
    /// </summary>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="hopModeSettings">The reading of the effective hop settings.</param>
    /// <param name="scannerContextCollector">Collector of the scanning-device context.</param>
    /// <param name="statusPublisher">Publisher of the intermediate statuses of the hop family.</param>
    /// <param name="browserMark">The browser mark of the link.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public HopObservationSteps(
        ITransactionStore transactionStore,
        HopModeSettings hopModeSettings,
        ScannerContextCollector scannerContextCollector,
        HopChannelStatusPublisher statusPublisher,
        HopBrowserMark browserMark,
        TimeProvider timeProvider,
        ILogger<HopObservationSteps> logger)
    {
        ArgumentNullException.ThrowIfNull(transactionStore);
        ArgumentNullException.ThrowIfNull(hopModeSettings);
        ArgumentNullException.ThrowIfNull(scannerContextCollector);
        ArgumentNullException.ThrowIfNull(statusPublisher);
        ArgumentNullException.ThrowIfNull(browserMark);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _transactionStore = transactionStore;
        _hopModeSettings = hopModeSettings;
        _scannerContextCollector = scannerContextCollector;
        _statusPublisher = statusPublisher;
        _browserMark = browserMark;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs steps 4–5 for an admitted presentation. A preview is not observed; a person's presentation
    /// marks the first resolve and, if this call wrote the mark, hands the browser its mark, captures the
    /// scanner context and publishes <see cref="HopChannelStatusKeys.Scanned"/>; if the mark was already
    /// there, a presentation from a browser without a valid mark marks the reopening and, if this call wrote
    /// that mark, publishes <see cref="HopChannelStatusKeys.Reopened"/>, and any other repeated presentation
    /// publishes <see cref="HopChannelStatusKeys.Scanned"/> again.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="requestKind">Who presented the URL (step 1).</param>
    /// <param name="outcome">Outcome of the check of steps 2–3; the transition is admitted.</param>
    /// <returns>
    /// The observation settings the presentation was observed under — the one reading of them per request,
    /// which the launch page reuses to decide its client signals (SPEC-025 HOP-066c); null — a preview, or
    /// the settings could not be read.
    /// </returns>
    /// <exception cref="ArgumentException">The outcome is not an admitted presentation.</exception>
    public async Task<HopObservationSettings?> ObserveAsync(
        HttpContext httpContext,
        HopRequestKind requestKind,
        HopPresentationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome is not { Refusal: null, Transaction: { } transaction, Token: { } token })
        {
            throw new ArgumentException("Only an admitted presentation is observed.", nameof(outcome));
        }

        // A preview never marks the link and never tells the desktop it was scanned (SPEC-025 HOP-072).
        if (requestKind is HopRequestKind.Preview)
        {
            LogSkipped(transaction, PreviewSkipReason);
            return null;
        }

        // The settings are read before the mark: when they cannot be read, the link stays unmarked and the
        // next presentation runs the steps again.
        var settings = await ReadSettingsAsync(transaction, httpContext.RequestAborted);
        if (settings is null)
        {
            return null;
        }

        // Step 4. The mark goes first and depends on neither CollectScannerContext nor EmitScannedStatus
        // (HOP-071a). From here on nothing is cancelled with the request: the anchor gives no second
        // attempt, so an abort after the mark would lose the snapshot for good and the status until the next
        // resolve of a person (HOP-189).
        var resolvedAt = _timeProvider.GetUtcNow();
        if (await TryMarkFirstResolveAsync(transaction, resolvedAt) is not { } marked)
        {
            return settings;
        }

        if (!marked)
        {
            LogSkipped(transaction, AlreadyResolvedSkipReason);

            // A repeated scan by a person tells the window again that the code was scanned, unless this very
            // call already told it of a reopening, which brings the window to the hop by itself.
            if (!await ObserveRepeatResolveAsync(httpContext, transaction, token, settings))
            {
                await _statusPublisher.PublishAsync(
                    transaction, token.ChannelType, HopChannelStatusKeys.Scanned, settings);
            }

            return settings;
        }

        // The browser gets its mark before the answer is written, whatever its form — a 302, the launch page
        // or the page of the channel buttons: it adds a Set-Cookie and changes nothing else of the answer.
        if (settings.WarnOnRepeatScan)
        {
            IssueBrowserMark(httpContext, transaction, resolvedAt);
        }

        if (_scannerContextCollector.Collect(httpContext, settings, resolvedAt) is { } snapshot)
        {
            await AttachScannerContextAsync(transaction, snapshot);
        }

        // Step 5. The channel type is that of the token as it is: a token naming no channel publishes the
        // status without one.
        await _statusPublisher.PublishAsync(transaction, token.ChannelType, HopChannelStatusKeys.Scanned, settings);

        return settings;
    }

    /// <summary>
    /// Reads what the hop mode captures and publishes for the transaction — through the canonical
    /// resolver, in the ownership context of the transaction.
    /// </summary>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="cancellationToken">Cancellation token of the request: nothing is written yet.</param>
    /// <returns>The settings; null — they could not be read.</returns>
    private async Task<HopObservationSettings?> ReadSettingsAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            return await _hopModeSettings.ResolveObservationAsync(
                TransactionResolutionContext.For(transaction),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogStepFailed(ex, transaction, SettingsStep);
            return null;
        }
    }

    /// <summary>
    /// Writes the first-resolve mark once, by the first writer (SPEC-025 HOP-071a).
    /// </summary>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="resolvedAt">Moment the mark carries.</param>
    /// <returns>
    /// true — this call wrote the mark; false — the mark is already there or the transaction is gone;
    /// null — the write failed.
    /// </returns>
    private async Task<bool?> TryMarkFirstResolveAsync(Transaction transaction, DateTimeOffset resolvedAt)
    {
        try
        {
            return await _transactionStore.TryMarkHopObservationAsync(
                transaction.Id,
                HopObservationMark.FirstResolve,
                resolvedAt,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogStepFailed(ex, transaction, FirstResolveMarkStep);
            return null;
        }
    }

    /// <summary>
    /// Observes a person's presentation of a link whose first resolve is already marked (SPEC-041 L93): from
    /// a browser without a valid mark of the transaction it is a reopening elsewhere, marked once; the call
    /// that wrote the mark publishes <see cref="HopChannelStatusKeys.Reopened"/>. Whether it is a reopening
    /// is known from the mark of the request and the outcome of the conditional write alone, never from a
    /// read of the transaction.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="token">The presented token — the channel type the status carries.</param>
    /// <param name="settings">Observation settings of the act.</param>
    /// <returns>
    /// true — this call wrote the mark of the reopening and published <see cref="HopChannelStatusKeys.Reopened"/>;
    /// false — it is no reopening, or not the first one.
    /// </returns>
    private async Task<bool> ObserveRepeatResolveAsync(
        HttpContext httpContext,
        Transaction transaction,
        HopToken token,
        HopObservationSettings settings)
    {
        if (!settings.WarnOnRepeatScan || _browserMark.IsPresentFor(httpContext.Request, transaction))
        {
            return false;
        }

        if (!await TryMarkRepeatResolveAsync(transaction, _timeProvider.GetUtcNow()))
        {
            return false;
        }

        await _statusPublisher.PublishAsync(transaction, token.ChannelType, HopChannelStatusKeys.Reopened, settings);
        return true;
    }

    /// <summary>
    /// Writes the mark of a reopening elsewhere once, by the first writer (SPEC-041 L93).
    /// </summary>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="reopenedAt">Moment the mark carries.</param>
    /// <returns>true — this call wrote the mark; false — it is already there, the transaction is gone or the write failed.</returns>
    private async Task<bool> TryMarkRepeatResolveAsync(Transaction transaction, DateTimeOffset reopenedAt)
    {
        try
        {
            return await _transactionStore.TryMarkHopObservationAsync(
                transaction.Id,
                HopObservationMark.RepeatResolve,
                reopenedAt,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogStepFailed(ex, transaction, RepeatResolveMarkStep);
            return false;
        }
    }

    /// <summary>
    /// Hands the browser of the request the mark of the transaction; a failure leaves the answer as it is.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="resolvedAt">Moment of the resolve the remaining lifetime of the mark is counted from.</param>
    private void IssueBrowserMark(HttpContext httpContext, Transaction transaction, DateTimeOffset resolvedAt)
    {
        try
        {
            _browserMark.Issue(httpContext, transaction, resolvedAt);
        }
        catch (Exception ex)
        {
            LogStepFailed(ex, transaction, BrowserMarkStep);
        }
    }

    /// <summary>
    /// Attaches the scanner context to the transaction once, by the first writer (SPEC-025 HOP-080).
    /// </summary>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="snapshot">The snapshot, without a fingerprint.</param>
    /// <returns>The completed operation.</returns>
    private async Task AttachScannerContextAsync(Transaction transaction, ScannerContextSnapshot snapshot)
    {
        try
        {
            await _transactionStore.TryAttachScannerContextAsync(transaction.Id, snapshot, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogStepFailed(ex, transaction, ScannerContextStep);
        }
    }

    /// <summary>
    /// Logs a presentation whose scanner context is not captured (SPEC-025 §14).
    /// </summary>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="reason">Why the context is not captured.</param>
    private void LogSkipped(Transaction transaction, string reason) =>
        _logger.LogInformation(
            "The scanner context is not captured. ResultCode: {ResultCode}, Reason: {Reason}, TransactionId: {TransactionId}",
            HopModeResultCodes.ScannerContextSkipped,
            reason,
            transaction.Id.ToString());

    /// <summary>
    /// Logs a failed step. transaction.Id.ToString() is Base62 — no log injection.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="step">Name of the failed step.</param>
    private void LogStepFailed(Exception exception, Transaction transaction, string step) =>
        _logger.LogWarning(exception, StepFailedLogTemplate, step, transaction.Id.ToString());
}
