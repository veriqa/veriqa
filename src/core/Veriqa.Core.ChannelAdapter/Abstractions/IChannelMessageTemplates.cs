// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Gives a channel adapter the template of a message it sends into its channel — the confirmation
/// prompt or the deep-link prefill — resolved the way the core resolves it for the built-in channels.
/// The adapter states only what it is, which message it needs and for which transaction; the rest of
/// the address is the core's: the surface is the in-channel one, the channel is the
/// <see cref="IChannelAdapter.ChannelType"/> of the adapter passed in, and the type and the action type
/// of the address and the ownership levels are those the transaction states, so the prefill of a
/// confirmation is answered by the wording of a confirmation. The question of a confirmation is its
/// subject: the prompt template resolved for that transaction does not reach the text.
/// <para>
/// A message placed in a link — the deep-link prefill — may also be resolved for the
/// <see cref="DeepLinkRequest"/> the adapter received: the template is then the one of the purpose of
/// the link, and where none is stated for the purpose, the template of the channel.
/// </para>
/// <para>
/// Only the template comes from here. The adapter still builds the message itself: it renders the
/// template, adds its buttons and markup, and applies the limits of its platform.
/// </para>
/// </summary>
public interface IChannelMessageTemplates
{
    /// <summary>
    /// Resolves the template of a message the adapter sends for a transaction.
    /// </summary>
    /// <remarks>
    /// A transaction the store does not have is not a failure: the template is then resolved over the
    /// core level, and the result names no locale, time zone or application. A template that cannot be
    /// resolved — the deployment's configuration leaves the kind without one, or the store or the
    /// resolver fails — is logged at the <c>Error</c> level, counted by the template resolution failure
    /// counter of the channel meter and returned as a failure; the adapter then sends nothing.
    /// </remarks>
    /// <param name="adapter">The calling adapter — a built-in adapter passes itself. Its
    /// <see cref="IChannelAdapter.ChannelType"/> is the channel the template is resolved for.</param>
    /// <param name="kind">Message kind identifier (<c>MessageKinds</c>).</param>
    /// <param name="transactionId">Transaction the message belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved template with the transaction context it is rendered in; a failure with
    /// the <see cref="Constants.ChannelAdapterErrorCodes.MessageSendFailed"/> code when no template
    /// could be resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="adapter"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is null or empty.</exception>
    Task<Result<ResolvedChannelMessage>> ResolveAsync(
        IChannelAdapter adapter,
        string kind,
        TransactionId transactionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the template of a message the adapter places in the link it was asked for — the
    /// overload above, narrowed by the purpose of the link as well.
    /// </summary>
    /// <remarks>
    /// The template is resolved for the transaction of the request exactly as the overload above
    /// resolves it, with the same outcome for a transaction the store does not have and for a template
    /// that cannot be resolved. The one difference is the address: it names the purpose of the link
    /// too, and a purpose for which no level states a template is answered by the template of the
    /// channel.
    /// </remarks>
    /// <param name="adapter">The calling adapter — a built-in adapter passes itself. Its
    /// <see cref="IChannelAdapter.ChannelType"/> is the channel the template is resolved for.</param>
    /// <param name="kind">Message kind identifier (<c>MessageKinds</c>).</param>
    /// <param name="request">The request for the link the adapter received: the transaction the message
    /// belongs to and the purpose of the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved template with the transaction context it is rendered in; a failure with
    /// the <see cref="Constants.ChannelAdapterErrorCodes.MessageSendFailed"/> code when no template
    /// could be resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="adapter"/> or <paramref name="request"/>
    /// is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The purpose of <paramref name="request"/> is not a
    /// defined value of <see cref="DeepLinkPurpose"/>.</exception>
    Task<Result<ResolvedChannelMessage>> ResolveAsync(
        IChannelAdapter adapter,
        string kind,
        DeepLinkRequest request,
        CancellationToken cancellationToken = default);
}
