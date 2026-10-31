// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Decides on a client signal of the launch page (SPEC-025 HOP-066b, HOP-190) or, with the unified token,
/// of the scanner page (HOP-156, HOP-191), and makes the records of an accepted one. The token is
/// checked first, then the body, then the conditions of acceptance; the first that fails is the rejection.
/// <para>
/// The receiver is not a presentation (SPEC-025 HOP-060): it neither tells a person from a preview nor
/// asks the channel for a target. A signal is an observation, not a confirmation: nothing it does changes
/// the state of the transaction, its concurrency token or the issuance of tokens (HOP-063, HOP-131), and
/// neither its log nor its status says that the launch of the channel succeeded or failed (HOP-066a).
/// Once-only rests on the outcome of the conditional writes alone, never on a read of the transaction.
/// </para>
/// </summary>
internal sealed class HopSignalReceiver
{
    /// <summary>
    /// Name of the step writing the mark of a signal, for the log entry of its failure.
    /// </summary>
    private const string MarkStep = "signal_mark";

    /// <summary>
    /// Name of the step filling the fingerprint of the scanner context, for the log entry of its failure.
    /// </summary>
    private const string FingerprintStep = "fingerprint_fill";

    /// <summary>
    /// Template of the log entry of a rejected signal.
    /// </summary>
    private const string RejectedLogTemplate =
        "The hop signal receiver rejected a client signal. ResultCode: {ResultCode}, Reason: {Reason}, "
        + "TransactionId: {TransactionId}, Signal: {Signal}";

    /// <summary>
    /// Template of the log entry of a failed step.
    /// </summary>
    private const string StepFailedLogTemplate =
        "A hop client signal step failed; the answer of the receiver is not affected. Step: {Step}, "
        + "TransactionId: {TransactionId}";

    /// <summary>
    /// The check of the token and the transaction (steps 2–3 of the resolve endpoint).
    /// </summary>
    private readonly HopPresentationCheck _presentationCheck;

    /// <summary>
    /// Where a token that cannot be accepted is written down.
    /// </summary>
    private readonly HopRefusalJournal _refusalJournal;

    /// <summary>
    /// The reading of the effective hop settings.
    /// </summary>
    private readonly HopModeSettings _hopModeSettings;

    /// <summary>
    /// Store the marks and the fingerprint are written to — by its one-time conditional writes.
    /// </summary>
    private readonly ITransactionStore _transactionStore;

    /// <summary>
    /// Publisher of the intermediate statuses of the hop family.
    /// </summary>
    private readonly HopChannelStatusPublisher _statusPublisher;

    /// <summary>
    /// Clock the moment of a mark is taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HopSignalReceiver> _logger;

