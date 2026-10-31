// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Events.Channels;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.DependencyInjection;

/// <summary>
/// Builder for configuring the Transaction Engine.
/// Allows choosing the store implementation and other parameters.
/// </summary>
public sealed class TransactionEngineBuilder
{
    /// <summary>
    /// Service collection the builder registers into. Exposed for ecosystem packages that add their
    /// own registrations through an extension method on this builder; not part of the everyday
    /// configuration surface.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IServiceCollection Services { get; }

    /// <summary>
    /// Indicates that a store has been registered.
    /// The setter is internal so satellite store packages can flag their registration.
    /// </summary>
    internal bool StoreRegistered { get; set; }

    /// <summary>
    /// Creates the builder.
    /// </summary>
    /// <param name="services">Service collection.</param>
    internal TransactionEngineBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Uses the in-memory store (for development and testing).
    /// </summary>
    /// <returns>Builder for chaining.</returns>
    public TransactionEngineBuilder UseInMemoryStore()
    {
        // Register the in-memory store as a Singleton
        Services.AddSingleton<ITransactionStore, InMemoryTransactionStore>();
        StoreRegistered = true;

        return this;
    }

    /// <summary>
    /// Uses a collected claims store (SPEC-041 C37): the claims Veriqa collected for a subject are kept and
    /// not asked again at the next sign-in. Not calling it leaves the store out — nothing is kept (R17).
    /// </summary>
    /// <remarks>
    /// The one registration every implementation goes through, the shipped EF Core one
    /// (<c>UseEfCoreCollectedClaimsStore</c>) included, so a store of the integrator's own gets exactly what
    /// the shipped one gets: the deletion by a call of the host (<see cref="ICollectedClaimsDeletion"/>, C83),
    /// the automatic removal of the keys unused for the retention period (C84) and, in the auth server, the
    /// deletion endpoint (C82). The port is registered <c>Scoped</c>: the engine asks for it inside a scope of
    /// its own per operation, which keeps a scoped implementation — the natural shape of an EF Core one —
    /// safe. Called more than once, the last call wins.
    /// </remarks>
    /// <typeparam name="TStore">Store implementation.</typeparam>
    /// <returns>Builder for chaining.</returns>
    public TransactionEngineBuilder UseCollectedClaimsStore<TStore>()
        where TStore : class, ICollectedClaimsStore
    {
        // AddScoped, not TryAddScoped: this is the choice of a store, not a shipped default, and the last
        // registration of a service is the one resolved.
        Services.AddScoped<ICollectedClaimsStore, TStore>();

        // The deletion and the removal do not depend on which store is chosen, so a repeated call keeps
        // the one registration of each: TryAdd, and AddHostedService adds a hosted service only once.
        Services.TryAddSingleton<CollectedClaimsDeletion>();
        Services.TryAddSingleton<ICollectedClaimsDeletion>(
            serviceProvider => serviceProvider.GetRequiredService<CollectedClaimsDeletion>());
        Services.AddHostedService<CollectedClaimsRetentionService>();

        return this;
    }

    /// <summary>
    /// Configures the in-process event dispatch based on System.Threading.Channels.
    /// </summary>
    /// <remarks>
    /// In-process dispatch to the registered <see cref="ITransactionEventHandler"/> instances is a
    /// part of the engine and is wired unconditionally, so calling this method is never required
    /// for the audit trail, the real-time sign-in push or the prompt cleanup to receive events.
    /// What it does is set the parameters of that dispatch — today the capacity of its queue.
    /// Publishing events OUT of the process is the separate choice of a transport
    /// (<c>UseRabbitMqPublisher</c>), and it is additive rather than a replacement.
    /// </remarks>
    /// <param name="configure">Optional action for configuring the parameters.</param>
    /// <returns>Builder for chaining.</returns>
    public TransactionEngineBuilder UseChannelsPublisher(Action<ChannelEventPublisherOptions>? configure = null)
    {
        // Apply the user configuration if provided; the dispatcher itself is registered by
        // AddVeriqaTransactionEngine, whichever transport the deployment chose
        if (configure is not null)
        {
            Services.Configure(configure);
        }

        return this;
    }
}
