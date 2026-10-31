// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Whether the Email channel starts by its Push mail — the only start of the channel whose target is an
/// absolute address, and therefore the only one a hop URL of the channel is issued for. The Pull start is
/// a relative path of the sign-in window, and no hop URL carries it. The auth server and the Email adapter
/// do not reference each other, so the question lives here, beside <see cref="IEmailPullAvailability"/>,
/// and the Email adapter answers it itself. An adapter that does not answer is asked nothing.
/// </summary>
internal interface IEmailPushAvailability
{
    /// <summary>
    /// Tells whether Push is the effective sign-in mode of the channel in an ownership context: the
    /// sign-in modes are those of the context, the tenant credentials those of the tenant scope of the
    /// call.
    /// </summary>
    /// <param name="context">Ownership context of the transaction — the core one where there is none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// true — the target of the channel is its Push mail; false — the channel starts by Pull or not at all,
    /// and no hop URL is issued for it.
    /// </returns>
    ValueTask<bool> IsPushEffectiveAsync(ResolutionContext context, CancellationToken cancellationToken);
}
