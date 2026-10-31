// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// The rules of the email block of the web step (SPEC-041 L108, C96, E61), shared by the page that shows the
/// block and the handler of its actions, so the two cannot disagree: when the block is on, which way it is
/// on, when a way can no longer obtain the email, and what the block shows. The methods
/// <see cref="EmailCompletionMethod.Code"/> and <see cref="EmailCompletionMethod.MagicLink"/> are ONE way
/// for the user — "confirm via email"; which of them the effective list names decides only what the mail
/// of that way carries (<see cref="ClaimEmailMailContents"/>).
/// </summary>
/// <remarks>
/// The available ways are asked of <see cref="EmailCompletionAvailability"/> on every show and every action —
/// the list may change while the transaction is open (the transaction fixes the levels, not the ways). The
/// mail side is the port that judgement holds. Singleton: it holds the judgement and the localizer, both
/// singletons.
/// </remarks>
internal sealed class EmailCompletionBlock
{
    /// <summary>
    /// The one judgement of the ways of obtaining the email.
    /// </summary>
    private readonly EmailCompletionAvailability _availability;

    /// <summary>
    /// Locale-file localizer of the errors the block shows.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Clock the expiry of the transaction is judged against when an address is held.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the rules.
    /// </summary>
    /// <param name="availability">The judgement of the ways of obtaining the email.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="timeProvider">Time provider.</param>
    public EmailCompletionBlock(
        EmailCompletionAvailability availability,
        IConfirmationPromptLocalizer localizer,
        TimeProvider timeProvider)
    {
        _availability = availability ?? throw new ArgumentNullException(nameof(availability));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Mail side of the email completion; null without an enabled Email adapter — then only
    /// <see cref="EmailCompletionMethod.Unverified"/> is available.
    /// </summary>
    public IClaimCompletionEmail? Email => _availability.Email;

    /// <summary>
    /// Normalizes an entered address the way the block writes it
    /// (<see cref="EmailCompletionAvailability.NormalizeAddress"/>).
    /// </summary>
    /// <param name="entered">Address as entered.</param>
    /// <returns>The normalized address; null — not an address the block admits.</returns>
    public string? NormalizeAddress(string entered) => _availability.NormalizeAddress(entered);

    /// <summary>
    /// The item the block obtains, when the block is on: the transaction is in the claims completion, its
    /// current step is the web step and the email of that step has no outcome yet. The page shows the block and
    /// the handler acts on it under this condition only — the same one the link page of the mail side offers
    /// its confirmation under (<see cref="ClaimEmailItem.Open"/>).
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>The open email item of the web step; null — the block is off.</returns>
    public static ClaimCompletionItem? OpenEmailItem(Transaction transaction) => ClaimEmailItem.Open(transaction);

    /// <summary>
    /// The methods available to the transaction on a surface, in the order of the effective list (C96).
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="surface">Surface the web step is shown on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The available methods; empty — the email cannot be obtained on this surface.</returns>
    public async ValueTask<IReadOnlyList<EmailCompletionMethod>> AvailableAsync(
        Transaction transaction,
        WebStepSurface surface,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var scope = new ConfigResolutionScope(TransactionResolutionContext.For(transaction));

        return await _availability.ResolveAsync(scope, surface, cancellationToken);
    }

    /// <summary>
    /// The way the block is on (SPEC-041 C96): the one the user chose while it is still available,
    /// otherwise the main one — the first available. The way "confirm via email" is named by the first of
    /// its two methods in the list; a choice of either of them — a transaction started before the two
    /// became one way may keep either — is a choice of that way.
    /// </summary>
    /// <param name="available">The available methods, in the order of the effective list.</param>
    /// <param name="state">State of the mails; null — not known, the main way is taken.</param>
    /// <returns>The current way; null — no way is available.</returns>
    public static EmailCompletionMethod? CurrentMethod(
        IReadOnlyList<EmailCompletionMethod> available,
        ClaimEmailState? state)
    {
        ArgumentNullException.ThrowIfNull(available);

        var ways = ClaimEmailMailContents.WaysOf(available);

        if (state?.SelectedMethod is { } selected)
        {
            foreach (var way in ways)
            {
                if (way == selected
                    || (ClaimEmailMailContents.IsMailMethod(way) && ClaimEmailMailContents.IsMailMethod(selected)))
                {
                    return way;
                }
            }
        }

        return ways.Count > 0 ? ways[0] : null;
    }

    /// <summary>
    /// Whether a way can no longer obtain the email (SPEC-041 E61): the way "confirm via email", when
    /// nothing its mail carries is live any more — neither the code nor the link, each counted only while
    /// the list names its method — and no mail may be sent; the mail the user sends and the address without
    /// verification never are.
    /// </summary>
    /// <param name="method">The way.</param>
    /// <param name="available">The available methods — they say what the mail of the way carries.</param>
    /// <param name="state">State of the mails of the transaction.</param>
    /// <returns><c>true</c> when the way is exhausted.</returns>
    public static bool IsExhausted(
        EmailCompletionMethod method,
        IReadOnlyList<EmailCompletionMethod> available,
        ClaimEmailState state)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(state);

        if (!ClaimEmailMailContents.IsMailMethod(method) || ClaimEmailMailContents.Of(available) is not { } content)
        {
            return false;
        }

        var codeLive = content.CarriesCode() && state.CodeLive;
        var linkLive = content.CarriesLink() && state.LinkLive;

        return !codeLive && !linkLive && !state.MailBudgetLeft;
    }

