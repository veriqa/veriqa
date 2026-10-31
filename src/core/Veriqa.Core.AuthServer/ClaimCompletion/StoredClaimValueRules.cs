// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// The rules a value from the collected claims store is accepted by for a transaction (SPEC-041 L58) — the
/// rules a value collected now would have to pass, in the ownership context of the transaction:
/// <list type="bullet">
/// <item>the email — obtained by a method the effective list of <c>ClaimCompletion.EmailMethods</c> admits,
/// and still an address the email block would write;</item>
/// <item>the phone number — asked for in the channel, and still a number in E.164;</item>
/// <item>any other claim — collected by the form, its catalog row still describes a completion, and the value
/// passes the rules of that field.</item>
/// </list>
/// </summary>
/// <remarks>
/// Singleton: it holds the resolver and the judgement of the email methods, both singletons. The ownership
/// context is the one the transaction states — its tenant is the tenant the records are keyed by.
/// </remarks>
internal sealed class StoredClaimValueRules : IStoredClaimValueRules
{
    /// <summary>
    /// The canonical resolver — the catalog of the claims is read through it.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// The judgement of the ways of obtaining the email: the effective list and the normalization of an
    /// address.
    /// </summary>
    private readonly EmailCompletionAvailability _email;

    /// <summary>
    /// Creates the rules.
    /// </summary>
    /// <param name="resolver">Canonical resolver of the effective setting values.</param>
    /// <param name="email">Judgement of the ways of obtaining the email.</param>
    public StoredClaimValueRules(IConfigurationResolver resolver, EmailCompletionAvailability email)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _email = email ?? throw new ArgumentNullException(nameof(email));
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsAcceptableAsync(
        Transaction transaction,
        ClaimCompletionResult stored,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(stored);

        if (stored is not { Value: { } value, Method: { } method })
        {
            return false;
        }

        if (VeriqaClaimTypes.NameComparer.Equals(stored.Claim, VeriqaClaimTypes.Email))
        {
            return await IsAcceptableEmailAsync(transaction, value, method, cancellationToken);
        }

        if (VeriqaClaimTypes.NameComparer.Equals(stored.Claim, VeriqaClaimTypes.PhoneNumber))
        {
            return method is ClaimCollectionMethod.ChannelRequest
                && IdentityValueNormalizer.TryNormalize(IdentityNormalizationRule.E164Phone, value, out _);
        }

        return method is ClaimCollectionMethod.Form
            && await IsAcceptableFieldAsync(transaction, stored.Claim, value, cancellationToken);
    }

    /// <summary>
    /// Whether a stored email holds: its method is one the effective list admits for the transaction, and
    /// the address still normalizes.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="address">Stored address.</param>
    /// <param name="method">How the address was obtained.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — the email is handed out.</returns>
    private async ValueTask<bool> IsAcceptableEmailAsync(
        Transaction transaction,
        string address,
        ClaimCollectionMethod method,
        CancellationToken cancellationToken)
    {
        if (!EmailCompletionMethods.TryFromCollectionMethod(method, out var emailMethod)
            || _email.NormalizeAddress(address) is null)
        {
            return false;
        }

        using var scope = new ConfigResolutionScope(transaction.ToResolutionContext(tenantId: null));
        var admitted = await _email.ReadListAsync(scope, cancellationToken);

        return admitted.Contains(emailMethod);
    }

    /// <summary>
    /// Whether a value collected by the form holds: the effective catalog still describes a completion for
    /// the claim, and the value passes the rules of that field.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="claim">Name of the claim.</param>
    /// <param name="value">Stored value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — the value is handed out.</returns>
    private async ValueTask<bool> IsAcceptableFieldAsync(
        Transaction transaction,
        string claim,
        string value,
        CancellationToken cancellationToken)
    {
        var catalog = (await _resolver.ResolveAsync(
                ScopesClaimsConfigKeys.ScopeCatalog,
                transaction.ToResolutionContext(tenantId: null),
                ConfigDimensionValues.None,
                cancellationToken))
            .Value ?? [];

        return ScopesClaimsCatalogRules.FindClaim(catalog, claim)?.Completion is { } completion
            && ClaimCompletionFieldRules.AcceptsStoredValue(completion, value);
    }
}