    /// <summary>
    /// Creates the receiver.
    /// </summary>
    /// <param name="presentationCheck">The check of the token and the transaction.</param>
    /// <param name="refusalJournal">Where a token that cannot be accepted is written down.</param>
    /// <param name="hopModeSettings">The reading of the effective hop settings.</param>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="statusPublisher">Publisher of the intermediate statuses of the hop family.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public HopSignalReceiver(
        HopPresentationCheck presentationCheck,
        HopRefusalJournal refusalJournal,
        HopModeSettings hopModeSettings,
        ITransactionStore transactionStore,
        HopChannelStatusPublisher statusPublisher,
        TimeProvider timeProvider,
        ILogger<HopSignalReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(presentationCheck);
        ArgumentNullException.ThrowIfNull(refusalJournal);
        ArgumentNullException.ThrowIfNull(hopModeSettings);
        ArgumentNullException.ThrowIfNull(transactionStore);
        ArgumentNullException.ThrowIfNull(statusPublisher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _presentationCheck = presentationCheck;
        _refusalJournal = refusalJournal;
        _hopModeSettings = hopModeSettings;
        _transactionStore = transactionStore;
        _statusPublisher = statusPublisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Receives one client signal. Whatever the outcome, the caller answers the same way: a refusal is
    /// written down here, and an accepted signal is recorded here.
    /// </summary>
    /// <param name="rawToken">The token string as presented in the route.</param>
    /// <param name="contentType">The declared media type of the body; null when absent.</param>
    /// <param name="body">The body, already read under its limit.</param>
    /// <param name="cancellationToken">
    /// Cancellation token of the request — for the reads only: once the signal is accepted nothing is
    /// cancelled with the request.
    /// </param>
    /// <returns>The completed operation.</returns>
    /// <exception cref="InvalidOperationException">
    /// The hop settings of the transaction cannot be resolved; the failure is not replaced by a default.
    /// </exception>
    public async Task ReceiveAsync(
        string? rawToken,
        string? contentType,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        // Steps 2–3, judged by the expiry of the transaction alone (SPEC-025 HOP-190, HOP-149).
        var outcome = await _presentationCheck.CheckAsync(rawToken, HopExpiryBasis.TransactionOnly, cancellationToken);

        if (outcome.Refusal is { } refusal)
        {
            // A token that cannot be accepted is the same hop_token_invalid a presentation gets, with the
            // same audit where the transaction can be recovered (SPEC-025 HOP-114); every other refusal is
            // a rejected signal, never a security event.
            if (refusal is HopRefusal.TokenUndecodable or HopRefusal.TransactionNotFound or HopRefusal.ChannelTypeMismatch)
            {
                await _refusalJournal.RecordAsync(outcome, HopRequestMarkers.Signal, cancellationToken);
            }
            else
            {
                LogRejected(RejectionOf(refusal), outcome.Transaction, signal: null);
            }

            return;
        }

        if (outcome is { UnifiedChannelSet: { } channelSet, Transaction: { } unifiedTransaction })
        {
            await ReceiveUnifiedAsync(unifiedTransaction, channelSet, contentType, body, cancellationToken);
            return;
        }

        if (outcome is not
            {
                Transaction: { } transaction, Token: { } token, Adapter: { } adapter, ChannelMode: { } channelMode
            })
        {
            throw new InvalidOperationException(
                "An admitted hop presentation carries its transaction, token, adapter and channel mode.");
        }

        if (!HopClientSignal.TryParse(contentType, body, out var signal, out var rejection))
        {
            LogRejected(rejection, transaction, signal: null);
            return;
        }

        // A channel answered with the page of the channel buttons sends what that page sends — the answer
        // is the one the presentation itself goes by, by the redirect style of this act: under Direct the
        // channel has no page, and its signals are refused as those of any channel under that style.
        var sentByButtonsPage = await HopChannelModes.AnswersOnButtonsPageAsync(
            adapter, channelMode, TransactionResolutionContext.For(transaction), cancellationToken);

        // The channel field belongs to the unified token (SPEC-025 HOP-191); with the token of one channel
        // it refuses the signal — unless the page of the channel buttons sent it and it names the channel
        // of the token. The channel published is always that of the token, never the value of the body.
        if (signal.Channel is { } namedChannel
            && !(sentByButtonsPage
                && string.Equals(namedChannel, token.ChannelType, StringComparison.OrdinalIgnoreCase)))
        {
            LogRejected(HopSignalRejection.ChannelNotAllowed, transaction, signal.Signal);
            return;
        }

        // Read per act, in the context of the transaction, and not trusted to the mapping of the route: with
        // a level bound above the core one the mapping is unconditional (SPEC-025 HOP-190, HOP-129a).
        var settings = await _hopModeSettings.ResolveObservationAsync(
            TransactionResolutionContext.For(transaction),
            cancellationToken);

        if (ConditionFailedBy(transaction, settings, channelMode) is { } condition)
        {
            LogRejected(condition, transaction, signal.Signal);
            return;
        }

        if (sentByButtonsPage && signal.Signal is HopChannelStatusKeys.InterstitialOpened)
        {
            await AcceptOpenedButtonsPageAsync(signal, transaction, settings);
            return;
        }

        await AcceptAsync(signal, transaction, token, settings);
    }

    /// <summary>
    /// Records that a presentation took the person straight into the channel — the answer was a redirect
    /// to a target that opens the app of the channel, so there is no page left to report a tap. The record
    /// is the one an accepted <see cref="HopChannelStatusKeys.ChannelLaunched"/> signal makes: the one-time
    /// mark and the status. It is an observation of the server, not a client
    /// signal, so the acceptance of client signals does not apply to it.
    /// </summary>
    /// <param name="transaction">The transaction of the presentation.</param>
    /// <param name="channelType">The channel type the status carries.</param>
    /// <param name="settings">Observation settings of the presentation.</param>
    /// <returns>The completed operation.</returns>
    public Task RecordLaunchedByRedirectAsync(
        Transaction transaction,
        string channelType,
        HopObservationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(settings);

        return MarkAndPublishAsync(transaction, HopChannelStatusKeys.ChannelLaunched, channelType, settings);
    }

    /// <summary>
    /// Receives a signal sent with the unified token — by the scanner page: the channel the person chose
    /// (SPEC-025 HOP-156, HOP-191) or, with no channel named, the opened page carrying the browser
    /// fingerprint (HOP-090). The conditions of HOP-190 hold with the substitutions
    /// of the unified token: the unified mode applies instead of "hop is on for the channel" (the check of
    /// the token has judged it already), the channel is in the set of the transaction instead of "the
    /// adapter of the channel is on", and there is no redirect style to require.
    /// </summary>
    /// <param name="transaction">The transaction, as the check loaded it.</param>
    /// <param name="channelSet">The channel set of the transaction, as the check judged it.</param>
    /// <param name="contentType">The declared media type of the body; null when absent.</param>
    /// <param name="body">The body, already read under its limit.</param>
    /// <param name="cancellationToken">Cancellation token of the request — for the reads only.</param>
    /// <returns>The completed operation.</returns>
    private async Task ReceiveUnifiedAsync(
        Transaction transaction,
        IReadOnlyList<string> channelSet,
        string? contentType,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (!HopClientSignal.TryParse(contentType, body, out var signal, out var rejection))
        {
            LogRejected(rejection, transaction, signal: null);
            return;
        }

        // The opened page: no channel is chosen yet, so none is asked for.
        if (signal.Signal is HopChannelStatusKeys.InterstitialOpened)
        {
            var openedSettings = await _hopModeSettings.ResolveObservationAsync(
                TransactionResolutionContext.For(transaction),
                cancellationToken);

            if (ConditionFailedBy(transaction, openedSettings, channelMode: null) is { } openedCondition)
            {
                LogRejected(openedCondition, transaction, signal.Signal);
                return;
            }

            await AcceptOpenedButtonsPageAsync(signal, transaction, openedSettings);
            return;
        }

        if (signal.Channel is not { } requestedChannel)
        {
            LogRejected(HopSignalRejection.ChannelMissing, transaction, signal.Signal);
            return;
        }

        // The channel published is the one of the set, not the raw value of the body: the case of the body
        // does not reach the status.
        var channelType = channelSet.FirstOrDefault(
            member => string.Equals(member, requestedChannel, StringComparison.OrdinalIgnoreCase));
        if (channelType is null)
        {
            LogRejected(HopSignalRejection.ChannelNotInSet, transaction, signal.Signal);
            return;
        }

        var settings = await _hopModeSettings.ResolveObservationAsync(
            TransactionResolutionContext.For(transaction),
            cancellationToken);

        if (ConditionFailedBy(transaction, settings, channelMode: null) is { } condition)
        {
            LogRejected(condition, transaction, signal.Signal);
            return;
        }

        // One mark per transaction (SPEC-025 HOP-190): the first accepted tap keeps the moment, and a tap on
        // another button does not overwrite it, yet publishes its own channel — the one the phone opens now.
        await MarkAndPublishAsync(transaction, signal.Signal, channelType, settings);
    }

    /// <summary>
    /// The rejection of a signal whose token or transaction the check refused for a reason other than an
    /// unacceptable token.
    /// </summary>
    /// <param name="refusal">The refusal of the check.</param>
    /// <returns>The rejection.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The check cannot give that refusal to a signal.</exception>
    private static HopSignalRejection RejectionOf(HopRefusal refusal) => refusal switch
    {
        // The substitutions of the unified token (SPEC-025 HOP-191): an empty set holds no channel a signal
        // could name, and a mode that does not apply is the unified counterpart of hop being off.
        HopRefusal.UnifiedSetEmpty => HopSignalRejection.ChannelNotInSet,
        HopRefusal.UnifiedModeInactive => HopSignalRejection.UnifiedModeInactive,
        HopRefusal.TransactionExpired => HopSignalRejection.TransactionExpired,
        HopRefusal.NotInTransitionState => HopSignalRejection.NotInTransitionState,
        HopRefusal.AdapterMissing => HopSignalRejection.AdapterMissing,
        HopRefusal.HopDisabled => HopSignalRejection.HopDisabled,
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "The refusal does not apply to a hop client signal.")
    };

