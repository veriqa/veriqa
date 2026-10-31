// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// OIDC client parameters from the Veriqa:OpenIddict root configuration section,
/// which contains a nested Clients array.
/// </summary>
public sealed class OidcClientsOptions
{
    /// <summary>
    /// Name of the OpenIddict root configuration section (contains a nested Clients array).
    /// </summary>
    public const string SectionName = "Veriqa:OpenIddict";

    /// <summary>
    /// List of registered OIDC clients.
    /// </summary>
    public IReadOnlyList<OidcClientOptions> Clients { get; set; } = [];

    /// <summary>
    /// Name of an application as the texts show it: the <c>DisplayName</c> of its entry, else the client
    /// identifier itself (SPEC-017 §5.2, field No. 2) — one rule for a sign-in and a confirmation.
    /// </summary>
    /// <param name="clientId">OIDC client identifier.</param>
    /// <returns>The name; null when the identifier is missing.</returns>
    internal string? ApplicationNameOf(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var entry = Clients.FirstOrDefault(client => string.Equals(client.ClientId, clientId, StringComparison.Ordinal));

        return string.IsNullOrWhiteSpace(entry?.DisplayName) ? clientId : entry.DisplayName;
    }

    /// <summary>
    /// Deployment gate for the client compatibility quirks (protective ceiling, CFG-212). False by default:
    /// without it the per-client <see cref="OidcClientOptions.CompatibilityQuirks"/> sets are intersected
    /// with the empty core-level set and no relaxation applies. The gate is deployment-wide rather than
    /// per-client, which is why it lives next to the Clients array it governs rather than inside a client
    /// entry.
    /// </summary>
    public bool CompatibilityQuirksAllowApplicationOverride { get; set; }
}
