// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Carries what an action of the email block of the page of the claims completion shows besides the
/// transaction itself — the error of the block, its address step brought back and the address typed into it
/// — over the redirect that answers the action, to the one page load that follows it (post/redirect/get).
/// Without it such an action would have to answer its POST with the page itself, and reloading that page
/// would post the action again: send one more mail, spend one more attempt of the code. The values typed
/// into the simple fields are not carried here: the script of the page keeps them in the tab.
/// </summary>
/// <remarks>
/// The carry is a cookie protected by ASP.NET Core Data Protection under a purpose of its own, bound to the
/// transaction and to the surface of the page, read once and removed by the read; it lives a minute — the
/// time the redirect takes, with a margin. Nothing of it is kept on the server or written to the log.
/// </remarks>
internal sealed class ClaimCompletionPageCarry
{
    /// <summary>
    /// Name of the cookie carrying what the page shows next.
    /// </summary>
    private const string CookieName = "veriqa_claims_carry";

    /// <summary>
    /// Purpose of the Data Protection protector of the carry: isolated from every other use of Data
    /// Protection, so no other protected payload reads as a carry, nor a carry as anything else.
    /// </summary>
    private const string ProtectorPurpose = "Veriqa.Core.ClaimCompletion.PageCarry.v1";

    /// <summary>
    /// Longest protected value the cookie takes — under the 4 KB a browser keeps for one cookie, its name
    /// and attributes included. Only a long typed address makes a value longer; it is then left out.
    /// </summary>
    private const int MaxCookieValueLength = 3600;

    /// <summary>
    /// Lifetime of the cookie: the redirect follows at once, so a minute is a margin, not a wait.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Protector of the carry purpose.
    /// </summary>
    private readonly IDataProtector _protector;

    /// <summary>
    /// Creates the carry from the Data Protection provider of the host.
    /// </summary>
    /// <param name="dataProtectionProvider">Data Protection provider (the key ring is the host's).</param>
    public ClaimCompletionPageCarry(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    /// <summary>
    /// Hands the browser of the request what the next load of its page shows. It adds a <c>Set-Cookie</c> to
    /// the answer and nothing else.
    /// </summary>
    /// <param name="httpContext">Context of the action request; the answer has not started.</param>
    /// <param name="transactionId">The transaction of the page.</param>
    /// <param name="crossDevice">Whether the page is the one of the device that confirmed the sign-in.</param>
    /// <param name="shown">What the page shows.</param>
    public void Issue(HttpContext httpContext, TransactionId transactionId, bool crossDevice, ClaimCompletionShownAgain shown)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(shown);

        var value = Protect(transactionId, crossDevice, shown);

        // An address too long for a cookie is not shown again: the user types it once more.
        if (value.Length > MaxCookieValueLength)
        {
            value = Protect(transactionId, crossDevice, shown with { EmailEnteredAddress = null });
        }

        if (value.Length <= MaxCookieValueLength)
        {
            httpContext.Response.Cookies.Append(CookieName, value, CookieOptionsOf(httpContext, Lifetime));
        }
    }

    /// <summary>
    /// The protected value of the cookie.
    /// </summary>
    /// <param name="transactionId">The transaction of the page.</param>
    /// <param name="crossDevice">Whether the page is the one of the device that confirmed the sign-in.</param>
    /// <param name="shown">What the page shows.</param>
    /// <returns>The value.</returns>
    private string Protect(TransactionId transactionId, bool crossDevice, ClaimCompletionShownAgain shown) =>
        _protector.Protect(JsonSerializer.Serialize(new Payload(
            transactionId.ToString(), crossDevice, shown.EmailError, shown.EmailAddressStep, shown.EmailEnteredAddress)));

    /// <summary>
    /// Takes what the action before this page load left for it, and removes the cookie. Never throws: no
    /// cookie, a value Data Protection refuses and the carry of another transaction or surface read as none.
    /// </summary>
    /// <param name="httpContext">Context of the page request; the answer has not started.</param>
    /// <param name="transactionId">The transaction of the page.</param>
    /// <param name="crossDevice">Whether the page is the one of the device that confirmed the sign-in.</param>
    /// <returns>What the page shows; null — nothing was carried to it.</returns>
    public ClaimCompletionShownAgain? Take(HttpContext httpContext, TransactionId transactionId, bool crossDevice)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!httpContext.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        httpContext.Response.Cookies.Delete(CookieName, CookieOptionsOf(httpContext, maxAge: null));

        Payload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(value));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Data Protection reports every refusal — a malformed value, tampering, another purpose, a
            // missing key — as one exception type. The value is not logged.
            return null;
        }

        return payload is not null
            && payload.CrossDevice == crossDevice
            && string.Equals(payload.TransactionId, transactionId.ToString(), StringComparison.Ordinal)
                ? new ClaimCompletionShownAgain(payload.EmailError, payload.EmailAddressStep, payload.EmailEnteredAddress)
                : null;
    }

    /// <summary>
    /// Options of the cookie: sent to the pages of the application only, never read by a script.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="maxAge">Lifetime of the cookie; null — none, the cookie is being removed.</param>
    /// <returns>The options.</returns>
    private static CookieOptions CookieOptionsOf(HttpContext httpContext, TimeSpan? maxAge) =>
        new()
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            // Under a sub-path the browser matches the cookie path against the full request path.
            Path = httpContext.Request.PathBase.HasValue ? httpContext.Request.PathBase.Value : "/",
            MaxAge = maxAge
        };

    /// <summary>
    /// What the cookie carries.
    /// </summary>
    /// <param name="TransactionId">The transaction the carry is for.</param>
    /// <param name="CrossDevice">Whether it is for the page of the device that confirmed the sign-in.</param>
    /// <param name="EmailError">See <see cref="ClaimCompletionShownAgain.EmailError"/>.</param>
    /// <param name="EmailAddressStep">See <see cref="ClaimCompletionShownAgain.EmailAddressStep"/>.</param>
    /// <param name="EmailEnteredAddress">See <see cref="ClaimCompletionShownAgain.EmailEnteredAddress"/>.</param>
    private sealed record Payload(
        string TransactionId,
        bool CrossDevice,
        string? EmailError,
        bool EmailAddressStep,
        string? EmailEnteredAddress);
}

/// <summary>
/// What a page of the claims completion shows after an action of the email block besides the transaction
/// itself.
/// </summary>
/// <param name="EmailError">Localized error of the email block; null — none.</param>
/// <param name="EmailAddressStep">Whether the email block brings the address step of its way back over a
/// live mail.</param>
/// <param name="EmailEnteredAddress">The address typed into the email block, shown again in the input of its
/// address step; null — the input shows the address of the last mail.</param>
internal sealed record ClaimCompletionShownAgain(
    string? EmailError,
    bool EmailAddressStep,
    string? EmailEnteredAddress);
