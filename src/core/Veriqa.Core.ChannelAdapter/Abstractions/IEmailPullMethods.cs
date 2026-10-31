// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// What the mail of the Pull start of the Email channel carries (<see cref="EmailPullMethod"/>): the address
/// form of a page names its button by it and asks for the code once a mail carrying one is sent. The auth
/// server, which renders the form, and the Email adapter, which answers the question, do not reference each
/// other, so the question lives here, beside <see cref="IEmailPullAvailability"/>, and the Email adapter
/// answers it itself. An adapter that does not answer is taken as one whose mail carries the magic link.
/// </summary>
internal interface IEmailPullMethods
{
    /// <summary>
    /// Tells what the mail of the Pull start carries in an ownership context: the set is chosen by the
    /// tenant and the application of the transaction, as the sign-in modes are.
    /// </summary>
    /// <param name="context">Ownership context of the transaction — the core one where there is none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The methods, in the order of <see cref="EmailPullMethod"/> and without repeats; never empty. It
    /// applies only where the Pull start serves the context (<see cref="IEmailPullAvailability"/>).
    /// </returns>
    ValueTask<IReadOnlyList<EmailPullMethod>> GetPullMethodsAsync(ResolutionContext context, CancellationToken cancellationToken);
}