    /// <summary>
    /// Whether every available way is exhausted — the email can no longer be obtained on this surface.
    /// </summary>
    /// <param name="available">The available methods.</param>
    /// <param name="state">State of the mails of the transaction; null — not known.</param>
    /// <returns>
    /// <c>true</c> — exhausted (no way available counts as exhausted); <c>false</c> — some way can still
    /// obtain it; null — it cannot be told, because a way that depends on the mails is available and their
    /// state is not known.
    /// </returns>
    public static bool? AllExhausted(IReadOnlyList<EmailCompletionMethod> available, ClaimEmailState? state)
    {
        ArgumentNullException.ThrowIfNull(available);

        if (available.Count == 0)
        {
            return true;
        }

        // A way that is never exhausted settles the question without the state of the mails.
        if (available.Any(method => method is EmailCompletionMethod.Push or EmailCompletionMethod.Unverified))
        {
            return false;
        }

        return state is null ? null : available.All(method => IsExhausted(method, available, state));
    }

    /// <summary>
    /// The state of the mails of a transaction, read of the mail side.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state; null — no mail side, or its store could not be reached.</returns>
    public async Task<ClaimEmailState?> ReadStateAsync(Transaction transaction, CancellationToken cancellationToken) =>
        Email is { } email ? await email.GetStateAsync(transaction, cancellationToken) : null;

