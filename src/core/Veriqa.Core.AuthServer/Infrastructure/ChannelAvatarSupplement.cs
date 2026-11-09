// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// Supplies the avatar of the channel user to the resolved identity an issuance maps its claims from,
/// asking the optional <see cref="IChannelAvatarSource"/> of the channel of the transaction. Every surface
/// that maps the claims of a completed transaction — the authorization code, the link handle exchange and
/// the confirmation token — calls this one place right before <see cref="IClaimsMapper.MapToClaimsAsync"/>,
/// so a sign-in and a confirmation get the avatar on the same terms.
/// </summary>
/// <remarks>
/// <para>
/// The source is asked only when this issuance requested <see cref="VeriqaScopes.Avatar"/>, when neither
/// snapshot of the transaction carries a non-empty <c>picture</c> already (a value in a snapshot comes
/// first), and when the adapter of the channel implements the port. The call runs under the tenant of
/// the channel identity snapshot and within <see cref="ChannelAvatarConstants.AvatarBudgetSeconds"/>; a
/// failure, a run-out budget or a value <see cref="AvatarDataUri.IsAcceptedAvatar"/> refuses leave the
/// issuance without the avatar. The check is made here and not left to the mapper: the mapper is
/// replaceable, and a replacement is not bound to make it.
/// </para>
/// <para>
/// The supplemented snapshot is a copy handed to the mapping of this issuance only; it is never stored,
/// so each issuance asks the source again.
/// </para>
/// <para>
/// Scoped: it reads the channel adapters, whose lifetime a host may narrow.
/// </para>
/// </remarks>
internal sealed class ChannelAvatarSupplement
{
    /// <summary>
    /// Registered channel adapters — the one of the channel of the transaction is asked for the avatar.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Clock the budget of the call runs on.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ChannelAvatarSupplement> _logger;

    /// <summary>
    /// Creates the supplement.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="timeProvider">Clock the budget of the call runs on.</param>
    /// <param name="logger">Logger.</param>
    public ChannelAvatarSupplement(
        IEnumerable<IChannelAdapter> adapters,
        TimeProvider timeProvider,
        ILogger<ChannelAvatarSupplement> logger)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns the resolved identity the claims of this issuance are mapped from: a copy carrying the
    /// avatar of the channel under <see cref="VeriqaClaimTypes.Picture"/> when the source was asked and
    /// gave an accepted value, otherwise <paramref name="resolvedIdentity"/> itself.
    /// </summary>
    /// <param name="transaction">Completed transaction the claims are issued for.</param>
    /// <param name="resolvedIdentity">Resolved identity of the transaction.</param>
    /// <param name="completion">Completion snapshot of the transaction.</param>
    /// <param name="scopes">Scopes of this issuance.</param>
    /// <param name="cancellationToken">Token of the request; its cancellation propagates.</param>
    /// <returns>The resolved identity to map the claims from.</returns>
    public async Task<ResolvedIdentitySnapshot> SupplementAsync(
        Transaction transaction,
        ResolvedIdentitySnapshot resolvedIdentity,
        CompletionSnapshot completion,
        IReadOnlySet<string> scopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(resolvedIdentity);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(scopes);

        if (!scopes.Contains(VeriqaScopes.Avatar)
            || HasPicture(resolvedIdentity.Claims)
            || HasPicture(completion.Claims)
            || transaction.ChannelIdentitySnapshot is not { } snapshot
            || ChannelWebhookPipeline.FindAdapter(_adapters, snapshot.ChannelType) is not IChannelAvatarSource source)
        {
            return resolvedIdentity;
        }

        var avatar = await ReadAsync(transaction, snapshot, source, cancellationToken);

        return AvatarDataUri.IsAcceptedAvatar(avatar)
            ? WithPicture(resolvedIdentity, avatar)
            : resolvedIdentity;
    }

    /// <summary>
    /// Whether a snapshot already carries a non-empty <c>picture</c>.
    /// </summary>
    /// <param name="claims">Claims of the snapshot.</param>
    /// <returns>true — the snapshot has an avatar of its own.</returns>
    private static bool HasPicture(IReadOnlyDictionary<string, string>? claims)
    {
        if (claims is null)
        {
            return false;
        }

        foreach (var (name, value) in claims)
        {
            if (VeriqaClaimTypes.NameComparer.Equals(name, VeriqaClaimTypes.Picture)
                && !string.IsNullOrEmpty(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks the source for the avatar under the tenant of the snapshot, within the budget. A failure and a
    /// run-out budget are reported and give null; a cancelled request propagates.
    /// </summary>
    /// <remarks>
    /// The call is awaited through the budget token as well as handed it, so a source that does not honour
    /// the token still costs the issuance no more than the budget.
    /// </remarks>
    /// <param name="transaction">Transaction the avatar is read for (its identifier goes to the log).</param>
    /// <param name="snapshot">Channel identity snapshot of the transaction.</param>
    /// <param name="source">Avatar source of the adapter of the channel.</param>
    /// <param name="cancellationToken">Token of the request.</param>
    /// <returns>The value the source gave; null — none.</returns>
    private async Task<string?> ReadAsync(
        Transaction transaction,
        ChannelIdentitySnapshot snapshot,
        IChannelAvatarSource source,
        CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(
            TimeSpan.FromSeconds(ChannelAvatarConstants.AvatarBudgetSeconds),
            _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

        try
        {
            using (ChannelTenantContext.BeginScope(snapshot.TenantId))
            {
                return await source.GetAvatarAsync(snapshot.ChannelUserId, linked.Token)
                    .AsTask()
                    .WaitAsync(linked.Token);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // transaction.Id.ToString() — a Base62 string (alphabet 0-9, a-z, A-Z), log injection is excluded
            _logger.LogWarning(
                "Channel {ChannelType} did not provide the avatar within {BudgetSeconds} s — the claims are issued without it. TransactionId: {TransactionId}",
                snapshot.ChannelType,
                ChannelAvatarConstants.AvatarBudgetSeconds,
                transaction.Id.ToString());

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Channel {ChannelType} failed to provide the avatar — the claims are issued without it. TransactionId: {TransactionId}",
                snapshot.ChannelType,
                transaction.Id.ToString());

            return null;
        }
    }

    /// <summary>
    /// A copy of the resolved identity with the avatar under <see cref="VeriqaClaimTypes.Picture"/>. The
    /// claims are copied key for key; an empty <c>picture</c> of any spelling gives way to the avatar. The
    /// copy names every property of <see cref="ResolvedIdentitySnapshot"/>: a property added there is to be
    /// added here as well.
    /// </summary>
    /// <param name="resolvedIdentity">Resolved identity of the transaction.</param>
    /// <param name="avatar">Accepted avatar value.</param>
    /// <returns>The supplemented copy.</returns>
    private static ResolvedIdentitySnapshot WithPicture(ResolvedIdentitySnapshot resolvedIdentity, string avatar)
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);

        if (resolvedIdentity.Claims is not null)
        {
            foreach (var (name, value) in resolvedIdentity.Claims)
            {
                if (!VeriqaClaimTypes.NameComparer.Equals(name, VeriqaClaimTypes.Picture))
                {
                    claims[name] = value;
                }
            }
        }

        claims[VeriqaClaimTypes.Picture] = avatar;

        return new ResolvedIdentitySnapshot
        {
            Subject = resolvedIdentity.Subject,
            Claims = claims
        };
    }
}
