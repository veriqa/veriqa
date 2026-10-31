// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Configuration;

/// <summary>
/// Transaction Engine configuration.
/// Configuration section: "Veriqa:TransactionEngine".
/// All time parameters are specified in seconds.
/// </summary>
public sealed class TransactionEngineOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:TransactionEngine";

    /// <summary>
    /// Minimum allowed transaction TTL (seconds). Single source of the lower bound shared by the
    /// clamp in <c>TransactionService</c> and the range check in <see cref="TransactionEngineOptionsValidator"/>.
    /// </summary>
    public const int MinTransactionTtlSeconds = 60;

    /// <summary>
    /// Maximum allowed transaction TTL (seconds). Single source of the upper bound shared by the
    /// clamp in <c>TransactionService</c> and the range check in <see cref="TransactionEngineOptionsValidator"/>.
    /// </summary>
    public const int MaxTransactionTtlSeconds = 1800;

    /// <summary>
    /// Reject confirmations from bots (ChannelIdentitySnapshot.IsBot == true).
    /// Default: true (protection enabled).
    /// </summary>
    public bool RejectBots { get; set; } = true;

    /// <summary>
    /// Gate (CFG-212/CFG-222): whether the application level may relax <see cref="RejectBots"/>.
    /// Default: false — the gate is closed, so a lower level may only tighten the value (a per-app
    /// value cannot disable bot rejection). Set to true to let a per-client
    /// <c>OidcClientOptions.RejectBots</c> override the tenant/core value.
    /// </summary>
    public bool RejectBotsAllowApplicationOverride { get; set; }

    /// <summary>
    /// Transaction lifetime from creation to a terminal state (in seconds).
    /// Default: 300 seconds (5 minutes). Range: see <see cref="MinTransactionTtlSeconds"/>…<see cref="MaxTransactionTtlSeconds"/>.
    /// </summary>
    public int TransactionTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum time allowed for finalization after Confirmed (in seconds).
    /// Default: 30 seconds. Range: 10–120 seconds.
    /// </summary>
    public int ConfirmationFinalizationTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Retention time of a dead transaction before cleanup (in seconds): how long a terminal
    /// transaction stays readable after reaching its state, and how long a transaction that ran
    /// past <see cref="TransactionTtlSeconds"/> stays readable after <c>ExpiresAt</c> — long enough for
    /// the cleanup pass to move it to Expired and for a late reader to see that outcome.
    /// Default: 600 seconds (10 minutes). Range: 60–3600 seconds.
    /// </summary>
    public int CompletedRetentionSeconds { get; set; } = 600;

    /// <summary>
    /// Interval of the background check and cleanup (in seconds). It bounds how late an expiry nobody
    /// answered is noticed — the receipt in the chat and the expired status of a waiting page.
    /// Default: 15 seconds. Range: 10–300 seconds.
    /// </summary>
    public int CleanupIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Maximum batch size for the background cleanup.
    /// </summary>
    public int CleanupBatchSize { get; set; } = 100;

    /// <summary>
    /// Delay before retrying cleanup after an error (in seconds).
    /// Default: 10 seconds.
    /// </summary>
    public int CleanupErrorRetryDelaySeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of transactions kept simultaneously (InMemory store only).
    /// Protects against memory exhaustion when no external database is used.
    /// Default: 10000. Range: 100–100000.
    /// </summary>
    public int MaxInMemoryEntries { get; set; } = 10_000;
}
