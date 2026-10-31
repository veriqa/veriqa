// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Issues the short code of a transaction: decimal digits of the configured length drawn from the
/// cryptographic random generator, reserved in <see cref="IChannelStartCodeStore"/> so that the code
/// is unique among the live codes of the deployment.
/// </summary>
/// <remarks>
/// The value of a code is never written to a log: it is a secret of the transaction for as long as
/// the transaction lives.
/// </remarks>
internal sealed class ChannelStartCodeIssuer : IChannelStartCodeIssuer
{
    /// <summary>
    /// Characters a code is drawn from.
    /// </summary>
    private const string Alphabet = "0123456789";

    /// <summary>
    /// How many codes are tried for one transaction before the issue is refused. A draw collides with
    /// any live code of the deployment — the codes of all its tenants share one space — so the chance
    /// of one collision is the share of that space the live codes take, and a run of this many means
    /// the space of the configured length is nearly full: a longer code is the remedy, not a longer
    /// run.
    /// </summary>
    private const int MaxIssueAttempts = 8;

    /// <summary>
    /// Store the codes are reserved in.
    /// </summary>
    private readonly IChannelStartCodeStore _store;

    /// <summary>
    /// Settings of the code.
    /// </summary>
    private readonly IOptions<ChannelCodeStartOptions> _options;

    /// <summary>
    /// Clock the deadline of a transaction is read against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ChannelStartCodeIssuer> _logger;

    /// <summary>
    /// Creates the issuer.
    /// </summary>
    /// <param name="store">Store the codes are reserved in.</param>
    /// <param name="options">Settings of the code.</param>
    /// <param name="timeProvider">Clock the deadline of a transaction is read against.</param>
    /// <param name="logger">Logger.</param>
    public ChannelStartCodeIssuer(
        IChannelStartCodeStore store,
        IOptions<ChannelCodeStartOptions> options,
        TimeProvider timeProvider,
        ILogger<ChannelStartCodeIssuer> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Result<ChannelStartCode>> GetOrIssueAsync(
        TransactionId transactionId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        // The code already issued wins, so a page shown again repeats it. Otherwise a code is drawn and
        // reserved; a refused reservation is either a collision with another transaction's code (draw
        // again) or a concurrent issue for this very transaction (return what that one reserved).
        var existing = await _store.FindByTransactionAsync(transactionId, cancellationToken);
        if (existing is not null)
        {
            return Result<ChannelStartCode>.Success(existing);
        }

        if (_timeProvider.GetUtcNow() >= expiresAt)
        {
            return Result<ChannelStartCode>.Failure(
                ChannelAdapterErrorCodes.StartCodeIssueFailed,
                "The transaction is past its deadline, so no start code is issued for it.");
        }

        var codeLength = _options.Value.CodeLength;

        for (var attempt = 0; attempt < MaxIssueAttempts; attempt++)
        {
            var record = new ChannelStartCode
            {
                Code = RandomNumberGenerator.GetString(Alphabet, codeLength),
                TransactionId = transactionId,
                ExpiresAt = expiresAt
            };

            if (await _store.TryAddAsync(record, cancellationToken))
            {
                return Result<ChannelStartCode>.Success(record);
            }

            var concurrent = await _store.FindByTransactionAsync(transactionId, cancellationToken);
            if (concurrent is not null)
            {
                return Result<ChannelStartCode>.Success(concurrent);
            }
        }

        _logger.LogWarning(
            "No free start code of {CodeLength} digits was found for transaction {TransactionId} in {Attempts} attempts; "
            + "the code space of this length is nearly full",
            codeLength,
            transactionId,
            MaxIssueAttempts);

        return Result<ChannelStartCode>.Failure(
            ChannelAdapterErrorCodes.StartCodeIssueFailed,
            "No free start code was found for the transaction.");
    }
}
