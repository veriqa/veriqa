// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Identity;

/// <summary>
/// Basic implementation of <see cref="IIdentityResolutionService"/>.
/// Deterministic strategy: sub = channel_type:channel_user_id.
/// Persists <see cref="ChannelIdentity"/> via <see cref="IChannelIdentityRepository"/> when the
/// integrator registered one, and builds a <see cref="ResolvedIdentitySnapshot"/> with
/// OIDC-compatible claims. The port has no implementation in the shipped assembly, so by default
/// nothing is stored: the identity data of the entry travels with the transaction and the channel
/// identity id is derived from the snapshot instead of being read back from a record.
///
/// The port is asked for once per resolution and never held, so an integrator may register it with
/// any lifetime — see <see cref="StoreChannelIdentityAsync"/>.
///
/// Scope limitation:
///   The current implementation is equivalent to the existing pipeline behavior (CA-022).
///   Extended resolution strategies (linking, matching) are not implemented.
/// </summary>
internal sealed class DefaultIdentityResolutionService : IIdentityResolutionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DefaultIdentityResolutionService> _logger;
    private readonly TimeProvider _timeProvider;

    public DefaultIdentityResolutionService(
        IServiceScopeFactory scopeFactory,
        ILogger<DefaultIdentityResolutionService> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The deterministic strategy has no refusal of its own: every snapshot that reaches it yields a
    /// subject. The failure branch of the contract exists for a replacing implementation, which is
    /// where a lookup that can come back empty lives.
    /// </remarks>
    public async Task<Result<ResolvedIdentitySnapshot>> ResolveAsync(
        ChannelIdentitySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var channelIdentityId = await StoreChannelIdentityAsync(snapshot, cancellationToken);

        // Deterministic subject: channel_type:channel_user_id (CA-022)
        var subject = CoreSubject.From(snapshot);

        // Build the claims from the snapshot (OIDC-compatible names)
        var claims = BuildClaims(snapshot, _logger);

        // Create the ResolvedIdentity
        var now = _timeProvider.GetUtcNow();
        var resolvedIdentity = new ResolvedIdentity
        {
            Id = Guid.NewGuid().ToString("N"),
            Subject = subject,
            ChannelIdentityId = channelIdentityId,
            Claims = claims,
            CreatedAt = now,
            UpdatedAt = now,
        };

        return Result<ResolvedIdentitySnapshot>.Success(resolvedIdentity.ToSnapshot());
    }

    /// <summary>
    /// Stores the channel identity through <see cref="IChannelIdentityRepository"/> and returns the
    /// id of the record — or, when no implementation is registered, writes nothing and derives the
    /// id from the identity key of the snapshot, so it stays the same value across the entries of
    /// the same channel user of the same tenant.
    ///
    /// <para>
    /// The port is asked for inside a scope created here rather than injected into this singleton:
    /// that is what keeps a <c>Scoped</c> implementation — the natural shape of an EF Core one —
    /// safe, and what keeps "nobody registered it" a legal composition. The scope closes before the
    /// method returns, so nothing resolved from it outlives the operation; only the id, a string,
    /// travels out.
    /// </para>
    /// </summary>
    private async Task<string> StoreChannelIdentityAsync(
        ChannelIdentitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetService<IChannelIdentityRepository>();

        if (repository is null)
        {
            var derivedId = ComputeChannelIdentityId(snapshot);

            _logger.LogDebug(
                "ChannelIdentity not stored: no IChannelIdentityRepository is registered. "
                + "Id: {ChannelIdentityId}, ChannelType: {ChannelType}, UserFingerprint: {UserFingerprint}",
                derivedId,
                snapshot.ChannelType,
                ComputeUserFingerprint(snapshot.ChannelType, snapshot.ChannelUserId));

            return derivedId;
        }

        // Save/update the ChannelIdentity (upsert)
        var channelIdentity = await repository.SaveAsync(snapshot, cancellationToken);

        _logger.LogDebug(
            "ChannelIdentity saved. Id: {ChannelIdentityId}, ChannelType: {ChannelType}, UserFingerprint: {UserFingerprint}",
            channelIdentity.Id,
            channelIdentity.ChannelType,
            ComputeUserFingerprint(channelIdentity.ChannelType, channelIdentity.ChannelUserId));

        return channelIdentity.Id;
    }

    /// <summary>
    /// Builds the OIDC claim set from a <see cref="ChannelIdentitySnapshot"/>.
    /// Includes the mandatory channel claims (SPEC-003 §7.3) and the standard OIDC profile claims.
    /// </summary>
    /// <remarks>
    /// The one projection of "what the channel gave": the claims completion reads it to decide which
    /// claims are missing (SPEC-041 L43), so the two answers cannot drift apart. A verification mark
    /// stands next to the value it verifies: <c>email_verified</c> and <c>phone_number_verified</c> are
    /// issued only with an email or a number the channel gave (<see cref="IsGiven"/>), so a value the
    /// completion collects in place of a missing one never inherits a mark the channel stated about
    /// nothing (SPEC-041 L50).
    /// </remarks>
    internal static Dictionary<string, string> BuildClaims(
        ChannelIdentitySnapshot snapshot,
        ILogger logger)
    {
        var claims = new Dictionary<string, string>(VeriqaClaimTypes.NameComparer);

        // Mandatory channel claims (SPEC-003 §7.3, scope: channel)
        claims[VeriqaClaimTypes.ChannelType] = snapshot.ChannelType;
        claims[VeriqaClaimTypes.ChannelUserId] = snapshot.ChannelUserId;
        claims[VeriqaClaimTypes.Name] = snapshot.DisplayName;

        if (snapshot.FirstName is not null)
        {
            claims[VeriqaClaimTypes.GivenName] = snapshot.FirstName;
        }

        if (snapshot.LastName is not null)
        {
            claims[VeriqaClaimTypes.FamilyName] = snapshot.LastName;
        }

        if (snapshot.Username is not null)
        {
            claims[VeriqaClaimTypes.PreferredUsername] = snapshot.Username;
        }

        if (snapshot.Locale is not null)
        {
            claims[VeriqaClaimTypes.Locale] = snapshot.Locale;
        }

        if (snapshot.PhoneNumber is not null)
        {
            claims[VeriqaClaimTypes.PhoneNumber] = snapshot.PhoneNumber;
        }

        // SPEC-041 R10: the mark goes with every number the channel gave, and it is true only where the
        // channel proved the number belongs to the sender — a channel that said nothing proved nothing.
        // The mark is the adapter's statement and is issued as stated: the number's format is the adapter's
        // duty (SPEC-041 L47), so the core does not second-guess the mark by the spelling of the number.
        if (IsGiven(snapshot.PhoneNumber))
        {
            claims[VeriqaClaimTypes.PhoneNumberVerified] = FormatBooleanClaim(snapshot.PhoneNumberVerified ?? false);
        }

        if (snapshot.Email is not null)
        {
            claims[VeriqaClaimTypes.Email] = snapshot.Email;
        }

        if (snapshot.EmailVerified is not null && IsGiven(snapshot.Email))
        {
            claims[VeriqaClaimTypes.EmailVerified] = FormatBooleanClaim(snapshot.EmailVerified.Value);
        }

        if (snapshot.AvatarUrl is not null)
        {
            claims[VeriqaClaimTypes.Picture] = snapshot.AvatarUrl;
        }

        // is_bot — always included for audit
        claims[VeriqaClaimTypes.IsBot] = FormatBooleanClaim(snapshot.IsBot);

        // Additional channel-specific adapter claims (SPEC-016 §6.3) — additive, and only for the
        // names that belong to the adapter. This is the single point where claim-name discipline is
        // enforced: the keys are runtime values, so nothing can be checked when an adapter registers.
        if (snapshot.AdditionalClaims is not null)
        {
            MergeAdapterClaims(claims, snapshot, logger);
        }

        return claims;
    }

    /// <summary>
    /// Whether a claim value of the projection is one the channel gave. An empty value is not: the claims
    /// completion counts that claim missing and may collect it, and the collected value takes its place
    /// (SPEC-041 L43). The one statement of the rule in the engine — the planner, the projection and the
    /// merge of the collected values read it here; the built-in claims mapper, in another assembly,
    /// mirrors it and names this member.
    /// </summary>
    /// <param name="value">Claim value; null — the channel did not state the claim.</param>
    /// <returns>true — a non-empty value.</returns>
    internal static bool IsGiven([NotNullWhen(true)] string? value) => !string.IsNullOrEmpty(value);

    /// <summary>
    /// Merges an adapter's free-form claims into the claim set, dropping every key the adapter does
    /// not own. Ownership is decided in a fixed order, so a key has exactly one verdict: names the
    /// core issues or reserves are never taken from an adapter; the closed lists of standard profile
    /// claims and of vendor names are accepted as exceptions; everything else must live under the
    /// adapter's own <c>{ChannelType}_</c> prefix. A violating key is dropped with a warning and never fails the authentication —
    /// a bad claim name is not a reason to refuse a user who has already confirmed.
    ///
    /// Ownership is decided through <see cref="VeriqaClaimTypes.NameComparison"/>, the same
    /// comparison the token consumers use: were this check stricter than they are, a reserved name
    /// with one letter re-cased would pass the gate and still reach them as the reserved claim.
    /// Acceptance is narrower than ownership — a name the adapter may issue is taken only in the
    /// spelling the contract declares, because the claim leaves Veriqa as a JSON member name and
    /// the consumer reading that JSON has no case-insensitive lookup.
    /// </summary>
    private static void MergeAdapterClaims(
        Dictionary<string, string> claims,
        ChannelIdentitySnapshot snapshot,
        ILogger logger)
    {
        var adapterPrefix = snapshot.ChannelType + VeriqaClaimTypes.AdapterClaimPrefixSeparator;

        foreach (var kvp in snapshot.AdditionalClaims!)
        {
            if (VeriqaClaimTypes.MandatoryClaims.Contains(kvp.Key)
                || VeriqaClaimTypes.CoreReservedClaims.Contains(kvp.Key))
            {
                logger.LogWarning(
                    "Additional claim dropped: the name is issued or reserved by the core. "
                    + "ClaimName: {ClaimName}, ChannelType: {ChannelType}",
                    kvp.Key,
                    snapshot.ChannelType);
                continue;
            }

            // Names the adapter may issue: the closed standard-profile and vendor lists, or its own
            // {ChannelType}_ namespace. Both questions are asked with the contract's name identity, so
            // the verdict "not yours" does not depend on letter case either.
            var declaredExceptionName = FindDeclaredExceptionClaim(kvp.Key);
            if (declaredExceptionName is null
                && !kvp.Key.StartsWith(adapterPrefix, VeriqaClaimTypes.NameComparison))
            {
                logger.LogWarning(
                    "Additional claim dropped: the name is outside the adapter's own namespace. "
                    + "ClaimName: {ClaimName}, ChannelType: {ChannelType}, ExpectedPrefix: {ExpectedPrefix}",
                    kvp.Key,
                    snapshot.ChannelType,
                    adapterPrefix);
                continue;
            }

            // The name is the adapter's — but the part of it the contract owns (the whole listed
            // name, or the channel-type prefix) must be spelled the way the contract writes it:
            // "Veriqa_Channel" and "TELEGRAM_note" would otherwise reach the token under a name no
            // documented reader looks for. What follows the prefix is the adapter's own to spell.
            var declaredSpelling = declaredExceptionName ?? adapterPrefix;
            if (!kvp.Key.StartsWith(declaredSpelling, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Additional claim dropped: the name is not spelled as the contract declares it — "
                    + "letter case is part of the claim name a consumer reads. "
                    + "ClaimName: {ClaimName}, ChannelType: {ChannelType}, DeclaredSpelling: {DeclaredSpelling}",
                    kvp.Key,
                    snapshot.ChannelType,
                    declaredSpelling);
                continue;
            }

            // The claim set holds one entry per claim name, and two names differing only in letter
            // case are one name: one of them is merged and the rest are reported. Which spelling
            // survives is not defined and must not be relied on — the set reaches the gate frozen,
            // and a FrozenDictionary enumerates by its own layout, so adding unrelated keys is
            // enough to change which of the two comes first. TryAdd rather than an assignment:
            // assigning would overwrite the earlier value silently, and a claim lost without a
            // trace is the one thing CA-188 rules out.
            if (!claims.TryAdd(kvp.Key, kvp.Value))
            {
                logger.LogWarning(
                    "Additional claim dropped: the name repeats one already merged. "
                    + "ClaimName: {ClaimName}, ChannelType: {ChannelType}",
                    kvp.Key,
                    snapshot.ChannelType);
            }
        }
    }

    /// <summary>
    /// Returns the claim of that name as one of the closed exception lists —
    /// <see cref="VeriqaClaimTypes.StandardProfileClaims"/> or <see cref="VeriqaClaimTypes.VendorClaims"/>
    /// — spells it, or null when the name is on neither. The lookup ignores letter case — the name
    /// identity of the contract — while the returned value carries the declared spelling, so the
    /// caller can both recognize the name and tell how it is meant to be written.
    /// </summary>
    private static string? FindDeclaredExceptionClaim(string claimName) =>
        FindDeclaredClaim(VeriqaClaimTypes.StandardProfileClaims, claimName)
        ?? FindDeclaredClaim(VeriqaClaimTypes.VendorClaims, claimName);

    /// <summary>
    /// Returns the member of a declared list equal to the name under the contract's name identity, in
    /// the spelling the list declares; null — no such member.
    /// </summary>
    private static string? FindDeclaredClaim(IReadOnlySet<string> declaredNames, string claimName)
    {
        foreach (var declaredName in declaredNames)
        {
            if (string.Equals(declaredName, claimName, VeriqaClaimTypes.NameComparison))
            {
                return declaredName;
            }
        }

        return null;
    }

    /// <summary>
    /// Renders a boolean claim value for the claim TRANSPORT, whose slot is a string: the snapshot
    /// carries claims as name/value pairs of strings all the way to the mapper. The rendered text is
    /// the JSON boolean literal, not the shape the claim takes on the wire — the claim reaches a
    /// relying party as a JSON boolean, which the mapper states by typing the names listed in
    /// <see cref="Contracts.VeriqaClaimTypes.BooleanClaims"/>.
    /// </summary>
    internal static string FormatBooleanClaim(bool value) => value ? "true" : "false";

    /// <summary>
    /// Computes a stable fingerprint for the channel user id (SHA-256, first 8 bytes as hex).
    /// Analogous to <see cref="Store.IdempotencyKeyFingerprint"/> — does not leak PII (CA-121).
    /// </summary>
    private static string ComputeUserFingerprint(string channelType, string channelUserId) =>
        ComputeFingerprint(channelType, channelUserId);

    /// <summary>
    /// Derives the channel identity id when no repository is registered and there is therefore no
    /// stored record to read it from. The input is the identity key itself — (tenant, channel type,
    /// channel user id), the triple <see cref="IChannelIdentityRepository"/> defines uniqueness by —
    /// so the same channel user of the same tenant keeps one id across entries, while two tenants
    /// seeing the same channel user keep two, exactly as separate records would. The tenant nobody
    /// stated is spelled the one fixed way <see cref="TenantKey.Segment"/> spells it. The value is a
    /// hash, so it carries no PII (CA-121).
    /// </summary>
    private static string ComputeChannelIdentityId(ChannelIdentitySnapshot snapshot) =>
        ComputeFingerprint(
            TenantKey.Segment(snapshot.TenantId),
            snapshot.ChannelType,
            snapshot.ChannelUserId);

    /// <summary>
    /// Hashes a length-prefixed join of the parts (SHA-256, first 8 bytes as hex). The length prefix
    /// is what keeps the join unambiguous: without it two different part sets could produce the same
    /// input string.
    /// </summary>
    private static string ComputeFingerprint(params ReadOnlySpan<string> parts)
    {
        var input = new StringBuilder();

        foreach (var part in parts)
        {
            if (input.Length > 0)
            {
                input.Append('|');
            }

            input.Append(part.Length).Append(':').Append(part);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
