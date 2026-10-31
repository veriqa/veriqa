// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Host;

/// <summary>
/// Startup check of the audit sink of this host (SPEC-011 E45, SPEC-012 CFG-253). Registered only when
/// the effective connection string of the sink is empty, i.e. when the journal falls back to memory.
/// It resolves Logging.Mode through the shared resolver in the core context — no precedence of its own
/// (SPEC-011 R8) — and, when the mode is <see cref="LoggingMode.Audit"/>:
/// <list type="bullet">
///   <item>in <c>Development</c> — the host starts and a warning names the cure;</item>
///   <item>anywhere else, or without an <see cref="IHostEnvironment"/> — the host fails to start with the same text.</item>
/// </list>
/// Any other mode passes silently.
/// <para>
/// The check lives in the host rather than in the core: a library caller of <c>UseInMemorySink()</c>
/// states the choice, while the fallback of this host picks the in-memory journal without anyone saying so.
/// </para>
/// <para>
/// Coverage is a fact of today's delivery. The mode is read once, at startup, at the core level: the only
/// configuration source of this host serves no tenant level. Not covered are <c>Audit</c> stated at the
/// tenant level in a composition that registers a source of that level, and a switch to <c>Audit</c>
/// without a restart (SPEC-011 R9).
/// </para>
/// </summary>
internal sealed class AuditStoreStartupCheckService : IHostedService
{
    /// <summary>
    /// Text of the refusal and of the Development warning; it names the keys that cure it.
    /// </summary>
    internal const string AuditStoreInMemoryMessage =
        $"{LoggingConfigKeys.CoreSectionName}:Mode is '{nameof(LoggingMode.Audit)}', but the audit store is in memory: "
        + $"set '{InfrastructureConfigConstants.AuditStoreConnectionStringKey}' "
        + $"or '{InfrastructureConfigConstants.TransactionStoreConnectionStringKey}'.";

    /// <summary>
    /// Text of the refusal and of the Development warning when the transaction store provider names Redis:
    /// the sink borrows nothing from it, so only the key of the sink itself cures it.
    /// </summary>
    internal const string AuditStoreInMemoryRedisTransactionStoreMessage =
        $"{LoggingConfigKeys.CoreSectionName}:Mode is '{nameof(LoggingMode.Audit)}', but the audit store is in memory: "
        + $"set '{InfrastructureConfigConstants.AuditStoreConnectionStringKey}'.";

    /// <summary>
    /// Host environment, or <see langword="null"/> when the services were assembled outside a host.
    /// </summary>
    private readonly IHostEnvironment? _environment;

    /// <summary>
    /// Canonical resolver of the effective setting value.
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<AuditStoreStartupCheckService> _logger;

    /// <summary>
    /// Whether Veriqa:TransactionEngine:Store:Provider names Redis, which picks the text of the refusal and of
    /// the warning.
    /// </summary>
    private readonly bool _transactionStoreOnRedis;

    /// <summary>
    /// Creates the check instance.
    /// </summary>
    /// <param name="environment">Host environment, or null when there is none.</param>
    /// <param name="configurationResolver">Configuration resolver.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="transactionStoreOnRedis">
    /// Whether Veriqa:TransactionEngine:Store:Provider names Redis, with or without a connection string: the
    /// sink then borrows nothing from the transaction store, and the text names the sink key alone.
    /// </param>
    public AuditStoreStartupCheckService(
        IHostEnvironment? environment,
        IConfigurationResolver configurationResolver,
        ILogger<AuditStoreStartupCheckService> logger,
        bool transactionStoreOnRedis)
    {
        _environment = environment;
        _configurationResolver = configurationResolver;
        _logger = logger;
        _transactionStoreOnRedis = transactionStoreOnRedis;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = await _configurationResolver.ResolveAsync(
            LoggingConfigKeys.Mode,
            ResolutionContext.Core,
            ConfigDimensionValues.None,
            cancellationToken);

        if (mode.Value is not LoggingMode.Audit)
        {
            return;
        }

        // Development keeps starting: a developer without a database is the scenario the in-memory
        // journal exists for. Anywhere else an audit trail that dies with the process stops the host.
        if (_environment is not null && _environment.IsDevelopment())
        {
            // Two calls rather than one with a picked text: a log message template stays a constant.
            if (_transactionStoreOnRedis)
            {
                _logger.LogWarning(AuditStoreInMemoryRedisTransactionStoreMessage);
            }
            else
            {
                _logger.LogWarning(AuditStoreInMemoryMessage);
            }

            return;
        }

        throw new InvalidOperationException(_transactionStoreOnRedis
            ? AuditStoreInMemoryRedisTransactionStoreMessage
            : AuditStoreInMemoryMessage);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
