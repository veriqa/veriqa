// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.WebUtilities;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Constants;

/// <summary>
/// Routes of a web provider channel — a public contract: the address of return is the one the
/// integrator registers with the provider. Every route of a channel lives under
/// <c>auth/web-provider/{channel}/</c>, relative to the public base URL of the channel.
/// </summary>
internal static class WebProviderRoutes
{
    /// <summary>
    /// Prefix of the routes of every web provider channel, without the leading separator: the form
    /// OpenIddict resolves against the base address of the request, path base included.
    /// </summary>
    public const string PathPrefix = "auth/web-provider";

    /// <summary>
    /// Segment of the link of the channel — the route a sign-in starts on.
    /// </summary>
    public const string StartSegment = "start";

    /// <summary>
    /// Segment of the address of return a provider sends the user back to.
    /// </summary>
    public const string CallbackSegment = "callback";

    /// <summary>
    /// Segment of the page of the channel — what the browser that came back from the provider shows.
    /// </summary>
    public const string PageSegment = "page";

    /// <summary>
    /// Segment of the state of the page the page script watches, under the page.
    /// </summary>
    public const string PageStateSegment = "page/state";

    /// <summary>
    /// Segment the answer to the question is posted to.
    /// </summary>
    public const string AnswerSegment = "answer";

    /// <summary>
    /// Segment the press of an item of the menu is posted to.
    /// </summary>
    public const string PressSegment = "press";

    /// <summary>
    /// Query parameter of the link of the channel carrying the transaction.
    /// </summary>
    public const string TransactionQueryParameter = "tx";

    /// <summary>
    /// Separator of the segments of a route.
    /// </summary>
    public const char PathSeparator = '/';

    /// <summary>
    /// Path every route of a channel lives under, rooted: <c>/auth/web-provider/{channel}</c>. The page
    /// mark of the channel is scoped to it, so a mark of one channel never travels to another.
    /// </summary>
    /// <param name="channelType">Channel type, already checked against the channel-type format.</param>
    /// <returns>The rooted path, without a trailing separator.</returns>
    public static string ChannelPath(string channelType) =>
        PathSeparator + string.Join(PathSeparator, PathPrefix, channelType);

    /// <summary>
    /// Rooted path of one route of a channel: <c>/auth/web-provider/{channel}/{segment}</c>.
    /// </summary>
    /// <param name="channelType">Channel type, already checked against the channel-type format.</param>
    /// <param name="segment">Segment of the route.</param>
    /// <returns>The rooted path.</returns>
    public static string RoutePath(string channelType, string segment) =>
        ChannelPath(channelType) + PathSeparator + segment;

    /// <summary>
    /// Path of the address of return of a channel, relative to its base and without the leading
    /// separator: <c>auth/web-provider/{channel}/callback</c>.
    /// </summary>
    /// <param name="channelType">Channel type, already checked against the channel-type format.</param>
    /// <returns>The relative path.</returns>
    public static string CallbackPath(string channelType) =>
        string.Join(PathSeparator, PathPrefix, channelType, CallbackSegment);

    /// <summary>
    /// Absolute address of return of a channel: <c>{PublicBaseUrl}/auth/web-provider/{channel}/callback</c>.
    /// A path of the base URL is kept, with or without its trailing separator.
    /// </summary>
    /// <param name="publicBaseUrl">Absolute public base URL of the channel.</param>
    /// <param name="channelType">Channel type, already checked against the channel-type format.</param>
    /// <returns>The absolute address.</returns>
    public static Uri CallbackUri(Uri publicBaseUrl, string channelType) =>
        new(publicBaseUrl.AbsoluteUri.TrimEnd(PathSeparator) + PathSeparator + CallbackPath(channelType), UriKind.Absolute);

    /// <summary>
    /// Link of the channel for one transaction:
    /// <c>{PublicBaseUrl}/auth/web-provider/{channel}/start?tx={transactionId}</c>. It depends on the
    /// transaction alone, so the link of one transaction is always the same.
    /// </summary>
    /// <param name="publicBaseUrl">Absolute public base URL of the channel.</param>
    /// <param name="channelType">Channel type, already checked against the channel-type format.</param>
    /// <param name="transactionId">The transaction the link starts.</param>
    /// <returns>The absolute link.</returns>
    public static string StartUri(Uri publicBaseUrl, string channelType, string transactionId) =>
        QueryHelpers.AddQueryString(
            publicBaseUrl.AbsoluteUri.TrimEnd(PathSeparator)
                + PathSeparator
                + string.Join(PathSeparator, PathPrefix, channelType, StartSegment),
            TransactionQueryParameter,
            transactionId);
}
