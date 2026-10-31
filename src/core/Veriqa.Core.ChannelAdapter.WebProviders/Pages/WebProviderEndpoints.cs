// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net.Mime;
using System.Text;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using OpenIddict.Client.AspNetCore;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.ChannelAdapter.WebProviders.Constants;
using Veriqa.Core.ChannelAdapter.WebProviders.Identity;
using Veriqa.Core.ChannelAdapter.WebProviders.State;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Pages;

/// <summary>
/// Routes of one web provider channel, all under <c>/auth/web-provider/{channel}/</c>:
/// <list type="bullet">
/// <item><c>GET start?tx=</c> — the link of the channel: a live transaction is challenged to the
/// provider of the channel through the OpenIddict client;</item>
/// <item><c>GET|POST callback</c> — the address of return: OpenIddict validates the answer of the
/// provider, the route builds the identity snapshot, hands the browser its mark, gives the start of the
/// sign-in to the core and redirects to the page. It creates no session of the host;</item>
/// <item><c>GET page</c> — the page of the session the mark names, and <c>GET page/state</c> — the
/// fingerprint of its state the page script watches;</item>
/// <item><c>POST answer</c> — "Yes" or "No" to the question; <c>POST press</c> — the press of an item
/// of the menu.</item>
/// </list>
/// Every event goes to the core the way a webhook event does, under the tenant of its transaction.
/// Nothing here depends on the type of the transaction.
/// </summary>
internal static class WebProviderEndpoints
{
    /// <summary>
    /// Item of the authentication properties carrying the transaction from the challenge to the address
    /// of return: OpenIddict keeps the items in the state token, which it protects.
    /// </summary>
    private const string TransactionPropertyName = "veriqa_transaction_id";

    /// <summary>
    /// Base language of the page — the language of the Natural Keys.
    /// </summary>
    private const string BaseLanguage = "en";

    /// <summary>
    /// Separator of the primary subtag of a language tag.
    /// </summary>
    private const char LanguageSubtagSeparator = '-';

    /// <summary>
    /// Maps the routes of one enabled channel.
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <param name="channelType">The channel.</param>
    /// <param name="pagePolicyName">Rate limiting policy of the pages a person opens; <c>null</c> — no
    /// limiting. The page and its state are not limited: the script of an open page reads the state on
    /// its own.</param>
    public static void Map(IEndpointRouteBuilder endpoints, string channelType, string? pagePolicyName)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        Limit(
            endpoints.MapGet(
                WebProviderRoutes.RoutePath(channelType, WebProviderRoutes.StartSegment),
                Bound(HandleStartAsync, channelType)),
            pagePolicyName);

        // A provider answering with form_post (Apple) posts to the address of return.
        Limit(
            endpoints.MapMethods(
                WebProviderRoutes.RoutePath(channelType, WebProviderRoutes.CallbackSegment),
                [HttpMethods.Get, HttpMethods.Post],
                Bound(HandleCallbackAsync, channelType)),
            pagePolicyName);

        endpoints.MapGet(
            WebProviderRoutes.RoutePath(channelType, WebProviderRoutes.PageSegment),
            Bound(HandlePageAsync, channelType));

        endpoints.MapGet(
            WebProviderRoutes.RoutePath(channelType, WebProviderRoutes.PageStateSegment),
            Bound(HandlePageStateAsync, channelType));

        Limit(
            endpoints.MapPost(
                WebProviderRoutes.RoutePath(channelType, WebProviderRoutes.AnswerSegment),
                Bound(HandleAnswerAsync, channelType)),
            pagePolicyName);

