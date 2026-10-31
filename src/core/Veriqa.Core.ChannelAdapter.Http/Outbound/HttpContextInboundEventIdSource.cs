// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Http;

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// The shipped source of the triggering event identifier (SPEC-042 L43): an outbound call made while
/// the inbound route of its own channel is being served runs inside the processing of that event, and
/// the event is named by the <c>webhook-id</c> header of the request. Any other request — the route of
/// another channel, a sign-in page — and a call outside any request (a background path) have no
/// triggering event.
/// </summary>
internal sealed class HttpContextInboundEventIdSource : IHttpChannelInboundEventIdSource
{
    /// <summary>
    /// Separator of the segments of a request path.
    /// </summary>
    private const char PathSeparator = '/';

    /// <summary>
    /// Accessor of the HTTP request being served.
    /// </summary>
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Creates the source over the accessor of the current request.
    /// </summary>
    /// <param name="httpContextAccessor">Accessor of the HTTP request being served.</param>
    public HttpContextInboundEventIdSource(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    /// <inheritdoc />
    public string? GetCurrentEventId(string channelType)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        var request = _httpContextAccessor.HttpContext?.Request;
        if (request is null || !HttpMethods.IsPost(request.Method))
        {
            return null;
        }

        // The inbound route of the channel is its webhook path, alone or followed by exactly one tenant
        // segment (the demux of the route); a longer path is not the route of this channel. The routing
        // of the host serves the route with a trailing slash as well, so one is not taken for a segment.
        if (!request.Path.StartsWithSegments(CustomChannelConstants.BuildWebhookPath(channelType), out var rest))
        {
            return null;
        }

        var tenantSegment = rest.Value ?? string.Empty;
        if (tenantSegment.EndsWith(PathSeparator))
        {
            tenantSegment = tenantSegment[..^1];
        }

        if (tenantSegment.LastIndexOf(PathSeparator) > 0)
        {
            return null;
        }

        var webhookId = request.Headers[HttpChannelWire.WebhookIdHeader].ToString();

        return string.IsNullOrEmpty(webhookId) ? null : webhookId;
    }
}
