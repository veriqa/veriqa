// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// The page a link of the completion mail opens — the Email adapter's link way of the email completion of
/// the claims (SPEC-041 L50). It records the address the link was sent to with <c>email_verified = true</c>,
/// or holds it while other fields of the web step are open (<see cref="ClaimEmailWrites"/>).
/// The inbound mail of the <c>Push</c> way is handled by <see cref="EmailInboundMailPipeline"/>.
/// </summary>
public static partial class EmailAuthEndpoints
{
    /// <summary>
    /// Placeholder of the masked address in the question of the link page.
    /// </summary>
    private const string ClaimLinkAddressSlot = "{address}";

    /// <summary>
    /// Shows the page of a link of the claims-completion mail (GET) — reading only (SPEC-016 EM-010, EM-011):
    /// a mail gateway that follows links changes neither the transaction nor the link. While the link is the
    /// live one and the sign-in still waits for the address, the page asks to confirm it with a button that
    /// posts the token; otherwise it says the link no longer works (<c>410</c>).
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <returns>The page; every answer is <c>Cache-Control: no-store</c>.</returns>
    private static async Task<IResult> HandleClaimLinkGetAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;
        var token = httpContext.Request.Query[EmailAdapterConstants.TokenQueryParam].ToString();

        if (string.IsNullOrWhiteSpace(token))
        {
            return await ClaimLinkPageAsync(httpContext, transaction: null, ClaimLinkPageKind.Gone, cancellationToken);
        }

        EmailClaimLinkUse? link;
        Transaction? transaction = null;
        EmailClaimHeldAddress? held = null;

