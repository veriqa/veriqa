// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

using Veriqa.Core.ChannelAdapter.WebProviders.Constants;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Pages;

/// <summary>
/// The mark of the browser that came back from the provider: a cookie that ties that browser to its
/// session of the page — one transaction and one account. The cookie carries a random value and
/// nothing of the user; the value is protected by ASP.NET Core Data Protection under a purpose of its
/// own, the platform's integrity over the host's key ring. The session itself lives in the state store
/// under that value.
/// <para>
/// The cookie is scoped to the routes of its channel, so a mark of one channel never travels to
/// another, and a browser handed the mark of a later sign-in on the same channel replaces the earlier
/// one, as a chat shows its latest conversation.
/// </para>
/// </summary>
internal sealed class WebProviderPageMark
{
    /// <summary>
    /// Name of the cookie carrying the mark.
    /// </summary>
    private const string CookieName = "veriqa_web_provider_page";

    /// <summary>
    /// Purpose of the Data Protection protector of the mark: isolated from every other use of Data
    /// Protection, so no other protected payload reads as a mark, nor a mark as anything else.
    /// </summary>
    private const string ProtectorPurpose = "Veriqa.Core.WebProviders.PageMark.v1";

    /// <summary>
    /// Number of random bytes of a mark.
    /// </summary>
    private const int MarkLengthBytes = 32;

    /// <summary>
    /// Data Protection provider of the host; the protector of a channel is created per channel.
    /// </summary>
    private readonly IDataProtectionProvider _dataProtectionProvider;

    /// <summary>
    /// Creates the mark over the Data Protection provider of the host.
    /// </summary>
    /// <param name="dataProtectionProvider">Data Protection provider (the key ring is the host's).</param>
    public WebProviderPageMark(IDataProtectionProvider dataProtectionProvider)
    {
        _dataProtectionProvider = dataProtectionProvider ?? throw new ArgumentNullException(nameof(dataProtectionProvider));
    }

    /// <summary>
    /// A new random mark — the key the session of the page is stored under.
    /// </summary>
    /// <returns>The mark.</returns>
    public static string NewMark() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(MarkLengthBytes));

    /// <summary>
    /// Hands the browser of the request the mark of its session. It adds a <c>Set-Cookie</c> to the
    /// answer and nothing else: the cookie goes only to the routes of the channel, is never read by a
    /// script and lives no longer than the session.
    /// </summary>
    /// <param name="httpContext">Context of the request; the answer has not started.</param>
    /// <param name="channelType">Channel of the session.</param>
    /// <param name="mark">The mark the session is stored under.</param>
    /// <param name="lifetime">Remaining lifetime of the session by the server's clock; positive.</param>
    public void Issue(HttpContext httpContext, string channelType, string mark, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrEmpty(mark);

        // The lifetime goes as Max-Age: an absolute Expires would be compared with the browser's clock.
        // The path carries PathBase: under a sub-path the browser matches the cookie path against the
        // full request path. Lax is enough, and it is what a provider answering with form_post needs:
        // the cookie set on that cross-site POST comes back with the top-level GET of the page it
        // redirects to, while the answers and the presses are POSTs Lax does not carry cross-site and
        // the anti-forgery token guards besides.
        httpContext.Response.Cookies.Append(
            CookieName,
            ProtectorOf(channelType).Protect(mark),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = $"{httpContext.Request.PathBase}{WebProviderRoutes.ChannelPath(channelType)}",
                MaxAge = lifetime
            });
    }

    /// <summary>
    /// Reads the mark the request carries for a channel. Never throws: no cookie, and a value Data
    /// Protection refuses (a forgery, another purpose or channel, a key no longer in the ring), read as
    /// no mark.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="channelType">Channel of the route.</param>
    /// <returns>The mark; <c>null</c> — the request carries none of this channel.</returns>
    public string? Read(HttpRequest request, string channelType)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return ProtectorOf(channelType).Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Data Protection reports every refusal — a malformed value, tampering, another purpose, a
            // missing key — as this one exception type. The value is not logged.
            return null;
        }
    }

    /// <summary>
    /// The protector of the marks of one channel: the channel is part of the purpose, so a mark is
    /// valid only for the channel that issued it.
    /// </summary>
    /// <param name="channelType">The channel.</param>
    /// <returns>The protector.</returns>
    private IDataProtector ProtectorOf(string channelType) =>
        _dataProtectionProvider.CreateProtector(ProtectorPurpose, channelType);
}
