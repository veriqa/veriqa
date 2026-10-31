// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The single point at which a render point obtains the text of a receipt (SPEC-036 TPL-123,
/// SPEC-039 R51): the terminal text of a transaction, or the text of the one non-terminal state of
/// the group — a sign-in confirmed and waiting for its claims. The caller states the ADDRESS of the
/// receipt and gets the text back; which wording that address resolves to is decided by the
/// declared dimensions of the message template key, level by level, and never by the caller.
/// </summary>
internal interface IOutcomeReceiptText
{
    /// <summary>
    /// Resolves and renders the receipt of one outcome or of the claims completion state.
    /// </summary>
    /// <param name="address">What the render point knows about the receipt it is about to show.</param>
    /// <param name="recipientLocale">Recipient locale (IETF tag; null — base language).</param>
    /// <param name="recipientTimeZone">
    /// Zone the moments of the receipt are shown in (IANA identifier; null — none is known, and a
    /// moment is then shown as UTC with the marker that says so). The receipt group may carry server
    /// slots (SPEC-036 TPL-123) and a <c>datetime</c> is one of them (TPL-016), so the zone the
    /// owner's norm shows a moment in is stated here in the same completeness the owner declares it
    /// — a port does not narrow the norm it serves (TPL-057).
    /// </param>
    /// <param name="outcomeMoment">
    /// Moment the transaction ended — for the claims completion state, the moment it was entered —
    /// as the value of the <c>outcome_at</c> server slot; null when
    /// this render point has no moment to state. Whether it reaches the text at all is decided by
    /// the contract of the receipt: the shipped one declares no slots, and a deployment declaring
    /// this one gets a value instead of an empty variant (TPL-123, TPL-001).
    /// </param>
    /// <param name="transaction">
    /// What the render point knows about the transaction this receipt reports — the source of the
    /// values its slots are filled with (SPEC-036 TPL-123, TPL-124): the caller values, the attribution
    /// and the initiator context. Null when there is no transaction. A render point answering a decision
    /// from a surface the question of the transaction was never shown on states the source without its
    /// caller values (SPEC-039 E41 × SPEC-003 CA-192). The initiator context is stated as collected: the
    /// fields the display decision of the transaction hides are cleared by the port itself (SPEC-017
    /// ICC-081). Which values reach the text is decided by the contract of the receipt alone, and the
    /// shipped contract declares no slots.
    /// </param>
    /// <param name="context">
    /// Ownership context of the TRANSACTION this receipt belongs to (SPEC-036 TPL-116). Mandatory:
    /// a receipt resolved without it answers from the core level alone, so a wording an application
    /// or a <c>ui_config</c> record declares never reaches the screen. A render point with no
    /// ownership at all passes <see cref="ResolutionContext.Core"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The receipt text, already localized, as PLAIN text: escaping it for the surface it
    /// lands on is that surface's own business.</returns>
    /// <exception cref="InvalidOperationException">No level declares a receipt for this address —
    /// the step that names nothing ships with the product, so its absence is a broken build rather
    /// than a configuration of the deployment.</exception>
    ValueTask<string> RenderAsync(
        OutcomeReceiptAddress address,
        string? recipientLocale,
        string? recipientTimeZone,
        DateTimeOffset? outcomeMoment,
        TransactionSlotSource? transaction,
        ResolutionContext context,
        CancellationToken cancellationToken);
}
