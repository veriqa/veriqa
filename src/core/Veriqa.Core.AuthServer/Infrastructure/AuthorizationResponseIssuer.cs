// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Http.Extensions;
using OpenIddict.Server;

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// The issuer identifier an authorization response assembled by hand states in its <c>iss</c> parameter
/// (RFC 9207), so that such a response is told apart from one of another server exactly as the
/// responses OpenIddict writes are.
/// </summary>
/// <remarks>
/// The server metadata announces <c>authorization_response_iss_parameter_supported</c>, and OpenIddict
/// attaches <c>iss</c> to every authorization response it sends to a <c>redirect_uri</c>, an error
/// included. A client that follows RFC 9207 checks the parameter before it reads anything else, so a
/// response without it is rejected before its <c>error</c> is seen.
/// <para>
/// The value is derived by the rule OpenIddict applies to its own responses and to the <c>issuer</c> of
/// the metadata: the issuer set in its server options, otherwise the base address of the current request
/// (scheme, host and path base), in the canonical form <see cref="Uri.AbsoluteUri"/> gives — the
/// trailing slash of an address with no path included. The options read are OpenIddict's own, not the
/// product setting they were filled from, so the two cannot differ by a normalization.
/// </para>
/// </remarks>
internal static class AuthorizationResponseIssuer
{
    /// <summary>
    /// Resolves the issuer identifier for a response of the current request.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <returns>
    /// The identifier; null when none can be stated — no issuer is set and the request names no host,
    /// or its address is not a valid absolute URI.
    /// </returns>
    public static string? Resolve(HttpContext httpContext)
    {
        var issuer = httpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>()
            .CurrentValue.Issuer;

        if (issuer is null)
        {
            var request = httpContext.Request;
            if (!request.Host.HasValue
                || !Uri.TryCreate(
                    UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase),
                    UriKind.Absolute,
                    out issuer))
            {
                return null;
            }
        }

        return issuer.IsAbsoluteUri ? issuer.AbsoluteUri : null;
    }
}