    /// <summary>
    /// Builds what the block shows for a transaction on a surface: the current way, the other ways, the state
    /// of the mails and the error — the one passed by the action that is shown again, or the one the block
    /// itself cannot help showing (no way available, every way exhausted, the state of the mails unknown, no
    /// prepared mail). An address the block holds is shown with the link that changes it while it may be
    /// changed; an address the web step has recorded is shown with nothing to act on. Reading only: the
    /// <c>mailto:</c> link of <see cref="EmailCompletionMethod.Push"/> is the single write, the correlation the
    /// mail side keeps for it (idempotent).
    /// </summary>
    /// <param name="transaction">The transaction of the page.</param>
    /// <param name="surface">Surface the page is on.</param>
    /// <param name="language">Content language of the page.</param>
    /// <param name="error">Localized error of the action shown again; null — none.</param>
    /// <param name="addressStep">Whether the answer brings the address step of the way back over a live
    /// mail (<see cref="ClaimCompletionEmailBlockView.AddressStep"/>).</param>
    /// <param name="enteredAddress">The address the action typed into the block, shown again in the input of
    /// the address step (<see cref="ClaimCompletionEmailBlockView.EnteredAddress"/>); null — none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the block shows; null — the web step neither awaits the email
    /// (<see cref="OpenEmailItem"/>) nor has recorded it.</returns>
    public async Task<ClaimCompletionEmailBlockView?> BuildViewAsync(
        Transaction transaction,
        WebStepSurface surface,
        string language,
        string? error,
        bool addressStep,
        string? enteredAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        // The method reads the address the block holds, the available ways and the state of the mails, which
        // also carries the way the user chose. A block no way can obtain the email in offers only "Continue";
        // otherwise it shows its current way with the others as alternatives. The error of the action comes
        // first; without one the block states why its way cannot go on, when it cannot. An email the web step
        // has already recorded is shown as recorded.
        if (OpenEmailItem(transaction) is not { } item)
        {
            return RecordedView(transaction, language);
        }

        var available = await AvailableAsync(transaction, surface, cancellationToken);

        // The address the block holds was obtained while the other fields were open; the form records it when
        // it is finished. It may be changed while some way can still obtain the email, so letting it go
        // cannot leave only an email that can no longer be obtained.
        if ((await HeldAddressAsync(transaction, cancellationToken)).Held is { } held)
        {
            return AddressView(item, held.Address, held.Verified, language) with
            {
                RecordedChangeable = AllExhausted(
                    available, available.Count > 0 ? await ReadStateAsync(transaction, cancellationToken) : null) is false
            };
        }

        var state = available.Count > 0 ? await ReadStateAsync(transaction, cancellationToken) : null;

        var current = CurrentMethod(available, state);
        var exhausted = AllExhausted(available, state) is true;

        var pushMailto = current is EmailCompletionMethod.Push && Email is { } email
            ? await email.GetPushMailtoAsync(transaction, language, cancellationToken)
            : null;

        error ??= ShownError(current, available, state, exhausted, pushMailto, language);

        return new ClaimCompletionEmailBlockView
        {
            Language = language,
            ActionPath = OidcEndpoints.AuthorizeCallbackClaimsEmail,
            Required = item.Level is ClaimRequirementLevel.Required,
            Current = current,
            Alternatives = [.. ClaimEmailMailContents.WaysOf(available).Where(way => way != current)],
            MailContent = ClaimEmailMailContents.Of(available),
            State = state,
            PushMailto = pushMailto,
            Exhausted = exhausted,
            AddressStep = addressStep,
            EnteredAddress = enteredAddress,
            Error = error
        };
    }

