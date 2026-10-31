// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The page of the claims completion on the device that confirmed the sign-in, opened by the link with the
/// form token (<see cref="OidcEndpoints.ClaimCompletionFormPage"/>, SPEC-041 L52, N78). It is mapped with the
/// callback, under the same rate limit (<see cref="AuthorizeCallbackEndpoint.MapAuthorizeCallbackEndpoint"/>).
/// </summary>
/// <remarks>
/// The page only reads: opening the link — by the user or by a service that previews links — changes
/// nothing on the transaction and spends nothing, so the same link can be opened again. The right is checked
/// by <see cref="ClaimCompletionActionRight"/>, the one check the actions of the page go through as well.
/// </remarks>
internal static class ClaimCompletionFormPageEndpoint
{
    /// <summary>
    /// Handles the opening of the link: the form of the web step while the form token carries the right,
    /// otherwise the answer the page ends in.
    /// </summary>
    internal static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        ClaimCompletionActionRight right,
        ClaimCompletionPageResponder pages)
    {
        var query = httpContext.Request.Query;
        var formToken = query[ClaimCompletionFormFields.FormToken].FirstOrDefault();

        var (transaction, requestLanguage, refusal) = await right.CheckFormTokenAsync(
            httpContext,
            query[OidcConstants.SessionIdParameterName].FirstOrDefault(),
            formToken);

        if (refusal is not null)
        {
            return refusal;
        }

        return await pages.RespondCrossDeviceCarriedAsync(httpContext, transaction!, formToken!, requestLanguage);
    }

    /// <summary>
    /// Builds the address of the page for a transaction and a form token, without the path base — the link
    /// of the channel message is this address on the base of the issuer.
    /// </summary>
    /// <param name="sessionId">External session identifier of the transaction.</param>
    /// <param name="formToken">The form token.</param>
    /// <returns>Path and query of the page.</returns>
    internal static string BuildPath(string sessionId, string formToken) =>
        $"{OidcEndpoints.ClaimCompletionFormPage}?{OidcConstants.SessionIdParameterName}={Uri.EscapeDataString(sessionId)}"
        + $"&{ClaimCompletionFormFields.FormToken}={Uri.EscapeDataString(formToken)}";
}
