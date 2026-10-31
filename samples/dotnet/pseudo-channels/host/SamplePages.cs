// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Net.Http.Headers;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Sample.PseudoChannels.Adapter;

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// The pages of the sample: the three a phone sees and the two of the desktop. They are plain HTML
/// built here, with no script at all — the security headers of the embedded Veriqa apply to every
/// response of the host and admit only scripts carrying its nonce. Every value that did not come
/// from this file is HTML-encoded.
/// </summary>
internal static class SamplePages
{
    /// <summary>
    /// Style sheet shared by every page.
    /// </summary>
    private const string Style =
        "body{font-family:system-ui,sans-serif;margin:0;padding:24px;background:#f5f5f7;color:#1d1d1f}" +
        "main{max-width:440px;margin:0 auto;background:#fff;border-radius:14px;padding:24px;" +
        "box-shadow:0 1px 4px rgba(0,0,0,.08)}" +
        "h1{font-size:22px;margin:0 0 12px}p{line-height:1.45;margin:0 0 12px}" +
        "dl{margin:0 0 16px}dt{font-size:13px;color:#6e6e73;margin-top:10px}dd{margin:2px 0 0;word-break:break-word}" +
        "form{display:flex;gap:12px}button{flex:1;font-size:17px;padding:14px;border-radius:10px;border:0}" +
        "button.yes{background:#0a7d33;color:#fff}button.no{background:#e5e5ea;color:#1d1d1f}" +
        "a{color:#0a58ca}";

    /// <summary>
    /// Format the moment a sign-in was started is shown in; the zone is named after it.
    /// </summary>
    private const string InitiatedAtFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// Marker of a moment shown in UTC — the zone of the recipient is not known.
    /// </summary>
    private const string UtcMarker = "UTC";

    /// <summary>
    /// Separator between a moment and the zone it is stated in.
    /// </summary>
    private const string ZoneSeparator = " ";

    /// <summary>
    /// Separator between the parts of one line of the question.
    /// </summary>
    private const string PartSeparator = " · ";

    /// <summary>
    /// Separator between the city and the country.
    /// </summary>
    private const string PlaceSeparator = ", ";

    /// <summary>
    /// Style class of the confirming button.
    /// </summary>
    private const string YesButtonClass = "yes";

    /// <summary>
    /// Style class of the declining button.
    /// </summary>
    private const string NoButtonClass = "no";

    /// <summary>
    /// A page that says one thing and offers nothing to press.
    /// </summary>
    /// <param name="title">Heading.</param>
    /// <param name="text">The one paragraph.</param>
    /// <param name="statusCode">HTTP status of the response.</param>
    /// <returns>The page.</returns>
    public static IResult Notice(string title, string text, int statusCode = StatusCodes.Status200OK)
        => Page(title, $"<p>{Encode(text)}</p>", statusCode);

    /// <summary>
    /// Writes a notice straight into a response — for the places that hold a response rather than
    /// return a result (the events of an authentication handler).
    /// </summary>
    /// <param name="context">Current request.</param>
    /// <param name="title">Heading.</param>
    /// <param name="text">The one paragraph.</param>
    /// <returns>A task that completes when the page is written.</returns>
    public static Task WriteNoticeAsync(HttpContext context, string title, string text)
        => Notice(title, text).ExecuteAsync(context);