    /// <summary>
    /// The first condition of acceptance that does not hold (SPEC-025 HOP-190): signals are accepted in
    /// the context of the transaction, the presentation of the token is answered with a page to send them
    /// from (<see cref="HopChannelModes.AnswersByPage"/> — the answer the presentation itself goes by), and
    /// a person's presentation was marked before. The unified token has no channel of its own and no
    /// redirect style: the scanner page it resolves to is where its signal comes from (HOP-191).
    /// </summary>
    /// <param name="transaction">The transaction, as the check loaded it.</param>
    /// <param name="settings">Observation settings for this act.</param>
    /// <param name="channelMode">Hop mode of the channel of the token for this act; null — the unified token.</param>
    /// <returns>The rejection; null when every condition holds.</returns>
    private static HopSignalRejection? ConditionFailedBy(
        Transaction transaction,
        HopObservationSettings settings,
        HopChannelMode? channelMode)
    {
        if (!settings.AcceptClientSignals)
        {
            return HopSignalRejection.SignalsDisabled;
        }

        if (channelMode is not null && !HopChannelModes.AnswersByPage(channelMode))
        {
            return HopSignalRejection.NotInterstitial;
        }

        return transaction.HopFirstResolvedAt is null ? HopSignalRejection.NotResolved : null;
    }