        Limit(
            endpoints.MapPost(
                WebProviderRoutes.RoutePath(channelType, WebProviderRoutes.PressSegment),
                Bound(HandlePressAsync, channelType)),
            pagePolicyName);
    }

    /// <summary>
    /// The link of the channel. A live transaction — known, not terminal, within its deadline — is
    /// challenged to the provider of the channel: the registration of the channel is named by its
    /// identifier, and the transaction travels in the state OpenIddict protects. Any other link is
    /// answered with <c>404</c> and the page of a request not found, without leaving for the provider.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The challenge, or the refusal page.</returns>
    private static async Task<IResult> HandleStartAsync(HttpContext httpContext, string channelType)
    {
        var services = httpContext.RequestServices;
        var transaction = await TryLoadTransactionAsync(
            services,
            httpContext.Request.Query[WebProviderRoutes.TransactionQueryParameter].FirstOrDefault(),
            httpContext.RequestAborted);

        if (!IsLive(transaction, services, out var live))
        {
            return await NoticeAsync(
                httpContext, channelType, transaction, WebProviderPageStrings.RequestNotFound, StatusCodes.Status404NotFound);
        }

        var properties = new AuthenticationProperties(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [OpenIddictClientAspNetCoreConstants.Properties.RegistrationId] = channelType,
            [TransactionPropertyName] = live.Id.ToString()
        });

        return Results.Challenge(properties, [OpenIddictClientAspNetCoreDefaults.AuthenticationScheme]);
    }

    /// <summary>
    /// The address of return. OpenIddict has validated the request by the time the route runs
    /// (pass-through mode), and the authentication of its scheme yields the account the provider
    /// confirmed — or the refusal: an error of the provider, a cancellation by the user, a state that
    /// did not pass, none of which reaches the core; the user may follow the link again. A confirmed
    /// account of a live transaction gets its session of the page and the mark of it, then the start of
    /// the sign-in goes to the core the way a webhook event does, and the browser is sent to the page.
    /// No session of the host is created.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The redirect to the page, or a refusal page.</returns>
    private static async Task<IResult> HandleCallbackAsync(HttpContext httpContext, string channelType)
    {
        // The method turns the answer of the provider into a session of the page and the start event

        var services = httpContext.RequestServices;
        var logger = LoggerOf(services);
        var cancellationToken = httpContext.RequestAborted;

        var result = await httpContext.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);

        string? transactionValue = null;
        result.Properties?.Items.TryGetValue(TransactionPropertyName, out transactionValue);
        var transaction = await TryLoadTransactionAsync(services, transactionValue, cancellationToken);

        if (!result.Succeeded || result.Principal is null)
        {
            // The code of the error is the provider's or OpenIddict's, not a value of the user.
            string? error = null;
            result.Properties?.Items.TryGetValue(OpenIddictClientAspNetCoreConstants.Properties.Error, out error);
            logger.LogWarning(
                "Web provider channel {ChannelType}: the sign-in at the provider was not confirmed. Error: {Error}",
                channelType,
                error);

            return await NoticeAsync(
                httpContext, channelType, transaction, WebProviderPageStrings.ProviderRefused, StatusCodes.Status400BadRequest);
        }

        if (!IsLive(transaction, services, out var live))
        {
            return await NoticeAsync(
                httpContext, channelType, transaction, WebProviderPageStrings.RequestNotFound, StatusCodes.Status404NotFound);
        }

        var timeProvider = services.GetRequiredService<TimeProvider>();
        var now = timeProvider.GetUtcNow();
        var snapshot = WebProviderIdentitySnapshotBuilder.Build(channelType, result.Principal, live.GetTenantId(), now);
        if (snapshot is null)
        {
            logger.LogWarning(
                "Web provider channel {ChannelType}: the provider stated no identifier of the account. TransactionId: {TransactionId}",
                channelType,
                live.Id.ToString());

            return await NoticeAsync(
                httpContext, channelType, live, WebProviderPageStrings.ProviderRefused, StatusCodes.Status400BadRequest);
        }

        if (AdapterOf(services, channelType) is not { } adapter)
        {
            logger.LogError("Web provider channel {ChannelType}: the adapter is not registered", channelType);

            return await NoticeAsync(
                httpContext, channelType, live, WebProviderPageStrings.Failed, StatusCodes.Status500InternalServerError);
        }

        // The session ends with the state of its transaction; the transaction is live, so it ends later
        // than now.
        var end = WebProviderStateLifetime.EndOf(live, services.GetRequiredService<IOptionsMonitor<TransactionEngineOptions>>().CurrentValue);
        var mark = WebProviderPageMark.NewMark();
        await services.GetRequiredService<IWebProviderStateStore>().SaveSessionAsync(
            mark,
            new WebProviderPageSession(channelType, snapshot.ChannelUserId, live.Id, snapshot, end),
            cancellationToken);

        services.GetRequiredService<WebProviderPageMark>().Issue(httpContext, channelType, mark, end - now);

        await ProcessAsync(
            services,
            new ChannelAuthStartResult { TransactionId = live.Id, Identity = snapshot },
            adapter,
            live.GetTenantId(),
            cancellationToken);

        return RedirectToPage(httpContext, channelType);
    }

    /// <summary>
    /// The page of the session the mark of the browser names. A browser without the mark — the
    /// address opened elsewhere — gets <c>403</c> and a page with nothing of the session; while the
    /// claim form waits, the browser is sent to the form (<see cref="WebProviderSessionPage.FormToOpen"/>).
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The page.</returns>
    private static async Task<IResult> HandlePageAsync(HttpContext httpContext, string channelType)
    {
        var services = httpContext.RequestServices;

        DisableCaching(httpContext);

        var page = await LoadSessionPageAsync(httpContext, channelType);
        if (page is null)
        {
            return await NoticeAsync(
                httpContext, channelType, transaction: null, WebProviderPageStrings.NotInThisBrowser, StatusCodes.Status403Forbidden);
        }

        if (page.FormToOpen() is { } formUrl)
        {
            httpContext.Response.Headers.Location = formUrl;
            return Results.StatusCode(StatusCodes.Status303SeeOther);
        }

        var frame = await FrameAsync(httpContext, channelType, page.Transaction);
        var remaining = page.Session.ExpiresAt - services.GetRequiredService<TimeProvider>().GetUtcNow();

        var html = WebProviderPageRenderer.BuildSessionPage(
            frame,
            page.View,
            page.Session.TransactionId.ToString(),
            services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(httpContext),
            (long)Math.Max(0, remaining.TotalMilliseconds));

        return Html(html, StatusCodes.Status200OK);
    }

    /// <summary>
    /// The fingerprint of the state of the page the mark names, for the page script; <c>403</c> without
    /// a live session. A state that cannot be read is answered by the guard of the routes, and the script
    /// reads such an answer as a passing failure.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The fingerprint as plain text.</returns>
    private static async Task<IResult> HandlePageStateAsync(HttpContext httpContext, string channelType)
    {
        DisableCaching(httpContext);

        var page = await LoadSessionPageAsync(httpContext, channelType);

        return page is null
            ? Results.StatusCode(StatusCodes.Status403Forbidden)
            : Results.Text(page.View.Fingerprint(), MediaTypeNames.Text.Plain, Encoding.UTF8);
    }

    /// <summary>
    /// "Yes" or "No" to the question. Taken only from the browser that holds the mark of the session of
    /// the question's transaction, and only for the question put to the account of that session: an
    /// answer without the mark, or with the mark of another transaction, is refused with <c>403</c>
    /// and reaches nobody; an answer the question no longer waits for — answered already, put to
    /// another account, gone — sends the browser back to the page without an event. The question is
    /// marked answered only once the core has processed the answer; a failed processing leaves it open.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The redirect to the page, or a refusal page.</returns>
    private static async Task<IResult> HandleAnswerAsync(HttpContext httpContext, string channelType)
    {
        // The method checks the browser, the form and the question, then hands the answer to the core

        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;

        var (session, form) = await AdmitFormAsync(httpContext, channelType);
        if (session is null || form is null)
        {
            return await RefusalOfFormAsync(httpContext, channelType, session);
        }

        if (!string.Equals(form[WebProviderPageFields.Transaction].ToString(), session.TransactionId.ToString(), StringComparison.Ordinal))
        {
            return await NoticeAsync(
                httpContext, channelType, transaction: null, WebProviderPageStrings.NotInThisBrowser, StatusCodes.Status403Forbidden);
        }

        var answer = form[WebProviderPageFields.Answer].ToString();
        var confirms = string.Equals(answer, WebProviderPageFields.AnswerConfirm, StringComparison.Ordinal);
        if (!confirms && !string.Equals(answer, WebProviderPageFields.AnswerDecline, StringComparison.Ordinal))
        {
            return await NoticeAsync(
                httpContext, channelType, transaction: null, WebProviderPageStrings.PageOutOfDate, StatusCodes.Status400BadRequest);
        }

        var store = services.GetRequiredService<IWebProviderStateStore>();
        var prompt = await store.FindPromptAsync(channelType, session.TransactionId, cancellationToken);
        if (prompt is null
            || prompt.Answered
            || !string.Equals(prompt.ChannelUserId, session.ChannelUserId, StringComparison.Ordinal)
            || AdapterOf(services, channelType) is not { } adapter)
        {
            return RedirectToPage(httpContext, channelType);
        }

        ChannelInboundResult inbound = confirms
            ? new ChannelAuthConfirmResult { TransactionId = session.TransactionId, Identity = session.Identity }
            : new ChannelAuthDeclineResult { TransactionId = session.TransactionId, Identity = session.Identity };

        // An answer the core did not take leaves the question open, so the person can answer again.
        if (await ProcessAsync(services, inbound, adapter, session.Identity.TenantId, cancellationToken))
        {
            await store.MarkPromptAnsweredAsync(channelType, session.TransactionId, cancellationToken);
        }

        return RedirectToPage(httpContext, channelType);
    }

    /// <summary>
    /// The press of an item of the menu. Taken only from the browser that holds the mark of a session
    /// of the account, and only for an item the message the page shows carries; the identifier the press was
    /// given when its button was drawn goes with it, so sending the same form again is the same press.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The redirect to the page, or a refusal page.</returns>
    private static async Task<IResult> HandlePressAsync(HttpContext httpContext, string channelType)
    {
        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;

        var (session, form) = await AdmitFormAsync(httpContext, channelType);
        if (session is null || form is null)
        {
            return await RefusalOfFormAsync(httpContext, channelType, session);
        }

        var item = form[WebProviderPageFields.Item].ToString();
        if (!MenuItemPayload.TryRead(item, out var value))
        {
            return RedirectToPage(httpContext, channelType);
        }

        // Only the message the page shows takes a press: an item of a past sign-in, still in the feed of the
        // account, is not one the person sees.
        var transactionTask = TryLoadTransactionAsync(services, session.TransactionId, cancellationToken);
        var feedTask = services.GetRequiredService<IWebProviderStateStore>()
            .ListMessagesAsync(channelType, session.ChannelUserId, cancellationToken);

        await Task.WhenAll(transactionTask, feedTask);

        var feed = WebProviderPageView.CurrentFeed(
            await feedTask,
            await transactionTask,
            services.GetRequiredService<TimeProvider>().GetUtcNow());
        var carried = feed.Any(message => message.ActionButtons?.Buttons.Any(button =>
            string.Equals(button.CallbackData, item, StringComparison.Ordinal)) is true);

        if (!carried || AdapterOf(services, channelType) is not { } adapter)
        {
            return RedirectToPage(httpContext, channelType);
        }

        var pressId = form[WebProviderPageFields.PressId].ToString();

        await ProcessAsync(
            services,
            new ChannelMenuItemPressedResult
            {
                Identity = session.Identity,
                Value = value,
                PressId = pressId.Length is > 0 and <= WebProviderPageFields.MaxPressIdLength ? pressId : null
            },
            adapter,
            session.Identity.TenantId,
            cancellationToken);

        return RedirectToPage(httpContext, channelType);
    }

    /// <summary>
    /// Admits a form of the page: the browser holds the mark of a live session of the channel, and the
    /// anti-forgery token of the form is valid.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The session, <c>null</c> when the browser holds no mark of a live session; and the form,
    /// <c>null</c> when it is not admitted (<see cref="RefusalOfFormAsync"/> words the refusal).</returns>
    private static async Task<(WebProviderPageSession? Session, IFormCollection? Form)> AdmitFormAsync(
        HttpContext httpContext,
        string channelType)
    {
        var session = await FindSessionAsync(httpContext, channelType);
        if (session is null || !httpContext.Request.HasFormContentType)
        {
            return (session, null);
        }

        try
        {
            await httpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return (session, null);
        }

        return (session, await httpContext.Request.ReadFormAsync(httpContext.RequestAborted));
    }

    /// <summary>
    /// Words the refusal of a form <see cref="AdmitFormAsync"/> did not admit: <c>403</c> for a browser
    /// without the mark, <c>400</c> and the page of an out-of-date form otherwise.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <param name="session">The session <see cref="AdmitFormAsync"/> found; <c>null</c> — none.</param>
    /// <returns>The refusal page.</returns>
    private static async Task<IResult> RefusalOfFormAsync(
        HttpContext httpContext,
        string channelType,
        WebProviderPageSession? session) =>
        session is null
            ? await NoticeAsync(
                httpContext, channelType, transaction: null, WebProviderPageStrings.NotInThisBrowser, StatusCodes.Status403Forbidden)
            : await NoticeAsync(
                httpContext, channelType, transaction: null, WebProviderPageStrings.PageOutOfDate, StatusCodes.Status400BadRequest);

    /// <summary>
    /// Hands an event of the page to the core the way a webhook event is handed: the question goes back
    /// through <see cref="IChannelAdapter.SendConfirmationPromptAsync"/>, messages through
    /// <see cref="IChannelAdapter.SendMessageAsync"/>. The tenant of the event is the tenant of its
    /// transaction. A failure of the processing is logged; the page then shows what the store holds.
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <param name="inbound">The event.</param>
    /// <param name="adapter">The adapter of the channel.</param>
    /// <param name="tenantId">Tenant of the transaction of the event; <c>null</c> — the default tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the core processed the event; <c>false</c> — the processing failed.</returns>
    private static async Task<bool> ProcessAsync(
        IServiceProvider services,
        ChannelInboundResult inbound,
        IChannelAdapter adapter,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

        try
        {
            await services.GetRequiredService<ChannelInboundProcessor>().ProcessAsync(
                inbound,
                adapter,
                services.GetRequiredService<CustomChannelRegistry>().ReplyTextsOf(adapter.ChannelType),
                asksInChannelPrompt: true,
                cancellationToken);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LoggerOf(services).LogError(
                exception,
                "Web provider channel {ChannelType}: an event of the page could not be processed. Intent: {Intent}",
                adapter.ChannelType,
                inbound.GetType().Name);

            return false;
        }
    }

    /// <summary>
    /// Reads the page of the session the mark of the browser names: the session, its transaction and
    /// what the page shows, derived from the store of the adapter and the transaction. A failure of the
    /// state store surfaces as its exception.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The page; <c>null</c> — the browser holds no mark of a live session of the channel.</returns>
    private static async Task<WebProviderSessionPage?> LoadSessionPageAsync(HttpContext httpContext, string channelType)
    {
        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;

        var session = await FindSessionAsync(httpContext, channelType);
        if (session is null)
        {
            return null;
        }

        // The reads are independent: one read of the transaction store, which may be scoped, and three of
        // the state store, a singleton safe for concurrent use.
        var store = services.GetRequiredService<IWebProviderStateStore>();
        var transactionTask = TryLoadTransactionAsync(services, session.TransactionId, cancellationToken);
        var outcomeTask = store.FindOutcomeAsync(channelType, session.TransactionId, cancellationToken);
        var promptTask = store.FindPromptAsync(channelType, session.TransactionId, cancellationToken);
        var feedTask = store.ListMessagesAsync(channelType, session.ChannelUserId, cancellationToken);

        await Task.WhenAll(transactionTask, outcomeTask, promptTask, feedTask);

        var transaction = await transactionTask;
        var view = WebProviderPageView.Of(
            session,
            transaction,
            await promptTask,
            await outcomeTask,
            await feedTask,
            services.GetRequiredService<TimeProvider>().GetUtcNow());

        return new WebProviderSessionPage(session, transaction, view);
    }

    /// <summary>
    /// The live session of the channel the mark of the browser names.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The session; <c>null</c> — no mark, or one no live session of this channel holds.</returns>
    private static async Task<WebProviderPageSession?> FindSessionAsync(HttpContext httpContext, string channelType)
    {
        var services = httpContext.RequestServices;
        var mark = services.GetRequiredService<WebProviderPageMark>().Read(httpContext.Request, channelType);
        if (mark is null)
        {
            return null;
        }

        var session = await services.GetRequiredService<IWebProviderStateStore>()
            .FindSessionAsync(mark, httpContext.RequestAborted);

        return session is not null && string.Equals(session.ChannelType, channelType, StringComparison.Ordinal)
            ? session
            : null;
    }

    /// <summary>
    /// Tells whether a transaction can still be signed in for: known, not terminal and within its
    /// deadline.
    /// </summary>
    /// <param name="transaction">The transaction; <c>null</c> — unknown.</param>
    /// <param name="services">Request service provider.</param>
    /// <param name="live">The transaction when it is live.</param>
    /// <returns><c>true</c> — live.</returns>
    private static bool IsLive(
        Transaction? transaction,
        IServiceProvider services,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Transaction? live)
    {
        live = transaction is not null
            && !transaction.IsTerminal()
            && !transaction.HasRunOutOfTime(services.GetRequiredService<TimeProvider>().GetUtcNow())
                ? transaction
                : null;

        return live is not null;
    }

    /// <summary>
    /// Loads a transaction by its identifier as it arrived. Never throws over a page: an identifier that
    /// does not parse reads as no transaction, the rest as the load by the parsed identifier reads it.
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <param name="transactionIdValue">Identifier as it arrived.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction; <c>null</c> — none to read.</returns>
    private static Task<Transaction?> TryLoadTransactionAsync(
        IServiceProvider services,
        string? transactionIdValue,
        CancellationToken cancellationToken) =>
        TransactionId.TryParse(transactionIdValue, out var transactionId)
            ? TryLoadTransactionAsync(services, transactionId, cancellationToken)
            : Task.FromResult<Transaction?>(null);

    /// <summary>
    /// Loads a transaction as the store holds it. Never throws over a page: an unknown transaction and a
    /// store that fails both read as no transaction, the latter logged.
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction; <c>null</c> — none to read.</returns>
    private static async Task<Transaction?> TryLoadTransactionAsync(
        IServiceProvider services,
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await services.GetRequiredService<ITransactionStore>().GetByIdAsync(transactionId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LoggerOf(services).LogWarning(exception, "The transaction of a web provider page could not be read");

            return null;
        }
    }

    /// <summary>
    /// Builds what a page of the channel is rendered within: branded and worded over the ownership of
    /// its transaction, spoken in the language the sign-in page of that transaction was rendered in —
    /// else in the language of the request — and headed with the brand name its levels state, or, when none
    /// is stated, with the name of the channel, by the rule the sign-in button names it with: the declared
    /// label, translated by the locale files and overridden by the texts of the owning levels.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <param name="transaction">The transaction of the page; <c>null</c> — none is known.</param>
    /// <returns>The frame.</returns>
    private static async Task<WebProviderPageFrame> FrameAsync(
        HttpContext httpContext,
        string channelType,
        Transaction? transaction)
    {
        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;
        var localizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var ownership = TransactionResolutionContext.For(transaction);

        var branding = await services.GetRequiredService<CorePageBrandingResolver>().ResolveAsync(ownership, cancellationToken);
        var texts = await services.GetRequiredService<CorePageTextResolver>().ResolveAsync(ownership, cancellationToken);
        var requested = ChannelLocaleTag.Normalize(transaction?.GetUiLocale())
            ?? ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString());
        var language = ContentLanguage(localizer, requested);

        var adapter = AdapterOf(services, channelType);
        var displayName = adapter is null
            ? null
            : ChannelNames.ReadDeclaredMetadata(adapter, LoggerOf(services)).DisplayName;

        // The brand name is a Natural Key, as on the brand row of the sign-in window.
        return new WebProviderPageFrame(
            channelType,
            ChannelNames.LocalizeChannelName(localizer, texts, channelType, displayName, language),
            string.IsNullOrWhiteSpace(branding.BrandName) ? null : localizer.ResolveOrBaseText(branding.BrandName, language),
            httpContext.Request.PathBase.ToString(),
            language,
            localizer,
            texts,
            branding,
            httpContext.Items[HttpContextItemKeys.CspNonce] as string);
    }

    /// <summary>
    /// The language the page is rendered in: the requested one when the locale files carry it, else its
    /// primary subtag when they carry that, else the base language — so the <c>lang</c> of the page
    /// matches the language of its texts.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="requested">The requested language, normalized; <c>null</c> — none.</param>
    /// <returns>The language.</returns>
    private static string ContentLanguage(IConfirmationPromptLocalizer localizer, string? requested)
    {
        if (requested is null)
        {
            return BaseLanguage;
        }

        var available = localizer.AvailableLocales;
        if (available.Contains(requested))
        {
            return requested;
        }

        var separator = requested.IndexOf(LanguageSubtagSeparator);

        return separator > 0 && available.Contains(requested[..separator])
            ? requested[..separator]
            : BaseLanguage;
    }

    /// <summary>
    /// Answers with a page of one line.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <param name="transaction">The transaction the page is branded and worded for; <c>null</c> — none.</param>
    /// <param name="naturalKey">Natural Key of the line.</param>
    /// <param name="statusCode">Status of the answer.</param>
    /// <returns>The page.</returns>
    private static async Task<IResult> NoticeAsync(
        HttpContext httpContext,
        string channelType,
        Transaction? transaction,
        string naturalKey,
        int statusCode)
    {
        DisableCaching(httpContext);

        var frame = await FrameAsync(httpContext, channelType, transaction);

        return Html(WebProviderPageRenderer.BuildNoticePage(frame, naturalKey), statusCode);
    }

    /// <summary>
    /// Sends the browser to the page of the channel. 303: a POST is answered by a GET of the page.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The redirect.</returns>
    private static IResult RedirectToPage(HttpContext httpContext, string channelType)
    {
        httpContext.Response.Headers.Location =
            $"{httpContext.Request.PathBase}{WebProviderRoutes.RoutePath(channelType, WebProviderRoutes.PageSegment)}";

        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>
    /// Keeps the answer out of every cache: the page and its state change with every event.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    private static void DisableCaching(HttpContext httpContext) =>
        httpContext.Response.Headers.CacheControl = CacheControlHeaderValue.NoStoreString;

    /// <summary>
    /// An HTML answer.
    /// </summary>
    /// <param name="html">The page.</param>
    /// <param name="statusCode">Status of the answer.</param>
    /// <returns>The answer.</returns>
    private static IResult Html(string html, int statusCode) =>
        Results.Content(html, MediaTypeNames.Text.Html, Encoding.UTF8, statusCode);

    /// <summary>
    /// The adapter of the channel in the composition; <c>null</c> — it is not registered.
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The adapter.</returns>
    private static IChannelAdapter? AdapterOf(IServiceProvider services, string channelType) =>
        services.GetRequiredService<IEnumerable<IChannelAdapter>>()
            .FirstOrDefault(adapter => string.Equals(adapter.ChannelType, channelType, StringComparison.Ordinal));

    /// <summary>
    /// Logger of the routes.
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <returns>The logger.</returns>
    private static ILogger LoggerOf(IServiceProvider services) =>
        services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebProviderEndpoints));

    /// <summary>
    /// Binds a handler to its channel as the delegate a route maps. The delegate is typed: a lambda over
    /// <see cref="HttpContext"/> would bind as a <see cref="RequestDelegate"/>, whose result the
    /// framework does not execute.
    /// </summary>
    /// <param name="handle">The handler.</param>
    /// <param name="channelType">The channel.</param>
    /// <returns>The delegate of the route.</returns>
    private static Delegate Bound(Func<HttpContext, string, Task<IResult>> handle, string channelType)
    {
        Func<HttpContext, Task<IResult>> bound = httpContext => GuardedAsync(httpContext, channelType, handle);

        return bound;
    }

    /// <summary>
    /// Runs a handler under the guard of the routes: a failure of the state store or of the services of
    /// the page — Redis unavailable, among others — is logged and answered with <c>500</c> and the page
    /// of a failure, never an exception escaping into a pipeline that has no exception handler. Only a
    /// real cancellation of the request propagates.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="channelType">The channel.</param>
    /// <param name="handle">The handler.</param>
    /// <returns>The answer of the handler, or the page of a failure.</returns>
    private static async Task<IResult> GuardedAsync(
        HttpContext httpContext,
        string channelType,
        Func<HttpContext, string, Task<IResult>> handle)
    {
        try
        {
            return await handle(httpContext, channelType);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !httpContext.RequestAborted.IsCancellationRequested)
        {
            LoggerOf(httpContext.RequestServices).LogError(
                exception,
                "Web provider channel {ChannelType}: a request of the page failed. Path: {Path}",
                channelType,
                httpContext.Request.Path.ToString());

            return await NoticeAsync(
                httpContext, channelType, transaction: null, WebProviderPageStrings.Failed, StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Applies the rate limiting policy of the pages to a route, when the host states one.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <param name="policyName">The policy; <c>null</c> — none.</param>
    private static void Limit(RouteHandlerBuilder route, string? policyName)
    {
        if (!string.IsNullOrEmpty(policyName))
        {
            route.RequireRateLimiting(policyName);
        }
    }
}
