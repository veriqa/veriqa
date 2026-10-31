// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Authentication page constants (SPEC-007 §7).
/// </summary>
public static class AuthPageConstants
{
    /// <summary>
    /// Value of the QR &lt;img&gt; width/height attributes (px) — a pre-CSS sizing hint (aspect ratio,
    /// no layout shift) equal to the default of the <c>--veriqa-qr-size</c> token, which owns the
    /// rendered size. SPEC-007 UI-017, SPEC-015 §4.2 — scannability is a two-part criterion:
    /// a 200px scanning minimum and a verified module density, so lowering this value lowers both.
    /// The LOWER bound of the QR pixel scale is derived from this number
    /// (<see cref="Configuration.QrCodeOptions.MinPixelsPerModule"/>) — changing it moves that bound, and
    /// with it the module count from which the default scale still fills this box
    /// (<see cref="Configuration.QrCodeOptions.DefaultPixelsPerModule"/>, quoted in the custom-channel
    /// guide); the upper bound is sized for the largest box an integrator may set, not for this one.
    /// </summary>
    public const int QrCodeImgAttributeSizePx = 220;

    /// <summary>
    /// HttpContext.Items key carrying the UI locale tag of the transaction whose sign-in is being
    /// completed, for a page of THIS request that is not handed the transaction. It is written by the
    /// authorization endpoint before it hands the principal to OpenIddict, and read by the
    /// <c>form_post</c> auto-posting page, which the OpenIddict pipeline invokes later in the same
    /// request and hands no argument but the HTTP context (SPEC-007 UI-102). The value is the locale tag
    /// exactly as the sign-in window stated it: a language tag whose primary subtag is the page language,
    /// carrying whatever subtags the request stated for that language — a region (<c>en-DE</c>), a script
    /// (<c>zh-Hans</c>) or none at all (<c>en</c>); a region is there only when the request asked for one.
    /// The reader clamps it to the supported languages. Responses that never had a transaction — errors
    /// applied to the <c>redirect_uri</c> — leave the key unset, and the reader falls back to the language
    /// of the request.
    /// </summary>
    public const string TransactionUiLocaleItemKey = "Veriqa.AuthPage.TransactionUiLocale";

    /// <summary>
    /// MIME type of the HTML content.
    /// </summary>
    public const string HtmlContentType = "text/html";

    /// <summary>
    /// Data URI prefix for the QR code PNG image.
    /// </summary>
    public const string PngDataUriPrefix = "data:image/png;base64,";
}
