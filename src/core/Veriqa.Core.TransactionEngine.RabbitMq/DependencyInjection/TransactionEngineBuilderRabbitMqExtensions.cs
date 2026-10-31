// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Events.RabbitMq;

namespace Veriqa.Core.TransactionEngine.DependencyInjection;

/// <summary>RabbitMQ event publisher registration extensions for <see cref="TransactionEngineBuilder"/>.</summary>
public static class TransactionEngineBuilderRabbitMqExtensions
{
    /// <summary>
    /// Publishes transaction events out of the process over RabbitMQ.
    /// Suitable for distributed systems and integration with external consumers.
    /// </summary>
    /// <remarks>
    /// The transport is additive: in-process dispatch to the registered
    /// <c>ITransactionEventHandler</c> instances stays wired, so the audit trail, the real-time
    /// sign-in push and the prompt cleanup keep receiving events. Reliability of what leaves the
    /// process is the reliability of the broker and of nothing else — the engine builds no
    /// guarantee of its own on top of the transport.
    /// <para>
    /// Supported extension point: the shipped standalone host wires it by configuration —
    /// a connection string in <c>Veriqa:Events:RabbitMq:ConnectionString</c>. CI does not run that
    /// host against a live broker — validate the connection in the consuming host when you enable
    /// it. What CI does cover is the composition: the in-process subscribers keep their events with
    /// this transport in place.
    /// </para>
    /// <para>
    /// A host writing a transport of its own can copy the shape below, and then owes what the shape
    /// implies: one object standing under several registrations (the concrete type, the publisher
    /// interface, the hosted service) is released once per registration on shutdown, so its
    /// <c>Dispose</c>/<c>DisposeAsync</c> has to do nothing on the calls after the first — the
    /// contract of <see cref="IDisposable"/>, and what the publisher here does with a flag of its
    /// own. That is a property of the registrations, not of the engine: the engine folds the
    /// interface registration into the fan-out without adding or removing a release.
    /// </para>
    /// </remarks>
    /// <param name="builder">Transaction Engine builder.</param>
    /// <param name="configure">Action configuring the RabbitMQ connection parameters.</param>
    /// <returns>Builder for chaining.</returns>
    public static TransactionEngineBuilder UseRabbitMqPublisher(
        this TransactionEngineBuilder builder,
        Action<RabbitMqEventPublisherOptions> configure)
    {
        // Register the RabbitMQ connection parameters
        builder.Services.Configure(configure);

        // Register as Singleton + IHostedService: connection management for the entire application lifetime
        builder.Services.AddSingleton<RabbitMqTransactionEventPublisher>();
        builder.Services.AddSingleton<ITransactionEventPublisher>(
            sp => sp.GetRequiredService<RabbitMqTransactionEventPublisher>());
        builder.Services.AddHostedService(
            sp => sp.GetRequiredService<RabbitMqTransactionEventPublisher>());

        return builder;
    }
}
