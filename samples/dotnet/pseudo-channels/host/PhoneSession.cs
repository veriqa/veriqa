// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;

using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;
using Veriqa.Sample.PseudoChannels.Adapter;

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// What the short-lived provider cookie says about the browser on the phone: which transaction it
/// signed in at the provider for, and as whom. The cookie is written by the provider handler after
/// it validated the provider's answer, and it is the only source of both facts — neither the
/// transaction nor the identity is ever read from a query string or a form.
/// </summary>
/// <param name="TransactionId">Veriqa transaction the sign-in at the provider belongs to.</param>
/// <param name="ProviderUser">Principal built from the provider's ID token.</param>
/// <param name="Subject">Subject the provider issued for that principal — the user in the channel.
/// The question and the outcome of a transaction are read by it, so an account the core did not
/// address them to is shown neither.</param>
internal sealed record PhoneSession(TransactionId TransactionId, ClaimsPrincipal ProviderUser, string Subject)
{
    /// <summary>
    /// Builds the state a sign-in at the provider is started with. The transaction travels inside
    /// it: the handler protects the state on the way out and restores it on the way back.
    /// </summary>
    /// <param name="channelType">Channel type the sign-in is started for.</param>
    /// <param name="transactionId">Veriqa transaction.</param>
    /// <returns>Properties for the challenge.</returns>
    public static AuthenticationProperties CreateState(string channelType, TransactionId transactionId)
    {
        var properties = new AuthenticationProperties
        {
            RedirectUri = PseudoHostConstants.Routes.ForChannel(channelType, PseudoHostConstants.Routes.ConfirmSegment)
        };
        properties.Items[PseudoHostConstants.StateItems.Transaction] = transactionId.ToString();
        properties.Items[PseudoHostConstants.StateItems.ChannelType] = channelType;

        return properties;
    }

    /// <summary>
    /// Reads the transaction a restored state names for a channel.
    /// </summary>
    /// <param name="properties">Restored state.</param>
    /// <param name="channelType">Channel type the request is addressed to.</param>
    /// <param name="transactionId">The transaction, when the state names one for this channel.</param>
    /// <returns>Whether the state belongs to this channel and names a transaction.</returns>
    public static bool TryReadTransaction(
        AuthenticationProperties? properties,
        string channelType,
        out TransactionId transactionId)
    {
        transactionId = default;

        return properties is not null
            && properties.Items.TryGetValue(PseudoHostConstants.StateItems.ChannelType, out var stated)
            && string.Equals(stated, channelType, StringComparison.Ordinal)
            && properties.Items.TryGetValue(PseudoHostConstants.StateItems.Transaction, out var value)
            && TransactionId.TryParse(value, out transactionId);
    }

    /// <summary>
    /// Reads the provider cookie of the request.
    /// </summary>
    /// <param name="context">Current request.</param>
    /// <param name="channelType">Channel type the request is addressed to.</param>
    /// <returns>The session, or null when this browser did not sign in at the provider for this
    /// channel (or did so too long ago), or the provider named no subject.</returns>
    public static async Task<PhoneSession?> ReadAsync(HttpContext context, string channelType)
    {
        var result = await context.AuthenticateAsync(PseudoHostConstants.Schemes.ProviderCookie);

        return result is { Succeeded: true, Principal: { } providerUser }
            && TryReadTransaction(result.Properties, channelType, out var transactionId)
            && providerUser.FindFirstValue(PseudoChannelConstants.Fields.Subject) is { Length: > 0 } subject
            ? new PhoneSession(transactionId, providerUser, subject)
            : null;
    }

    /// <summary>
    /// Tells whether a transaction can still be acted upon: it exists, it has not been confirmed,
    /// completed or failed, and its time has not run out.
    /// </summary>
    /// <param name="store">Transaction store of the embedded engine.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="transactionId">Veriqa transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the transaction is open.</returns>
    public static async Task<bool> IsOpenAsync(
        ITransactionStore store,
        TimeProvider timeProvider,
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        var transaction = await store.GetByIdAsync(transactionId, cancellationToken);

        return transaction is { State: TransactionState.Created or TransactionState.Pending }
            && !transaction.IsExpired(timeProvider.GetUtcNow());
    }
}