    /// <summary>
    /// The question and the two buttons. What stands above the buttons depends on what the core
    /// handed over: the details of the sign-in — where it leads, which device asked for it, when;
    /// a question the core worded itself; or nothing but the account the phone signed in under,
    /// when the core withheld the details. The question is asked in every case: once the core has
    /// handed a prompt to the channel, the channel is where the answer is given.
    /// </summary>
    /// <param name="channelType">Channel type the question is asked in.</param>
    /// <param name="prompt">Context the core handed to the adapter with the question.</param>
    /// <param name="providerUser">Principal built from the provider's ID token.</param>
    /// <param name="antiforgery">Request token of the form.</param>
    /// <returns>The page.</returns>
    public static IResult Question(
        string channelType,
        ConfirmationPromptContext prompt,
        ClaimsPrincipal providerUser,
        AntiforgeryTokenSet antiforgery)
    {
        var body = new StringBuilder();
        body.Append("<p>")
            .Append(Encode(prompt is SubjectConfirmationPromptContext subject ? subject.PromptText : SampleTexts.QuestionLead))
            .Append("</p><dl>");

        if (prompt is DetailedConfirmationPromptContext details)
        {
            AppendRow(body, SampleTexts.ApplicationLabel, details.ClientApplicationName);
            AppendRow(body, SampleTexts.DeviceLabel, Join(PartSeparator, details.Browser, details.OsPlatform, details.DeviceType));
            AppendRow(body, SampleTexts.LocationLabel, Join(PlaceSeparator, details.GeoCity, details.GeoCountry));
            AppendRow(body, SampleTexts.StartedLabel, DescribeMoment(details.InitiatedAt, details.RecipientTimeZone));
        }

        AppendRow(body, SampleTexts.AccountLabel, DescribeAccount(providerUser));

        body.Append("</dl><p>").Append(Encode(SampleTexts.QuestionWarning)).Append("</p>");

        body.Append("<form method=\"post\" action=\"")
            .Append(Encode(PseudoHostConstants.Routes.ForChannel(channelType, PseudoHostConstants.Routes.AnswerSegment)))
            .Append("\"><input type=\"hidden\" name=\"")
            .Append(Encode(antiforgery.FormFieldName))
            .Append("\" value=\"")
            .Append(Encode(antiforgery.RequestToken ?? string.Empty))
            .Append("\">");
        AppendButton(body, PseudoHostConstants.AnswerForm.No, NoButtonClass, SampleTexts.NoButton);
        AppendButton(body, PseudoHostConstants.AnswerForm.Yes, YesButtonClass, SampleTexts.YesButton);
        body.Append("</form>");

        return Page(SampleTexts.QuestionTitle, body.ToString(), StatusCodes.Status200OK);
    }

    /// <summary>
    /// The desktop page of a visitor who is not signed in.
    /// </summary>
    /// <returns>The page.</returns>
    public static IResult Anonymous()
        => Page(
            SampleTexts.HomeTitle,
            $"<p>{Encode(SampleTexts.HomeLead)}</p>" +
            $"<p><a href=\"{PseudoHostConstants.Routes.SignIn}\">{Encode(SampleTexts.SignInLink)}</a></p>",
            StatusCodes.Status200OK);

    /// <summary>
    /// The desktop page of a signed-in user: the claims Veriqa issued, as they arrived.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <returns>The page.</returns>
    public static IResult SignedIn(ClaimsPrincipal user)
    {
        var body = new StringBuilder();
        body.Append("<p>").Append(Encode(SampleTexts.SignedInLead)).Append("</p><dl>");

        foreach (var claim in user.Claims)
        {
            AppendRow(body, claim.Type, claim.Value);
        }

        body.Append("</dl><p><a href=\"")
            .Append(PseudoHostConstants.Routes.SignOut)
            .Append("\">")
            .Append(Encode(SampleTexts.SignOutLink))
            .Append("</a></p>");

        return Page(SampleTexts.SignedInTitle, body.ToString(), StatusCodes.Status200OK);
    }

    /// <summary>
    /// The desktop page shown when the sign-in was declined or failed.
    /// </summary>
    /// <returns>The page.</returns>
    public static IResult SignInFailed()
        => Page(
            SampleTexts.SignInFailedTitle,
            $"<p>{Encode(SampleTexts.SignInFailedText)}</p>" +
            $"<p><a href=\"{PseudoHostConstants.Routes.Home}\">{Encode(SampleTexts.BackLink)}</a></p>",
            StatusCodes.Status200OK);

