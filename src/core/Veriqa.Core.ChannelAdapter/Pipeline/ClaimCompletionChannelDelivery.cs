// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Default <see cref="IClaimCompletionChannelDelivery"/>: the outbound seam of the core
/// (<see cref="ChannelOutbound.SendMessageAsync"/>) under the tenant of the identity snapshot, with the
/// retry policy of <see cref="ClaimCompletionDeliveryConstants"/> on top of it.
/// </summary>
/// <remarks>
/// The retries cover the row "message send error" of SPEC-003 §12.2 only. The rows about a rate limit
/// (429) and a platform error (5xx) are carried out by the platform clients where they are carried out;
/// this seam does not tell the codes apart and does not repeat them. The background mode exists
/// because the callers cannot wait: the step starter returns after its synchronous part, and the queue
/// of the in-process transaction events is one sequential handler that a pause between retries — let
/// alone a wait for an answer — would hold up for every other event.
/// </remarks>
internal sealed class ClaimCompletionChannelDelivery : IClaimCompletionChannelDelivery
{
    /// <summary>
    /// Registered channel adapters.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Store of the chat of a transaction — where the reference to a sent phone number request message
    /// is written.
    /// </summary>
    private readonly IChannelPromptMessageStore _promptMessageStore;

    /// <summary>
    /// Component metrics a failed send is counted into by the outbound seam.
    /// </summary>
    private readonly ChannelAdapterMetrics _metrics;

    /// <summary>
    /// Clock of the pauses between the retries.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Lifetime of the host — the stopping token of the background operations.
    /// </summary>
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ClaimCompletionChannelDelivery> _logger;

    /// <summary>
    /// Creates the delivery seam.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="promptMessageStore">Store of the chat of a transaction.</param>
    /// <param name="metrics">Component metrics.</param>
    /// <param name="timeProvider">Clock of the pauses between the retries.</param>
    /// <param name="lifetime">Lifetime of the host.</param>
    /// <param name="logger">Logger.</param>
    public ClaimCompletionChannelDelivery(
        IEnumerable<IChannelAdapter> adapters,
        IChannelPromptMessageStore promptMessageStore,
        ChannelAdapterMetrics metrics,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger<ClaimCompletionChannelDelivery> logger)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _promptMessageStore = promptMessageStore ?? throw new ArgumentNullException(nameof(promptMessageStore));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> DeliverAsync(
        Transaction transaction,
        ChannelMessage message,
        CancellationToken cancellationToken)
    {
        // Validates the addressing, finds the adapter of the snapshot's channel and sends under the
        // snapshot's tenant; a failed send is repeated after each pause of the policy, a refusal of the
        // content-kind gate is final at once — the channel will not accept the kind on a retry either.
        // A phone number request that went out leaves the reference to its message in the record of
        // the chat of the transaction, for the terminal notice — the outcome receipt or the expiry —
        // to take the request off the chat by.
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);

        var snapshot = transaction.ChannelIdentitySnapshot
            ?? throw new ArgumentException(
                "The transaction has no channel identity snapshot to deliver the message to.",
                nameof(transaction));

        if (!string.Equals(message.ChannelUserId, snapshot.ChannelUserId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The message is addressed to somebody other than the user of the transaction's channel identity snapshot.",
                nameof(message));
        }

        ValidateContent(message);

        using var tenantScope = ChannelTenantContext.BeginScope(snapshot.TenantId);

        var adapter = ChannelWebhookPipeline.FindAdapter(_adapters, snapshot.ChannelType);
        if (adapter is null)
        {
            _logger.LogWarning(
                "No adapter serves the channel of a claim completion message — not delivered. ChannelType: {ChannelType}, TransactionId: {TransactionId}, Kind: {MessageKind}",
                snapshot.ChannelType,
                transaction.Id.ToString(),
                message.Kind.Value);
            return false;
        }

        var retryDelays = ClaimCompletionDeliveryConstants.RetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            var result = await ChannelOutbound.SendMessageAsync(adapter, message, _logger, _metrics, cancellationToken);
            if (result.IsSuccess)
            {
                if (message.Kind == ChannelMessageKind.PhoneNumberRequest && result.Value is { } requestMessage)
                {
                    try
                    {
                        // Not the token of the delivery: the message was sent, and a cancellation
                        // arriving now must not leave its reference unwritten. A request sent again
                        // goes the same way, so the record keeps the reference of the last one. A
                        // transaction with no record of its chat keeps no reference at all.
                        await _promptMessageStore.SetPhoneRequestMessageAsync(
                            transaction.Id,
                            snapshot.ChannelType,
                            requestMessage,
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // The message is already in the chat; the reference only serves taking the
                        // request off it later, so the delivery still counts as made.
                        _logger.LogWarning(
                            ex,
                            "The reference to a sent phone number request message was not stored — the terminal notice will not take the request off the chat. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                            snapshot.ChannelType,
                            transaction.Id.ToString());
                    }
                }

                return true;
            }

            if (string.Equals(result.Error.Code, ChannelAdapterErrorCodes.UnsupportedMessageKind, StringComparison.Ordinal)
                || attempt >= retryDelays.Count)
            {
                return false;
            }

            await Task.Delay(retryDelays[attempt], _timeProvider, cancellationToken);
        }
    }

    /// <inheritdoc />
    public void DeliverInBackground(
        Transaction transaction,
        ChannelMessage message,
        Func<bool, CancellationToken, Task>? onOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);

        var stopping = _lifetime.ApplicationStopping;

        // The task handed over is the release signal of the press gate, not work bound to a context:
        // its source runs continuations asynchronously, the operation awaits it on the thread pool and
        // nothing blocks on the operation, so the deadlock VSTHRD003 guards against cannot occur.
