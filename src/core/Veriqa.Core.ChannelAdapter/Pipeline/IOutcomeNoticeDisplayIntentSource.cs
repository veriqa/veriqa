// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The ONE port every point that builds a <see cref="TransactionOutcomeNotice"/> takes the
/// desired display intent from — the live pipeline and the background expiry path alike, so both
/// paths of <c>ReportOutcomeAsync</c> state the same intent for the same ownership.
/// </summary>
internal interface IOutcomeNoticeDisplayIntentSource
{
    /// <summary>
    /// Resolves the display intent stated for this notice.
    /// </summary>
    /// <param name="context">
    /// Ownership context of the resolution — who the value is being resolved for. The port takes the
    /// context the shared resolver takes and states no narrower one of its own: the key is declared
    /// over the core, tenant and application levels, so a caller that leaves a dimension of the
    /// context unset is answered from the levels below it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The display intent to put on the notice.</returns>
    ValueTask<OutcomeNoticeDisplayIntent> ResolveAsync(
        ResolutionContext context,
        CancellationToken cancellationToken = default);
}
