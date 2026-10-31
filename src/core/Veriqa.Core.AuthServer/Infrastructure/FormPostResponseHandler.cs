// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using OpenIddict.Server;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Endpoints;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// Delivers the authorization response in the <c>form_post</c> response mode with Veriqa's own
/// auto-posting page, replacing the one OpenIddict ships (its descriptor is removed from the pipeline
/// in the same registration that adds this handler). The library's page carries an inline script with
/// no nonce, which this deployment's content security policy refuses; rendering the page ourselves
/// lets the script take the nonce of this request through the ordinary path, so <c>script-src</c> is
/// never relaxed. Only <c>form-action</c> widens, and only to the target of THIS response — the
/// <c>redirect_uri</c> OpenIddict already validated against the client registration (SPEC-007 UI-091).
/// <para>
/// The trigger conditions are the ones the removed handler used: an ASP.NET Core request is present,
/// the response has a redirect target, and the request asked for the <c>form_post</c> mode. Otherwise
/// the handler returns without calling <c>HandleRequest()</c> and the <c>query</c> / <c>fragment</c>
/// handlers of OpenIddict answer the request.
/// </para>
/// </summary>
internal sealed class FormPostResponseHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyAuthorizationResponseContext>
{
    /// <summary>
    /// Content type of the rendered page.
    /// </summary>
    private const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>
    /// Cache-Control of the response: the body carries an authorization code and must not be stored.
    /// </summary>
    private const string NoStore = "no-store";

    /// <summary>
    /// Pragma of the response, for HTTP/1.0 caches that ignore Cache-Control.
    /// </summary>
    private const string NoCache = "no-cache";

    /// <inheritdoc />
    public async ValueTask HandleAsync(OpenIddictServerEvents.ApplyAuthorizationResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // This handler is ASP.NET Core-specific: a transaction that did not come through it has no
        // HTTP response to write, exactly as in the handler it replaces.
        var httpRequest = context.Transaction.GetHttpRequest();
        if (httpRequest is null)
        {
            return;
        }

        // No trusted place to send the response to (an error raised before redirect_uri was validated)
        // — the built-in handling answers instead, and nothing is posted anywhere.
        if (string.IsNullOrEmpty(context.RedirectUri))
        {
            return;
        }

        if (context.Request is null || !context.Request.IsFormPostResponseMode())
        {
            return;
        }

        var httpContext = httpRequest.HttpContext;
        var services = httpContext.RequestServices;

        // The page speaks the language of its transaction: on the code-issuing path the authorization
        // endpoint states the transaction's locale tag on this very HTTP context before it hands the
        // principal over, and the endpoint and this handler are two frames of ONE request (SPEC-007 UI-102).
        // Nothing stated — the browser's Accept-Language with the deployment default, which is the only
        // ladder there is on responses that never had a transaction: errors applied to the redirect_uri.
        var veriqaOptions = services.GetRequiredService<IOptions<VeriqaOptions>>();
        var languageRegistry = services.GetRequiredService<IAuthPageLanguageRegistry>();
        var defaultLanguage = veriqaOptions.Value.Localization.DefaultLanguage;
        var requestLanguage = AuthPageStrings.DetectLanguage(
            httpContext.Request.Headers.AcceptLanguage.ToString(),
            defaultLanguage,
            languageRegistry.SupportedLanguages);

        // The stated tag carries whatever subtags the request stated for the page language — a region
        // (en-DE), a script (zh-Hans) or none at all (en); the sign-in window keeps a regional subtag only
        // when the request asked for one. Whatever its shape, it is clamped to the supported languages here,
        // exactly as for every other page of a transaction: the page has no formatted values of its own.
        var language = AuthPageLocalization.ResolveTransactionLanguage(
            httpContext.Items[AuthPageConstants.TransactionUiLocaleItemKey] as string,
            requestLanguage,
            defaultLanguage,
            languageRegistry.SupportedLanguages);

        // Branding by the tenant and application levels of the validated client_id, without a ui_config
        // record: the record selector lives on the transaction, and this handler is given the HTTP
        // context and does not read the transaction (SPEC-007 UI-102). The tenant lives there as well,
        // and no ambient tenant scope is open on this path, so it is resolved from the client again —
        // the mapping is deterministic, the answer is the one the sign-in stored.
        var tenantId = await ClientTenantLookup.ResolveAsync(
            httpContext,
            context.Request.ClientId,
            services.GetRequiredService<ILoggerFactory>().CreateLogger<FormPostResponseHandler>());
        var resolution = TransactionResolutionContext.ForClient(tenantId, context.Request.ClientId, uiConfigSelector: null);
        var brandingResolver = services.GetRequiredService<CorePageBrandingResolver>();
        var branding = await brandingResolver.ResolveAsync(resolution, httpContext.RequestAborted);

        // The text overrides come from the same levels as the branding, for the same reason.
        var texts = await services.GetRequiredService<CorePageTextResolver>()
            .ResolveAsync(resolution, httpContext.RequestAborted);

        // State the CSP source of this response BEFORE the body starts: the policy header is written
        // from a Response.OnStarting callback, which runs when the first byte is about to go out.
        var formActionSource = SecurityHeadersMiddleware.GetFormActionSource(context.RedirectUri);
        if (!string.IsNullOrEmpty(formActionSource))
        {
            httpContext.Items[SecurityHeaderConstants.FormActionTargetItemKey] = formActionSource;
        }

        var html = FormPostResponsePage.Build(
            action: context.RedirectUri,
            fields: GetResponseFields(context.Response),
            cspNonce: httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string,
            localizer: services.GetRequiredService<IConfirmationPromptLocalizer>(),
            language: language,
            texts: texts,
            branding: branding);

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = HtmlContentType;
        httpContext.Response.Headers[HeaderNames.CacheControl] = NoStore;
        httpContext.Response.Headers[HeaderNames.Pragma] = NoCache;

        await httpContext.Response.WriteAsync(html, Encoding.UTF8, httpContext.RequestAborted);

        context.HandleRequest();
    }

    /// <summary>
    /// Flattens the response parameters into form fields: a multi-valued parameter yields one field per
    /// value, which is how an HTML form states a repeated name. Empty values are skipped — a form field
    /// holding nothing is indistinguishable from an absent parameter to the receiving side anyway.
    /// </summary>
    /// <param name="response">Authorization response being applied.</param>
    /// <returns>Name/value pairs for the hidden fields of the page.</returns>
    private static IEnumerable<KeyValuePair<string, string>> GetResponseFields(OpenIddictResponse response)
    {
        foreach (var parameter in response.GetParameters())
        {
            // The conversion to a string array is the one OpenIddict itself applies when it turns a
            // response into a wire form: it flattens every representation a parameter can hold.
            if ((ImmutableArray<string?>?)parameter.Value is not { } values)
            {
                continue;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    yield return new KeyValuePair<string, string>(parameter.Key, value);
                }
            }
        }
    }
}
