// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Diagnostics;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Constants;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The single seam through which the core calls a channel adapter's outbound operations
/// (SPEC-003 §6.2).
/// </summary>
/// <remarks>
/// It exists so that the three obligations of every outbound call live in exactly one place instead
/// of being copied to each call site: the content-kind gate against the channel's declared
/// capabilities, the capability gate of the outcome notice, and the "a failed user notification is
/// never lost" discipline — logged with the channel type and the error code, and counted.
/// </remarks>
internal static class ChannelOutbound
{
    // Every method here takes the metrics instrument as a parameter, exactly as it takes the logger,
    // and never as an optional one. The seam is reached from a web request and from a background
    // handler alike, and a nullable instrument would let the background branch — the very one that
    // reports a failed expiry notice — stop counting silently behind a green build.

    /// <summary>
    /// Whether an adapter refusal carries the runtime signal "I cannot carry this confirmation out"
    /// (SPEC-003 CA-191). The single place the core recognizes the signal: the seam picks the log
    /// level by it, and the confirmation orchestrator picks the outcome — so the two can never drift
    /// apart into "reported loudly, yet handled as an ordinary refusal".
    /// </summary>
    /// <param name="errorCode">Error code of the adapter's refusal.</param>
    /// <returns>true when the refusal is the signal, not an ordinary failure.</returns>
    public static bool IsCannotContinueSignal(string errorCode) =>
        string.Equals(errorCode, ChannelAdapterErrorCodes.ChannelCannotContinue, StringComparison.Ordinal);

    /// <summary>
    /// Sends a message to the user, gating the content kind by the channel's declared capabilities.
    /// </summary>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="message">Message to deliver.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference to the sent message (null when the channel has no addressable one), or a failure.</returns>
    public static async Task<Result<ChannelMessageRef?>> SendMessageAsync(
        IChannelAdapter adapter,
        ChannelMessage message,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken = default)
    {
        // The content-kind gate lives here and only here: the rich message object must not turn a
        // compile-time refusal into a runtime one at every call site. Today all traffic is plain
        // text, so this branch does not fire — it exists so that it never fires silently.
        if (!adapter.Capabilities.SupportedMessageKinds.Contains(message.Kind))
        {
            logger.LogWarning(
                "Message content kind is not supported by the channel. ChannelType: {ChannelType}, Kind: {MessageKind}",
                adapter.ChannelType,
                message.Kind.Value);

            var refusal = Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.UnsupportedMessageKind,
                "The message content kind is outside the channel's supported set.");

            metrics.RecordFailedNotice(
                adapter.ChannelType, ChannelAdapterErrorCodes.UnsupportedMessageKind, ChannelNoticeKinds.Message);

            return refusal;
        }

        var result = await adapter.SendMessageAsync(message, cancellationToken);
        if (result.IsFailure)
        {
            logger.LogWarning(
                "Failed to deliver a message to the user. ChannelType: {ChannelType}, Error: {ErrorCode}",
                adapter.ChannelType,
                result.Error.Code);

            metrics.RecordFailedNotice(
                adapter.ChannelType, result.Error.Code, ChannelNoticeKinds.Message);
        }

