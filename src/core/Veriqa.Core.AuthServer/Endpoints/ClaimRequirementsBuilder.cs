// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

using OpenIddict.Abstractions;

using Veriqa.Core.AuthServer.ClaimCompletion;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The one point where the requirement set of a sign-in transaction is written (SPEC-041 C35): it runs on
/// <c>/connect/authorize</c>, before the transaction is created, and turns the scopes of the request, the
/// <c>claims</c> parameter and the configuration into the set the transaction carries unchanged
/// afterwards. With the phone number required it also narrows the channels of the transaction to those
/// able to provide it (SPEC-041 L48).
/// </summary>
/// <remarks>
/// Scoped: it reads the channel adapters, whose lifetime a host may narrow to the request.
/// </remarks>
internal sealed class ClaimRequirementsBuilder
{
    /// <summary>
    /// Members of the <c>claims</c> parameter that request claims (OIDC Core §5.5): the ID token and the
    /// userinfo response. Veriqa completes a claim for the sign-in, whichever of the two asks for it.
    /// </summary>
    private static readonly string[] ClaimRequestTargets =
    [
        OpenIddictConstants.ClaimRequestMembers.IdToken,
        OpenIddictConstants.ClaimRequestMembers.UserInfo
    ];

    /// <summary>
    /// The one reading of the configured claim levels.
    /// </summary>
    private readonly ClaimRequirementSettings _settings;

    /// <summary>
    /// The canonical resolver — the switch of the claims completion form is read through it.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Registered channel adapters — the channels a sign-in can use when <c>acr_values</c> names none.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// The one judgement of the ways of obtaining the email (SPEC-041 C96).
    /// </summary>
    private readonly EmailCompletionAvailability _emailAvailability;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ClaimRequirementsBuilder> _logger;

    /// <summary>
    /// Creates the builder.
    /// </summary>
    /// <param name="settings">Reading of the configured claim levels.</param>
    /// <param name="resolver">Canonical resolver of the effective setting values.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="emailAvailability">Judgement of the ways of obtaining the email.</param>
    /// <param name="logger">Logger.</param>
    public ClaimRequirementsBuilder(
        ClaimRequirementSettings settings,
        IConfigurationResolver resolver,
        IEnumerable<IChannelAdapter> adapters,
        EmailCompletionAvailability emailAvailability,
        ILogger<ClaimRequirementsBuilder> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _emailAvailability = emailAvailability ?? throw new ArgumentNullException(nameof(emailAvailability));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds the requirement set of the transaction an authorization request creates, and the allowed
    /// channel types that set leaves.
    /// </summary>
    /// <param name="request">Authorization request, already validated by OpenIddict.</param>
    /// <param name="resolution">Ownership context of the request (tenant, <c>client_id</c>).</param>
    /// <param name="allowedChannelTypes">Channel types of <c>acr_values</c>; null admits every channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The decision; a failure with <see cref="OidcErrorCodes.OidcRequestInvalid"/> when the <c>claims</c>
    /// parameter is not a request object, or with <see cref="OidcErrorCodes.NoChannelForRequiredPhone"/> when
    /// the phone number is required and no channel of the request can provide it.
    /// </returns>
    public async ValueTask<Result<ClaimRequirementsDecision>> BuildAsync(
        OpenIddictRequest request,
        ResolutionContext resolution,
        IReadOnlyList<string>? allowedChannelTypes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolution);

        // The method gathers the levels of three sources (scopes, configuration, claims parameter), keeps
        // the senior one per claim, drops what the scopes of the request do not cover and what the core
        // issues itself, lowers a level no path can enforce, lowers what only the switched-off form would
        // collect, and finally applies the phone channel filter.

        if (!TryReadRequestedClaims(request, out var requestedClaims))
        {
            return Result<ClaimRequirementsDecision>.Failure(
                OidcErrorCodes.OidcRequestInvalid,
                "The claims parameter must be a JSON object whose id_token and userinfo members are objects.");
        }

        var scopes = request.GetScopes().ToHashSet(StringComparer.Ordinal);

        using var resolutionScope = new ConfigResolutionScope(resolution);
        var configured = await _settings.ResolveAsync(resolutionScope, cancellationToken);

        var claimScopes = CatalogClaimScopes.Of(configured.Catalog);
        var merged = new Dictionary<string, (ClaimRequirementLevel Level, ClaimRequirementSource Source)>(
            VeriqaClaimTypes.NameComparer);

        // 1. Scopes of the request: every claim of a requested scope, IfAvailable (SPEC-041 L40).
        foreach (var scope in scopes)
        {
            foreach (var claim in ClaimsOfScope(configured.Catalog, scope))
            {
                Raise(merged, claim, ClaimRequirementLevel.IfAvailable, ClaimRequirementSource.Request);
            }
        }

        // 2. Configuration: IfAvailable is no requirement of the configuration.
        foreach (var (claim, level) in configured.Levels)
        {
            if (level > ClaimRequirementLevel.IfAvailable)
            {
                Raise(merged, claim, level, ClaimRequirementSource.Configuration);
            }
        }

        // 3. The claims parameter (OIDC Core §5.5): essential — Required, voluntary — Optional.
        foreach (var (claim, level) in requestedClaims)
        {
            Raise(merged, claim, level, ClaimRequirementSource.Request);
        }

        var requirements = new List<ClaimRequirement>(merged.Count);

        foreach (var (claim, (level, source)) in merged)
        {
            // SPEC-041 C35: a claim whose scope the transaction does not grant would not reach a token, so it
            // is not required of anyone; the claims the core issues itself are never asked for.
            var claimScope = claimScopes.ScopeOf(claim);
            if (claimScope is null
                || !scopes.Contains(claimScope)
                || ScopesClaimsCatalogRules.CoreIssuedClaims.Contains(claim))
            {
                continue;
            }

            // SPEC-041 L92: a level no path can enforce is recorded as IfAvailable, whoever stated it. The
            // email has a path only while a method of its list is available on the page of the initiating
            // device to the tenant of the request (C96) — the surface every web step can fall back to.
            var hasCompletionPath = ScopesClaimsCatalogRules.HasCompletionPath(configured.Catalog, claim)
                && (!VeriqaClaimTypes.NameComparer.Equals(claim, VeriqaClaimTypes.Email)
                    || (await _emailAvailability.ResolveAsync(
                        resolutionScope, WebStepSurface.InitiatingPage, cancellationToken)).Count > 0);
            requirements.Add(new ClaimRequirement(
                claim,
                hasCompletionPath ? level : ClaimRequirementLevel.IfAvailable,
                source,
                hasCompletionPath));
        }

        var formEnabled = (await _resolver.ResolveAsync(
                AuthServerConfigKeys.ClaimCompletionFormEnabled,
                resolutionScope,
                ConfigDimensionValues.None,
                cancellationToken))
            .Value;

        if (!formEnabled)
        {
            LowerWithoutForm(requirements);
        }

        return ApplyPhoneChannelFilter(request.ClientId, requirements, allowedChannelTypes);
    }

