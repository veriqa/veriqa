// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Whether the Email channel accepts an address typed into the sign-in window — its Pull mode. The window
/// offers the address form beside the Push controls only where the form can lead somewhere; the auth server
/// and the Email adapter do not reference each other, so the question lives here, where both see it, and
/// the Email adapter answers it itself. An adapter that does not answer is asked nothing.
/// </summary>
internal interface IEmailPullAvailability
{
    /// <summary>
    /// Tells whether the Pull start of the channel serves a request in an ownership context: the sign-in
    /// modes are chosen by the tenant and the application within what the core level serves, so the
    /// answer is that of the transaction the caller asks for.
    /// </summary>
    /// <param name="context">Ownership context of the transaction — the core one where there is none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// true — the Pull start serves a request of the context; false — it refuses every one, and a form
    /// leading to it is a way to sign in that cannot work.
    /// </returns>
    ValueTask<bool> IsPullAvailableAsync(ResolutionContext context, CancellationToken cancellationToken);
}
