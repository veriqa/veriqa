// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.ClaimCompletion;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The actions of the page of the claims completion (SPEC-041 C39, L108): submit the simple fields
/// (<see cref="OidcEndpoints.AuthorizeCallbackClaims"/>) — "Continue", which also skips an optional email
/// nothing was typed for — and cancel the sign-in (<see cref="OidcEndpoints.AuthorizeCallbackClaimsCancel"/>)
/// — from the page of the device the sign-in was
/// started on and from the page of the device that confirmed it alike. The routes lie under the callback
/// path and are mapped with it (<see cref="AuthorizeCallbackEndpoint.MapAuthorizeCallbackEndpoint"/>). The
/// actions of the email block share the reading of the form, the right and the answers kept here
/// (<see cref="ClaimCompletionEmailActionEndpoint"/>).
/// </summary>
/// <remarks>
/// Every handler takes its transaction from <see cref="ClaimCompletionActionRight"/> only, and the answer
/// belongs to the page whose right the request carried (<see cref="ClaimCompletionRightCarrier"/>). The
/// writes go through the transaction service, which checks after each of them whether the completion is
/// over and, on the last one, finalizes the sign-in; the answer is then chosen by the transaction the last
/// write returned. The sign-in itself is finished only on the device it was started on: the page of the
/// other device reports the outcome. No claim value reaches the log — the names of the claims do
/// (SPEC-041 N68).
/// </remarks>
internal static class ClaimCompletionActionEndpoints
{
    /// <summary>
    /// Handles "Continue": checks every simple field by the rules of the form and writes them all when all
    /// are accepted — a value as collected by the form, an empty optional field as skipped. A refused field
    /// writes nothing: the page is shown again with the values and the errors. An address the email block
    /// holds is recorded after the fields — the form is finished. With the email block on and
    /// nothing typed into it the email is handled as an empty field is: an optional one is skipped with the
    /// fields; a required one is not withheld from "Continue" — the fields are saved, and the page comes
    /// back saying, in the email block, that the address is to be confirmed. A code typed into the block,
    /// or an address on the way without verification, is the action of the block instead: the inputs of
    /// the block belong to this form, and Enter in one of them submits it as "Continue". An address typed
    /// on any other way stops "Continue": nothing is sent or written, and the block asks to confirm the
    /// address or to clear the field.
    /// </summary>
    internal static async Task<IResult> HandleSubmitAsync(
        HttpContext httpContext,
        ClaimCompletionActionRight right,
        ClaimCompletionPageResponder pages,
        EmailCompletionBlock emailBlock,
        ITransactionService transactionService,
        IConfirmationPromptLocalizer localizer,
        ILoggerFactory loggerFactory)
    {
        var form = await ReadFormAsync(httpContext);
        var (grantedTransaction, carrier, requestLanguage, refusal) = await right.CheckAsync(httpContext, form, ClaimCompletionAction.Submit);
        if (refusal is not null)
        {
            return refusal;
        }

        var transaction = grantedTransaction!;
        var formToken = FormTokenOf(form, carrier);

        // 0. A code or an address of the email block came with the form. A code, and an address on the way
        //    without verification, are the action of the block: the user was acting in it, and the browser
        //    sent the form by itself. An address in the address step of the way "confirm via email" is not
        //    yet confirmed, and "Continue" stops on it. In both cases the simple fields are neither checked
        //    nor written and come back as typed, like after any action of the block. An address the way can no
        //    longer take (the page is out of date) counts for nothing: "Continue" goes on as usual.
        if (EmailCompletionBlock.OpenEmailItem(transaction) is not null)
        {
            var (entered, addressToConfirm) = await ClaimCompletionEmailActionEndpoint.EnteredOperationAsync(form, transaction, formToken, emailBlock);

            if (entered is not null)
            {
                return await ClaimCompletionEmailActionEndpoint.RunAsync(
                    httpContext,
                    new ClaimCompletionEmailActionEndpoint.ActionRequest(transaction, formToken, requestLanguage, form, entered),
                    pages,
                    emailBlock,
                    transactionService,
                    localizer,
                    loggerFactory);
            }

            if (addressToConfirm && ClaimCompletionEmailActionEndpoint.EnteredAddress(form) is { } address)
            {
                return ClaimCompletionEmailActionEndpoint.AskToConfirmAddress(
                    httpContext,
                    new ClaimCompletionEmailActionEndpoint.ActionRequest(transaction, formToken, requestLanguage, form, Operation: null),
                    address,
                    pages,
                    localizer);
            }
        }

        // 1. The form of the transaction as it stands: its fields are the open simple claims of the web
        //    step, so a posted value of any other claim is ignored and a missing one counts as empty.
        var language = pages.ResolveLanguage(transaction, requestLanguage);
        var model = formToken is null
            ? await pages.BuildFormAsync(httpContext, transaction, language)
            : await pages.BuildCrossDeviceFormAsync(httpContext, transaction, formToken, language);

        var checkedFields = new List<ClaimCompletionFieldView>(model.Fields.Count);
        var refused = false;

        foreach (var field in model.Fields)
        {
            var value = ClaimCompletionFieldRules.Normalize(
                form[ClaimCompletionFormFields.ClaimValueName(field.Claim)].FirstOrDefault());
            var error = ClaimCompletionFieldRules.Check(field, value, localizer, language);

            refused |= error is not null;
            checkedFields.Add(field with { Value = value, Error = error });
        }

        if (refused)
        {
            var shownAgain = model with { Fields = checkedFields };

            return formToken is null
                ? await pages.RespondAsync(httpContext, transaction, requestLanguage, shownAgain)
                : await pages.RespondCrossDeviceAsync(httpContext, transaction, formToken, requestLanguage, shownAgain);
        }

        // 2. All accepted: one write per field, stopping at the first refusal. CancellationToken.None — a
        //    submission accepted as a whole is written as a whole even if the client goes away.
        Result<Transaction>? last = null;

        foreach (var field in checkedFields)
        {
            last = string.IsNullOrEmpty(field.Value)
                ? await transactionService.SkipClaimAsync(transaction.Id, field.Claim, CancellationToken.None)
                : await transactionService.RecordClaimValueAsync(
                    transaction.Id, field.Claim, field.Value, verified: false, ClaimCollectionMethod.Form, CancellationToken.None);

            if (last.IsFailure)
            {
                LogRefusedWrite(loggerFactory, transaction.Id, field.Claim, last.Error.Code);
                break;
            }
        }

        // 3. The email of the same page is still open. An address the block holds is recorded now — the form
        //    is finished, and the address goes to the transaction with the fields. Otherwise nothing was typed
        //    into the block — on any step of it. An optional email is skipped, as an empty optional field is.
        //    A required one stops the page: it is shown again with the reason in its email block, in text — a
        //    redirect would carry no message. The sign-in has not ended then, so there is nothing to report to
        //    the channel and nothing to complete. A page without simple fields has written nothing yet, and
        //    the transaction it holds is the one to read.
        var saved = last is null ? transaction : last.IsSuccess ? last.Value : null;

        if (saved is not null
            && saved.IsCompletingClaims()
            && EmailCompletionBlock.OpenEmailItem(saved) is { } emailItem)
        {
            var heldRead = await emailBlock.HeldAddressAsync(saved, CancellationToken.None);

            if (heldRead.Held is { } held)
            {
                last = await ClaimEmailWrites.RecordAsync(transactionService, saved, held, CancellationToken.None);

                if (last.IsFailure)
                {
                    LogRefusedWrite(loggerFactory, transaction.Id, emailItem.Claim, last.Error.Code);
                }

                return await AnswerAfterWritesAsync(httpContext, transaction, last, pages, requestLanguage, formToken);
            }

            // An address may be held that cannot be read: the email is neither skipped nor asked for again,
            // and the page says the block cannot be read now.
            if (!heldRead.Known)
            {
                var unknownNotice = localizer.ResolveOrBaseText(EmailCompletionBlockTexts.StateUnknown, language);

                return formToken is null
                    ? await pages.RespondAsync(httpContext, saved, requestLanguage, emailError: unknownNotice)
                    : await pages.RespondCrossDeviceAsync(httpContext, saved, formToken, requestLanguage, emailError: unknownNotice);
            }

            if (emailItem.Level is not ClaimRequirementLevel.Optional)
            {
                var emailNotice = localizer.ResolveOrBaseText(EmailCompletionBlockTexts.ConfirmAddressToContinue, language);

                return formToken is null
                    ? await pages.RespondAsync(httpContext, saved, requestLanguage, emailError: emailNotice)
                    : await pages.RespondCrossDeviceAsync(httpContext, saved, formToken, requestLanguage, emailError: emailNotice);
            }

            last = await transactionService.SkipClaimAsync(transaction.Id, emailItem.Claim, CancellationToken.None);

            if (last.IsFailure)
            {
                LogRefusedWrite(loggerFactory, transaction.Id, emailItem.Claim, last.Error.Code);
            }
        }

        return await AnswerAfterWritesAsync(httpContext, transaction, last, pages, requestLanguage, formToken);
    }

