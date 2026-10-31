// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Antiforgery;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The form behind the cancel button of a page — "Cancel sign-in" of the sign-in window, "Cancel
/// confirmation" of the entry page of a confirmation, and "Cancel sign-in" or "Cancel confirmation" of the
/// scanner page of the unified hop QR code (SPEC-007 UI-122): where it posts and the AntiForgery
/// field it carries. The caller states it because only the caller knows whether cancelling is a thing
/// this surface may offer and where the refusal is taken, and because the token is issued for the response
/// of the request, which a renderer does not see.
/// <para>
/// The session identifier of the form is not repeated here: the page takes it from
/// <see cref="AuthPageRenderContext.SessionId"/>.
/// </para>
/// </summary>
public sealed class AuthPageCancelForm
{
    /// <summary>
    /// Root-relative URL the form posts to. The path base is applied by the page itself, as it is to
    /// every other root-relative URL of the window (<see cref="AuthPageRenderContext.PathBase"/>).
    /// </summary>
    public required string ActionUrl { get; init; }

    /// <summary>
    /// Name of the hidden field carrying the AntiForgery token.
    /// </summary>
    public required string AntiforgeryFieldName { get; init; }

    /// <summary>
    /// AntiForgery request token of the form.
    /// </summary>
    public required string AntiforgeryToken { get; init; }

    /// <summary>
    /// Issues the form for the response of the request: the AntiForgery token is stored for it, and the form
    /// posts to the given route. The one construction of the form for every page that carries it.
    /// </summary>
    /// <param name="httpContext">HTTP context — the response the AntiForgery token is issued for.</param>
    /// <param name="actionUrl">Root-relative route the form posts to.</param>
    /// <returns>The form; null when no request token could be issued — the page then carries no button.</returns>
    internal static AuthPageCancelForm? Issue(HttpContext httpContext, string actionUrl)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var tokens = httpContext.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(httpContext);
        if (string.IsNullOrEmpty(tokens.RequestToken))
        {
            return null;
        }

        return new AuthPageCancelForm
        {
            ActionUrl = actionUrl,
            AntiforgeryFieldName = tokens.FormFieldName,
            AntiforgeryToken = tokens.RequestToken
        };
    }
}
