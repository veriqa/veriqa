// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The receiver of client signals of the launch page — <c>POST {HopPath}/{hop_token}</c> (SPEC-025
/// HOP-066b): the same route as the presentation of the hop URL, another method. This class holds the
/// protections of the route as a whole — HTTPS and the limit of the body; what a signal is and whether it
/// is accepted is decided by <see cref="HopSignalReceiver"/>.
/// </summary>
internal static class HopSignalEndpoint
{
    /// <summary>
    /// Maps the receiver under its own rate-limit policy (SPEC-025 HOP-061).
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="hopPath">Normalized base path of the resolve endpoint (<see cref="HopModeSettings.HopPath"/>).</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name.</param>
    /// <returns>The route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapHopSignalEndpoint(
        this IEndpointRouteBuilder endpoints,
        string hopPath,
        string rateLimitPolicyName)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(hopPath);
        ArgumentException.ThrowIfNullOrEmpty(rateLimitPolicyName);

        // No AntiForgery here, by an explicit exemption from the rule of core POST pages (SPEC-025 HOP-066e):
        // the request rides on no ambient credentials — no cookie authorizes it — but on the capability token
        // in its URL, and its body has a fixed schema. A party that knows the token sends the signal without
        // any page, so an AntiForgery token would not stop it, while a cookie-bound one would lose the
        // signals of in-app browsers with restricted cookies. The exemption covers this route alone.
        endpoints.MapPost(HopUrlForm.RouteTemplate(hopPath), HandleSignalAsync)
            .RequireRateLimiting(rateLimitPolicyName)
            .DisableAntiforgery();

        return endpoints;
    }

    /// <summary>
    /// Answers a client signal: the protections of the route first — HTTPS, then the limit of the body,
    /// neither of which looks at the token — then the signal itself. A processed signal, accepted or
    /// refused, gets <c>204</c> with no body: the answer tells nothing about the transaction (SPEC-025
    /// HOP-066b).
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="receiver">The receiver that decides on the signal.</param>
    /// <returns>The answer.</returns>
    private static async Task<IResult> HandleSignalAsync(HttpContext httpContext, HopSignalReceiver receiver)
    {
        var request = httpContext.Request;

        // HTTPS only, as for the presentation (SPEC-025 HOP-062): the token is neither decoded nor recorded.
        if (!request.IsHttps)
        {
            return Results.BadRequest();
        }

        // An honest Content-Length over the limit is refused before a byte is read; a body without one, or
        // with a false one, is refused by the bounded read at the same limit.
        if (request.ContentLength > HopModeConstants.ClientSignalMaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        byte[] body;
        try
        {
            body = await ReadBodyAsync(request, httpContext.RequestAborted);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode is StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        await receiver.ReceiveAsync(
            httpContext.GetRouteValue(HopModeConstants.HopTokenRouteParameter) as string,
            request.ContentType,
            body,
            httpContext.RequestAborted);

        return Results.NoContent();
    }

    /// <summary>
    /// Reads the body under <see cref="HopModeConstants.ClientSignalMaxBodyBytes"/>, by the shared bounded
    /// reader of the component.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token of the request.</param>
    /// <returns>The bytes of the body.</returns>
    /// <exception cref="BadHttpRequestException">The body crosses the limit (status <c>413</c>).</exception>
    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var bounded = new MemoryStream();
        await BoundedBodyReader.CopyAsync(
            request.Body,
            bounded,
            HopModeConstants.ClientSignalMaxBodyBytes,
            static () => new BadHttpRequestException(
                "The body of the hop client signal exceeds its limit.",
                StatusCodes.Status413PayloadTooLarge),
            cancellationToken);

        return bounded.ToArray();
    }
}