    /// <summary>
    /// Handles "cancel sign-in": the transaction fails as declined by the user (SPEC-041 L109). On the page of
    /// the device the sign-in was started on the client receives the same <c>access_denied</c> answer the "No"
    /// of the web confirmation page gives; the page of the device that confirmed it shows the receipt of the
    /// refusal, and the client learns it through the device the sign-in was started on.
    /// </summary>
    internal static async Task<IResult> HandleCancelAsync(
        HttpContext httpContext,
        ClaimCompletionActionRight right,
        ClaimCompletionPageResponder pages,
        ITransactionService transactionService,
        ILoggerFactory loggerFactory)
    {
        var form = await ReadFormAsync(httpContext);
        var (grantedTransaction, carrier, requestLanguage, refusal) = await right.CheckAsync(httpContext, form, ClaimCompletionAction.Cancel);
        if (refusal is not null)
        {
            return refusal;
        }

        var transaction = grantedTransaction!;
        var formToken = FormTokenOf(form, carrier);

        // CancellationToken.None — the refusal must be recorded even if the client goes away. On the page of
        // the initiating device a failed write does not change the answer: the user is not signed in there
        // either way.
        var failResult = await transactionService.FailTransactionAsync(
            transaction.Id,
            TransactionErrorCodes.DeclinedByUser,
            transaction.ConcurrencyToken,
            CancellationToken.None);

        if (failResult.IsFailure)
        {
            // transactionId.ToString() — a Base62 string, log injection is excluded
            loggerFactory.CreateLogger(typeof(ClaimCompletionActionEndpoints)).LogWarning(
                "Failed to move the transaction cancelled during the claims completion to Failed. " +
                "TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transaction.Id.ToString(),
                failResult.Error.Code);
        }
        else
        {
            await ReportOutcomeToChannelAsync(httpContext, failResult.Value);
        }

        if (formToken is null)
        {
            return await pages.AccessDeniedAsync(httpContext, transaction, requestLanguage);
        }

        // The other device reports the refusal only when it was written; a refused write sends the page back
        // to the transaction as it stands — a sign-in that went through in between is reported as such.
        return failResult.IsSuccess
            ? await pages.CrossDeviceReceiptAsync(
                httpContext, TransactionOutcome.Declined, failResult.Value, failResult.Value.UpdatedAt, requestLanguage)
            : ClaimCompletionPageResponder.SeeOtherToCrossDevicePage(httpContext, transaction.Id, formToken);
    }