        try
        {
            link = await services.GetRequiredService<IEmailClaimCompletionStore>().FindLiveLinkAsync(
                EmailClaimSecrets.HashOfToken(token),
                services.GetRequiredService<TimeProvider>().GetUtcNow(),
                cancellationToken);

            if (link is not null && TransactionId.TryParse(link.TransactionId, out var transactionId))
            {
                var found = await services.GetRequiredService<ITransactionService>()
                    .GetTransactionAsync(transactionId, cancellationToken);
                transaction = found.IsSuccess ? found.Value : null;
                held = await HeldAddressAsync(services, link.TransactionId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ClaimLinkLogger(services).LogWarning(ex, "The link page of the email completion could not read the link or its sign-in.");
            return await ClaimLinkPageAsync(httpContext, transaction: null, ClaimLinkPageKind.Unavailable, cancellationToken);
        }

        // The mail may carry a code beside the link: an address the code has already confirmed is
        // confirmed, and its link says so instead of calling itself broken.
        if (link is not null && transaction is not null && IsAlreadyConfirmed(transaction, held, link.Address))
        {
            return await ClaimLinkPageAsync(httpContext, transaction, ClaimLinkPageKind.Confirmed, cancellationToken);
        }

        if (link is null || transaction is null || ClaimEmailItem.Open(transaction) is null)
        {
            return await ClaimLinkPageAsync(httpContext, transaction, ClaimLinkPageKind.Gone, cancellationToken);
        }

        return await RenderClaimLinkPageAsync(
            httpContext,
            transaction,
            ClaimLinkPageKind.Question,
            (ChannelIdentityMask.MaskEmail(link.Address), token),
            cancellationToken);
    }

    /// <summary>
    /// Confirms the address of a link of the claims-completion mail (POST): the link is used — once, of
    /// concurrent posts one wins — and the address it was sent to is recorded as the verified email of the
    /// sign-in. A link that is not the live one, or a sign-in that no longer takes the address, is answered
    /// with <c>410</c>. A recorded address is announced to the pages of the sign-in by the channel status
    /// <see cref="EmailChannelStatuses.ClaimEmailConfirmed"/>.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <returns>The page of the outcome; every answer is <c>Cache-Control: no-store</c>.</returns>
    private static async Task<IResult> HandleClaimLinkPostAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var logger = ClaimLinkLogger(services);
        var token = await ExtractTokenAsync(httpContext);

        if (string.IsNullOrWhiteSpace(token))
        {
            return await ClaimLinkPageAsync(httpContext, transaction: null, ClaimLinkPageKind.Gone, httpContext.RequestAborted);
        }

        Transaction? transaction = null;
        string? linkAddress = null;
        Result<Transaction> recorded;
        var held = false;

        // CancellationToken.None from the use of the link on: once used, the link is spent, and the address it
        // carries must be recorded even if the browser goes away.
        try
        {
            var used = await services.GetRequiredService<IEmailClaimCompletionStore>().TryUseLinkAsync(
                EmailClaimSecrets.HashOfToken(token),
                services.GetRequiredService<TimeProvider>().GetUtcNow(),
                CancellationToken.None);

            if (used is null || !TransactionId.TryParse(used.TransactionId, out var transactionId))
            {
                return await ClaimLinkPageAsync(httpContext, transaction: null, ClaimLinkPageKind.Gone, CancellationToken.None);
            }

            linkAddress = used.Address;

            var transactionService = services.GetRequiredService<ITransactionService>();
            var found = await transactionService.GetTransactionAsync(transactionId, CancellationToken.None);
            transaction = found.IsSuccess ? found.Value : null;

            // The write may finish the sign-in, which reads the Email credentials of the tenant this sign-in
            // belongs to (CA-164).
            using var tenantScope = ChannelTenantContext.BeginScope(transaction?.GetTenantId());

            // The address is the one the link was sent to (SPEC-041 L50), never an input of this request.
            var obtained = new ClaimEmailHeldAddress(used.Address, Verified: true, EmailCompletionMethod.MagicLink);
            var write = transaction is null
                ? new ClaimEmailWrite(
                    await transactionService.RecordClaimValueAsync(
                        transactionId,
                        VeriqaClaimTypes.Email,
                        obtained.Address,
                        obtained.Verified,
                        EmailCompletionMethods.ToCollectionMethod(obtained.Method),
                        CancellationToken.None),
                    Held: false)
                : await ClaimEmailWrites.RecordOrHoldAsync(
                    transactionService,
                    services.GetService<IClaimCompletionEmail>(),
                    transaction,
                    obtained,
                    services.GetRequiredService<TimeProvider>().GetUtcNow(),
                    CancellationToken.None);

            recorded = write.Outcome;
            held = write.Held;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The link of the email completion could not be used or its address recorded.");
            return await ClaimLinkPageAsync(httpContext, transaction, ClaimLinkPageKind.Unavailable, CancellationToken.None);
        }

        if (recorded.IsFailure)
        {
            // The sign-in no longer waits for the address: obtained another way, cancelled, finished or expired.
            logger.LogInformation(
                "The link of the email completion was used, but the sign-in took no address. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transaction?.Id.ToString(),
                recorded.Error.Code);

            // Obtained another way may mean "by the code of the same mail", or by the form that recorded the
            // address this link had just held: the address of this link is then the confirmed one, and
            // confirming it once more changes nothing and is no failure. The transaction is read again, since
            // that write came after the read above.
            transaction = transaction is null ? null : await ReadAgainAsync(services, transaction, logger);
            var heldAddress = transaction is null
                ? null
                : await HeldAddressAsync(services, transaction.Id.ToString(), CancellationToken.None);
            var answer = transaction is not null && linkAddress is not null && IsAlreadyConfirmed(transaction, heldAddress, linkAddress)
                ? ClaimLinkPageKind.Confirmed
                : ClaimLinkPageKind.Gone;

            return await ClaimLinkPageAsync(httpContext, transaction, answer, CancellationToken.None);
        }

        logger.LogInformation(
            "The address of the email completion was confirmed by the link of its mail. TransactionId: {TransactionId}, Held: {Held}",
            recorded.Value.Id.ToString(),
            held);

        // The form of the web step learns of the confirmation from the hub and performs its "Check
        // confirmation" by itself. The hint goes out before the post-completion work, which may take long,
        // and a page that misses it still has the button.
        await PublishEmailChannelStatusAsync(services, recorded.Value.Id, EmailChannelStatuses.ClaimEmailConfirmed, logger);

        // A held address has not ended the sign-in: the form records it when it is finished.
        if (!held)
        {
            await DispatchCompletionBehaviorAsync(services, recorded.Value);
        }

        return await ClaimLinkPageAsync(httpContext, recorded.Value, ClaimLinkPageKind.Confirmed, CancellationToken.None);
    }

    /// <summary>
    /// Whether the sign-in already has an address as its verified email — the address a link was sent to,
    /// confirmed meanwhile by the code the same mail carried — recorded, or held until the form is finished.
    /// </summary>
    /// <param name="transaction">The sign-in the link belongs to.</param>
    /// <param name="held">The address held for the sign-in; null — none.</param>
    /// <param name="address">Address the link was sent to.</param>
    /// <returns><c>true</c> when exactly this address is recorded or held as verified.</returns>
    private static bool IsAlreadyConfirmed(Transaction transaction, EmailClaimHeldAddress? held, string address) =>
        (held is { Verified: true } && string.Equals(held.Address, address, StringComparison.Ordinal))
        || transaction.ClaimCompletion?.Results.Any(result =>
            VeriqaClaimTypes.NameComparer.Equals(result.Claim, VeriqaClaimTypes.Email)
            && result.Verified
            && string.Equals(result.Value, address, StringComparison.Ordinal)) is true;