    /// <summary>
    /// The records of an accepted signal: its one-time mark and its status; for
    /// <see cref="HopChannelStatusKeys.InterstitialOpened"/> also the fingerprint, independently of the mark.
    /// A fingerprint is dropped where it is not effectively collected, or where the store keeps none — no
    /// snapshot, or one already there; the signal stays accepted (SPEC-025 HOP-190).
    /// </summary>
    /// <param name="signal">The accepted signal.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="token">The token; its channel type is the one the status carries.</param>
    /// <param name="settings">Observation settings for this act.</param>
    /// <returns>The completed operation.</returns>
    private async Task AcceptAsync(
        HopClientSignal signal,
        Transaction transaction,
        HopToken token,
        HopObservationSettings settings)
    {
        await MarkAndPublishAsync(transaction, signal.Signal, token.ChannelType, settings);

        if (signal.Fingerprint is { } fingerprint && settings.CollectsFingerprintOnPage)
        {
            await TryFillFingerprintAsync(transaction, fingerprint);
        }
    }

    /// <summary>
    /// Accepts <see cref="HopChannelStatusKeys.InterstitialOpened"/> sent by the page of the channel
    /// buttons. That page sends it only as the carrier of the browser fingerprint (SPEC-025 HOP-090), so
    /// the signal is refused where the fingerprint is not effectively collected, and an accepted one
    /// leaves the fingerprint alone: no mark is written and no status is published — the mark and the
    /// status of the opened page belong to the launch page of a channel, and with the unified token no
    /// channel is chosen yet for a status to carry.
    /// </summary>
    /// <param name="signal">The signal, its conditions of acceptance already met.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="settings">Observation settings for this act.</param>
    /// <returns>The completed operation.</returns>
    private async Task AcceptOpenedButtonsPageAsync(
        HopClientSignal signal,
        Transaction transaction,
        HopObservationSettings settings)
    {
        if (!settings.CollectsFingerprintOnPage)
        {
            LogRejected(HopSignalRejection.SignalNotAllowed, transaction, signal.Signal);
            return;
        }

        if (signal.Fingerprint is { } fingerprint)
        {
            await TryFillFingerprintAsync(transaction, fingerprint);
        }
    }

