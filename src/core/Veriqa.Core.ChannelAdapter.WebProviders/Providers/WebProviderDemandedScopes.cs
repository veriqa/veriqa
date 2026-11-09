// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Http;

using Veriqa.Core.ChannelAdapter.WebProviders.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Providers;

/// <summary>
/// The scopes a sign-in asks of the provider beyond the ones every sign-in of the channel asks for: the
/// scopes of the phone number and of the claims of the registration, taken only by a transaction whose
/// requirements name the claim they serve. The link of the channel works them out from the transaction
/// and leaves them on the request; the challenge of the OpenIddict client adds them to the set it built
/// (<see cref="WebProviderClientComposition"/>), so <c>openid</c> and the scopes of the registration stay.
/// </summary>
internal static class WebProviderDemandedScopes
{
    /// <summary>
    /// Item of the request carrying the scopes from the link of the channel to the challenge.
    /// </summary>
    private static readonly object RequestItemKey = new();

    /// <summary>
    /// Works out the scopes of a transaction and leaves them on the request. A requirement to the phone
    /// number takes the scopes of the number only at the level <see cref="ClaimRequirementLevel.Required"/>
    /// or <see cref="ClaimRequirementLevel.Optional"/>: a number wanted "if available" is wanted without
    /// an action of the user, and asking the provider for it is one. A requirement to a claim of an entry
    /// takes its scopes at any level: such a claim usually has no completion path and is wanted "if
    /// available", so the requirement exists because the integrator declared the claim under a scope the
    /// transaction was granted, or because the claim belongs to a standard scope the transaction was
    /// granted (<c>nickname</c> of <c>profile</c>) — then without a row of the catalog.
    /// </summary>
    /// <param name="httpContext">The request of the link.</param>
    /// <param name="registration">The channel.</param>
    /// <param name="requirements">Requirement set of the transaction.</param>
    public static void Attach(
        HttpContext httpContext,
        WebProviderRegistration registration,
        IReadOnlyList<ClaimRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(requirements);

        var scopes = new HashSet<string>(StringComparer.Ordinal);

        if (registration.PhoneNumber is { } phoneNumber
            && requirements.Any(requirement =>
                requirement.Level is ClaimRequirementLevel.Required or ClaimRequirementLevel.Optional
                && IsOf(requirement, VeriqaClaimTypes.PhoneNumber)))
        {
            scopes.UnionWith(phoneNumber.Scopes);
        }

        foreach (var mapping in registration.Claims)
        {
            if (requirements.Any(requirement => IsOf(requirement, mapping.Claim)))
            {
                scopes.UnionWith(mapping.Scopes);
            }
        }

        if (scopes.Count > 0)
        {
            httpContext.Items[RequestItemKey] = scopes;
        }
    }

    /// <summary>
    /// The scopes the link of the channel left on the request.
    /// </summary>
    /// <param name="httpContext">The request of the challenge; <c>null</c> — a challenge outside a request.</param>
    /// <returns>The scopes; empty — none.</returns>
    public static IReadOnlyCollection<string> Of(HttpContext? httpContext) =>
        httpContext?.Items[RequestItemKey] as IReadOnlyCollection<string> ?? [];

    /// <summary>
    /// Whether a requirement is to the claim of that name, under the name identity of the contract.
    /// </summary>
    /// <param name="requirement">The requirement.</param>
    /// <param name="claim">Name of the claim.</param>
    /// <returns>Whether the requirement is to the claim.</returns>
    private static bool IsOf(ClaimRequirement requirement, string claim) =>
        string.Equals(requirement.Claim, claim, VeriqaClaimTypes.NameComparison);
}
