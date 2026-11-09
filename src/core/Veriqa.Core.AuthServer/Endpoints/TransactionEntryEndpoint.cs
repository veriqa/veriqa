// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;

using Microsoft.Extensions.Options;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Entry page of an already created confirmation transaction (GET /auth/transaction, SPEC-039 C15) —
/// the second kind of entry next to the channel material returned by the creation answer. The page
/// shows the way INTO the channel and nothing else: no subject matter on it and none in its URL.
/// <para>
/// The transaction is addressed by its public identifier, and the identifier is what makes the page
/// unguessable (256-bit CSPRNG, <see cref="TransactionId.NewId"/>) — there is no second token to
/// carry. Opening the page again before the transaction ends is normal: it is an entry point, not a
/// single-use nonce, and the transaction's own protections still admit exactly one confirmation.
/// </para>
/// <para>
/// The page never leaves itself. A confirmation transaction has no browser callback of its own
/// (SPEC-039 C19), so a terminal outcome is shown in place — the browser stays on this URL, the
/// transaction stays in the store, and the relying party still reads the outcome through its own
/// surfaces (R49).
/// </para>
/// </summary>
public static class TransactionEntryEndpoint
{
    /// <summary>
    /// Registers the transaction entry page.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name (null — no limiting).</param>
    /// <returns>The route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapTransactionEntryEndpoint(
        this IEndpointRouteBuilder endpoints,
        string? rateLimitPolicyName = null)
    {
        var builder = endpoints.MapGet(OidcEndpoints.TransactionEntryPage, HandleEntryPageAsync);

        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            builder.RequireRateLimiting(rateLimitPolicyName);
        }

        return endpoints;
    }

    /// <summary>
    /// Answers with the channel entry page of the addressed transaction, or with the neutral page.
    /// </summary>
    private static async Task<IResult> HandleEntryPageAsync(
        HttpContext httpContext,
        ITransactionService transactionService,
        IChannelDisplayService channelDisplayService,
        IAuthPageRenderer authPageRenderer,
        AuthPageSettingsResolver pageSettingsResolver,
        TransactionPageContextBuilder pageContextBuilder,
        HopUnifiedPageQr unifiedPageQr,
        IConfirmationPromptLocalizer promptLocalizer,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry,
        ILoggerFactory loggerFactory)
    {
        // The method resolves the addressed transaction and renders either its entry page or the
        // neutral one — the three refusals being indistinguishable from the outside

        var cancellationToken = httpContext.RequestAborted;

        // No answer of this route is cached: the body of the page holds the way into the channel — the QR
        // code and the links with their hop tokens, the spare start codes — which a stored copy would keep
        // for whoever opens the browser next. Every answer gets the header, the neutral one too, so the
        // headers tell the cases apart no more than the body does. The directive is the one of every page of
        // the contour, which the anti-forgery tokens of the cancel form leave as it is.
        CorePageCaching.Forbid(httpContext.Response);

        // The language of the REQUEST. It is what the neutral page uses in every one of its cases,
        // including the one where a transaction was found — see TransactionEntryUnavailablePage.
        var requestLanguage = AuthPageStrings.DetectLanguage(
            httpContext.Request.Headers.AcceptLanguage.ToString(),
            veriqaOptions.Value.Localization.DefaultLanguage,
            languageRegistry.SupportedLanguages);

        var transactionId = SessionIdMapper.ToTransactionId(
            httpContext.Request.Query[OidcConstants.SessionIdParameterName].FirstOrDefault());

        if (transactionId is null)
        {
            return await UnavailableAsync(
                promptLocalizer, requestLanguage, brandingResolver, cancellationToken);
        }

        var txResult = await transactionService.GetTransactionAsync(transactionId.Value, cancellationToken);
        if (txResult.IsFailure)
        {
            return await UnavailableAsync(
                promptLocalizer, requestLanguage, brandingResolver, cancellationToken);
        }

        var transaction = txResult.Value;

        // A transaction of another type is refused with the same page as a missing one: this route
        // addresses confirmations, and signing in has a route of its own.
        if (!string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal)
            || transaction.IsTerminal())
        {
            return await UnavailableAsync(
                promptLocalizer, requestLanguage, brandingResolver, cancellationToken);
        }

        // From here on the page belongs to a live transaction, and every level-owned setting is
        // resolved over ITS ownership context — the same one the channel messages of this transaction
        // resolve over.
        var resolution = TransactionResolutionContext.For(transaction);
        var pageSettings = await pageSettingsResolver.ResolveAsync(resolution, cancellationToken);

        // Per-request channel display override from the ui_config record (CFG-211) — same narrowing
        // rule as on the sign-in path.
        var channelDataResult = await channelDisplayService.GetChannelDisplayDataAsync(
            transaction.Id,
            transaction.ExpiresAt,
            transaction.AllowedChannelTypes,
            resolution,
            ChannelDisplayOverride.Of(pageSettings),
            ChannelQrSurface.CorePage,
            cancellationToken);

        if (channelDataResult.IsFailure)
        {
            loggerFactory.CreateLogger(typeof(TransactionEntryEndpoint)).LogError(
                "Failed to prepare channel data for transaction {TransactionId}: {Error}",
                transaction.Id.ToString(),
                channelDataResult.Error.Message);

            return Results.Problem(
                detail: "Failed to prepare authentication channel data",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: OidcErrorCodes.ChannelDisplayFailed);
        }

        // The one QR code of the transaction when the unified hop mode applies for this page (SPEC-025
        // HOP-183, HOP-187) — decided and drawn by the function the sign-in window shares; null keeps the
        // per-channel QR codes.
        var unifiedQr = await unifiedPageQr.RenderAsync(
            transaction.Id,
            transaction.ExpiresAt,
            channelDataResult.Value,
            resolution,
            pageSettings.QrVisibility,
            cancellationToken);

        // The context of the page is built by the one function the scanner page of the unified hop QR
        // code shares for a confirmation (SPEC-025 HOP-146): the same page, with the scanner's flags.
        var renderContext = await pageContextBuilder.BuildEntryPageAsync(
            httpContext, transaction, pageSettings, channelDataResult.Value, unifiedQr, cancellationToken);

        var html = authPageRenderer.RenderAuthPage(renderContext);

        return Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8);
    }

    /// <summary>
    /// Builds the neutral answer — the single response for "no such transaction", "already over" and
    /// "another type".
    /// </summary>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Language detected from the request.</param>
    /// <param name="brandingResolver">Resolver of the effective page branding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The neutral page.</returns>
    private static async Task<IResult> UnavailableAsync(
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CorePageBrandingResolver brandingResolver,
        CancellationToken cancellationToken)
    {
        // The branding is resolved over the CORE level even when a transaction was found: resolving it
        // over the transaction's levels would make the look of the answer a readable state of somebody
        // else's transaction. Status 200 — this is a page, not an API.
        var branding = await brandingResolver.ResolveAsync(ResolutionContext.Core, cancellationToken);
        var html = TransactionEntryUnavailablePage.Build(promptLocalizer, language, branding);

        return Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8);
    }
}
