// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;
using Veriqa.Sample.PseudoChannels.Adapter;

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// The addresses a phone visits for one pseudo-channel: the channel link that sends it to the
/// provider, the page that asks the question and the address the answer is posted to. The fourth
/// one — the provider's return address — is served by the provider handler itself.
/// </summary>
internal static class PhoneEndpoints
{
    /// <summary>
    /// Maps the phone addresses of one enabled pseudo-channel. A provider that is not enabled gets
    /// no routes, so its addresses answer 404.
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <param name="channelType">Channel type; also the scheme name of its provider handler.</param>
    public static void MapPseudoChannel(this IEndpointRouteBuilder endpoints, string channelType)
    {
        // The channel link: what the hop page of the node sends the phone to. The transaction is
        // checked before the phone leaves for the provider, and then travels there and back inside
        // the state the handler protects — it is never trusted from a query string again.
        endpoints.MapGet(
            PseudoHostConstants.Routes.ForChannel(channelType, PseudoChannelConstants.StartSegment),
            async (HttpContext context, ITransactionStore store, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            {
                var value = context.Request.Query[PseudoChannelConstants.TransactionQueryParameter].ToString();

                return TransactionId.TryParse(value, out var transactionId)
                    && await PhoneSession.IsOpenAsync(store, timeProvider, transactionId, cancellationToken)
                    ? Results.Challenge(PhoneSession.CreateState(channelType, transactionId), [channelType])
                    : SamplePages.Notice(SampleTexts.StatusTitle, SampleTexts.LinkNotValid, StatusCodes.Status404NotFound);
            });

        // The question. Only the browser that signed in at the provider for this transaction gets
        // it: the page is read from the provider cookie, not from the address.
        endpoints.MapGet(
            PseudoHostConstants.Routes.ForChannel(channelType, PseudoHostConstants.Routes.ConfirmSegment),
            async (
                HttpContext context,
                IPseudoChannelExchange exchange,
                IAntiforgery antiforgery,
                ITransactionStore store,
                TimeProvider timeProvider,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var session = await PhoneSession.ReadAsync(context, channelType);
                if (session is null)
                {
                    return SamplePages.Notice(SampleTexts.StatusTitle, SampleTexts.SessionMissing);
                }

                if (TryDescribeOutcome(exchange, session) is { } outcome)
                {
                    return SamplePages.Notice(SampleTexts.StatusTitle, outcome);
                }

                // A sign-in that was cancelled or ran out of time is not asked about again: the
                // answer would be refused anyway. This comes before the question is looked up, so
                // a closed sign-in reads the same to every account that holds a cookie for it.
                if (!await PhoneSession.IsOpenAsync(store, timeProvider, session.TransactionId, cancellationToken))
                {
                    return SamplePages.Notice(SampleTexts.StatusTitle, SampleTexts.LinkNotValid);
                }

                // The exchange hands the question only to the account the core asked. An account
                // whose start the core did not accept — the transaction already belongs to another
                // one — holds a cookie as well, and gets neither the details nor the buttons. The
                // same page answers when the core asks on another surface.
                if (!exchange.TryGetPrompt(session.TransactionId, session.Subject, out var prompt))
                {
                    return SamplePages.Notice(SampleTexts.StatusTitle, SampleTexts.ContinueOnOtherScreen);
                }

                // The context is a closed hierarchy that may still gain a variant: one this page
                // does not know is asked about without details, and said so in the log.
                if (prompt is not (DetailedConfirmationPromptContext
                    or SubjectConfirmationPromptContext
                    or SuppressedConfirmationPromptContext))
                {
                    loggerFactory.CreateLogger(typeof(PhoneEndpoints)).LogWarning(
                        "Unknown confirmation context {ContextType} of the {ChannelType} pseudo-channel — asked without details",
                        prompt.GetType().Name,
                        channelType);
                }

                return SamplePages.Question(channelType, prompt, session.ProviderUser, antiforgery.GetAndStoreTokens(context));
            });

        // The answer. Accepted from the browser that holds the provider cookie, with the request
        // token of the form; who answers and about which transaction comes from the cookie.
        endpoints.MapPost(
            PseudoHostConstants.Routes.ForChannel(channelType, PseudoHostConstants.Routes.AnswerSegment),
            async (
                HttpContext context,
                IPseudoChannelExchange exchange,
                IAntiforgery antiforgery,
                ITransactionStore store,
                TimeProvider timeProvider,
                PseudoChannelRelay relay,
                CancellationToken cancellationToken) =>
            {
                if (!context.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(context))
                {
                    return SamplePages.Notice(
                        SampleTexts.StatusTitle, SampleTexts.AnswerRejected, StatusCodes.Status400BadRequest);
                }

                var session = await PhoneSession.ReadAsync(context, channelType);
                if (session is null)
                {
                    return SamplePages.Notice(SampleTexts.StatusTitle, SampleTexts.SessionMissing);
                }

                var form = await context.Request.ReadFormAsync(cancellationToken);
                var intent = form[PseudoHostConstants.AnswerForm.Field].ToString() switch
                {
                    PseudoHostConstants.AnswerForm.Yes => PseudoChannelConstants.Intents.Confirm,
                    PseudoHostConstants.AnswerForm.No => PseudoChannelConstants.Intents.Decline,
                    _ => null
                };

                // An answer is relayed only to a question that was asked of this account and is
                // still open.
                if (intent is null
                    || !exchange.TryGetPrompt(session.TransactionId, session.Subject, out _)
                    || !await PhoneSession.IsOpenAsync(store, timeProvider, session.TransactionId, cancellationToken))
                {
                    return SamplePages.Notice(
                        SampleTexts.StatusTitle, SampleTexts.AnswerRejected, StatusCodes.Status400BadRequest);
                }

                var sent = await relay.SendAsync(
                    channelType, intent, session.TransactionId, session.ProviderUser, cancellationToken);

                // One answer per sign-in at the provider: the cookie is gone whatever came of it.
                await context.SignOutAsync(PseudoHostConstants.Schemes.ProviderCookie);

                if (!sent)
                {
                    return SamplePages.Notice(SampleTexts.StatusTitle, SampleTexts.AnswerRejected);
                }

                // The webhook answers 200 to an event it accepted and could not act upon as well, so
                // the page claims an outcome only when the core reported one to the adapter.
                return SamplePages.Notice(
                    SampleTexts.StatusTitle,
                    TryDescribeOutcome(exchange, session) ?? SampleTexts.AnswerSent);
            });
    }

    /// <summary>
    /// Returns the text of the outcome the core reported to the account of the session, when it
    /// reported one. The wording is the core's own receipt — the page does not compose one.
    /// </summary>
    /// <param name="exchange">Exchange the adapter leaves the outcome in.</param>
    /// <param name="session">Who asks, and about which transaction.</param>
    /// <returns>The text, or null when no outcome is known.</returns>
    private static string? TryDescribeOutcome(IPseudoChannelExchange exchange, PhoneSession session)
        => exchange.TryGetOutcome(session.TransactionId, session.Subject, out var outcome)
            && !string.IsNullOrWhiteSpace(outcome.StatusText)
            ? outcome.StatusText
            : null;
}