    /// <summary>
    /// Reads the form of an action request. A request without a form body gets an empty form, which the
    /// AntiForgery check then refuses like any request without its token.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <returns>The form.</returns>
    internal static async Task<IFormCollection> ReadFormAsync(HttpContext httpContext) =>
        httpContext.Request.HasFormContentType
            ? await httpContext.Request.ReadFormAsync(httpContext.RequestAborted)
            : FormCollection.Empty;

    /// <summary>
    /// The form token of a request whose right the token carried; null for the page of the device the
    /// sign-in was started on.
    /// </summary>
    /// <param name="form">The form of the request.</param>
    /// <param name="carrier">What carried the right.</param>
    /// <returns>The form token, or null.</returns>
    internal static string? FormTokenOf(IFormCollection form, ClaimCompletionRightCarrier carrier) =>
        carrier is ClaimCompletionRightCarrier.FormToken
            ? form[ClaimCompletionFormFields.FormToken].FirstOrDefault()
            : null;

    /// <summary>
    /// Sends the browser back to the page it acted from, which shows the transaction as it stands: the
    /// callback on the device the sign-in was started on, the page of the form token on the other device.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="formToken">The form token of the request; null — the page of the initiating device.</param>
    /// <returns>An empty 303 result.</returns>
    internal static IResult SeeOtherToPage(HttpContext httpContext, TransactionId transactionId, string? formToken) =>
        formToken is null
            ? AuthorizeCallbackEndpoint.SeeOtherToCallback(httpContext, transactionId)
            : ClaimCompletionPageResponder.SeeOtherToCrossDevicePage(httpContext, transactionId, formToken);