    /// <summary>
    /// The address the block holds for the transaction until its form is finished.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The held address; none — the email is not awaited, there is no mail side or none is held;
    /// <see cref="ClaimEmailHeldRead.Unknown"/> — the store of the mail side could not be reached.</returns>
    public async Task<ClaimEmailHeldRead> HeldAddressAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return OpenEmailItem(transaction) is not null && Email is { } email
            ? await email.GetHeldAddressAsync(transaction, cancellationToken)
            : ClaimEmailHeldRead.None;
    }

    /// <summary>
    /// Records an address the block obtained, or holds it while other fields of the web step are open
    /// (<see cref="ClaimEmailWrites.RecordOrHoldAsync"/>).
    /// </summary>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="transaction">The transaction as read by the action.</param>
    /// <param name="obtained">The address and how it was obtained.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The write of the address, or the transaction with the address held.</returns>
    public Task<ClaimEmailWrite> RecordOrHoldAsync(
        ITransactionService transactionService,
        Transaction transaction,
        ClaimEmailHeldAddress obtained,
        CancellationToken cancellationToken) =>
        ClaimEmailWrites.RecordOrHoldAsync(
            transactionService, Email, transaction, obtained, _timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>
    /// Whether the address the block holds may be let go — the user changes it: some available way can still
    /// obtain the email. A state of the mails that cannot be read counts as no.
    /// </summary>
    /// <param name="transaction">The transaction, its email held.</param>
    /// <param name="surface">Surface the page is on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the held address may be changed.</returns>
    public async Task<bool> CanChangeHeldAsync(
        Transaction transaction,
        WebStepSurface surface,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var available = await AvailableAsync(transaction, surface, cancellationToken);
        var state = available.Count > 0 ? await ReadStateAsync(transaction, cancellationToken) : null;

        return AllExhausted(available, state) is false;
    }

    /// <summary>
    /// Lets the address the block holds go, so the email is asked for again.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — let go; <c>false</c> — no mail side, or its store could not be reached.</returns>
    public async Task<bool> ReleaseHeldAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return Email is { } email && await email.HoldAddressAsync(transaction, held: null, cancellationToken);
    }

    /// <summary>
    /// What the block shows once the web step has recorded the email: the address, confirmed or saved
    /// without verification, and nothing to act on.
    /// </summary>
    /// <param name="transaction">The transaction of the page.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>The view of the recorded email; null — the web step asks for no email, or it was skipped.</returns>
    private static ClaimCompletionEmailBlockView? RecordedView(Transaction transaction, string language)
    {
        if (transaction.GetCurrentClaimCompletionStep() is not ClaimCompletionStep.WebStep
            || transaction.ClaimCompletion is not { } completion)
        {
            return null;
        }

        var item = completion.Items.FirstOrDefault(asked =>
            asked.Step is ClaimCompletionStep.WebStep
            && VeriqaClaimTypes.NameComparer.Equals(asked.Claim, VeriqaClaimTypes.Email));
        var recorded = completion.Results.FirstOrDefault(result =>
            VeriqaClaimTypes.NameComparer.Equals(result.Claim, VeriqaClaimTypes.Email));

        // A skipped email carries no value: the block shows nothing for it.
        if (item is null || recorded is not { Value: { } address })
        {
            return null;
        }

        return AddressView(item, address, recorded.Verified, language);
    }

    /// <summary>
    /// What the block shows for an address it has obtained — held or recorded: the address in a read-only
    /// input, marked as confirmed or not.
    /// </summary>
    /// <param name="item">The email item of the web step.</param>
    /// <param name="address">The address.</param>
    /// <param name="verified">Whether the address was confirmed.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>The view; the link that changes the address is the caller's to add.</returns>
    private static ClaimCompletionEmailBlockView AddressView(
        ClaimCompletionItem item,
        string address,
        bool verified,
        string language) =>
        new()
        {
            Language = language,
            ActionPath = OidcEndpoints.AuthorizeCallbackClaimsEmail,
            Required = item.Level is ClaimRequirementLevel.Required,
            Alternatives = [],
            Exhausted = false,
            RecordedAddress = address,
            RecordedVerified = verified
        };

    /// <summary>
    /// The error the block shows without an action: why its way cannot go on, when it cannot.
    /// </summary>
    /// <param name="current">The current way; null — none is available.</param>
    /// <param name="available">The available methods.</param>
    /// <param name="state">State of the mails; null — not known or not read.</param>
    /// <param name="exhausted">Whether every available way is exhausted.</param>
    /// <param name="pushMailto">The prepared mail of the way where the user sends it; null — none.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>The localized error; null — the way can go on.</returns>
    private string? ShownError(
        EmailCompletionMethod? current,
        IReadOnlyList<EmailCompletionMethod> available,
        ClaimEmailState? state,
        bool exhausted,
        string? pushMailto,
        string language)
    {
        if (current is null)
        {
            return _localizer.ResolveOrBaseText(EmailCompletionBlockTexts.NoMethods, language);
        }

        if (exhausted || (state is not null && IsExhausted(current.Value, available, state)))
        {
            return _localizer.ResolveOrBaseText(EmailCompletionBlockTexts.NoMoreMails, language);
        }

        // A way of mails with no state read: the store of the mail side could not be reached, so the block
        // shows the first step of its way and says that it may not go through.
        if (state is null && current is EmailCompletionMethod.Code or EmailCompletionMethod.MagicLink)
        {
            return _localizer.ResolveOrBaseText(EmailCompletionBlockTexts.StateUnknown, language);
        }

        if (current is EmailCompletionMethod.Push && pushMailto is null)
        {
            return _localizer.ResolveOrBaseText(EmailCompletionBlockTexts.PushUnavailable, language);
        }

        return null;
    }
}
