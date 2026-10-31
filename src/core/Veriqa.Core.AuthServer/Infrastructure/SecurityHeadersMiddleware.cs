// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// Middleware that sets security headers: CSP (with nonce), X-Content-Type-Options,
/// X-Frame-Options, Referrer-Policy (SPEC-007 §6.3).
/// Generates a unique nonce per request and passes it via HttpContext.Items.
/// </summary>
internal sealed class SecurityHeadersMiddleware
{
    /// <summary>
    /// Next middleware in the pipeline.
    /// </summary>
    private readonly RequestDelegate _next;

    /// <summary>
    /// Creates the middleware instance.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Sets the security headers and generates the CSP nonce.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>Completion task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        // Generate a unique nonce for each request
        var nonce = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(SecurityHeaderConstants.CspNonceLengthBytes));

        // Store the nonce in HttpContext.Items for use in the renderer
        context.Items[SecurityHeaderConstants.CspNonceItemKey] = nonce;

        // Set the security headers before the response starts. The callback is deferred on purpose:
        // by the time it runs, the page of this request has already stated the external resources it
        // actually links, and the policy is built from THOSE values rather than from the global
        // section (SPEC-007 UI-101, UI-054, UI-040).
        context.Response.OnStarting(async () =>
        {
            var headers = context.Response.Headers;

            var resources = await ResolveRequestResourcesAsync(context);
            var customCssOrigin = GetExternalOrigin(resources.StylesheetPath);
            var customJsOrigin = GetExternalOrigin(resources.ScriptPath);

            // WebSocket origin for SignalR: only when the host contains allowed characters
            // (protection against Host header injection — characters ';', '\'' can break the CSP)
            var websocketScheme = context.Request.IsHttps ? "wss" : "ws";
            var hostValue = context.Request.Host;
            var websocketOrigin = hostValue.HasValue && IsSafeHostForCsp(hostValue.Value)
                ? $"{websocketScheme}://{hostValue.Value}"
                : string.Empty;

            // script-src: add the external JS origin when set in configuration
            var scriptSrc = customJsOrigin is not null
                ? $"script-src 'self' 'nonce-{nonce}' {customJsOrigin}"
                : $"script-src 'self' 'nonce-{nonce}'";

            // style-src: add the external CSS origin when set in configuration
            var styleSrc = customCssOrigin is not null
                ? $"style-src 'self' 'unsafe-inline' {customCssOrigin}"
                : $"style-src 'self' 'unsafe-inline'";

            // form-action: 'self' always, plus the single target a generated page of THIS response
            // posts to, when one was stated. Four pages state it, all by the same rule of deriving the
            // source (GetFormActionSource) from the redirect_uri OpenIddict validated against the client
            // registration when it accepted the authorize request: the auto-posting page of the
            // form_post branches, which posts the issued code there; the web sign-in confirmation
            // page, whose "Yes" and "No" posts end on that redirect_uri through a chain of redirects the
            // browser applies form-action to; the sign-in window that carries the "Cancel sign-in"
            // button, whose post ends on that redirect_uri the same way; and the page of the claims
            // completion on the device the sign-in was started on, whose submission and "Cancel sign-in"
            // end there as well. Every other response leaves the key unset and keeps the directive at
            // 'self' — a sign-in window rendered without the button and the page of the claims
            // completion on the device that confirmed the sign-in among them (SPEC-007 UI-091).
            var formActionTarget = context.Items[SecurityHeaderConstants.FormActionTargetItemKey] as string;
            var formAction = !string.IsNullOrEmpty(formActionTarget)
                ? $"form-action 'self' {formActionTarget}"
                : "form-action 'self'";

            // Content-Security-Policy with a nonce for inline scripts
            headers[SecurityHeaderConstants.ContentSecurityPolicyHeader] =
                $"default-src 'self'; " +
                $"{scriptSrc}; " +
                $"{styleSrc}; " +
                $"connect-src 'self'{(websocketOrigin.Length > 0 ? " " + websocketOrigin : string.Empty)}; " +
                $"img-src 'self' data:; " +
                $"font-src 'self'; " +
                $"frame-ancestors 'none'; " +
                $"base-uri 'self'; " +
                $"{formAction}";

            // Additional security headers
            headers[SecurityHeaderConstants.XContentTypeOptionsHeader] =
                SecurityHeaderConstants.XContentTypeOptionsValue;
            headers[SecurityHeaderConstants.XFrameOptionsHeader] =
                SecurityHeaderConstants.XFrameOptionsValue;
            headers[SecurityHeaderConstants.ReferrerPolicyHeader] =
                SecurityHeaderConstants.ReferrerPolicyValue;
        });

        await _next(context);
    }

    /// <summary>
    /// Returns the external resources whose origins this response's CSP must allow.
    /// </summary>
    /// <remarks>
    /// A request that rendered a page of the contour has already stated them, resolved over the
    /// context of that page's own transaction — the policy then names exactly what the markup links.
    /// A request that rendered no such page (static files, token and API endpoints, an aborted
    /// authorize) has no transaction to resolve against, so the same keys are resolved at the core
    /// level, which is the only level it has: the same reading path, one step of it (SPEC-007 UI-090,
    /// SPEC-012 CFG-235). The resolution is guarded because the header must be written either way: a
    /// global section a deployment-side edit broke would otherwise throw while the response is
    /// starting, and the policy simply stays without an external origin.
    /// </remarks>
    /// <param name="context">HTTP context of the request.</param>
    /// <returns>External resources of this response.</returns>
    private static async Task<CorePageResourceScope> ResolveRequestResourcesAsync(HttpContext context)
    {
        var resources = context.RequestServices.GetRequiredService<CorePageResourceScope>();
        if (resources.IsDeclared)
        {
            return resources;
        }

        try
        {
            var resolver = context.RequestServices.GetRequiredService<IConfigurationResolver>();

            var css = await resolver.ResolveAsync(
                CorePageBrandingConfigKeys.CustomCss, ResolutionContext.Core, ConfigDimensionValues.None);
            var js = await resolver.ResolveAsync(
                AuthServerConfigKeys.PageCustomJs, ResolutionContext.Core, ConfigDimensionValues.None);

            resources.Declare(css.Value?.Path, js.Value?.Path);
        }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger<SecurityHeadersMiddleware>()
                .LogWarning(
                    exception,
                    "The external page resources of this request could not be resolved; "
                    + "the content security policy is written without an external origin.");

            resources.Declare(stylesheetPath: null);
        }

        return resources;
    }

    /// <summary>
    /// Extracts the origin of an external resource (scheme + host) from a URL.
    /// Returns null for relative paths and empty values.
    /// </summary>
    /// <param name="path">Resource URL from configuration.</param>
    /// <returns>Origin like https://cdn.example.com, or null.</returns>
    internal static string? GetExternalOrigin(string? path)
    {
        // Empty values do not require extending the CSP
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // Relative paths (starting with /) do not require extending the CSP
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        // Extract the origin for absolute HTTP/HTTPS URLs
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http")
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return null;
    }

    /// <summary>
    /// Builds the CSP source expression that allows a generated page to post to the given absolute
    /// URI — the value the <c>form_post</c> branches, the web sign-in confirmation page and the sign-in
    /// window carrying the "Cancel sign-in" button state in
    /// <see cref="SecurityHeaderConstants.FormActionTargetItemKey"/>.
    /// </summary>
    /// <remarks>
    /// The source is derived from the PARSED URI and never from the raw string, so a value that is not
    /// an absolute URI yields no source at all instead of an expression assembled out of text. An
    /// <c>http</c>/<c>https</c> target narrows to its origin (scheme, host and port — the tightest
    /// expression CSP Level 3 can state for a URL); any other scheme — a native application's custom
    /// scheme, which has no host to name — narrows to that scheme alone. A wildcard is never returned:
    /// <c>form-action *</c> is not introduced under any circumstance.
    /// </remarks>
    /// <param name="url">Absolute URI the page posts to (a validated <c>redirect_uri</c>).</param>
    /// <returns>The CSP source expression, or null when the value is not an absolute URI.</returns>
    internal static string? GetFormActionSource(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme is "https" or "http")
        {
            var origin = uri.GetLeftPart(UriPartial.Authority);
            return string.IsNullOrEmpty(origin) ? null : origin;
        }

        // A scheme may only be a scheme (RFC 3986 §3.1: letters, digits, '+', '-', '.'), so Uri having
        // parsed it is already the guarantee that it cannot break the directive apart.
        return $"{uri.Scheme}:";
    }

    /// <summary>
    /// Verifies that the host contains only characters safe for a CSP directive.
    /// Protects against Host header injection, where characters ';' or '\'' could
    /// break the structure of the CSP header.
    /// </summary>
    /// <param name="host">Host value (hostname + optional :port).</param>
    /// <returns>true if the host is safe to substitute into the CSP.</returns>
    private static bool IsSafeHostForCsp(string host)
    {
        // Allowed characters: letters, digits, '.', '-', ':', '[', ']' (for IPv6 addresses)
        foreach (var ch in host)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '-' and not ':' and not '[' and not ']')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Extension method for registering SecurityHeadersMiddleware in the pipeline.
/// </summary>
public static class SecurityHeadersMiddlewareExtensions
{
    /// <summary>
    /// Adds the security headers middleware (CSP, X-Frame-Options, etc.).
    /// Must be called BEFORE UseStaticFiles() and UseRouting() (SPEC-007 §6.3).
    /// </summary>
    /// <param name="app">Application.</param>
    /// <returns>Application for chaining.</returns>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