    /// <summary>
    /// Answers after the writes of an action, by the transaction the last write returned. On the page of the
    /// device the sign-in was started on a completed sign-in is completed here; a completion still in
    /// progress, and a refused write, send the browser to the callback, which shows the state the transaction
    /// is in; a confirmed transaction, finalized by another request, waits on the page; a failed one is sent
    /// to the callback as well, which returns that ending to the client. On the page of the device that confirmed the sign-in a completion still in
    /// progress, and a refused write, send the browser back to that page; a completed or confirmed sign-in
    /// gets the receipt of its confirmation; a failed one gets the "link no longer available" page.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction before the writes.</param>
    /// <param name="last">Result of the last write; null — nothing was written.</param>
    /// <param name="pages">The page of the claims completion.</param>
    /// <param name="requestLanguage">Language detected for the request.</param>
    /// <param name="formToken">The form token of the request; null — the page of the initiating device.</param>
    /// <returns>The answer.</returns>
    internal static async Task<IResult> AnswerAfterWritesAsync(
        HttpContext httpContext,
        Transaction transaction,
        Result<Transaction>? last,
        ClaimCompletionPageResponder pages,
        string requestLanguage,
        string? formToken)
    {
        if (last is null || last.IsFailure)
        {
            return SeeOtherToPage(httpContext, transaction.Id, formToken);
        }

        var after = last.Value;

        // A write that ended the sign-in — completed or failed, on either surface — is the one place
        // the channel learns the outcome from; it stands before every post-completion action below.
        await ReportOutcomeToChannelAsync(httpContext, after);

        if (formToken is not null)
        {
            return await AnswerCrossDeviceAfterWritesAsync(httpContext, after, pages, requestLanguage, formToken);
        }

        if (after.State is TransactionState.Completed)
        {
            await DispatchCompletionBehaviorAsync(httpContext, after);

            return await AuthorizeCallbackEndpoint.CompleteSignInAsync(httpContext, after, after.Id);
        }

        // A completion still in progress is shown by the callback; a sign-in the write failed — a
        // required claim that cannot be obtained — is returned to the client by it (SPEC-002 §4.3).
        if (after.IsCompletingClaims() || AuthorizeCallbackEndpoint.IsFailedSignIn(after))
        {
            return AuthorizeCallbackEndpoint.SeeOtherToCallback(httpContext, after.Id);
        }

        if (after.State is TransactionState.Confirmed)
        {
            return await pages.RespondAsync(httpContext, after, requestLanguage);
        }

        return await pages.ExpiredSessionAsync(httpContext, after, requestLanguage);
    }

