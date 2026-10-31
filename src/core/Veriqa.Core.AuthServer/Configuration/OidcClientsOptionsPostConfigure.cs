// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veriqa.Core.AuthServer.Constants;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// PostConfigure for the OIDC client options: normalization plus, on a configuration reload,
/// per-entry rejection of broken clients (SPEC-012 CFG-210).
/// <para>
/// Normalization fills null collections with empty lists before validation runs — that part is the
/// job of IPostConfigureOptions rather than IValidateOptions.
/// </para>
/// <para>
/// Rejection has exactly one mode of operation: a RELOAD of a watched configuration source. At
/// startup nothing is dropped — a broken entry reaches the validator, fails and stops the process,
/// because a deployment that cannot describe its own clients must not start. Once the application is
/// running, the same broken entry may not take its neighbours down with it: it is removed from the
/// effective set and reported, so an operator sees a disabled client instead of a host that silently
/// lives on the previous snapshot.
/// </para>
/// <para>
/// Whether this step runs at startup or on a reload, it acts only on the entries the binder BUILT —
/// that is the whole reach it has. An entry carrying a value that does not convert to the type its
/// property declares is dropped earlier, inside the binder, and never arrives here at all. That entry
/// is reported by the snapshot report of the configuration mechanism, over
/// <see cref="Snapshot.OidcClientEntryBindingCatalog"/> — which reads the raw configuration section for
/// exactly that reason.
/// </para>
/// </summary>
public sealed class OidcClientsOptionsPostConfigure : IPostConfigureOptions<OidcClientsOptions>
{
    /// <summary>
    /// Application lifetime — the only signal separating startup from a later reload.
    /// </summary>
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// Logger of rejected client entries.
    /// </summary>
    private readonly ILogger<OidcClientsOptionsPostConfigure> _logger;

    /// <summary>
    /// The claims catalog section — the source of the core catalog of scopes and claims an entry is
    /// judged against on a reload, exactly as the validator judges it at startup.
    /// </summary>
    private readonly IOptions<ScopesClaimsOptions> _scopesClaims;

    /// <summary>
    /// Creates the post-configure step.
    /// </summary>
    /// <param name="lifetime">Application lifetime (startup vs reload).</param>
    /// <param name="logger">Logger of rejected client entries.</param>
    /// <param name="scopesClaims">The claims catalog section holding the core catalog of scopes and claims.</param>
    public OidcClientsOptionsPostConfigure(
        IHostApplicationLifetime lifetime,
        ILogger<OidcClientsOptionsPostConfigure> logger,
        IOptions<ScopesClaimsOptions> scopesClaims)
    {
        _lifetime = lifetime;
        _logger = logger;
        _scopesClaims = scopesClaims ?? throw new ArgumentNullException(nameof(scopesClaims));
    }

    /// <summary>
    /// Normalizes the options (null collections become empty lists) and, on a reload, drops the
    /// client entries that break the per-entry rules.
    /// </summary>
    /// <param name="name">Named options instance name.</param>
    /// <param name="options">Options instance to normalize.</param>
    public void PostConfigure(string? name, OidcClientsOptions options)
    {
        // Normalize the client list — the configuration binder may return null
        options.Clients ??= [];

        foreach (var client in options.Clients)
        {
            // Normalize the nested lists for each client
            client.AllowedRedirectUris ??= [];
            client.AllowedScopes ??= [];

            ExpandCompatibilityProfile(client);
        }

        // ApplicationStarted is signalled once the host has started, so an options instance built
        // after that point comes from a reload of a watched configuration source.
        if (!_lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        RejectBrokenClients(options);
    }

    /// <summary>
    /// Expands the client's compatibility profile into quirk keys and unions them with the keys the
    /// entry lists explicitly.
    /// </summary>
    /// <param name="client">Client entry to normalize in place.</param>
    /// <remarks>
    /// Done here, in the options pipeline, so that the readers of the bound options — the validator and
    /// the startup diagnostics — see one expanded set. The resolver does not read these options: the
    /// application level of <c>AuthServerConfigKeys.ClientCompatibilityQuirks</c> reads the raw client
    /// entry and expands the profile itself through the same <see cref="ClientCompatibilityProfiles.Expand"/>,
    /// and the quirk handlers read that key through the resolver. The step is idempotent: post-configure
    /// runs again on every configuration reload, and a union through a set cannot accumulate duplicates.
    /// An unknown name expands to nothing and is reported by the validator, which runs after this.
    /// </remarks>
    private static void ExpandCompatibilityProfile(OidcClientOptions client)
    {
        if (string.IsNullOrWhiteSpace(client.CompatibilityProfile))
        {
            return;
        }

        var fromProfile = ClientCompatibilityProfiles.Expand(client.CompatibilityProfile);
        if (fromProfile.Count is 0)
        {
            return;
        }

        // Ordinal, like the registries: a differently-cased spelling is not the same key, and the
        // validator reports it instead of this step merging it away.
        var effective = new HashSet<string>(client.CompatibilityQuirks ?? [], StringComparer.Ordinal);
        effective.UnionWith(fromProfile);

        client.CompatibilityQuirks = [.. effective];
    }

    /// <summary>
    /// Removes the client entries that break the per-entry rules, reporting each removal once with all
    /// of its reasons. Leaves the entries in place when that would empty a non-empty set: the effective
    /// set of clients would then be useless anyway, and it is better for the validator to fail so the
    /// host keeps serving the last valid snapshot.
    /// </summary>
    private void RejectBrokenClients(OidcClientsOptions options)
    {
        var kept = new List<OidcClientOptions>(options.Clients.Count);
        var rejected = new List<(OidcClientOptions Client, IReadOnlyList<string> Failures)>();
        var scopeCatalog = _scopesClaims.Value.Scopes;

        for (var i = 0; i < options.Clients.Count; i++)
        {
            var client = options.Clients[i];
            var failures = OidcClientValidationRules.Collect(client, i, scopeCatalog);

            if (failures.Count is 0)
            {
                kept.Add(client);
            }
            else
            {
                rejected.Add((client, failures));
            }
        }

        if (rejected.Count is 0)
        {
            return;
        }

        if (kept.Count is 0)
        {
            // Every entry is broken. Dropping them all would leave the host with an empty — and
            // therefore unusable — client set while reporting success. Keeping them makes the
            // validator fail, and OidcClientsOptionsAccessor then serves the last valid snapshot.
            return;
        }

        foreach (var (client, failures) in rejected)
        {
            _logger.LogWarning(
                "OIDC client '{ClientId}' was dropped from the effective configuration after a reload: {Failures}",
                client.ClientId,
                string.Join(" ", failures));
        }

        options.Clients = kept;
    }
}