    /// <summary>
    /// Lowers to <see cref="ClaimRequirementLevel.IfAvailable"/> every requirement the claims completion
    /// form would have to collect, when the application switched the form off (SPEC-041 R16, C34): such a
    /// claim is issued when the channel provides it and does not block the sign-in. The phone number and
    /// the email keep their level — each has completion paths of its own, which the switch does not
    /// govern. The completion path and the source of a requirement stay as they are: the switch is a
    /// choice of the application, not a fact about the claim.
    /// </summary>
    /// <param name="requirements">Requirement set of the transaction, lowered in place.</param>
    private static void LowerWithoutForm(List<ClaimRequirement> requirements)
    {
        for (var index = 0; index < requirements.Count; index++)
        {
            var requirement = requirements[index];

            if (requirement.Level is not ClaimRequirementLevel.IfAvailable
                && !ScopesClaimsCatalogRules.OwnCompletionPathClaims.Contains(requirement.Claim))
            {
                requirements[index] = requirement with { Level = ClaimRequirementLevel.IfAvailable };
            }
        }
    }

    /// <summary>
    /// Narrows the channels of the transaction to those able to provide a required phone number
    /// (SPEC-041 L48). Without a required phone number the allowed channel types stay as they came.
    /// </summary>
    /// <param name="clientId">Client of the request — named in the log entry of a refusal.</param>
    /// <param name="requirements">Requirement set of the transaction.</param>
    /// <param name="allowedChannelTypes">Channel types of <c>acr_values</c>; null admits every channel.</param>
    /// <returns>The decision, or the refusal when no channel remains.</returns>
    private Result<ClaimRequirementsDecision> ApplyPhoneChannelFilter(
        string? clientId,
        IReadOnlyList<ClaimRequirement> requirements,
        IReadOnlyList<string>? allowedChannelTypes)
    {
        var phoneRequired = requirements.Any(requirement =>
            requirement.Level is ClaimRequirementLevel.Required
            && string.Equals(requirement.Claim, VeriqaClaimTypes.PhoneNumber, VeriqaClaimTypes.NameComparison));

        if (!phoneRequired)
        {
            return Result<ClaimRequirementsDecision>.Success(
                new ClaimRequirementsDecision(requirements, allowedChannelTypes));
        }

        var candidates = _adapters
            .Where(adapter => allowedChannelTypes is null
                || allowedChannelTypes.Contains(adapter.ChannelType, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var capable = candidates
            .Where(adapter => adapter.Capabilities.PhoneNumber.Acquisition is not PhoneNumberAcquisition.None)
            .Select(adapter => adapter.ChannelType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (capable.Count > 0)
        {
            return Result<ClaimRequirementsDecision>.Success(
                new ClaimRequirementsDecision(requirements, capable.AsReadOnly()));
        }

        // An empty set must not reach the transaction: it reads as "every channel" (SPEC-041 L48).
        _logger.LogWarning(
            "The phone number is required, but no channel of the request can provide it; the authorization "
            + "request is refused and no transaction is created. ClientId: {ClientId}, Channels: {Channels}",
            clientId,
            string.Join(", ", candidates.Select(adapter => adapter.ChannelType)));

        return Result<ClaimRequirementsDecision>.Failure(
            OidcErrorCodes.NoChannelForRequiredPhone,
            "The phone number is required, but no channel available to this request can provide it.");
    }

    /// <summary>
    /// Keeps the senior level of a claim (SPEC-041 L40); between two equal levels the configuration is the
    /// source.
    /// </summary>
    /// <param name="merged">Levels gathered so far, by claim name.</param>
    /// <param name="claim">Claim name.</param>
    /// <param name="level">Level stated by the source.</param>
    /// <param name="source">Source of the level.</param>
    private static void Raise(
        Dictionary<string, (ClaimRequirementLevel Level, ClaimRequirementSource Source)> merged,
        string claim,
        ClaimRequirementLevel level,
        ClaimRequirementSource source)
    {
        if (!merged.TryGetValue(claim, out var known)
            || level > known.Level
            || (level == known.Level && source is ClaimRequirementSource.Configuration))
        {
            // The first spelling of the name stays the key: the catalog and the standard scopes are read
            // before the claims parameter, so a claim is recorded as a token names it.
            merged[claim] = (level, source);
        }
    }

    /// <summary>
    /// Claims of a scope: the rows the catalog declares under it and the claims of the standard scope of
    /// that name.
    /// </summary>
    /// <param name="catalog">Effective catalog.</param>
    /// <param name="scope">Scope name.</param>
    /// <returns>Claim names; a name may repeat.</returns>
    private static IEnumerable<string> ClaimsOfScope(IReadOnlyList<ScopeDefinition> catalog, string scope) =>
        catalog
            .Where(scopeRow => scopeRow is not null && string.Equals(scopeRow.Name, scope, StringComparison.Ordinal))
            .SelectMany(scopeRow => scopeRow.Claims ?? Enumerable.Empty<ClaimDefinition>())
            .Where(claim => claim is not null && !string.IsNullOrWhiteSpace(claim.Name))
            .Select(claim => claim.Name)
            .Concat(ClaimDestinationsHelper.StandardClaimsOf(scope));

    /// <summary>
    /// Reads the claims the <c>claims</c> parameter asks for (OIDC Core §5.5) from its <c>id_token</c> and
    /// <c>userinfo</c> members, parsed by OpenIddict: a claim marked <c>essential: true</c> is
    /// <see cref="ClaimRequirementLevel.Required"/>, any other one — voluntary —
    /// <see cref="ClaimRequirementLevel.Optional"/>. The <c>value</c> and <c>values</c> members are not
    /// enforced.
    /// </summary>
    /// <param name="request">Authorization request.</param>
    /// <param name="requested">Level asked of each claim; empty without the parameter.</param>
    /// <returns><c>false</c> when the parameter is present but is not a request object.</returns>
    private static bool TryReadRequestedClaims(
        OpenIddictRequest request,
        out IReadOnlyDictionary<string, ClaimRequirementLevel> requested)
    {
        var levels = new Dictionary<string, ClaimRequirementLevel>(VeriqaClaimTypes.NameComparer);
        requested = levels;

        if (request.GetParameter(OpenIddictConstants.Parameters.Claims) is null)
        {
            return true;
        }

        // OpenIddict answers null for a stated parameter that is not a JSON object.
        if (request.Claims is not { } claims)
        {
            return false;
        }

        foreach (var member in ClaimRequestTargets)
        {
            if (!claims.TryGetPropertyValue(member, out var node))
            {
                continue;
            }

            if (node is not JsonObject claimRequests)
            {
                return false;
            }

            foreach (var (claim, claimRequest) in claimRequests)
            {
                if (string.IsNullOrWhiteSpace(claim))
                {
                    continue;
                }

                var level = IsEssential(claimRequest) ? ClaimRequirementLevel.Required : ClaimRequirementLevel.Optional;
                if (!levels.TryGetValue(claim, out var known) || level > known)
                {
                    levels[claim] = level;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the request of one claim is marked essential — its <c>essential</c> member is the JSON
    /// literal <c>true</c>.
    /// </summary>
    /// <param name="claimRequest">Request of the claim: null or an object.</param>
    /// <returns><c>true</c> for an essential claim.</returns>
    private static bool IsEssential(JsonNode? claimRequest) =>
        claimRequest is JsonObject members
        && members.TryGetPropertyValue(OpenIddictConstants.ClaimRequestMembers.Essential, out var essential)
        && essential?.GetValueKind() is JsonValueKind.True;
}
