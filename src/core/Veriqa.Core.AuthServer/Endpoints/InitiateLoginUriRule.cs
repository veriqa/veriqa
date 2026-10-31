// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The one rule that decides whether the client's own login-initiation address
/// (<c>initiate_login_uri</c>, OIDC Dynamic Client Registration 1.0 §2) may be put on a page as the way
/// to start a sign-in over: "the stated and admissible address, or nothing". Both surfaces that offer
/// such a way — the expired sign-in window and the "sign-in session expired" page of the callback —
/// ask here, so the two cannot disagree on which addresses are safe to link to.
/// <para>
/// A stated value is taken only when it is an ABSOLUTE URL of an allowed scheme — https, or http in
/// Development, where an entry point without TLS is a normal local arrangement. Anything else is
/// treated as unstated and logged: the value comes from configuration, and a page that put an
/// arbitrary scheme into an href would let a setting turn into script on a page the deployment
/// serves.
/// </para>
/// <para>
/// What a surface offers when the answer is "nothing" is its own business and is not decided here:
/// the sign-in window repeats the authorize request it is serving, the callback page closes the
/// window.
/// </para>
/// </summary>
internal static class InitiateLoginUriRule
{
    /// <summary>
    /// Resolves the login-initiation address of the application the context names and applies the
    /// admissibility rule to it.
    /// </summary>
    /// <param name="pageSettingsResolver">Resolver of the level-owned sign-in page settings.</param>
    /// <param name="resolution">Ownership context the address is resolved over.</param>
    /// <param name="clientId">Client the address is resolved for — named in the log entry.</param>
    /// <param name="environment">Host environment.</param>
    /// <param name="logger">Logger of the calling surface.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The address to link to; null when none is stated or the stated one is refused.</returns>
    public static async Task<string?> ResolveAdmissibleAsync(
        AuthPageSettingsResolver pageSettingsResolver,
        ResolutionContext resolution,
        string? clientId,
        IHostEnvironment environment,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var stated = await pageSettingsResolver.ResolveInitiateLoginUriAsync(resolution, cancellationToken);

        if (string.IsNullOrWhiteSpace(stated))
        {
            return null;
        }

        if (IsAdmissible(stated, environment))
        {
            return stated;
        }

        logger.LogWarning(
            "The initiate_login_uri stated for client {ClientId} is not an absolute https URL and is "
            + "ignored; the page offers its own fallback instead of a link to it",
            clientId);

        return null;
    }

    /// <summary>
    /// Tells whether a stated login-initiation address may be put on the page: an absolute URL whose
    /// scheme is https — or http, in Development only.
    /// </summary>
    /// <param name="stated">Value stated by the application level.</param>
    /// <param name="environment">Host environment.</param>
    /// <returns><see langword="true"/> when the address is admissible.</returns>
    private static bool IsAdmissible(string stated, IHostEnvironment environment)
    {
        if (!Uri.TryCreate(stated, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && environment.IsDevelopment());
    }
}