    /// <summary>
    /// Answers after the writes of an action on the page of the device that confirmed the sign-in (see
    /// <see cref="AnswerAfterWritesAsync"/>). The sign-in is not finished here: the browser of the device it
    /// was started on does that when it sees the transaction completed.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="after">The transaction the last write returned.</param>
    /// <param name="pages">The page of the claims completion.</param>
    /// <param name="requestLanguage">Language detected for the request.</param>
    /// <param name="formToken">The form token of the request.</param>
    /// <returns>The answer.</returns>
    private static async Task<IResult> AnswerCrossDeviceAfterWritesAsync(
        HttpContext httpContext,
        Transaction after,
        ClaimCompletionPageResponder pages,
        string requestLanguage,
        string formToken)
    {
        if (after.IsCompletingClaims())
        {
            return ClaimCompletionPageResponder.SeeOtherToCrossDevicePage(httpContext, after.Id, formToken);
        }

        if (after.State is TransactionState.Completed)
        {
            await DispatchCompletionBehaviorAsync(httpContext, after);
        }

        if (after.State is TransactionState.Completed or TransactionState.Confirmed)
        {
            return await pages.CrossDeviceReceiptAsync(
                httpContext, TransactionOutcome.Confirmed, after, after.UpdatedAt, requestLanguage);
        }

        return await pages.CrossDeviceLinkUnavailableAsync(httpContext, requestLanguage);
    }

    /// <summary>
    /// Shows the channel the user came through the outcome of a sign-in a request of the browser ended
    /// in the claims completion (SPEC-003 CA-142) — on this page, or by "Cancel sign-in" of the sign-in
    /// window. A transaction that has not ended, or never entered the completion, is not reported, and
    /// a host that composes no channel contour has no reporter — there is nothing to show.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="written">The transaction the write returned.</param>
    /// <returns>The completed operation.</returns>
    internal static async Task ReportOutcomeToChannelAsync(HttpContext httpContext, Transaction written)
    {
        // CancellationToken.None — a browser that goes away must not cancel the receipt of a sign-in
        // that has already ended.
        if (httpContext.RequestServices.GetService<IClaimCompletionOutcomeReporter>() is { } outcomeReporter)
        {
            await outcomeReporter.ReportAsync(written, CancellationToken.None);
        }
    }

    /// <summary>
    /// Performs the post-completion action of a sign-in the last write of an action completed, in the
    /// channel the user came through (SPEC-012 §4.9). A host that composes no channel contour has no
    /// performer, and there is nothing to perform.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="completed">The completed transaction the write returned.</param>
    /// <returns>The completed operation.</returns>
    private static async Task DispatchCompletionBehaviorAsync(HttpContext httpContext, Transaction completed)
    {
        // CancellationToken.None — a browser that goes away must not cancel a message of a sign-in that
        // is already completed.
        if (httpContext.RequestServices.GetService<ICompletionBehaviorDispatcher>() is { } completionBehavior)
        {
            await completionBehavior.DispatchAsync(completed, CancellationToken.None);
        }
    }

    /// <summary>
    /// Logs a write of the claims completion the engine refused. The claim is named; its value is not.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="claim">Name of the claim.</param>
    /// <param name="errorCode">Code of the refusal.</param>
    internal static void LogRefusedWrite(ILoggerFactory loggerFactory, TransactionId transactionId, string claim, string errorCode)
    {
        // transactionId.ToString() — a Base62 string, log injection is excluded; the claim name is a name
        // from the catalog of the deployment, not user input
        loggerFactory.CreateLogger(typeof(ClaimCompletionActionEndpoints)).LogWarning(
            "A write of the claims completion was refused. TransactionId: {TransactionId}, Claim: {Claim}, ErrorCode: {ErrorCode}",
            transactionId.ToString(),
            claim,
            errorCode);
    }
}
