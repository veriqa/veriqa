// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;

/// <summary>
/// The source of the <c>webhook-id</c> of the inbound event a call of the wrapper was caused by
/// (SPEC-042 L43) in the compositions of the suite. The suite has no request pipeline of the core: the
/// code that plays the event opens a scope with its identifier, and the calls of the wrapper made on
/// that asynchronous flow are keyed by it — so two calls in one scope carry one idempotency key.
/// </summary>
internal sealed class ConformanceInboundEventIds : IHttpChannelInboundEventIdSource
{
    /// <summary>
    /// Format of a random identifier: 32 hexadecimal digits.
    /// </summary>
    private const string IdentifierFormat = "N";

    /// <summary>
    /// Identifier of the event played on the current flow.
    /// </summary>
    private readonly AsyncLocal<string?> _current = new();

    /// <summary>
    /// Plays an event with a new random identifier on the current flow until the scope is disposed.
    /// </summary>
    /// <returns>The scope.</returns>
    public IDisposable BeginNewEvent() => Begin(NewId());

    /// <summary>
    /// Plays the event with the given identifier on the current flow until the scope is disposed — the
    /// receiver of the suite opens it with the <c>webhook-id</c> of an event that arrived.
    /// </summary>
    /// <param name="webhookId">Identifier of the event.</param>
    /// <returns>The scope.</returns>
    public IDisposable Begin(string webhookId)
    {
        ArgumentException.ThrowIfNullOrEmpty(webhookId);

        var outer = _current.Value;
        _current.Value = webhookId;
        return new EventScope(this, outer);
    }

    /// <summary>
    /// A new random identifier of an event.
    /// </summary>
    /// <returns>The identifier.</returns>
    public static string NewId() => Guid.NewGuid().ToString(IdentifierFormat);

    /// <summary>
    /// The identifier of the event played on the current flow, for any channel.
    /// </summary>
    /// <param name="channelType">Channel type of the call.</param>
    /// <returns>The identifier; <c>null</c> — the call is not caused by an event.</returns>
    public string? GetCurrentEventId(string channelType) => _current.Value;

    /// <summary>
    /// Restores the identifier the scope replaced.
    /// </summary>
    /// <param name="owner">The source.</param>
    /// <param name="outer">The identifier to restore.</param>
    private sealed class EventScope(ConformanceInboundEventIds owner, string? outer) : IDisposable
    {
        /// <summary>
        /// Restores the outer identifier.
        /// </summary>
        public void Dispose() => owner._current.Value = outer;
    }
}
