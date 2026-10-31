// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The answer mark of the core confirmation page: a cookie the channel hands the browser it attached the
/// channel identity from, so that an answer bound to that browser
/// (<see cref="ChannelSubjectBinding.AnswerBrowserBound"/>) is taken from it and from no other. The value is
/// the identifier of the transaction protected by ASP.NET Core Data Protection under a purpose of its own —
/// the platform's integrity over the host's key ring, not cryptography of ours.
/// <para>
/// One cookie name serves every transaction: a browser that is handed the mark of another transaction
/// replaces it, and following the first letter again hands the mark back.
/// </para>
/// </summary>
internal sealed class CoreConfirmPageAnswerMark
{
    /// <summary>
    /// Path of the core page that asks what a confirmation transaction is about (SPEC-039 R37). Its answers
    /// post under it, so a cookie scoped to it comes back with the question and with both answers.
    /// </summary>
    /// <remarks>
    /// The value duplicates the route constant of the auth server: the channel contour is below the auth
    /// server and does not reference it. A mark scoped anywhere else never comes back, so the acceptance
    /// check of the flow — an answer taken in the browser of the letter — covers both spellings.
    /// </remarks>
    internal const string PagePath = "/auth/transaction/confirm";

    /// <summary>
    /// Name of the cookie carrying the mark.
    /// </summary>
    private const string CookieName = "veriqa_confirm_answer";

    /// <summary>
    /// Purpose of the Data Protection protector of the mark: isolated from every other use of Data
    /// Protection, so no other protected payload reads as a mark, nor a mark as anything else.
    /// </summary>
    private const string ProtectorPurpose = "Veriqa.Core.ConfirmPage.AnswerMark.v1";

    /// <summary>
    /// Protector of the mark purpose.
    /// </summary>
    private readonly IDataProtector _protector;

    /// <summary>
    /// Creates the mark from the Data Protection provider of the host.
    /// </summary>
    /// <param name="dataProtectionProvider">Data Protection provider (the key ring is the host's).</param>
    public CoreConfirmPageAnswerMark(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    /// <summary>
    /// Hands the browser of the request the mark of the transaction. It adds a <c>Set-Cookie</c> to the
    /// answer and nothing else: the cookie is sent to the confirmation page only, is never read by a
    /// script, and lives as long as the transaction.
    /// </summary>
    /// <param name="httpContext">Context of the request; the answer has not started.</param>
    /// <param name="transaction">The transaction the answer is bound for.</param>
    /// <param name="now">The server's current moment the remaining lifetime of the transaction is counted from.</param>
    public void Issue(HttpContext httpContext, Transaction transaction, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(transaction);

        // The lifetime is the remainder of the transaction TTL by the server's clock, sent as Max-Age: an
        // absolute Expires would be compared with the browser's clock. A transaction with nothing left gets
        // no mark — there is no answer left to take.
        var remaining = transaction.ExpiresAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        // The path carries PathBase: under a sub-path the browser matches the cookie path against the full
        // request path, and without the prefix the cookie would never come back. Lax is enough: a cross-site
        // navigation that carries the mark only opens the question, and both answers are POSTs, which Lax
        // does not carry cross-site and the antiforgery token guards besides.
        httpContext.Response.Cookies.Append(
            CookieName,
            _protector.Protect(transaction.Id.ToString()),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = $"{httpContext.Request.PathBase}{PagePath}",
                MaxAge = remaining
            });
    }

    /// <summary>
    /// Whether the request carries a valid mark of the transaction. Never throws: no cookie, a value Data
    /// Protection refuses (a forgery, another purpose, a key no longer in the ring) and the mark of another
    /// transaction all read as no mark.
    /// </summary>
    /// <param name="request">The request to the confirmation page.</param>
    /// <param name="transaction">The transaction the request addresses.</param>
    /// <returns><c>true</c> when the mark was issued by this host for this transaction.</returns>
    public bool IsPresentFor(HttpRequest request, Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
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
