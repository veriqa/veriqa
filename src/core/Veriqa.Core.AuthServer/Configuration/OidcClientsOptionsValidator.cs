// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Validator for the OIDC client options. Verifies the configuration of each client every time the
/// options are created — at startup through ValidateOnStart, and again after every reload of a
/// watched configuration source.
/// <para>
/// The unit of MOST rules is a SINGLE client entry (<see cref="OidcClientValidationRules"/>), not the
/// whole array. At startup that distinction is invisible: any broken entry fails validation and stops
/// the process, which is the correct outcome for a deployment error. On a reload the broken entries
/// have already been dropped by <see cref="OidcClientsOptionsPostConfigure"/>, so what reaches this
/// validator is the surviving set and the neighbours keep working.
/// </para>
/// <para>
/// Uniqueness of <see cref="OidcClientOptions.ClientId"/> is the one rule of the SET (SPEC-012
/// CFG-155). It cannot be a per-entry rule: both duplicates are valid on their own, so the
/// reload-time dropper has nothing to remove and picking a winner would be the very shadowing the
/// rule prevents. A reload that introduces a duplicate therefore fails as a whole, and the accessor
/// keeps serving the last valid snapshot instead of disabling the client.
/// </para>
/// <para>
/// A reload that still fails here does not stop the process, and no read of the options is left
/// holding the failure. <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> throws on every read
/// until the configuration is fixed: the reads of the sign-in path go through
/// <see cref="OidcClientsOptionsAccessor"/>, which serves the last valid snapshot, and a read taken
/// straight from the monitor happens inside the guard the resolver puts around one level of one key
/// (SPEC-012 CFG-233), which degrades that level instead of failing the resolution.
/// <see cref="IOptions{TOptions}.Value"/> survives such a reload on its own because it caches the
/// instance created on its first read, and that read happens at startup: the client seeder is a
/// hosted service taking it in the constructor.
/// </para>
/// <para>
/// The monitor also reads the options ITSELF on the announcement path — it re-creates the instance
/// before notifying its change subscribers — and that read is behind neither guard, so this is where
/// the failure of such a reload actually surfaces: the change notification is not delivered and the
/// subscribers do not run for it, while the process keeps going. That costs nothing here, because a
/// reload that does not validate changes no effective value — every consumer keeps the snapshot it
/// already had — and the next reload that does validate announces itself normally.
/// </para>
/// </summary>
public sealed class OidcClientsOptionsValidator : IValidateOptions<OidcClientsOptions>
{
    /// <summary>
    /// The claims catalog section — the source of the core catalog of scopes and claims an entry is
    /// judged against (SPEC-041 C30). Bound on its own rather than read off the root options: reading the
    /// root would validate all of it on every read of the client entries.
    /// </summary>
    private readonly IOptions<ScopesClaimsOptions> _scopesClaims;

    /// <summary>
    /// Creates the validator of the OIDC client options.
    /// </summary>
    /// <param name="scopesClaims">The claims catalog section holding the core catalog of scopes and claims.</param>
    public OidcClientsOptionsValidator(IOptions<ScopesClaimsOptions> scopesClaims)
    {
        _scopesClaims = scopesClaims ?? throw new ArgumentNullException(nameof(scopesClaims));
    }

    /// <summary>
    /// Validates the OIDC client options.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Options instance to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, OidcClientsOptions options)
    {
        // The method verifies the configuration of each client

        var failures = new List<string>();
        var scopeCatalog = _scopesClaims.Value.Scopes;

        for (var i = 0; i < options.Clients.Count; i++)
        {
            // Normalization was done in OidcClientsOptionsPostConfigure — collections are guaranteed non-null
            failures.AddRange(OidcClientValidationRules.Collect(options.Clients[i], i, scopeCatalog));
        }

        // ClientId addresses the whole entry — every reading site looks it up and takes the first
        // match — so a duplicate silently drops everything the other entries state. Ordinal is the
        // comparison those lookups use.
        if (options.Clients.Select(client => client.ClientId).Distinct(StringComparer.Ordinal).Count()
            != options.Clients.Count)
        {
            failures.Add("Clients: ClientId must be unique across the Clients array.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
