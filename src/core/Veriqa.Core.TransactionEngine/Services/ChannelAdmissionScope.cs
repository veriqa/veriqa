// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// Ambient scope of one action of a channel — the handling of one request of a letter's page or of one
/// inbound event — within which the admission of a sender to a transaction
/// (<see cref="ITransactionService.ValidateChannelAdmissibilityAsync"/>) is decided once per pair
/// "transaction × identity": the gate of the channel asks first, and the engine, asked again by the write
/// of the same action, gets that same verdict instead of a second evaluation.
/// </summary>
/// <remarks>
/// The engine is a singleton and asks the admission of itself from inside its own operations, so no
/// wrapper of the service could see the second call: the memo is read and written by the method itself,
/// and the action only states where it begins and ends. Implemented via <see cref="AsyncLocal{T}"/>, like
/// the gate of a press of the channel pipeline: the scope belongs to one flow of work, not to a registry
/// keyed by the transaction. An opening inside an open scope joins it. Outside a scope nothing is
/// remembered, and every call evaluates.
/// <para>
/// The identity is told apart by its instance, not by its fields: the verdict reads the whole snapshot
/// (channel, bot flag, the claims a restriction policy evaluates), and the one instance an action hands to
/// the gate and to the write is the only proof that both ask about the same sender.
/// </para>
/// </remarks>
internal static class ChannelAdmissionScope
{
    /// <summary>
    /// Ambient storage of the scope of the current flow; null — no action is open on it.
    /// </summary>
    private static readonly AsyncLocal<ActionScope?> CurrentScope = new();

    /// <summary>
    /// Opens the scope of an action for the rest of its handling, or joins the scope already open on the
    /// flow. Disposing the scope that opened it closes it — on every way out, an exception included.
    /// </summary>
    /// <remarks>
    /// The caller is an <c>async</c> method: a value set by a method that is not one would outlive it in
    /// the flow of its caller.
    /// </remarks>
    /// <returns>Scope that closes the action on Dispose; a joined opening closes nothing.</returns>
    public static IDisposable Open()
    {
        if (CurrentScope.Value is { IsClosed: false })
        {
            return JoinedScope.Instance;
        }

        var scope = new ActionScope();
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// Reads the verdict the current action has already reached for the pair.
    /// </summary>
    /// <param name="transactionId">Transaction the sender acts on.</param>
    /// <param name="identity">Identity snapshot of the sender.</param>
    /// <param name="verdict">The remembered verdict, when there is one.</param>
    /// <returns>true — the action has decided the pair; false — no scope is open, or the pair is new.</returns>
    public static bool TryRecall(
        TransactionId transactionId,
        ChannelIdentitySnapshot identity,
        [NotNullWhen(true)] out Result? verdict)
    {
        verdict = null;

        return CurrentScope.Value is { IsClosed: false } scope
            && scope.Verdicts.TryGetValue(new AdmissionKey(transactionId, identity), out verdict);
    }

    /// <summary>
    /// Remembers the verdict of the pair for the rest of the current action; nothing happens outside a
    /// scope. The first verdict of a pair stays: a parallel evaluation of the same pair does not replace it.
    /// </summary>
    /// <param name="transactionId">Transaction the sender acts on.</param>
    /// <param name="identity">Identity snapshot of the sender.</param>
    /// <param name="verdict">The verdict reached.</param>
    /// <returns>The verdict the action holds for the pair: the remembered one, or <paramref name="verdict"/>.</returns>
    public static Result Remember(TransactionId transactionId, ChannelIdentitySnapshot identity, Result verdict)
    {
        if (CurrentScope.Value is not { IsClosed: false } scope)
        {
            return verdict;
        }

        var key = new AdmissionKey(transactionId, identity);
        var held = scope.Verdicts.GetOrAdd(key, verdict);

        // The action may have ended between the check above and the write: a closed scope keeps nothing,
        // so the verdict written past its end is taken back.
        if (scope.IsClosed)
        {
            scope.Verdicts.TryRemove(key, out _);
        }

        return held;
    }

    /// <summary>
    /// The pair a verdict is remembered for: the transaction by its identifier, the sender by the instance
    /// of its snapshot.
    /// </summary>
    /// <param name="TransactionId">Transaction the sender acts on.</param>
    /// <param name="Identity">Identity snapshot of the sender.</param>
    private readonly record struct AdmissionKey(TransactionId TransactionId, ChannelIdentitySnapshot Identity)
    {
        /// <inheritdoc />
        public bool Equals(AdmissionKey other) =>
            TransactionId.Equals(other.TransactionId) && ReferenceEquals(Identity, other.Identity);

        /// <inheritdoc />
        public override int GetHashCode() =>
            HashCode.Combine(TransactionId, ReferenceEqualityComparer.Instance.GetHashCode(Identity));
    }

    /// <summary>
    /// One open action and the verdicts it has reached.
    /// </summary>
    private sealed class ActionScope : IDisposable
    {
        /// <summary>
        /// Whether the action is over. A flow forked off the action keeps a reference to its scope after
        /// the action ends; a closed scope neither answers nor remembers, so such a flow evaluates afresh.
        /// </summary>
        private volatile bool _isClosed;

        /// <summary>
        /// Verdicts of the action by pair. Concurrent: the work of one action may ask from parallel flows.
        /// </summary>
        public ConcurrentDictionary<AdmissionKey, Result> Verdicts { get; } = new();

        /// <summary>
        /// Whether the action is over.
        /// </summary>
        public bool IsClosed => _isClosed;

        /// <inheritdoc />
        public void Dispose()
        {
            _isClosed = true;

            // A flow that outlives the action keeps this object reachable; the verdicts — and the identity
            // snapshots their keys hold — are not kept alive with it.
            Verdicts.Clear();
            CurrentScope.Value = null;
        }
    }

    /// <summary>
    /// The opening that joined a scope already open: closing it is left to the opening that opened it.
    /// </summary>
    private sealed class JoinedScope : IDisposable
    {
        /// <summary>
        /// The one instance — it holds nothing.
        /// </summary>
        public static readonly JoinedScope Instance = new();

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing to close: the scope stays open until the opening that opened it is disposed.
        }
    }
}