    /// <summary>
    /// The sign-in as it stands now; the copy read before when it cannot be read again.
    /// </summary>
    /// <param name="services">Services of the request.</param>
    /// <param name="transaction">The sign-in as read before.</param>
    /// <param name="logger">Logger of the link page.</param>
    /// <returns>The sign-in.</returns>
    private static async Task<Transaction> ReadAgainAsync(IServiceProvider services, Transaction transaction, ILogger logger)
    {
        try
        {
            var found = await services.GetRequiredService<ITransactionService>()
                .GetTransactionAsync(transaction.Id, CancellationToken.None);
            return found.IsSuccess ? found.Value : transaction;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The link page of the email completion could not read the sign-in again.");
            return transaction;
        }
    }

    /// <summary>
    /// The address held for a sign-in until its form is finished.
    /// </summary>
    /// <param name="services">Services of the request.</param>
    /// <param name="transactionId">The sign-in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The held address; null — none is held, or the store could not be reached.</returns>
    private static async Task<EmailClaimHeldAddress?> HeldAddressAsync(
        IServiceProvider services,
        string transactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await services.GetRequiredService<IEmailClaimCompletionStore>().GetAsync(transactionId, cancellationToken))?.Held;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ClaimLinkLogger(services).LogWarning(ex, "The link page of the email completion could not read the held address.");
            return null;
        }
    }

    /// <summary>
    /// Shows the channel the user came through the outcome of a sign-in the write of the address ended
    /// (SPEC-003 CA-142), and then performs its post-completion action when the recorded address was
    /// its last missing claim (SPEC-012 §4.9). A sign-in that still waits for other claims has not
    /// ended: nothing is shown and nothing is performed.
    /// </summary>
    /// <param name="services">Services of the request.</param>
    /// <param name="recorded">The transaction the write of the address returned.</param>
    /// <returns>The completed operation.</returns>
    internal static async Task DispatchCompletionBehaviorAsync(IServiceProvider services, Transaction recorded)
    {
        // CancellationToken.None — a connection that goes away must not cancel a message of a sign-in
        // that is already completed. The receipt of the outcome goes first: the action follows it.
        if (services.GetService<IClaimCompletionOutcomeReporter>() is { } outcomeReporter)
        {
            await outcomeReporter.ReportAsync(recorded, CancellationToken.None);
        }

        if (recorded.State is TransactionState.Completed
            && services.GetService<ICompletionBehaviorDispatcher>() is { } completionBehavior)
        {
            await completionBehavior.DispatchAsync(recorded, CancellationToken.None);
        }
    }

    /// <summary>
    /// Renders an outcome of the link page — confirmed, no longer working, unavailable.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="transaction">The transaction of the link; null — none is known.</param>
    /// <param name="kind">The outcome.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    private static Task<IResult> ClaimLinkPageAsync(
        HttpContext httpContext,
        Transaction? transaction,
        ClaimLinkPageKind kind,
        CancellationToken cancellationToken) =>
        RenderClaimLinkPageAsync(httpContext, transaction, kind, question: null, cancellationToken);

    /// <summary>
    /// Renders one state of the link page, branded and worded for the transaction of the link (the core
    /// level and the request language when there is none), with <c>Cache-Control: no-store</c>.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="transaction">The transaction of the link; null — none is known.</param>
    /// <param name="kind">The state of the page.</param>
    /// <param name="question">The masked address the link was sent to and the token the button posts — the
    /// state <see cref="ClaimLinkPageKind.Question"/> only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    private static async Task<IResult> RenderClaimLinkPageAsync(
        HttpContext httpContext,
        Transaction? transaction,
        ClaimLinkPageKind kind,
        (string MaskedAddress, string Token)? question,
        CancellationToken cancellationToken)
    {
        var services = httpContext.RequestServices;
        var localizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var (branding, texts) = await ResolvePageCustomizationAsync(
            services, TransactionResolutionContext.For(transaction), cancellationToken);

        // The language the sign-in page was rendered in, not that of the mail client's browser; without a
        // transaction — the request language. The texts are looked up in the language the page states in
        // <html lang>, which is what an entry stating texts by language is matched against.
        var storedLocale = transaction?.GetUiLocale();
        var locale = string.IsNullOrWhiteSpace(storedLocale)
            ? ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString())
            : storedLocale;
        var htmlLang = ResolveServedHtmlLang(localizer, locale);

        var appName = ApplicationNameOf(transaction);
        var brandName = transaction?.GetBrandName();

        // The question and the confirmed address carry the button that closes the tab: "Cancel" beside the
        // button of the question, "Close" under the confirmed address. It changes nothing.
        var (content, statusCode) = kind switch
        {
            ClaimLinkPageKind.Question when question is { } shown => (
                BuildClaimLinkQuestion(localizer, texts, htmlLang, appName, brandName, shown.MaskedAddress, shown.Token),
                StatusCodes.Status200OK),
            ClaimLinkPageKind.Confirmed => (
                $"""<div class="veriqa-status success">{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.ClaimLinkPageConfirmed, htmlLang))}</div>"""
                    + CorePageCloseAction.Row(
                        texts.Localize(localizer, CorePageCloseAction.CloseText, htmlLang),
                        texts.Localize(localizer, CorePageCloseAction.CloseHint, htmlLang)),
                StatusCodes.Status200OK),
            ClaimLinkPageKind.Unavailable => (
                $"""<div class="veriqa-status error">{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.ConfirmPageError, htmlLang))}</div>""",
                StatusCodes.Status503ServiceUnavailable),
            _ => (
                $"""<div class="veriqa-status error">{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.ClaimLinkPageGone, htmlLang))}</div>""",
                StatusCodes.Status410Gone)
        };

        var title = HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.ClaimLinkPageTitle, htmlLang));
        var closesTab = kind is ClaimLinkPageKind.Question or ClaimLinkPageKind.Confirmed;
        var closeScript = closesTab
            ? CorePageCloseAction.Script(httpContext.Items[HttpContextItemKeys.CspNonce] as string)
            : string.Empty;

        var html = $$"""
            <!DOCTYPE html>
            <html lang="{{htmlLang}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>{{title}}</title>
              {{CorePageHead.BuildServicePageHead(branding, closesTab ? CorePageCloseAction.Css : null)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{title}}</h1>
                {{content}}
              </main>
              {{closeScript}}
              {{CorePageAttribution.BuildLine(htmlLang)}}
            </body>
            </html>
            """;

        // The page carries the token of the link: neither it nor the address may stay in a cache.
        httpContext.Response.Headers[HeaderNames.CacheControl] = CacheControlHeaderValue.NoStoreString;

        return Results.Content(html, "text/html", Encoding.UTF8, statusCode);
    }

    /// <summary>
    /// The question of the link page: the masked address and the application, the warning of the link mail,
    /// the button that posts the token and "Cancel" beside it, which only closes the tab. The form has no
    /// action, so it posts to the URL of the page and works under a sub-path (PathBase); a POST, so that a
    /// scanner following links cannot confirm.
    /// </summary>
    /// <param name="localizer">Served page localizer.</param>
    /// <param name="texts">Overrides of the page's built-in texts, resolved for its transaction.</param>
    /// <param name="htmlLang">Language of the page's content — the one it states in <c>&lt;html lang&gt;</c>.</param>
    /// <param name="appName">Name of the application being signed in to.</param>
    /// <param name="brandName">Brand name the levels of the transaction state; null — none is stated.</param>
    /// <param name="maskedAddress">The masked address.</param>
    /// <param name="token">The token of the link.</param>
    /// <returns>The HTML of the question.</returns>
    private static string BuildClaimLinkQuestion(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string htmlLang,
        string appName,
        string? brandName,
        string maskedAddress,
        string token)
    {
        var question = EmailLinkPageNameSlots.Fill(
            texts.Localize(localizer, EmailAdapterMailStrings.ClaimLinkPageQuestion, htmlLang)
                .Replace(ClaimLinkAddressSlot, maskedAddress, StringComparison.Ordinal),
            localizer,
            htmlLang,
            appName,
            brandName);
        var warning = EmailLinkPageNameSlots.Fill(
            texts.Localize(localizer, EmailAdapterMailStrings.ClaimLinkPageWarning, htmlLang),
            localizer,
            htmlLang,
            appName,
            brandName);
        var cancel = CorePageCloseAction.BesideForm(
            texts.Localize(localizer, EmailAdapterMailStrings.CancelButton, htmlLang),
            texts.Localize(localizer, CorePageCloseAction.CloseHint, htmlLang));

        return $"""
            <p class="veriqa-instruction">{HtmlEncode(question)}</p>
            <p class="veriqa-warning-note">{HtmlEncode(warning)}</p>
            <div class="veriqa-actions">
              <form method="post">
                <input type="hidden" name="{EmailAdapterConstants.TokenQueryParam}" value="{HtmlEncode(token)}" />
                <button type="submit" class="veriqa-btn">{HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.ClaimLinkPageConfirm, htmlLang))}</button>
              </form>
              {cancel}
            </div>
            """;
    }

    /// <summary>
    /// Logger of the link page of the email completion.
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <returns>The logger.</returns>
    private static ILogger ClaimLinkLogger(IServiceProvider services) =>
        services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EmailAuthEndpoints));

    /// <summary>
    /// States of the link page.
    /// </summary>
    private enum ClaimLinkPageKind
    {
        /// <summary>The live link of a sign-in waiting for the address: the question and the button.</summary>
        Question,

        /// <summary>The address is recorded.</summary>
        Confirmed,

        /// <summary>The link no longer works (<c>410</c>).</summary>
        Gone,

        /// <summary>The store could not answer (<c>503</c>).</summary>
        Unavailable
    }
}
