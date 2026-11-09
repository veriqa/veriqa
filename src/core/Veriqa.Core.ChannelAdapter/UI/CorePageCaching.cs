// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The one caching directive of the answers of the "Core" contour that carry a way into a transaction — the
/// identifier of its session, a hop token, the QR code and the links into the channels — so that neither the
/// browser nor a cache between it and the server keeps a copy for whoever comes next. It lives in this assembly
/// because it is the lowest project those pages reference.
/// <para>
/// The value is the one the anti-forgery tokens of ASP.NET Core write on every answer that issues them: a page
/// that also offers a form finds the directive already in place, the tokens leave it as it is, and no warning
/// about overridden caching headers reaches the log. A bare <c>no-store</c> would be overwritten by them with a
/// warning on every answer.
/// </para>
/// </summary>
internal static class CorePageCaching
{
    /// <summary>
    /// The value of <c>Cache-Control</c>.
    /// </summary>
    public static readonly string NotCached =
        $"{CacheControlHeaderValue.NoCacheString}, {CacheControlHeaderValue.NoStoreString}";

    /// <summary>
    /// Marks the answer as not to be cached. Called before anything is written to the body.
    /// </summary>
    /// <param name="response">The answer.</param>
    public static void Forbid(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers[HeaderNames.CacheControl] = NotCached;
    }
}
