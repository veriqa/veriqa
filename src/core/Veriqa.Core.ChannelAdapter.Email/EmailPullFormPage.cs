// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// Rendering of the standalone pull form page (SPEC-016 §4) and what the pages of the adapter share:
/// the branding and the text overrides of a page, HTML escaping.
/// The pages use the service-page stylesheet of the "Core" contour, so they share tokens and
/// branding with the sign-in window. All interpolations are escaped (XSS protection).
/// </summary>
public static partial class EmailAuthEndpoints
{
    /// <summary>
    /// Resolves the branding and the text overrides of an email-adapter page over the ownership context
    /// of ITS OWN transaction (SPEC-007 UI-101): the tenant, the application and the <c>ui_config</c>
    /// record all come from the transaction the page is serving, exactly as on the sign-in window. A page
    /// with no transaction to name a context — the pull form opened without a <c>session_id</c>, an
    /// invalid or spent magic link — resolves against the levels that do not depend on one (UI-090): no
    /// entry of an application or a <c>ui_config</c> record reaches it. Each is resolved once for the
    /// page, so that the synchronous render reads neither a store nor the transaction again.
    /// <para>
    /// The same call states the resolved stylesheet for this request's CSP, so the policy and the
    /// markup cannot name different origins (UI-054, UI-040).
    /// </para>
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <param name="ownership">Resolution context of the page's own transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective branding (all values unset when nothing is branded) and the text overrides of the
    /// page.</returns>
    private static async Task<(CorePageBranding Branding, CorePageTexts Texts)> ResolvePageCustomizationAsync(
        IServiceProvider services,
        ResolutionContext ownership,
        CancellationToken cancellationToken)
    {
        var branding = await services.GetRequiredService<CorePageBrandingResolver>()
            .ResolveAsync(ownership, cancellationToken);
        var texts = await services.GetRequiredService<CorePageTextResolver>()
            .ResolveAsync(ownership, cancellationToken);

        return (branding, texts);
    }

    /// <summary>
    /// Loads the transaction a page of the adapter serves, as the store holds it — past the TTL gate of
    /// the service, which refuses a transaction whose deadline has passed: what the page is about (the
    /// ownership context that brands it, the type, the application and the language of a letter) does not
    /// run out with the transaction. Never throws over a page: an unreadable or unknown transaction
    /// degrades to null, and the page is then rendered without it — branded by the levels that do not
    /// depend on it and spoken in the request language.
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <param name="transactionIdValue">Transaction identifier as it arrived in the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction, or null when there is none to read.</returns>
    private static async Task<Transaction?> TryLoadTransactionAsync(
        IServiceProvider services,
        string? transactionIdValue,
        CancellationToken cancellationToken)
    {
        if (!TransactionId.TryParse(transactionIdValue, out var transactionId))
        {
            return null;
        }

        try
        {
            return await services.GetRequiredService<ITransactionStore>()
                .GetByIdAsync(transactionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal request cancellation — propagate, this is not a failure to read the transaction.
            throw;
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(EmailAuthEndpoints))
                .LogWarning(ex, "The transaction of the page could not be read; rendering the page without it.");

            return null;
        }
    }