#pragma warning disable VSTHRD003
        Launch(pressAnswered => DeliverWithOutcomeAsync(transaction, message, onOutcome, pressAnswered, stopping));
#pragma warning restore VSTHRD003
    }

    /// <inheritdoc />
    public void RunInBackground(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var stopping = _lifetime.ApplicationStopping;

        // The task handed over is the release signal of the press gate, not work bound to a context:
        // its source runs continuations asynchronously, the operation awaits it on the thread pool and
        // nothing blocks on the operation, so the deadlock VSTHRD003 guards against cannot occur.
#pragma warning disable VSTHRD003
        Launch(pressAnswered => RunWorkAsync(work, pressAnswered, stopping));
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// The one background executor of the seam: starts the operation on the thread pool and returns
    /// at once.
    /// </summary>
    /// <param name="operation">Operation to run, given what it waits for before its own work — the
    /// release of the gate of the channel press being handled on the caller's flow; it never throws —
    /// it observes its own failures.</param>
    private static void Launch(Func<Task, Task> operation)
    {
        // The gate of the press is read here, on the caller's flow and before the flow is suppressed:
        // the operation sees none of the caller's ambient state, so it is handed the gate by value. A
        // caller outside the handling of a press reads a completed task, and the operation starts at
        // once.
        var pressAnswered = ChannelPressReplyGate.Released;

        // The operation is handed the release signal of the press gate read above; as in the callers
        // of this method, VSTHRD003 does not apply to it.
#pragma warning disable VSTHRD003
        Func<Task> start = () => operation(pressAnswered);
#pragma warning restore VSTHRD003

        // The flow of the caller's execution context is suppressed: the operation outlives the request
        // that started it, and the ambient state of that request — its HttpContext, its activity, its
        // tenant scope — must not travel into it. The tenant a delivery needs is the snapshot's, and
        // the delivery opens that scope itself. A caller whose flow is already suppressed has nothing
        // to carry over, and suppressing it twice would throw.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = Task.Run(start, CancellationToken.None);
            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(start, CancellationToken.None);
        }
    }

    /// <summary>
    /// The background operation of <see cref="RunInBackground"/>: the caller's work, with nothing
    /// escaping — a background task has no caller left to observe an exception.
    /// </summary>
    /// <param name="work">Work to run.</param>
    /// <param name="pressAnswered">Completes once the handler of the channel press the work was
    /// launched under is left — the press answered by then on a normal way out; already completed when
    /// there was none.</param>
    /// <param name="stopping">Stopping token of the host.</param>
    /// <returns>The completed operation.</returns>
    private async Task RunWorkAsync(
        Func<CancellationToken, Task> work,
        Task pressAnswered,
        CancellationToken stopping)
    {
        try
        {
            await pressAnswered.WaitAsync(stopping);
            await work(stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The host is stopping: the work was cancelled on purpose.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A background claim completion operation failed.");
        }
    }

    /// <summary>
    /// The background operation: the wait for the release of the press gate, the delivery, then the
    /// continuation with its outcome. Nothing escapes — a background task has no caller left to
    /// observe an exception.
    /// </summary>
    /// <param name="transaction">Transaction the message belongs to.</param>
    /// <param name="message">Message to deliver.</param>
    /// <param name="onOutcome">Continuation with the outcome; null — none.</param>
    /// <param name="pressAnswered">Completes once the handler of the channel press the delivery was
    /// launched under is left — the press answered by then on a normal way out; already completed when
    /// there was none.</param>
    /// <param name="stopping">Stopping token of the host.</param>
    /// <returns>The completed operation.</returns>
    private async Task DeliverWithOutcomeAsync(
        Transaction transaction,
        ChannelMessage message,
        Func<bool, CancellationToken, Task>? onOutcome,
        Task pressAnswered,
        CancellationToken stopping)
    {
        bool delivered;
        try
        {
            // A host stopping during the wait ends the operation in the quiet branch below, like a
            // delivery cancelled by it: no warning and no continuation.
            await pressAnswered.WaitAsync(stopping);
            delivered = await DeliverAsync(transaction, message, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Background delivery of a claim completion message failed. TransactionId: {TransactionId}, Kind: {MessageKind}",
                transaction.Id.ToString(),
                message.Kind.Value);
            delivered = false;
        }

        if (onOutcome is null)
        {
            return;
        }

        try
        {
            await onOutcome(delivered, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The host is stopping: the continuation was cancelled on purpose.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The continuation of a background claim completion delivery failed. TransactionId: {TransactionId}, Kind: {MessageKind}",
                transaction.Id.ToString(),
                message.Kind.Value);
        }
    }

    /// <summary>
    /// Refuses a message whose content property does not match its kind: the adapter renders the kind
    /// from that property, and a mismatch would reach the chat as a request without its buttons.
    /// </summary>
    /// <param name="message">Message to check.</param>
    /// <exception cref="ArgumentException">The content does not match the kind.</exception>
    private static void ValidateContent(ChannelMessage message)
    {
        var isPhoneNumberRequest = message.Kind == ChannelMessageKind.PhoneNumberRequest;
        if (isPhoneNumberRequest != (message.PhoneNumberRequest is not null))
        {
            throw new ArgumentException(
                "The phone number request content is set exactly when the message kind is a phone number request.",
                nameof(message));
        }

        var isClaimFormLink = message.Kind == ChannelMessageKind.ClaimFormLink;
        if (isClaimFormLink != (message.ClaimFormLink is not null))
        {
            throw new ArgumentException(
                "The claim form link content is set exactly when the message kind is a claim form link.",
                nameof(message));
        }
    }
}