    /// <summary>
    /// Writes the mark of an accepted signal and publishes its status. The mark is written once and keeps the
    /// moment of the first signal; the status is published by every accepted signal, the mark already there
    /// included: after the way back to the channel choice the launch page of the next pass sends signals
    /// the first pass already marked, and the window waits for them (SPEC-025 HOP-190, HOP-170). Only a
    /// failed write publishes nothing. The status published is the code of the signal itself: the values of
    /// the field are the codes of the statuses (SPEC-025 HOP-161).
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="signal">The accepted signal.</param>
    /// <param name="channelType">The channel type the status carries.</param>
    /// <param name="settings">Observation settings for this act.</param>
    /// <returns>The completed operation.</returns>
    private async Task MarkAndPublishAsync(
        Transaction transaction,
        string signal,
        string channelType,
        HopObservationSettings settings)
    {
        var marked = await TryMarkAsync(transaction, MarkOf(signal));
        if (marked is null)
        {
            return;
        }

        if (marked is false)
        {
            _logger.LogDebug(
                "The hop client signal is already marked; its status is published again. Signal: {Signal}, TransactionId: {TransactionId}",
                signal,
                transaction.Id.ToString());
        }

        await _statusPublisher.PublishAsync(transaction, channelType, signal, settings);
    }

    /// <summary>
    /// The mark a signal writes.
    /// </summary>
    /// <param name="signal">The signal.</param>
    /// <returns>The mark.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The signal is not one the receiver accepts.</exception>
    private static HopObservationMark MarkOf(string signal) => signal switch
    {
        HopChannelStatusKeys.InterstitialOpened => HopObservationMark.InterstitialOpened,
        HopChannelStatusKeys.ChannelLaunched => HopObservationMark.ChannelLaunched,
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown hop client signal.")
    };

    /// <summary>
    /// Writes the mark of a signal once, by the first writer (SPEC-025 HOP-190, HOP-071a).
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="mark">The mark.</param>
    /// <returns>
    /// true — this call wrote the mark; false — the mark is already there or the transaction is gone;
    /// null — the write failed.
    /// </returns>
    private async Task<bool?> TryMarkAsync(Transaction transaction, HopObservationMark mark)
    {
        try
        {
            return await _transactionStore.TryMarkHopObservationAsync(
                transaction.Id,
                mark,
                _timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogStepFailed(ex, transaction, MarkStep);
            return null;
        }
    }

    /// <summary>
    /// Fills the fingerprint of the scanner context once, by the first writer (SPEC-025 HOP-190, HOP-080).
    /// The value itself never reaches the log.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="fingerprint">The fingerprint, of its form.</param>
    /// <returns>The completed operation.</returns>
    private async Task TryFillFingerprintAsync(Transaction transaction, string fingerprint)
    {
        try
        {
            await _transactionStore.TryFillScannerFingerprintAsync(transaction.Id, fingerprint, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogStepFailed(ex, transaction, FingerprintStep);
        }
    }

    /// <summary>
    /// Logs a rejected signal (SPEC-025 §14): the reason, the transaction and the signal when they are
    /// known — never the token, the hop URL or the fingerprint. transaction.Id.ToString() is Base62, and the
    /// signal is one of two constants — no log injection.
    /// </summary>
    /// <param name="rejection">Why the signal is rejected.</param>
    /// <param name="transaction">The loaded transaction, when there is one.</param>
    /// <param name="signal">The parsed signal, when the body was parsed.</param>
    private void LogRejected(HopSignalRejection rejection, Transaction? transaction, string? signal) =>
        _logger.LogInformation(
            RejectedLogTemplate,
            HopModeResultCodes.ClientSignalRejected,
            rejection,
            transaction?.Id.ToString(),
            signal);

    /// <summary>
    /// Logs a failed step of an accepted signal.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="step">Name of the failed step.</param>
    private void LogStepFailed(Exception exception, Transaction transaction, string step) =>
        _logger.LogWarning(exception, StepFailedLogTemplate, step, transaction.Id.ToString());
}
