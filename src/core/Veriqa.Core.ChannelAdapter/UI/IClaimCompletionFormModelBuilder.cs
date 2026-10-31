// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Builds the model of the form of the claims completion over a transaction. The port lives next to the
/// form and its implementation next to the catalog of the claims: every page that shows the form reaches
/// the catalog through it, whichever assembly the page is rendered in.
/// </summary>
internal interface IClaimCompletionFormModelBuilder
{
    /// <summary>
    /// Builds the model of the form for the web step of a transaction.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="surface">Surface the form is shown on.</param>
    /// <param name="language">Content language of the page the form is rendered into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model; its right fields are left empty for the page to fill in.</returns>
    ValueTask<ClaimCompletionFormModel> BuildAsync(
        Transaction transaction,
        WebStepSurface surface,
        string language,
        CancellationToken cancellationToken);
}