        return result;
    }

    /// <summary>
    /// Sends the confirmation prompt, gating the content kind the same way as a plain message:
    /// the prompt is plain text, and a channel that does not accept plain text cannot be prompted.
    /// </summary>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelUserId">Recipient within the channel.</param>
    /// <param name="promptContext">Confirmation context.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference to the sent prompt (null when the channel has no addressable one), or a failure.</returns>
    public static async Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        IChannelAdapter adapter,
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken = default)
    {
        // The single point of the core through which a prompt leaves for a messenger, and therefore
        // the only sensible owner of this span: the four adapter implementations behind it would
        // give four copies of the same code and no extra fact. The e-mail path builds its letter
        // outside this method and is not covered by the span at all.
        using var activity = ChannelActivitySource.Source.StartActivity(ChannelTelemetry.PromptSendActivityName);
        activity?.SetTag(ChannelTelemetry.ChannelTypeTag, adapter.ChannelType);
        activity?.SetTag(TransactionTelemetry.TransactionIdTag, transactionId.ToString());

        if (!adapter.Capabilities.SupportedMessageKinds.Contains(ChannelMessageKind.PlainText))
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(ChannelTelemetry.ErrorCodeTag, ChannelAdapterErrorCodes.UnsupportedMessageKind);

            logger.LogWarning(
                "Confirmation prompt content kind is not supported by the channel. ChannelType: {ChannelType}",
                adapter.ChannelType);

            metrics.RecordFailedNotice(
                adapter.ChannelType, ChannelAdapterErrorCodes.UnsupportedMessageKind, ChannelNoticeKinds.Message);

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.UnsupportedMessageKind,
                "The channel does not accept the confirmation prompt content kind.");
        }

        var result = await adapter.SendConfirmationPromptAsync(transactionId, channelUserId, promptContext, cancellationToken);
        if (result.IsFailure)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(ChannelTelemetry.ErrorCodeTag, result.Error.Code);

            // The signal ends the transaction, so it is reported loudly and with the adapter's own
            // message: it is the only account of WHY the user's sign-in stopped here. An ordinary
            // refusal leaves the transaction waiting for a retry or its TTL and stays a warning.
            if (IsCannotContinueSignal(result.Error.Code))
            {
                logger.LogError(
                    "The channel declared it cannot carry the confirmation out. TransactionId: {TransactionId}, ChannelType: {ChannelType}, Error: {ErrorCode} — {ErrorMessage}",
                    transactionId.ToString(),
                    adapter.ChannelType,
                    result.Error.Code,
                    result.Error.Message);
            }
            else
            {
                logger.LogWarning(
                    "Failed to send the confirmation message. TransactionId: {TransactionId}, ChannelType: {ChannelType}, Error: {ErrorCode}",
                    transactionId.ToString(),
                    adapter.ChannelType,
                    result.Error.Code);
            }

            metrics.RecordFailedNotice(
                adapter.ChannelType, result.Error.Code, ChannelNoticeKinds.Message);
        }

        return result;
    }

    /// <summary>
    /// Shows the user the state of a transaction — a terminal outcome, or the one non-terminal state
    /// of a sign-in confirmed and waiting for missing claims — but only when the channel declared it
    /// can (<c>DeliversOutcomeNotice</c>). A channel that did not is never called.
    /// </summary>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="notice">What to show and everything needed to address it.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task NotifyOutcomeAsync(
        IChannelAdapter adapter,
        TransactionOutcomeNotice notice,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken = default)
    {
        if (!adapter.Capabilities.DeliversOutcomeNotice)
        {
            logger.LogDebug(
                "Channel does not deliver outcome notices — nothing shown. ChannelType: {ChannelType}, TransactionId: {TransactionId}, Outcome: {Outcome}",
                adapter.ChannelType,
                notice.TransactionId.ToString(),
                notice.Outcome.Value);
            return;
        }

        var result = await adapter.ReportOutcomeAsync(notice, cancellationToken);
        if (result.IsFailure)
        {
            // The outcome code is a bounded, non-personal value: it names WHICH state of the
            // transaction the user was supposed to see, which is exactly what a failed notice log is
            // missing otherwise.
            logger.LogWarning(
                "Failed to show the transaction outcome to the user. ChannelType: {ChannelType}, TransactionId: {TransactionId}, Outcome: {Outcome}, Error: {ErrorCode}",
                adapter.ChannelType,
                notice.TransactionId.ToString(),
                notice.Outcome.Value,
                result.Error.Code);

            metrics.RecordFailedNotice(
                adapter.ChannelType, result.Error.Code, ChannelNoticeKinds.Outcome);
            return;
        }

        // Success(false) is a deliberate "nothing to show", not a failure: no warning, no counter.
        if (!result.Value)
        {
            logger.LogDebug(
                "Channel deliberately showed no outcome. ChannelType: {ChannelType}, TransactionId: {TransactionId}, Outcome: {Outcome}",
                adapter.ChannelType,
                notice.TransactionId.ToString(),
                notice.Outcome.Value);
        }
    }
}
