// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// The one reading of the sign-in modes of Email: every place that decides how a sign-in by Email is
/// offered asks it in the ownership context of its transaction, so the sign-in and the confirmation read
/// the same set.
/// </summary>
internal interface IEmailSignInModes
{
    /// <summary>
    /// Resolves the effective set of the sign-in modes in an ownership context.
    /// </summary>
    /// <param name="context">Ownership context — of the transaction, or the core one where there is none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective set with its preferred mode.</returns>
    ValueTask<EmailSignInModeSet> ResolveAsync(ResolutionContext context, CancellationToken cancellationToken);
}
