// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using Veriqa.Core.AuthServer.ClaimCompletion;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The actions of the email block of the web step (<see cref="OidcEndpoints.AuthorizeCallbackClaimsEmail"/>,
/// SPEC-041 L108, C96, E61): send the mail that confirms the address, bring its address step back ("Send
/// again"), check the code, save the address without verification, switch the way, show the block again and
/// change a held address — one route, told apart by <see cref="ClaimCompletionFormFields.EmailOperation"/>.
/// An optional email is skipped by "Continue" with nothing typed into the block
/// (<see cref="ClaimCompletionActionEndpoints.HandleSubmitAsync"/>).
/// </summary>
/// <remarks>
/// The right is the one of the other actions of the page (<see cref="ClaimCompletionActionRight"/>, both
/// carriers), and the action acts only while the block is on — the email is awaited
/// (<see cref="EmailCompletionBlock.OpenEmailItem"/>) and no address is held — and "Change" only while an
/// address is held; otherwise the browser is sent back to its page and nothing is asked of the mail side or
/// written. A confirmed or saved address is held while other fields of the web step are open, and recorded
/// when it is the last thing the step waits for (<see cref="ClaimEmailWrites"/>). A write of
/// the email is answered like the writes of the fields; an error of the block shows the page again with the
/// error in the block, and so does the address step brought back, a mark of that answer only; a refused send
/// or save also brings back the address typed into the block, and a refused send its address step, so the
/// address can be put right beside the error. Every answer is post/redirect/get — what the page shows
/// besides the transaction is carried over the redirect — so reloading the page never repeats the action.
/// After sending, checking, switching and showing again, the outcome of an email that can no longer be
/// obtained is settled (E61). No address, code or token reaches the log (SPEC-041 N68).
/// <para>
/// On a page with simple fields an action of the block arrives with the values typed into those fields. The
/// handler neither checks nor writes them — that is "Continue" — and keeps them nowhere on the server: the
/// script of the page keeps them in the tab and puts them back after the redirect.
/// </para>
/// <para>
/// The inputs of the block belong to the form of the page, so an action of the block may also arrive on
/// the route of "Continue", with no action named: the browser submits the shared form by itself when the
/// user presses Enter in an input of the block. The handler of "Continue" asks
/// <see cref="EnteredOperationAsync"/> which action that is and hands it over to <see cref="RunAsync"/>; an
/// address typed into the address step of a way that confirms it is no action, and "Continue" is answered by
/// <see cref="AskToConfirmAddress"/>.
/// </para>
/// </remarks>
internal static class ClaimCompletionEmailActionEndpoint
{
    /// <summary>
    /// Handles an action of the email block.
    /// </summary>
    internal static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        ClaimCompletionActionRight right,
        ClaimCompletionPageResponder pages,
        EmailCompletionBlock emailBlock,
        ITransactionService transactionService,
        IConfirmationPromptLocalizer localizer,
        ILoggerFactory loggerFactory)
    {
        var form = await ClaimCompletionActionEndpoints.ReadFormAsync(httpContext);
        var (grantedTransaction, carrier, requestLanguage, refusal) = await right.CheckAsync(httpContext, form, ClaimCompletionAction.Email);
        if (refusal is not null)
        {
            return refusal;
        }

        // A button of a way on a page with simple fields posts the name of its way and nothing else of the
        // block.
        var operation = form[ClaimCompletionFormFields.EmailOperation].FirstOrDefault()
            ?? (form.ContainsKey(ClaimCompletionFormFields.EmailMethod) ? ClaimCompletionFormFields.EmailOperations.Method : null);

        return await RunAsync(
            httpContext,
            new ActionRequest(
                grantedTransaction!,
                ClaimCompletionActionEndpoints.FormTokenOf(form, carrier),
                requestLanguage,
                form,
                operation),
            pages,
            emailBlock,
            transactionService,
            localizer,
            loggerFactory);
    }

    /// <summary>
    /// The action of the block a submission of the form of the page stands for. The inputs of the block
    /// belong to that form, and a submission the browser makes by itself — Enter in an input, "Go" of an
    /// on-screen keyboard — names no action: it arrives as "Continue". A code that came with it is what the
    /// user was entering, so the submission checks it; an address is saved on the way without verification.
    /// With both, the code goes first: it is the later step of the way. An address on the way "confirm via
    /// email" is no action — it is to be confirmed first (<see cref="EnteredAddress"/>), while the way still
    /// has an address step, that is while a mail may be sent. An address the way can no longer take — the
    /// page is out of date: the way is now the mail from the user's mailbox, or no mail is left — counts for
    /// nothing, and "Continue" goes on as with the block untouched.
    /// </summary>
    /// <param name="form">The posted form.</param>
    /// <param name="transaction">The transaction, with the block on.</param>
    /// <param name="formToken">The form token of the request; null — the page of the initiating device.</param>
    /// <param name="emailBlock">The rules of the email block.</param>
    /// <returns>The action (<see cref="ClaimCompletionFormFields.EmailOperations"/>), null — none; and whether
    /// the form posts an address the block is to ask to confirm.</returns>
    internal static async Task<(string? Operation, bool AddressToConfirm)> EnteredOperationAsync(
        IFormCollection form,
        Transaction transaction,
        string? formToken,
        EmailCompletionBlock emailBlock)
    {
        if (!string.IsNullOrWhiteSpace(form[ClaimCompletionFormFields.EmailCode].FirstOrDefault()))
        {
            return (ClaimCompletionFormFields.EmailOperations.Verify, false);
        }

        if (EnteredAddress(form) is null)
        {
            return (null, false);
        }

        // The way is the one the block is on now, as the actions themselves read it.
        var available = await emailBlock.AvailableAsync(transaction, SurfaceOf(formToken), CancellationToken.None);
        var state = await emailBlock.ReadStateAsync(transaction, CancellationToken.None);

        return EmailCompletionBlock.CurrentMethod(available, state) switch
        {
            EmailCompletionMethod.Unverified => (ClaimCompletionFormFields.EmailOperations.Save, false),
            EmailCompletionMethod.Code or EmailCompletionMethod.MagicLink => (null, state?.MailBudgetLeft ?? true),
            _ => (null, false)
        };
    }

    /// <summary>
    /// The address typed into the block that came with a submission of the form of the page.
    /// </summary>
    /// <param name="form">The posted form.</param>
    /// <returns>The address as typed; null — none, or only spaces.</returns>
    internal static string? EnteredAddress(IFormCollection form) =>
        form[ClaimCompletionFormFields.EmailAddress].FirstOrDefault() is { } address
        && !string.IsNullOrWhiteSpace(address)
            ? address
            : null;

    /// <summary>
    /// Answers "Continue" that came with an address typed into the address step of a way that confirms it.
    /// Nothing is sent or written: the page comes back, over a redirect, with the request to confirm the
    /// address or to clear the field in the block; the address step shows — over a live mail as well — with
    /// the typed address in its input.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="request">The submission, its right checked.</param>
    /// <param name="address">The address as typed (<see cref="EnteredAddress"/>).</param>
    /// <param name="pages">The page of the claims completion.</param>
    /// <param name="localizer">Locale-file localizer of the errors of the block.</param>
    /// <returns>An empty 303 result.</returns>
    internal static IResult AskToConfirmAddress(
        HttpContext httpContext,
        ActionRequest request,
        string address,
        ClaimCompletionPageResponder pages,
        IConfirmationPromptLocalizer localizer)
    {
        var (transaction, formToken, requestLanguage, _, _) = request;
        var error = localizer.ResolveOrBaseText(
            EmailCompletionBlockTexts.ConfirmOrClearAddress, pages.ResolveLanguage(transaction, requestLanguage));

        return pages.ShowAgain(
            httpContext, transaction, formToken, new ClaimCompletionShownAgain(error, EmailAddressStep: true, address));
    }

    /// <summary>
    /// Performs an action of the email block for a request whose right has been checked, and answers it.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="request">The action asked for.</param>
    /// <param name="pages">The page of the claims completion.</param>
    /// <param name="emailBlock">The rules of the email block.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="localizer">Locale-file localizer of the errors of the block.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <returns>The answer.</returns>
    internal static async Task<IResult> RunAsync(
        HttpContext httpContext,
        ActionRequest request,
        ClaimCompletionPageResponder pages,
        EmailCompletionBlock emailBlock,
        ITransactionService transactionService,
        IConfirmationPromptLocalizer localizer,
        ILoggerFactory loggerFactory)
    {
        var (transaction, formToken, requestLanguage, form, operation) = request;

        // "Change" acts on a block that is off — an address is held — and turns it on again.
        if (operation is ClaimCompletionFormFields.EmailOperations.Change)
        {
            return await ChangeHeldAsync(httpContext, request, pages, emailBlock);
        }

        // 1. The block must be on: the email of the web step still has no outcome and no address is held. A
        //    block that went off meanwhile — the address was confirmed by the link of the mail — leaves the
        //    simple fields on the page, and what the user typed into them stays there.
        if (EmailCompletionBlock.OpenEmailItem(transaction) is not { } item)
        {
            return BackToPage(httpContext, pages, transaction, formToken, error: null, addressStep: false, enteredAddress: null);
        }

        if (await emailBlock.HeldAddressAsync(transaction, CancellationToken.None) is { Held: { } held })
        {
            return await AnswerHeldAsync(httpContext, request, pages, transactionService, loggerFactory, item, held);
        }

        var context = new ActionContext(
            transaction,
            item,
            SurfaceOf(formToken),
            pages.ResolveLanguage(transaction, requestLanguage),
            form,
            emailBlock,
            transactionService);

        // 2. The action itself: a write of the email, an error of the block, or neither.
        var step = operation switch
        {
            ClaimCompletionFormFields.EmailOperations.Send => await SendAsync(context, localizer),
            ClaimCompletionFormFields.EmailOperations.Address => ActionStep.AddressAgain(),
            ClaimCompletionFormFields.EmailOperations.Verify => await VerifyAsync(context, localizer),
            ClaimCompletionFormFields.EmailOperations.Save => await SaveAsync(context, localizer),
            ClaimCompletionFormFields.EmailOperations.Method => await SelectMethodAsync(context, localizer),
            ClaimCompletionFormFields.EmailOperations.Refresh => ActionStep.Settle(),
            _ => ActionStep.Refused(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.UnknownAction, context.Language))
        };

        if (step.Write is { } write)
        {
            if (write.IsFailure)
            {
                ClaimCompletionActionEndpoints.LogRefusedWrite(loggerFactory, transaction.Id, item.Claim, write.Error.Code);
            }

            return await ClaimCompletionActionEndpoints.AnswerAfterWritesAsync(
                httpContext, transaction, write, pages, requestLanguage, formToken);
        }

        // 3. After sending, checking, switching and showing again: the outcome of an email no way can obtain
        //    any more, on the transaction as it stands after the action.
        var shown = transaction;
        var error = step.Error;

        if (step.SettlesOutcome)
        {
            var found = await transactionService.GetTransactionAsync(transaction.Id, CancellationToken.None);
            if (found.IsFailure)
            {
                return ClaimCompletionActionEndpoints.SeeOtherToPage(httpContext, transaction.Id, formToken);
            }

            // An address held meanwhile is obtained, and one that cannot be read may be: there is no outcome of
            // an exhausted email to settle.
            if (EmailCompletionBlock.OpenEmailItem(found.Value) is not { } openItem
                || await emailBlock.HeldAddressAsync(found.Value, CancellationToken.None) is not { Known: true, Held: null })
            {
                return BackToPage(httpContext, pages, found.Value, formToken, error: null, addressStep: false, enteredAddress: null);
            }

            shown = found.Value;

            var settled = await SettleExhaustionAsync(context with { Transaction = shown, Item = openItem }, localizer);
            if (settled.Write is { } outcome)
            {
                if (outcome.IsFailure)
                {
                    ClaimCompletionActionEndpoints.LogRefusedWrite(loggerFactory, shown.Id, openItem.Claim, outcome.Error.Code);
                }

                return await ClaimCompletionActionEndpoints.AnswerAfterWritesAsync(
                    httpContext, shown, outcome, pages, requestLanguage, formToken);
            }

            error ??= settled.Error;
        }

        // 4. The answer: the page again — with the error in the block, with its address step brought back and
        //    the address typed into it, with the values of the simple fields, or several of them — or
        //    post/redirect/get.
        return BackToPage(httpContext, pages, shown, formToken, error, step.AddressStep, step.EnteredAddress);
    }

    /// <summary>
    /// "Change" of a held address: the address is let go, and the page comes back, over a redirect, with the
    /// address step of its way and that address in the input. A confirmed address is confirmed again; the
    /// mails already sent stay counted. The address is let go only while some way can still obtain the email;
    /// otherwise, and with no address held, the page is shown as it stands.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="request">The action, its right checked.</param>
    /// <param name="pages">The page of the claims completion.</param>
    /// <param name="emailBlock">The rules of the email block.</param>
    /// <returns>An empty 303 result.</returns>
    private static async Task<IResult> ChangeHeldAsync(
        HttpContext httpContext,
        ActionRequest request,
        ClaimCompletionPageResponder pages,
        EmailCompletionBlock emailBlock)
    {
        var (transaction, formToken, _, _, _) = request;

        // CancellationToken.None — the address is let go as a whole even if the client goes away.
        if (await emailBlock.HeldAddressAsync(transaction, CancellationToken.None) is not { Held: { } held }
            || !await emailBlock.CanChangeHeldAsync(transaction, SurfaceOf(formToken), CancellationToken.None)
            || !await emailBlock.ReleaseHeldAsync(transaction, CancellationToken.None))
        {
            return ClaimCompletionActionEndpoints.SeeOtherToPage(httpContext, transaction.Id, formToken);
        }

        return pages.ShowAgain(
            httpContext, transaction, formToken, new ClaimCompletionShownAgain(EmailError: null, EmailAddressStep: true, held.Address));
    }

    /// <summary>
    /// Answers an action of the block that arrives while an address is held: the email is the last thing the
    /// web step waits for — the other fields were written meanwhile — and the held address is recorded, which
    /// ends the step; otherwise the page is shown as it stands, with the held address.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="request">The action, its right checked.</param>
    /// <param name="pages">The page of the claims completion.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="item">The open email item of the web step.</param>
    /// <param name="held">The held address.</param>
    /// <returns>The answer.</returns>
    private static async Task<IResult> AnswerHeldAsync(
        HttpContext httpContext,
        ActionRequest request,
        ClaimCompletionPageResponder pages,
        ITransactionService transactionService,
        ILoggerFactory loggerFactory,
        ClaimCompletionItem item,
        ClaimEmailHeldAddress held)
    {
        var (transaction, formToken, requestLanguage, _, _) = request;

        if (!ClaimEmailWrites.IsLastOpen(transaction))
        {
            return ClaimCompletionActionEndpoints.SeeOtherToPage(httpContext, transaction.Id, formToken);
        }

        // CancellationToken.None — the address is written as a whole even if the client goes away.
        var write = await ClaimEmailWrites.RecordAsync(transactionService, transaction, held, CancellationToken.None);

        if (write.IsFailure)
        {
            ClaimCompletionActionEndpoints.LogRefusedWrite(loggerFactory, transaction.Id, item.Claim, write.Error.Code);
        }

        return await ClaimCompletionActionEndpoints.AnswerAfterWritesAsync(
            httpContext, transaction, write, pages, requestLanguage, formToken);
    }

    /// <summary>
    /// Surface of the page a request comes from, told by what carried its right.
    /// </summary>
    /// <param name="formToken">The form token of the request; null — the page of the initiating device.</param>
    /// <returns>The surface.</returns>
    private static WebStepSurface SurfaceOf(string? formToken) =>
        formToken is null ? WebStepSurface.InitiatingPage : WebStepSurface.CrossDevicePage;

    /// <summary>
    /// Answers an action that wrote nothing: post/redirect/get either way — carrying to the page the error of
    /// the block, its address step brought back or the address typed into it when it has any of them to show
    /// (<see cref="ClaimCompletionPageResponder.ShowAgain"/>), with a bare redirect otherwise.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="pages">The page of the claims completion.</param>
    /// <param name="shown">The transaction as it stands after the action.</param>
    /// <param name="formToken">The form token of the request; null — the page of the initiating device.</param>
    /// <param name="error">Localized error of the block; null — none.</param>
    /// <param name="addressStep">Whether the block shows the address step of its way over a live mail.</param>
    /// <param name="enteredAddress">The address typed into the block, shown again in the input of its address
    /// step; null — none.</param>
    /// <returns>An empty 303 result.</returns>
    private static IResult BackToPage(
        HttpContext httpContext,
        ClaimCompletionPageResponder pages,
        Transaction shown,
        string? formToken,
        string? error,
        bool addressStep,
        string? enteredAddress) =>
        error is null && !addressStep && enteredAddress is null
            ? ClaimCompletionActionEndpoints.SeeOtherToPage(httpContext, shown.Id, formToken)
            : pages.ShowAgain(httpContext, shown, formToken, new ClaimCompletionShownAgain(error, addressStep, enteredAddress));

    /// <summary>
    /// Sends the mail of the way "confirm via email" — with a code, a link or both, as the available methods
    /// say — to the entered address; a request that posts no address field at all sends it again to the
    /// address of the last mail. A refused send answers with the address step of the way — over a live mail
    /// too, while a mail is left — and the address as typed in its input, so the address the error is about
    /// can be put right beside it rather than typed anew.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <param name="localizer">Locale-file localizer of the errors of the block.</param>
    /// <returns>The error of the block, if any; the outcome is settled after it.</returns>
    private static async Task<ActionStep> SendAsync(ActionContext context, IConfirmationPromptLocalizer localizer)
    {
        // The field is read once, as posted: the input shows it again on a refusal — an empty field stays
        // empty — and the send normalizes the same value.
        var typed = context.Form[ClaimCompletionFormFields.EmailAddress].FirstOrDefault();
        ActionStep Refuse(string error) => ActionStep.Settle(error, addressStep: true, enteredAddress: typed);

        // The way is the one the block is on now, read of the transaction rather than of the page the user
        // saw: a page shown before the list changed sends nothing the list no longer allows.
        var available = await context.EmailBlock.AvailableAsync(context.Transaction, context.Surface, CancellationToken.None);
        var state = await context.EmailBlock.ReadStateAsync(context.Transaction, CancellationToken.None);

        if (context.EmailBlock.Email is not { } email)
        {
            return Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.MethodUnavailable, context.Language));
        }

        if (state is null)
        {
            return Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.StateUnknown, context.Language));
        }

        // What the mail carries is decided by the methods available now — a code, a link or both in one
        // mail; the user is not asked which.
        if (EmailCompletionBlock.CurrentMethod(available, state) is not { } method
            || !ClaimEmailMailContents.IsMailMethod(method)
            || ClaimEmailMailContents.Of(available) is not { } content)
        {
            return Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.MethodUnavailable, context.Language));
        }

        string? address = null;

        if (context.Form.ContainsKey(ClaimCompletionFormFields.EmailAddress))
        {
            address = email.NormalizeAddress(typed ?? string.Empty);

            if (address is null)
            {
                return Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.InvalidAddress, context.Language));
            }
        }

        // CancellationToken.None — a mail that goes out is counted and stored as a whole even if the client
        // goes away.
        var sent = await email.SendAsync(context.Transaction, content, address, context.Language, CancellationToken.None);

        return sent switch
        {
            ClaimEmailSendOutcome.Sent => ActionStep.Settle(),
            ClaimEmailSendOutcome.TransactionLimit or ClaimEmailSendOutcome.SubjectLimit =>
                Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.NoMoreMails, context.Language)),
            ClaimEmailSendOutcome.AddressLimit => Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.AddressLimit, context.Language)),
            ClaimEmailSendOutcome.DomainLimit => Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.DomainLimit, context.Language)),
            ClaimEmailSendOutcome.TooSoon => Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.SendTooSoon, context.Language)),
            ClaimEmailSendOutcome.NoAddress => Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.NoAddress, context.Language)),
            _ => Refuse(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.DeliveryFailed, context.Language))
        };
    }

    /// <summary>
    /// Checks the entered code; a matched code records or holds the address it was sent to (SPEC-041 L50) —
    /// never the address of the request. A code out of attempts is answered with the address step of the way
    /// while a mail may still be sent, and without a call to request one when none may.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <param name="localizer">Locale-file localizer of the errors of the block.</param>
    /// <returns>The write of the email, or the error of the block with the outcome settled after it.</returns>
    private static async Task<ActionStep> VerifyAsync(ActionContext context, IConfirmationPromptLocalizer localizer)
    {
        // The shape of the code is checked before the mail side is asked, so a mistyped code spends no attempt.
        if (!ClaimEmailCode.TryClean(context.Form[ClaimCompletionFormFields.EmailCode].FirstOrDefault(), out var code))
        {
            return ActionStep.Settle(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.CodeShape, context.Language));
        }

        var available = await context.EmailBlock.AvailableAsync(context.Transaction, context.Surface, CancellationToken.None);

        if (context.EmailBlock.Email is not { } email || !available.Contains(EmailCompletionMethod.Code))
        {
            return ActionStep.Settle(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.MethodUnavailable, context.Language));
        }

        var check = await email.CheckCodeAsync(context.Transaction, code, CancellationToken.None);

        return check switch
        {
            { Status: ClaimEmailCodeStatus.Matched, Address: { } address } => ActionStep.Obtained(
                await context.EmailBlock.RecordOrHoldAsync(
                    context.TransactionService,
                    context.Transaction,
                    new ClaimEmailHeldAddress(address, Verified: true, EmailCompletionMethod.Code),
                    CancellationToken.None)),
            { Status: ClaimEmailCodeStatus.Mismatch } => ActionStep.Settle(
                localizer.ResolveOrBaseText(EmailCompletionBlockTexts.WrongCode, context.Language)
                    .Replace(EmailCompletionBlockTexts.AttemptsSlot, check.AttemptsLeft.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)),
            { Status: ClaimEmailCodeStatus.Exhausted } => await ExhaustedCodeAsync(context, localizer),
            { Status: ClaimEmailCodeStatus.Expired } => ActionStep.Settle(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.CodeExpired, context.Language)),
            { Status: ClaimEmailCodeStatus.NoCode } => ActionStep.Settle(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.NoCode, context.Language)),
            _ => ActionStep.Settle(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.StateUnknown, context.Language))
        };
    }

    /// <summary>
    /// The answer to a code out of attempts (SPEC-041 E61): while the limits allow another mail, the error
    /// asks for one and the address step of the way, filled with the address of the last mail, comes in the
    /// same answer; when they do not, the error says only that the code is spent — the block has no new mail
    /// to offer. A state that cannot be read is taken as a mail left: the send itself then says what holds.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <param name="localizer">Locale-file localizer of the errors of the block.</param>
    /// <returns>The error of the block; the outcome is settled after it.</returns>
    private static async Task<ActionStep> ExhaustedCodeAsync(ActionContext context, IConfirmationPromptLocalizer localizer)
    {
        var state = await context.EmailBlock.ReadStateAsync(context.Transaction, CancellationToken.None);

        return state is { MailBudgetLeft: false }
            ? ActionStep.Settle(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.CodeNoAttemptsLeft, context.Language))
            : ActionStep.Settle(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.CodeExhausted, context.Language), addressStep: true);
    }

    /// <summary>
    /// Saves — records or holds — the entered address without verification (SPEC-041 L50:
    /// <c>email_verified</c> false). A refused save answers with the address as typed in the input of the way.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <param name="localizer">Locale-file localizer of the errors of the block.</param>
    /// <returns>The write of the email, or the error of the block.</returns>
    private static async Task<ActionStep> SaveAsync(ActionContext context, IConfirmationPromptLocalizer localizer)
    {
        var available = await context.EmailBlock.AvailableAsync(context.Transaction, context.Surface, CancellationToken.None);
        // The field is read once, as posted: the input shows it again on a refusal and the save normalizes it.
        var typed = context.Form[ClaimCompletionFormFields.EmailAddress].FirstOrDefault();

        if (!available.Contains(EmailCompletionMethod.Unverified))
        {
            return ActionStep.Refused(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.MethodUnavailable, context.Language), typed);
        }

        var address = context.EmailBlock.NormalizeAddress(typed ?? string.Empty);

        if (address is null)
        {
            return ActionStep.Refused(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.InvalidAddress, context.Language), typed);
        }

        return ActionStep.Obtained(await context.EmailBlock.RecordOrHoldAsync(
            context.TransactionService,
            context.Transaction,
            new ClaimEmailHeldAddress(address, Verified: false, EmailCompletionMethod.Unverified),
            CancellationToken.None));
    }

    /// <summary>
    /// Switches the block to another available way.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <param name="localizer">Locale-file localizer of the errors of the block.</param>
    /// <returns>The error of the block, if any; the outcome is settled after it.</returns>
    private static async Task<ActionStep> SelectMethodAsync(ActionContext context, IConfirmationPromptLocalizer localizer)
    {
        // Only the name of a way is accepted: a number would parse as a way too. The way "confirm via
        // email" is available under the name of either of its two methods, whichever of them the list names.
        var posted = context.Form[ClaimCompletionFormFields.EmailMethod].FirstOrDefault();
        var available = await context.EmailBlock.AvailableAsync(context.Transaction, context.Surface, CancellationToken.None);

        if (!Enum.TryParse<EmailCompletionMethod>(posted, ignoreCase: false, out var method)
            || !string.Equals(method.ToString(), posted, StringComparison.Ordinal)
            || !(available.Contains(method)
                || (ClaimEmailMailContents.IsMailMethod(method) && ClaimEmailMailContents.Of(available) is not null)))
        {
            return ActionStep.Settle(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.MethodUnavailable, context.Language));
        }

        // Without the mail side only the way without verification is available, and there is nothing to
        // switch between; the choice is kept by the mail side.
        if (context.EmailBlock.Email is { } email)
        {
            await email.SelectMethodAsync(context.Transaction, method, CancellationToken.None);
        }

        // The address typed so far goes over to the chosen way.
        return ActionStep.Settle(enteredAddress: EnteredAddress(context.Form));
    }

    /// <summary>
    /// The outcome of an email no available way can obtain any more (SPEC-041 E61): a required email fails the
    /// transaction as unobtainable, an optional one is skipped. The one place the outcome is decided; the page
    /// itself never decides it — showing the block only offers "Continue", which asks this.
    /// </summary>
    /// <param name="context">The action, on the transaction as it stands after it, with the block on.</param>
    /// <param name="localizer">Locale-file localizer of the errors of the block.</param>
    /// <returns>The write of the outcome; the error of the block when the state of the mails is unknown; or
    /// neither — the email can still be obtained.</returns>
    private static async Task<ActionStep> SettleExhaustionAsync(ActionContext context, IConfirmationPromptLocalizer localizer)
    {
        var available = await context.EmailBlock.AvailableAsync(context.Transaction, context.Surface, CancellationToken.None);
        var state = available.Count > 0
            ? await context.EmailBlock.ReadStateAsync(context.Transaction, CancellationToken.None)
            : null;

        switch (EmailCompletionBlock.AllExhausted(available, state))
        {
            case null:
                return ActionStep.Refused(localizer.ResolveOrBaseText(EmailCompletionBlockTexts.StateUnknown, context.Language));
            case false:
                return ActionStep.Settle();
        }

        // CancellationToken.None — the outcome is written as a whole even if the client goes away.
        return ActionStep.Written(context.Item.Level is ClaimRequirementLevel.Required
            ? await context.TransactionService.FailTransactionAsync(
                context.Transaction.Id,
                TransactionErrorCodes.RequiredClaimUnobtainable,
                context.Transaction.ConcurrencyToken,
                CancellationToken.None)
            : await context.TransactionService.SkipClaimAsync(context.Transaction.Id, context.Item.Claim, CancellationToken.None));
    }

    /// <summary>
    /// An action of the block asked for by a request whose right has been checked.
    /// </summary>
    /// <param name="Transaction">The transaction the right was granted on.</param>
    /// <param name="FormToken">The form token of the request; null — the page of the initiating device.</param>
    /// <param name="RequestLanguage">Language detected for the request.</param>
    /// <param name="Form">The posted form.</param>
    /// <param name="Operation">The action (<see cref="ClaimCompletionFormFields.EmailOperations"/>); null —
    /// the request names none.</param>
    internal sealed record ActionRequest(
        Transaction Transaction,
        string? FormToken,
        string RequestLanguage,
        IFormCollection Form,
        string? Operation);

    /// <summary>
    /// What an action of the block works on.
    /// </summary>
    /// <param name="Transaction">The transaction.</param>
    /// <param name="Item">The open email item of the web step.</param>
    /// <param name="Surface">Surface of the page the request comes from.</param>
    /// <param name="Language">Content language of the page.</param>
    /// <param name="Form">The posted form.</param>
    /// <param name="EmailBlock">The rules of the email block.</param>
    /// <param name="TransactionService">Transaction service.</param>
    private sealed record ActionContext(
        Transaction Transaction,
        ClaimCompletionItem Item,
        WebStepSurface Surface,
        string Language,
        IFormCollection Form,
        EmailCompletionBlock EmailBlock,
        ITransactionService TransactionService);

    /// <summary>
    /// What an action of the block came to.
    /// </summary>
    /// <param name="Write">The write it made; null — none.</param>
    /// <param name="Error">Localized error of the block; null — none.</param>
    /// <param name="SettlesOutcome">Whether the outcome of an exhausted email is settled after it.</param>
    /// <param name="AddressStep">Whether the answer shows the address step of the way over a live mail.</param>
    /// <param name="EnteredAddress">The address the refused action typed into the block, shown again in the
    /// input of the way; null — none.</param>
    private sealed record ActionStep(Result<Transaction>? Write, string? Error, bool SettlesOutcome, bool AddressStep, string? EnteredAddress)
    {
        /// <summary>
        /// The action wrote the email.
        /// </summary>
        public static ActionStep Written(Result<Transaction> write) =>
            new(write, Error: null, SettlesOutcome: false, AddressStep: false, EnteredAddress: null);

        /// <summary>
        /// The action is followed by the outcome of an exhausted email; it may carry an error of its own,
        /// bring the address step of the way back and show the address it typed again.
        /// </summary>
        public static ActionStep Settle(string? error = null, bool addressStep = false, string? enteredAddress = null) =>
            new(Write: null, error, SettlesOutcome: true, addressStep, enteredAddress);

        /// <summary>
        /// The action was refused with an error and is not followed by the outcome; it may show the address it
        /// typed again.
        /// </summary>
        public static ActionStep Refused(string error, string? enteredAddress = null) =>
            new(Write: null, error, SettlesOutcome: false, AddressStep: false, enteredAddress);

        /// <summary>
        /// "Send again": the address step of the way comes back; nothing is sent, written or settled.
        /// </summary>
        public static ActionStep AddressAgain() =>
            new(Write: null, Error: null, SettlesOutcome: false, AddressStep: true, EnteredAddress: null);

        /// <summary>
        /// The address was obtained: written, or held — then nothing more is to be done, and the page shows
        /// the held address.
        /// </summary>
        /// <param name="write">The write or the hold of the address.</param>
        /// <returns>The step.</returns>
        public static ActionStep Obtained(ClaimEmailWrite write) =>
            write.Held
                ? new(Write: null, Error: null, SettlesOutcome: false, AddressStep: false, EnteredAddress: null)
                : Written(write.Outcome);
    }
}