    /// <summary>
    /// Renders the standalone pull form page (GET /auth/email/start).
    /// </summary>
    private static async Task<IResult> HandleEmailPullFormAsync(HttpContext httpContext)
    {
        // The method renders the standalone pull form: enter email → magic link
        var services = httpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EmailAuthEndpoints));

        // Served page localization (ICC-050): the form and its messages render in the request language.
        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var servedLocale = ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString());

        // The transaction of this page is named by the session_id of the link the user followed, so
        // the page is branded for its own application and ui_config record (UI-101). Opened without
        // one — the form says so and renders disabled — there is no context and the core level stands
        // alone (UI-090). The same context decides below whether the sign-in modes of the transaction
        // offer the form at all.
        var sessionId = httpContext.Request.Query[EmailAdapterConstants.SessionIdQueryParam].ToString();
        var transaction = await TryLoadTransactionAsync(services, sessionId, httpContext.RequestAborted);
        var ownership = TransactionResolutionContext.For(transaction);
        var (branding, texts) = await ResolvePageCustomizationAsync(services, ownership, httpContext.RequestAborted);

        // GET and POST share the /auth/email/start route, and this GET is the one a user reaches
        // first — so it is this read that materializes the Email section for the whole process. It
        // goes through the adapter's guarded entry point for exactly that reason (TryReadEmailOptions).
        var emailOptions = TryReadEmailOptions(services, logger);
        if (emailOptions is null)
        {
            // The channel cannot be served, and the reason is not "the mode is off": say that instead
            // of the disabled-mode text, which would state something untrue to the user.
            return Results.Content(
                BuildConfirmPageHtml(pageLocalizer, servedLocale, branding, texts, isError: true, errorMessage: EmailAdapterMailStrings.ApiErrorChannelUnavailable),
                "text/html", Encoding.UTF8, StatusCodes.Status503ServiceUnavailable);
        }

        // The ceiling first, then the choice: Pull switched off at the core level and Pull left out of the
        // sign-in modes of the tenant or the application of the transaction are answered the same way. The
        // same set says what the mail carries, and the form is worded by it.
        var modes = emailOptions.Enabled && emailOptions.PullEnabled
            ? await services.GetRequiredService<IEmailSignInModes>().ResolveAsync(ownership, httpContext.RequestAborted)
            : null;
        if (modes is not { Pull: true })
        {
            return Results.Content(
                BuildConfirmPageHtml(pageLocalizer, servedLocale, branding, texts, isError: true, errorMessage: EmailAdapterMailStrings.PullFormModeDisabled),
                "text/html", Encoding.UTF8, StatusCodes.Status503ServiceUnavailable);
        }

        // CSP nonce from SecurityHeadersMiddleware: without it the page's inline script
        // is blocked by the script-src policy (review feedback, SPEC-007 §6.3)
        var cspNonce = httpContext.Items[HttpContextItemKeys.CspNonce] as string;

        var html = BuildPullFormPageHtml(
            pageLocalizer,
            servedLocale,
            branding,
            texts,
            new PullFormPage(
                sessionId,
                modes.PullCarriesLink,
                modes.PullCarriesCode,
                IsSignIn: transaction is null
                    || string.Equals(transaction.Type, TransactionTypes.Login, StringComparison.Ordinal)),
            cspNonce,
            httpContext.Request.PathBase.Value ?? string.Empty);
        return Results.Content(html, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// Identifier of the form of the pull form page — the element its script listens to the submit of.
    /// </summary>
    private const string PullFormElementId = "veriqa-pull-form";

    /// <summary>
    /// Identifier of the form of the code of the pull form page, rendered hidden for a mail that carries a
    /// code and shown by the script once the start says the mail went out with one.
    /// </summary>
    private const string PullCodeFormElementId = "veriqa-code-form";

    /// <summary>
    /// What the pull form page is built for: its transaction and what the mail of Pull carries there.
    /// </summary>
    /// <param name="SessionId">The transaction identifier (session_id); empty — the page has none.</param>
    /// <param name="CarriesLink">The mail carries the magic link.</param>
    /// <param name="CarriesCode">The mail carries a code: the page asks for it once the mail is sent.</param>
    /// <param name="IsSignIn">The transaction is a sign-in, or the page has none; otherwise the page is worded
    /// for the act being confirmed.</param>
    private sealed record PullFormPage(string SessionId, bool CarriesLink, bool CarriesCode, bool IsSignIn);

    /// <summary>
    /// Builds the HTML of the standalone pull form page (GET /auth/email/start). The address field stands
    /// in a real form with a name — what a browser offers its remembered addresses for — and the script
    /// sends the request on its submit, keeping the page; where the script did not run, the form posts
    /// the address and the session to the start by itself. After a sent link the field stays editable and
    /// the button, renamed to a repeated sending, is held for the interval the start names
    /// (<see cref="EmailOptions.MailResendInterval"/>); a refusal for asking too soon shows the text of
    /// the answer and holds the button the same way.
    /// </summary>
    /// <param name="localizer">Localizer of the page texts (Natural Keys).</param>
    /// <param name="servedLocale">The locale the page is served in (null — base language).</param>
    /// <param name="branding">Effective branding of the generated page (null — neutral canon).</param>
    /// <param name="texts">Overrides of the page's built-in texts, resolved for its transaction.</param>
    /// <param name="page">The transaction of the page and what the mail of Pull carries there.</param>
    /// <param name="cspNonce">The CSP nonce for the inline script (null — the attribute is not added).</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <returns>The HTML string of the page with the email input form.</returns>
    /// <remarks>
    /// The texts of the page name what the mail carries — the magic link, a code or both — and the act, a
    /// sign-in or the confirmation of any other transaction; a mail with a code is answered on the same page:
    /// the step of the code takes the place of the address, and the accepted code takes the page where the
    /// answer names, or shows the line the answer carries (SPEC-016 §4). The user is given one button and no
    /// choice of what the mail carries.
    /// </remarks>
    private static string BuildPullFormPageHtml(
        IConfirmationPromptLocalizer localizer,
        string? servedLocale,
        CorePageBranding? branding,
        CorePageTexts texts,
        PullFormPage page,
        string? cspNonce,
        string pathBase)
    {
        // The method renders a minimalist pull form in the white style consistent with the auth page,
        // localized into the request language (ICC-050)
        var sessionId = page.SessionId;
        var withCode = page.CarriesCode;
        var codeOnly = withCode && !page.CarriesLink;
        var htmlLang = ResolveServedHtmlLang(localizer, servedLocale);

        // The texts that name what the mail carries. A sign-in and any other transaction are one process for
        // the user: only the words naming the act differ.
        var title = texts.Localize(
            localizer,
            page.IsSignIn
                ? codeOnly ? EmailAdapterMailStrings.PullFormTitleCode
                    : withCode ? EmailAdapterMailStrings.PullFormTitleLinkAndCode
                    : EmailAdapterMailStrings.PullFormTitle
                : codeOnly ? EmailAdapterMailStrings.PullFormConfirmTitleCode
                    : withCode ? EmailAdapterMailStrings.PullFormConfirmTitleLinkAndCode
                    : EmailAdapterMailStrings.PullFormConfirmTitle,
            htmlLang);
        var heading = texts.Localize(
            localizer,
            page.IsSignIn
                ? codeOnly ? EmailAdapterMailStrings.PullFormHeadingCode
                    : withCode ? EmailAdapterMailStrings.PullFormTitleLinkAndCode
                    : EmailAdapterMailStrings.PullFormHeading
                : codeOnly ? EmailAdapterMailStrings.PullFormConfirmHeadingCode
                    : withCode ? EmailAdapterMailStrings.PullFormConfirmTitleLinkAndCode
                    : EmailAdapterMailStrings.PullFormConfirmHeading,
            htmlLang);
        var description = texts.Localize(
            localizer,
            page.IsSignIn
                ? codeOnly ? EmailAdapterMailStrings.PullFormDescriptionCode
                    : withCode ? EmailAdapterMailStrings.PullFormDescriptionLinkAndCode
                    : EmailAdapterMailStrings.PullFormDescription
                : codeOnly ? EmailAdapterMailStrings.PullFormConfirmDescriptionCode
                    : withCode ? EmailAdapterMailStrings.PullFormConfirmDescriptionLinkAndCode
                    : EmailAdapterMailStrings.PullFormConfirmDescription,
            htmlLang);
        var submitText = texts.Localize(
            localizer,
            codeOnly ? EmailAdapterMailStrings.PullFormSubmitCode
                : withCode ? EmailAdapterMailStrings.PullFormSubmitLinkAndCode
                : EmailAdapterMailStrings.PullFormSubmitButton,
            htmlLang);
        var jsSessionId = JsonSerializer.Serialize(sessionId);
        // The path the form posts to carries the path base: this application may be hosted under a
        // sub-path, and a root-relative fetch would reach past it.
        var jsStartPath = JsonSerializer.Serialize(pathBase + EmailAdapterConstants.PullStartPath);
        // The "link sent" text is an ordinary localized string of this page, not a message of the
        // template mechanism: the address in it is a masked address in a sentence, not a slot. The server
        // resolves the localized text WITH the token in place; the client substitutes the address by
        // token replacement into textContent (TPL-052). Replacing the token — not prefixing — keeps the
        // address in its localized position (in ru/zh it is not at the end) and never reaches markup.
        var jsLinkSentTemplate = JsonSerializer.Serialize(texts.Localize(
            localizer,
            page.IsSignIn ? EmailAdapterMailStrings.PullFormLinkSent : EmailAdapterMailStrings.PullFormConfirmLinkSent,
            htmlLang));
        var jsEmailMaskedToken = JsonSerializer.Serialize("{" + SlotNames.EmailMasked + "}");
        var jsSendError = JsonSerializer.Serialize(texts.Localize(
            localizer,
            withCode ? EmailAdapterMailStrings.PullFormSendMailError : EmailAdapterMailStrings.PullFormSendError,
            htmlLang));
        var jsSendAgain = JsonSerializer.Serialize(texts.Localize(localizer, EmailAdapterMailStrings.PullFormSendAgainButton, htmlLang));
        var hasSession = !string.IsNullOrWhiteSpace(sessionId);
        var noSessionWarning = hasSession
            ? ""
            : $"""<p class="veriqa-msg err" style="display:block;margin-bottom:12px;">{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.PullFormNoSession, htmlLang))}</p>""";

        // The nonce attribute — as in DefaultAuthPageRenderer: empty when there is no nonce
        var nonceAttr = !string.IsNullOrEmpty(cspNonce)
            ? $" nonce=\"{HtmlEncode(cspNonce)}\""
            : string.Empty;

        // The step of the code: a form of its own, hidden, for a mail that carries a code. Its field has no
        // name and the form no target — without the script the page never asks for a code.
        var codeFormMarkup = withCode
            ? $$"""
                <form id="{{PullCodeFormElementId}}" novalidate hidden>
                  <p class="veriqa-instruction" id="veriqa-code-sent"></p>
                  <input
                    class="veriqa-input"
                    id="veriqa-code"
                    type="text"
                    inputmode="numeric"
                    autocomplete="one-time-code"
                    aria-label="{{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.PullFormCodeAriaLabel, htmlLang))}}"
                  />
                  <button type="submit" id="veriqa-code-submit" class="veriqa-btn">{{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.PullFormCodeConfirm, htmlLang))}}</button>
                  <button type="button" id="veriqa-resend" class="veriqa-text-link">{{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.PullFormSendAgainButton, htmlLang))}}</button>
                </form>
            """
            : string.Empty;
        var jsCodeSettings = withCode
            ? JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["path"] = pathBase + EmailAdapterConstants.PullCodePath,
                ["sentTemplate"] = texts.Localize(localizer, EmailAdapterMailStrings.PullFormCodeSent, htmlLang),
                ["addressSlot"] = EmailAdapterMailStrings.PullFormCodeAddressSlot,
                ["checkError"] = texts.Localize(localizer, EmailAdapterMailStrings.PullFormCodeCheckError, htmlLang),
                ["length"] = ClaimEmailCode.Length,
                ["sessionField"] = EmailAdapterConstants.SessionIdQueryParam,
                ["codeField"] = EmailPullJsonFields.Code,
                ["expectedField"] = EmailPullJsonFields.CodeExpected,
                ["nextUrlField"] = EmailPullJsonFields.NextUrl,
                ["messageField"] = EmailPullJsonFields.Message
            })
            : "null";

        return $$"""
            <!DOCTYPE html>
            <html lang="{{htmlLang}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>{{HtmlEncode(title)}}</title>
              {{CorePageHead.BuildServicePageHead(branding)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{HtmlEncode(heading)}}</h1>
                <p class="veriqa-instruction">{{HtmlEncode(description)}}</p>
                {{noSessionWarning}}
                <form id="{{PullFormElementId}}" method="post" action="{{HtmlEncode(pathBase + EmailAdapterConstants.PullStartPath)}}">
                  <input type="hidden" name="{{EmailAdapterConstants.SessionIdQueryParam}}" value="{{HtmlEncode(sessionId)}}" />
                  <input
                    class="veriqa-input"
                    id="veriqa-email"
                    type="email"
                    name="{{EmailStartFormFields.Email}}"
                    autocomplete="email"
                    placeholder="{{HtmlEncode(EmailAdapterMailStrings.PullFormEmailPlaceholder)}}"
                    aria-label="{{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.PullFormEmailAriaLabel, htmlLang))}}"
                    required
                    {{(hasSession ? "" : "disabled")}}
                  />
                  <button type="submit" id="veriqa-submit" class="veriqa-btn" {{(hasSession ? "" : "disabled")}}>
                    {{HtmlEncode(submitText)}}
                  </button>
                </form>
                {{codeFormMarkup}}
                <div id="veriqa-msg" class="veriqa-msg" role="status" aria-live="polite"></div>
              </main>
              {{CorePageAttribution.BuildLine(htmlLang)}}
              <script{{nonceAttr}}>
                (function () {
                  var sessionId = {{jsSessionId}};
                  var startPath = {{jsStartPath}};
                  var linkSentTemplate = {{jsLinkSentTemplate}};
                  var emailMaskedToken = {{jsEmailMaskedToken}};
                  var sendError = {{jsSendError}};
                  var sendAgain = {{jsSendAgain}};
                  var code = {{jsCodeSettings}};
                  var form = document.getElementById({{JsonSerializer.Serialize(PullFormElementId)}});
                  var btn = document.getElementById('veriqa-submit');
                  var input = document.getElementById('veriqa-email');
                  var msg = document.getElementById('veriqa-msg');
                  if (!form || !btn || !sessionId) { return; }
                  var holdTimer = null;
                  function hold(seconds) {
                    clearTimeout(holdTimer);
                    holdTimer = null;
                    if (!(seconds > 0)) { btn.disabled = false; return; }
                    btn.disabled = true;
                    holdTimer = setTimeout(function () { holdTimer = null; btn.disabled = false; }, seconds * 1000);
                  }
                  function retryAfter(data, header) {
                    if (data && typeof data.retryAfterSeconds === 'number' && data.retryAfterSeconds > 0) { return data.retryAfterSeconds; }
                    var fromHeader = parseInt(header, 10);
                    return fromHeader > 0 ? fromHeader : 0;
                  }
                  form.addEventListener('submit', function (event) {
                    event.preventDefault();
                    if (btn.disabled) { return; }
                    var email = (input.value || '').trim();
                    if (!email) { return; }
                    clearTimeout(holdTimer);
                    holdTimer = null;
                    btn.disabled = true;
                    msg.className = 'veriqa-msg';
                    msg.style.display = 'none';
                    fetch(startPath, {
                      method: 'POST',
                      headers: { 'Content-Type': 'application/json' },
                      body: JSON.stringify({ email: email, session_id: sessionId })
                    })
                      .then(function (r) {
                        return r.json().catch(function () { return {}; }).then(function (d) {
                          return { ok: r.ok, status: r.status, header: r.headers.get('Retry-After'), data: d };
                        });
                      })
                      .then(function (res) {
                        if (res.ok && res.data && res.data.success) {
                          var maskedAddr = (res.data.maskedEmail || email);
                          btn.textContent = sendAgain;
                          hold(retryAfter(res.data, null));
                          if (codeForm && res.data[code.expectedField]) {
                            showCodeStep(maskedAddr);
                            return;
                          }
                          msg.className = 'veriqa-msg ok';
                          msg.textContent = linkSentTemplate.replace(emailMaskedToken, function () { return maskedAddr; });
                          msg.style.display = 'block';
                        } else {
                          var tooSoon = res.status === 429;
                          msg.className = 'veriqa-msg err';
                          msg.textContent = (tooSoon && res.data && res.data.error) || sendError;
                          msg.style.display = 'block';
                          hold(tooSoon ? retryAfter(res.data, res.header) : 0);
                        }
                      })
                      .catch(function () {
                        msg.className = 'veriqa-msg err';
                        msg.textContent = sendError;
                        msg.style.display = 'block';
                        hold(0);
                      });
                  });
                  // The step of the code takes the place of the address once the mail went out with a code;
                  // "Send again" brings the address back, with the address in its field.
                  var codeForm = code ? document.getElementById({{JsonSerializer.Serialize(PullCodeFormElementId)}}) : null;
                  if (!codeForm) { return; }
                  var codeInput = document.getElementById('veriqa-code');
                  var codeBtn = document.getElementById('veriqa-code-submit');
                  var codeSent = document.getElementById('veriqa-code-sent');
                  var resend = document.getElementById('veriqa-resend');
                  var codePending = false;
                  function showError(text) {
                    msg.className = 'veriqa-msg err';
                    msg.textContent = text;
                    msg.style.display = 'block';
                  }
                  function showCodeStep(maskedAddr) {
                    msg.style.display = 'none';
                    codeSent.textContent = code.sentTemplate.replace(code.addressSlot, function () { return maskedAddr; });
                    codeInput.value = '';
                    codeBtn.disabled = false;
                    form.hidden = true;
                    codeForm.hidden = false;
                    codeInput.focus();
                  }
                  codeInput.addEventListener('input', function () {
                    var digits = codeInput.value.replace(/\D+/g, '').slice(0, code.length);
                    if (digits !== codeInput.value) { codeInput.value = digits; }
                  });
                  resend.addEventListener('click', function () {
                    if (codePending) { return; }
                    msg.style.display = 'none';
                    codeForm.hidden = true;
                    form.hidden = false;
                    input.focus();
                  });
                  codeForm.addEventListener('submit', function (event) {
                    event.preventDefault();
                    if (codePending || codeBtn.disabled) { return; }
                    var entered = (codeInput.value || '').replace(/\s+/g, '');
                    if (!entered) { return; }
                    codePending = true;
                    codeBtn.disabled = true;
                    msg.style.display = 'none';
                    var body = {};
                    body[code.sessionField] = sessionId;
                    body[code.codeField] = entered;
                    fetch(code.path, {
                      method: 'POST',
                      headers: { 'Content-Type': 'application/json' },
                      body: JSON.stringify(body)
                    })
                      .then(function (r) {
                        return r.json().catch(function () { return {}; });
                      })
                      .then(function (d) {
                        codePending = false;
                        if (d && d.success) {
                          // The code did what the link of the same mail does: the page goes where the answer
                          // names — the path carries the path base already — or shows the line it carries.
                          var nextUrl = d[code.nextUrlField];
                          if (nextUrl) { window.location.href = nextUrl; return; }
                          codeForm.hidden = true;
                          msg.className = 'veriqa-msg ok';
                          msg.textContent = d[code.messageField] || '';
                          msg.style.display = 'block';
                          return;
                        }
                        showError((d && d.error) || code.checkError);
                        codeBtn.disabled = false;
                        codeInput.focus();
                        codeInput.select();
                      })
                      .catch(function () {
                        codePending = false;
                        showError(code.checkError);
                        codeBtn.disabled = false;
                      });
                  });
                })();
              </script>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Escapes a string for safe insertion into HTML.
    /// </summary>
    /// <param name="value">The source string.</param>
    /// <returns>The HTML-escaped string.</returns>
    private static string HtmlEncode(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }
}
