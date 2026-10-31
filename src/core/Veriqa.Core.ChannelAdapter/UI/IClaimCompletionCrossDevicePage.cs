// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Http;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Opens the page of the claims completion on the device that confirmed the sign-in for a page that is not
/// the link of the channel — the confirmation page of the mail (SPEC-041 L52, N78). The port lives next to the
/// form, its implementation next to the page of the completion: the page of the mail reaches the form token
/// and the page through it without a reference to the assembly they live in.
/// </summary>
internal interface IClaimCompletionCrossDevicePage
{
    /// <summary>
    /// Issues the form token of the transaction and answers with the page of the completion carrying it: the
    /// same page the link of the channel opens. The token lives only in that page — nothing keeps it.
    /// </summary>
    /// <remarks>
    /// The caller answers for the request coming from the device that confirmed the sign-in: the port checks
    /// the state of the web step, not who is asking. The page of the mail calls it only for a sign-in
    /// confirmed by the letter the request carries.
    /// </remarks>
    /// <param name="httpContext">Current request.</param>
    /// <param name="id">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The HTML answer of the page with the issued form token; null — no token was issued: the transaction
    /// is not in the claims completion, its step is not the web step, the surface of the web step is not the
    /// device that confirmed the sign-in, or a token was issued already.
    /// </returns>
    Task<IResult?> TryOpenAsync(HttpContext httpContext, TransactionId id, CancellationToken cancellationToken);

    /// <summary>
    /// Issues the form token of the transaction as <see cref="TryOpenAsync"/> does — the same checks, the same
    /// conditional write, the same status of the sign-in page — and answers with the path of the page of the
    /// completion carrying it, for a page that goes there by itself: the page a code of a sign-in mail was
    /// entered on, which is the device that confirmed the sign-in (SPEC-041 N78).
    /// </summary>
    /// <remarks>
    /// The caller answers for the request coming from the device that confirmed the sign-in, as for
    /// <see cref="TryOpenAsync"/>. The token lives only in the path returned — nothing keeps it.
    /// </remarks>
    /// <param name="httpContext">Current request.</param>
    /// <param name="id">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path of the page of the completion with the issued form token, path base included; null —
    /// no token was issued, for the reasons <see cref="TryOpenAsync"/> names.</returns>
    Task<string?> TryIssueFormPathAsync(HttpContext httpContext, TransactionId id, CancellationToken cancellationToken);
}