    /// <summary>
    /// Names the account the phone signed in under: the email when the provider gave one, the name
    /// next, the bare subject last.
    /// </summary>
    /// <param name="providerUser">Principal built from the provider's ID token.</param>
    /// <returns>Text for the account row.</returns>
    private static string? DescribeAccount(ClaimsPrincipal providerUser)
        => providerUser.FindFirstValue(PseudoChannelConstants.Fields.Email)
            ?? providerUser.FindFirstValue(PseudoChannelConstants.Fields.Name)
            ?? providerUser.FindFirstValue(PseudoChannelConstants.Fields.Subject);

    /// <summary>
    /// States a moment in the zone of the recipient, naming the zone; when the core named none, or
    /// named one this system does not know, the moment is stated in UTC and says so.
    /// </summary>
    /// <param name="moment">The moment.</param>
    /// <param name="timeZoneId">IANA identifier of the recipient's zone, when the core knows it.</param>
    /// <returns>Text for the row.</returns>
    private static string DescribeMoment(DateTimeOffset moment, string? timeZoneId)
    {
        var (local, zoneName) = !string.IsNullOrWhiteSpace(timeZoneId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone)
            ? (TimeZoneInfo.ConvertTime(moment, zone), timeZoneId)
            : (moment.ToUniversalTime(), UtcMarker);

        return local.ToString(InitiatedAtFormat, CultureInfo.InvariantCulture) + ZoneSeparator + zoneName;
    }

    /// <summary>
    /// Wraps a body into the page shell. Pages of the sample are never cached: each one states the
    /// moment of one transaction.
    /// </summary>
    /// <param name="title">Heading and document title.</param>
    /// <param name="bodyHtml">Markup of the body, already encoded.</param>
    /// <param name="statusCode">HTTP status of the response.</param>
    /// <returns>The page.</returns>
    private static IResult Page(string title, string bodyHtml, int statusCode)
    {
        var encodedTitle = Encode(title);
        var html =
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            $"<title>{encodedTitle}</title><style>{Style}</style></head>" +
            $"<body><main><h1>{encodedTitle}</h1>{bodyHtml}</main></body></html>";

        return new NoStoreResult(Results.Content(html, PseudoHostConstants.HtmlContentType, Encoding.UTF8, statusCode));
    }

    /// <summary>
    /// Appends one labelled row; a row without a value is left out.
    /// </summary>
    /// <param name="body">Markup being built.</param>
    /// <param name="label">Label of the row.</param>
    /// <param name="value">Value of the row.</param>
    private static void AppendRow(StringBuilder body, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        body.Append("<dt>").Append(Encode(label)).Append("</dt><dd>").Append(Encode(value)).Append("</dd>");
    }

    /// <summary>
    /// Appends one submit button of the answer form.
    /// </summary>
    /// <param name="body">Markup being built.</param>
    /// <param name="value">Value the button submits.</param>
    /// <param name="cssClass">Class of the button.</param>
    /// <param name="text">Caption.</param>
    private static void AppendButton(StringBuilder body, string value, string cssClass, string text)
        => body.Append("<button type=\"submit\" class=\"")
            .Append(cssClass)
            .Append("\" name=\"")
            .Append(PseudoHostConstants.AnswerForm.Field)
            .Append("\" value=\"")
            .Append(Encode(value))
            .Append("\">")
            .Append(Encode(text))
            .Append("</button>");

    /// <summary>
    /// Joins the parts that are present.
    /// </summary>
    /// <param name="separator">Separator.</param>
    /// <param name="parts">Parts, any of which may be missing.</param>
    /// <returns>The joined text, or null when nothing is present.</returns>
    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();

        return present.Length == 0 ? null : string.Join(separator, present);
    }

    /// <summary>
    /// HTML-encodes a value.
    /// </summary>
    /// <param name="value">Raw value.</param>
    /// <returns>Encoded value.</returns>
    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    /// <summary>
    /// A result that forbids caching of the response it wraps.
    /// </summary>
    /// <param name="inner">The response itself.</param>
    private sealed class NoStoreResult(IResult inner) : IResult
    {
        /// <inheritdoc />
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = CacheControlHeaderValue.NoStoreString;

            return inner.ExecuteAsync(httpContext);
        }
    }
}
