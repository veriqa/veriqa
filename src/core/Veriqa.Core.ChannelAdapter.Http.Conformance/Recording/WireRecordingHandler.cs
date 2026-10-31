// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Net.Http.Headers;

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;

/// <summary>
/// The handler the suite adds to every HTTP client of a composition (<c>ConfigureHttpClientDefaults</c>):
/// it records the body the wrapper sends and the status and body of the answer that comes back to it.
/// The body of the answer is read up to the limit of the channel contour and one byte more — enough to
/// know it is over the limit — and handed to the wrapper as it came, so the wrapper decides exactly as
/// it would without the handler: a body over the limit is still over it.
/// </summary>
/// <param name="recorder">Recorder of the exchanges.</param>
internal sealed class WireRecordingHandler(WireCallRecorder recorder) : DelegatingHandler
{
    /// <summary>
    /// Records one exchange.
    /// </summary>
    /// <param name="request">The request of the wrapper.</param>
    /// <param name="cancellationToken">Cancellation token of the attempt.</param>
    /// <returns>The answer, its body replaced by the bytes read.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            recorder.Record(new RecordedExchange
            {
                RequestBody = requestBody,
                TransportFailure = DescribeFailure(exception)
            });
            throw;
        }

        var limit = ChannelAdapterLimits.MaxWebhookBodySize;
        var buffer = new byte[limit + 1];
        int read;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        }
        catch (Exception exception)
        {
            // The exchange broke while the body was on its way: no answer reached the wrapper.
            recorder.Record(new RecordedExchange
            {
                RequestBody = requestBody,
                StatusCode = (int)response.StatusCode,
                TransportFailure = DescribeFailure(exception)
            });
            response.Dispose();
            throw;
        }

        var body = buffer.AsMemory(0, read).ToArray();
        var exceedsLimit = read > limit;

        recorder.Record(new RecordedExchange
        {
            RequestBody = requestBody,
            StatusCode = (int)response.StatusCode,
            ResponseBody = exceedsLimit ? null : body,
            ResponseExceedsLimit = exceedsLimit
        });

        response.Content = ReplaceContent(response.Content, body);
        return response;
    }

    /// <summary>
    /// The reason of an exchange that brought no answer: the type and the message of its exception.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The reason.</returns>
    private static string DescribeFailure(Exception exception) =>
        exception.GetType().Name + ": " + exception.Message;

    /// <summary>
    /// A content of the bytes read that keeps the headers of the original one — all but the length,
    /// which the new content states itself.
    /// </summary>
    /// <param name="original">The content of the answer; disposed here.</param>
    /// <param name="body">The bytes read.</param>
    /// <returns>The new content.</returns>
    private static ByteArrayContent ReplaceContent(HttpContent original, byte[] body)
    {
        var replacement = new ByteArrayContent(body);

        foreach (var header in original.Headers)
        {
            if (!string.Equals(header.Key, HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase))
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        original.Dispose();
        return replacement;
    }
}
