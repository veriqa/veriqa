// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Security.Claims;
using OpenIddict.Abstractions;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// Mapper of the resolved identity and completion snapshot into OIDC claims.
/// Builds the full claim set with destinations for token issuance.
/// <para>
/// The shipped implementation reads the relying party attribution for one thing only — the effective
/// catalog of scopes and claims of that relying party, which gates the claims it declares (SPEC-041
/// R21). The subject it issues is global across relying parties, which is a property of the Veriqa
/// contract. A substituted mapper is the place where a pairwise subject or an enrichment from the
/// integrator's directory belongs.
/// </para>
/// </summary>
public sealed class ClaimsMapper : IClaimsMapper
{
    /// <summary>
    /// The canonical resolver of the effective setting values — the source of the catalog.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Creates the mapper.
    /// </summary>
    /// <param name="resolver">Canonical resolver of the effective setting values (the catalog of scopes
    /// and claims).</param>
    public ClaimsMapper(IConfigurationResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Claim names the core emits itself and never accepts from a snapshot. <c>sub</c> comes from the
    /// typed field of the resolved identity, and <c>amr</c> states the channel the confirmation
    /// actually came through — a value an adapter put under either name would compete with it. For
    /// <c>amr</c> dropping the snapshot value is also what keeps the array a relying party reads at a
    /// single element (OIDC Core 1.0 section 2).
    /// </summary>
    private static readonly FrozenSet<string> CoreEmittedClaims = new[]
    {
        OpenIddictConstants.Claims.Subject,
        OpenIddictConstants.Claims.AuthenticationMethodReference
    }.ToFrozenSet(VeriqaClaimTypes.NameComparer);

    /// <summary>
    /// Maps transaction data to a set of OIDC claims with destinations.
    /// </summary>
    /// <param name="resolvedIdentity">Resolved identity from the completed transaction.</param>
    /// <param name="completion">Transaction completion data.</param>
    /// <param name="context">Context of the relying party the claims are built for.</param>
    /// <param name="cancellationToken">Token that cancels the mapping.</param>
    /// <returns>Result with the claim set or an error.</returns>
    public async Task<Result<IReadOnlyList<Claim>>> MapToClaimsAsync(
        ResolvedIdentitySnapshot resolvedIdentity,
        CompletionSnapshot completion,
        ClaimsMappingContext context,
        CancellationToken cancellationToken = default)
    {
        // The method builds the full claim set from the resolved identity and completion snapshot

        ArgumentNullException.ThrowIfNull(resolvedIdentity);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(context);

        // A cancelled request is not a mapping outcome: the exception goes to the caller instead of
        // being folded into a Result.
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(resolvedIdentity.Subject))
        {
            return Result<IReadOnlyList<Claim>>.Failure(
                OidcErrorCodes.ClaimsMappingFailed,
                "Subject (sub) is missing in ResolvedIdentitySnapshot.");
        }

        var claims = new List<Claim>();

        // sub — a mandatory claim
        claims.Add(new Claim(OpenIddictConstants.Claims.Subject, resolvedIdentity.Subject));

        // Claims from ResolvedIdentitySnapshot
        var addedClaimTypes = new HashSet<string>(VeriqaClaimTypes.NameComparer);
        if (resolvedIdentity.Claims is not null)
        {
            foreach (var kvp in resolvedIdentity.Claims)
            {
                // sub and amr are emitted by the core below, never taken from a snapshot. An empty value
                // the completion filled is taken from the completion snapshot in the loop below.
                if (CoreEmittedClaims.Contains(kvp.Key)
                    || !IsIssuable(kvp.Key, kvp.Value, context.Scopes)
                    || IsFilledByCompletion(kvp.Key, kvp.Value, completion.Claims))
                {
                    continue;
                }

                claims.Add(CreateClaim(kvp.Key, kvp.Value));
                addedClaimTypes.Add(kvp.Key);
            }
        }

        // Claims from CompletionSnapshot (only those not yet present — duplication prevention). The values
        // Veriqa collected during the claim completion come this way too, and pass the same gates.
        if (completion.Claims is not null)
        {
            foreach (var kvp in completion.Claims)
            {
                if (CoreEmittedClaims.Contains(kvp.Key)
                    || addedClaimTypes.Contains(kvp.Key)
                    || !IsIssuable(kvp.Key, kvp.Value, context.Scopes))
                {
                    continue;
                }

                claims.Add(CreateClaim(kvp.Key, kvp.Value));
            }
        }

        // zoneinfo — the zone the user's moments are stated in (OIDC Core 1.0 section 5.1). It comes
        // from the CONTEXT of this sign-in and not from the identity snapshot: a channel reports a
        // language, never a zone, and what the relying party stated for the request is the only thing
        // known about it. A sign-in that resolved no zone issues no claim — a UTC fallback passed off
        // as the user's own zone would be a value the relying party could not tell from a real one.
        if (!string.IsNullOrWhiteSpace(context.TimeZone)
            && !addedClaimTypes.Contains(VeriqaClaimTypes.Zoneinfo))
        {
            claims.Add(new Claim(VeriqaClaimTypes.Zoneinfo, context.TimeZone));
        }

        // auth_time — the transaction completion moment (NumericDate / Unix timestamp)
        var authTimeValue = completion.CompletedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        claims.Add(new Claim(
            OpenIddictConstants.Claims.AuthenticationTime,
            authTimeValue,
            ClaimValueTypes.Integer64));

        // amr — the authentication method (channel type). The single point where the claim is added.
        // A blank channel type yields no claim at all: an empty element would reach the relying party
        // as [""] and be read as a method of authentication.
        if (!string.IsNullOrEmpty(completion.ChannelType))
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.AuthenticationMethodReference, completion.ChannelType));
        }

        // acr is deliberately absent, and its absence is a decision rather than an omission. The claim
        // is a level of assurance (OIDC Core 1.0 section 2), and Veriqa has no dictionary of levels to
        // draw a value from: issuing one today would be inventing a requirement. Note that acr_values
        // IS already read on the authorize endpoint — as a channel filter ("channel:<type>"), which is
        // Veriqa's own use of the parameter and not a level of assurance. Which specification the
        // dictionary will live in is still open — SPEC-019 (Binding Levels, frozen) and the hop mode
        // are the two candidates (SPEC-025 section 13.2) — and the claim is introduced together with
        // it, wherever it lands; because acr_values is voluntary in OIDC, "ignored today, supported
        // tomorrow" is not a breaking change.

        // Set destinations for each claim
        var claimScopes = await CatalogClaimScopes.ResolveAsync(
            _resolver, context.TenantId, context.ClientId, cancellationToken);

        foreach (var claim in claims)
        {
            claim.SetDestinations(ClaimDestinationsHelper.GetDestinations(claim, context.Scopes, claimScopes));
        }

        return Result<IReadOnlyList<Claim>>.Success(claims.AsReadOnly());
    }

    /// <summary>
    /// Whether an identity claim the channel left empty carries a value in the completion snapshot: the
    /// claims completion counted it missing and collected it from the user (SPEC-041 L43 — an empty value
    /// is not a value the channel gave). Without a completion the snapshot repeats the identity claims,
    /// so an empty value is issued as it stands.
    /// </summary>
    /// <remarks>
    /// "Empty is not given" is the rule of the engine, stated once there by
    /// <c>DefaultIdentityResolutionService.IsGiven</c> (internal to <c>Veriqa.Core.TransactionEngine</c>);
    /// this check mirrors it on the side that issues the claims. Change the two together.
    /// </remarks>
    /// <param name="name">Claim name.</param>
    /// <param name="value">Value of the resolved identity.</param>
    /// <param name="completionClaims">Claims of the completion snapshot.</param>
    /// <returns>true — the claim is issued from the completion snapshot instead.</returns>
    private static bool IsFilledByCompletion(
        string name,
        string value,
        IReadOnlyDictionary<string, string>? completionClaims)
        => string.IsNullOrEmpty(value)
           && completionClaims is not null
           && completionClaims.TryGetValue(name, out var completed)
           && !string.IsNullOrEmpty(completed);

    /// <summary>
    /// Tells whether a snapshot entry may become a claim of this sign-in. Only the avatar is gated
    /// here; every other name passes, and its token placement is decided by the destinations.
    /// </summary>
    /// <remarks>
    /// <c>picture</c> is issued only when the relying party requested <see cref="VeriqaScopes.Avatar"/>
    /// and <see cref="AvatarDataUri.IsAcceptedAvatar"/> accepts the value — an image data URI or an
    /// absolute https URL (OIDC Core 1.0 section 5.1). The gate sits in the mapper and not only in the
    /// destinations: OpenIddict stores every claim of the principal in the authorization code and the
    /// refresh token whatever their destinations, so a claim left out of the token set would still be
    /// persisted. A refused value is dropped silently — the sign-in succeeds without an avatar, and the
    /// mapper has no logger by construction.
    /// </remarks>
    /// <param name="name">Claim name from the snapshot.</param>
    /// <param name="value">Claim value from the snapshot.</param>
    /// <param name="scopes">Scopes requested by the relying party.</param>
    /// <returns><see langword="true"/> when the entry may be issued.</returns>
    private static bool IsIssuable(string name, string value, IReadOnlySet<string> scopes)
    {
        if (!string.Equals(name, VeriqaClaimTypes.Picture, VeriqaClaimTypes.NameComparison))
        {
            return true;
        }

        return scopes.Contains(VeriqaScopes.Avatar)
            && AvatarDataUri.IsAcceptedAvatar(value);
    }

    /// <summary>
    /// Builds a claim out of a snapshot entry, declaring its value type where the core states one.
    /// A name in <see cref="VeriqaClaimTypes.BooleanClaims"/> gets <see cref="ClaimValueTypes.Boolean"/>
    /// so the claim reaches the wire as a JSON boolean (OIDC Core 1.0 section 5.1) instead of the
    /// string the internal transport carries it in, and a name in <see cref="VeriqaClaimTypes.NumericDateClaims"/>
    /// gets <see cref="ClaimValueTypes.Integer64"/> so it reaches the wire as a JSON number; every other
    /// name keeps the default string type.
    /// </summary>
    /// <remarks>
    /// The decision is made by MEMBERSHIP in the registries and never by naming a claim here: that keeps
    /// one place in the core knowing which claims are boolean and which are numeric dates. A value that
    /// does not have the shape of its registry (a corrupted snapshot) is passed through as it stands —
    /// refusing the token is not this layer's call, and the mapper neither throws nor logs over it.
    /// </remarks>
    /// <param name="name">Claim name from the snapshot.</param>
    /// <param name="value">Claim value from the snapshot (transport form).</param>
    /// <returns>The claim, typed when the core states a type for that name.</returns>
    private static Claim CreateClaim(string name, string value)
    {
        if (VeriqaClaimTypes.BooleanClaims.Contains(name))
        {
            return new Claim(name, value, ClaimValueTypes.Boolean);
        }

        return VeriqaClaimTypes.NumericDateClaims.Contains(name)
            ? new Claim(name, value, ClaimValueTypes.Integer64)
            : new Claim(name, value);
    }
}
