// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The browser mark of a hop link (SPEC-041 L93): a cookie the first person's resolve of the link hands
/// its browser, so a later resolve from the same browser is told from a reopening elsewhere. The value is
/// the identifier of the transaction protected by ASP.NET Core Data Protection under a purpose of its own —
/// the platform's integrity, not cryptography of ours, over the host's key ring, as for the hop token.
/// <para>
/// The mark is a hint for a risk signal and nothing more: it grants no right and is read by the resolve
/// endpoint only, when it decides whether a repeat resolve is a reopening elsewhere.
/// </para>
/// </summary>
internal sealed class HopBrowserMark
{
    /// <summary>
    /// Protector of the browser-mark purpose.
    /// </summary>
    private readonly IDataProtector _protector;

    /// <summary>
    /// The reading of the effective hop settings — the base path the cookie is scoped to.
    /// </summary>
    private readonly HopModeSettings _hopModeSettings;

    /// <summary>
    /// Creates the mark from the Data Protection provider of the host.
    /// </summary>
    /// <param name="dataProtectionProvider">Data Protection provider (the key ring is the host's).</param>
    /// <param name="hopModeSettings">The reading of the effective hop settings.</param>
    public HopBrowserMark(IDataProtectionProvider dataProtectionProvider, HopModeSettings hopModeSettings)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(hopModeSettings);

        _protector = dataProtectionProvider.CreateProtector(HopModeConstants.BrowserMarkProtectorPurpose);
        _hopModeSettings = hopModeSettings;
    }

    /// <summary>
    /// Hands the browser of the request the mark of the transaction. It adds a <c>Set-Cookie</c> to the
    /// answer and nothing else: the cookie is visible to the hop path of the host only, is never read by a
    /// script, and lives as long as the transaction.
    /// </summary>
    /// <param name="httpContext">Context of the resolve request; the answer has not started.</param>
    /// <param name="transaction">The transaction whose link was resolved.</param>
    /// <param name="now">The server's current moment the remaining lifetime of the transaction is counted from.</param>
    public void Issue(HttpContext httpContext, Transaction transaction, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(transaction);

        // The lifetime is the remainder of the transaction TTL by the server's clock, sent as Max-Age: an
        // absolute Expires would be compared with the browser's clock, and a phone clock running ahead would
        // drop the mark early and turn a return to the same browser into a reopening elsewhere. A transaction
        // with nothing left gets no mark.
        var remaining = transaction.ExpiresAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        // The path carries PathBase: under a sub-path the browser matches the cookie path against the full
        // request path, and without the prefix the cookie would never come back.
        httpContext.Response.Cookies.Append(
            HopModeConstants.BrowserMarkCookieName,
            _protector.Protect(transaction.Id.ToString()),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = $"{httpContext.Request.PathBase}{_hopModeSettings.HopPath}",
                MaxAge = remaining
            });
    }

    /// <summary>
    /// Whether the request carries a valid mark of the transaction. Never throws: no cookie, a value Data
    /// Protection refuses (a forgery, another purpose, a key no longer in the ring) and the mark of another
    /// transaction all read as no mark.
    /// </summary>
    /// <param name="request">The resolve request.</param>
    /// <param name="transaction">The transaction whose link was resolved.</param>
    /// <returns><c>true</c> when the mark was issued by this host for this transaction.</returns>
    public bool IsPresentFor(HttpRequest request, Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!request.Cookies.TryGetValue(HopModeConstants.BrowserMarkCookieName, out var value)
            || string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            return string.Equals(_protector.Unprotect(value), transaction.Id.ToString(), StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            // Data Protection reports every refusal — a malformed value, tampering, another purpose, a
            // missing key — as this one exception type. The value is not logged.
            return false;
        }
    }
}
