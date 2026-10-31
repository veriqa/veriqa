// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Renderer of an ALREADY RESOLVED outcome receipt (SPEC-036 TPL-035). Pure and free of I/O: the
/// message is brought by the caller, exactly as
/// <see cref="ConfirmationPromptRenderer.Render"/> is handed the prompt it renders.
/// <para>
/// It holds no rule for picking a wording, because there is none to hold: the address is stated by
/// the render point, the wording of that address is chosen by the ladder of the template key, and
/// the variant within the ladder is chosen by the one substitution engine of the contour
/// (<see cref="MessageTextRenderer"/>). The values a receipt renders with are assembled by the one
/// assembly of the slot values of a transaction: the caller values of the transaction for the slots the
/// contract of the receipt declares as caller slots, then the server values — the application, the
/// initiator details, the masked identity of the channel account and the moment of the outcome
/// (SPEC-036 TPL-123, TPL-124). The shipped contract of
/// the receipt kinds declares no slots, so on today's shipment none of those values enters a
/// receipt; a deployment declaring such slots on its own level gets them filled.
/// </para>
/// </summary>
public static class OutcomeReceiptText
{
    /// <summary>
    /// Renders the receipt.
    /// </summary>
    /// <param name="template">The resolved message: the receipt's contract and its template ladder.</param>
    /// <param name="recipientLocale">Recipient locale (IETF tag; null — base language).</param>
    /// <param name="recipientTimeZone">Zone the moments of the receipt are shown in (IANA identifier;
    /// null — none is known, and a moment is shown as UTC with the marker that says so).</param>
    /// <param name="outcomeMoment">Moment the transaction ended, or null when the render point states
    /// none.</param>
    /// <param name="transaction">What the render point knows about the transaction the receipt reports;
    /// null when there is none. Its initiator context is taken as already filtered by the display
    /// decision of the transaction (SPEC-017 ICC-081).</param>
    /// <param name="surface">Surface of the receipt address (<see cref="OutcomeReceiptSurfaces"/>); it
    /// decides whether the identity of the channel account may be stated in the receipt.</param>
    /// <param name="localizer">Natural Key localizer (null — base language).</param>
    /// <param name="logger">Logger of the render warnings (null — none).</param>
    /// <returns>The receipt text, plain and localized.</returns>
    /// <exception cref="InvalidOperationException">No step of the resolved ladder states a plain
    /// text — a receipt is delivered in that one edition (SPEC-036 TPL-123), so a ladder overridden
    /// with an HTML-only one is a broken installation and there is nothing to show.</exception>
    public static string Render(
        ResolvedMessage template,
        string? recipientLocale,
        string? recipientTimeZone,
        DateTimeOffset? outcomeMoment,
        TransactionSlotSource? transaction,
        string surface,
        IConfirmationPromptLocalizer? localizer,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(template);

        var values = TransactionSlotValues.Of(transaction, template.Contract, outcomeMoment, surface);

        var text = MessageTextRenderer.Render(
            template,
            values,
            recipientLocale,
            recipientTimeZone,
            localizer,
            MessageRenderMode.PlainText,
            logger);

        if (text is not null)
        {
            return text;
        }

        logger?.LogError(
            "No step of the resolved ladder of message kind {MessageKind} states a plain text; the "
            + "outcome receipt cannot be rendered.",
            template.Kind);

        throw new InvalidOperationException(
            $"The resolved ladder of message kind '{template.Kind}' states no plain text. An outcome "
            + "receipt is delivered as plain text and has no other edition to fall back on.");
    }
}
