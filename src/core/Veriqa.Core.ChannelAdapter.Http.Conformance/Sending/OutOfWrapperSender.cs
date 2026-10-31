// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;

using Microsoft.Extensions.Configuration;

using Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Sending;

/// <summary>
/// The sender of the calls the wrapper does not make by construction (SPEC-042 R23): it posts a body to
/// the base URL of the registration, signed with the current transport secret under a
/// <c>webhook-id</c> of its choice (SPEC-042 C31) or not signed at all. It builds no envelope and signs
/// only through <see cref="HttpChannelSignature.Sign"/>; the bodies are those the wrapper produced and
/// the recording handler kept, mutated. It also gives the signature headers of an event the suite hands
/// to the wrapper itself, signed the way an adapter signs it. The address and the secret are the keys of
/// the channel read as data: the start of the working composition has checked them.
/// </summary>
internal sealed class OutOfWrapperSender
{
    /// <summary>
    /// Message of a request that the recording handler did not see.
    /// </summary>
    private const string SeamMissingMessage =
        "The recording handler of the suite saw no request of the sender: the handler the suite adds to every HTTP client does not stand in the chain of the sender's client, so the suite cannot read the answers of the adapter.";

    /// <summary>
    /// The HTTP clients of the working composition.
    /// </summary>
    private readonly IHttpClientFactory _httpClients;

    /// <summary>
    /// Recorder of the exchanges.
    /// </summary>
    private readonly WireCallRecorder _recorder;

    /// <summary>
    /// Base URL of the registration.
    /// </summary>
    private readonly Uri _baseUrl;

    /// <summary>
    /// The current transport secret of the registration; kept in memory, never printed.
    /// </summary>
    private readonly string[] _secrets;

    /// <summary>
    /// Creates the sender.
    /// </summary>
    /// <param name="httpClients">The HTTP clients of the working composition.</param>
    /// <param name="recorder">Recorder of the exchanges.</param>
    /// <param name="baseUrl">Base URL of the registration.</param>
    /// <param name="transportSecret">The current transport secret of the registration.</param>
    private OutOfWrapperSender(IHttpClientFactory httpClients, WireCallRecorder recorder, Uri baseUrl, string transportSecret)
    {
        _httpClients = httpClients;
        _recorder = recorder;
        _baseUrl = baseUrl;
        _secrets = [transportSecret];
    }

    /// <summary>
    /// Creates the sender of the registration under test.
    /// </summary>
    /// <param name="configuration">Configuration of the suite — the channel section is read from it.</param>
    /// <param name="channelType">The channel under test.</param>
    /// <param name="httpClients">The HTTP clients of the working composition.</param>
    /// <param name="recorder">Recorder of the exchanges.</param>
    /// <returns>The sender.</returns>
    public static OutOfWrapperSender Create(
        IConfiguration configuration,
        string channelType,
        IHttpClientFactory httpClients,
        WireCallRecorder recorder)
    {
        var httpSection = configuration.GetSection(ConfigurationPath.Combine(
            ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName,
            channelType,
            ConformanceConfigKeys.ChannelHttpSectionName));

        // The working composition has started on these values, so both are present and valid here.
        var baseUrl = httpSection[ConformanceConfigKeys.BaseUrlMember]
            ?? throw new InvalidOperationException("The base URL of the channel is missing after the composition started.");
        var transportSecret = httpSection[ConformanceConfigKeys.TransportSecretMember]
            ?? throw new InvalidOperationException("The transport secret of the channel is missing after the composition started.");

        return new OutOfWrapperSender(httpClients, recorder, new Uri(baseUrl, UriKind.Absolute), transportSecret);
    }

    /// <summary>
    /// Posts a body with no signature headers.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the post did on the wire — exactly one exchange.</returns>
    /// <exception cref="ConformanceSetupException">The recording handler of the suite is not in the chain.</exception>
    public Task<WireCallCapture> SendUnsignedAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
        SendAsync(body, webhookId: null, cancellationToken);

    /// <summary>
    /// Posts a body signed with the current transport secret, at the current moment.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="webhookId">The <c>webhook-id</c> of the message — for a call with an effect, its idempotency key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the post did on the wire — exactly one exchange.</returns>
    /// <exception cref="ConformanceSetupException">The recording handler of the suite is not in the chain.</exception>
    public Task<WireCallCapture> SendSignedAsync(ReadOnlyMemory<byte> body, string webhookId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(webhookId);
        return SendAsync(body, webhookId, cancellationToken);
    }

    /// <summary>
    /// The three Standard Webhooks headers of a body signed with the current transport secret of the
    /// registration at a given moment.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="webhookId">The <c>webhook-id</c> of the message.</param>
    /// <param name="timestamp">The signing moment.</param>
    /// <returns>The headers, names compared without case.</returns>
    public IReadOnlyDictionary<string, string> SignatureHeadersOf(ReadOnlySpan<byte> body, string webhookId, DateTimeOffset timestamp) =>
        SignatureHeadersOf(body, webhookId, timestamp, _secrets);

    /// <summary>
    /// The three Standard Webhooks headers of a body signed with the given secrets at a given moment.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="webhookId">The <c>webhook-id</c> of the message.</param>
    /// <param name="timestamp">The signing moment.</param>
    /// <param name="secrets">One or two secrets in the <c>whsec_</c> serialization, the current one first.</param>
    /// <returns>The headers, names compared without case.</returns>
    public static IReadOnlyDictionary<string, string> SignatureHeadersOf(
        ReadOnlySpan<byte> body,
        string webhookId,
        DateTimeOffset timestamp,
        IReadOnlyList<string> secrets) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [HttpChannelWire.WebhookIdHeader] = webhookId,
            [HttpChannelWire.WebhookTimestampHeader] = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            [HttpChannelWire.WebhookSignatureHeader] = HttpChannelSignature.Sign(webhookId, timestamp, body, secrets)
        };

    /// <summary>
    /// Posts a body inside a capture of its own. A failed exchange is an answer about conformance, not
    /// a failure of the suite: the recording handler has recorded it with its reason.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="webhookId">The <c>webhook-id</c>; <c>null</c> — no signature headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capture.</returns>
    private async Task<WireCallCapture> SendAsync(ReadOnlyMemory<byte> body, string? webhookId, CancellationToken cancellationToken)
    {
        using var capture = _recorder.Capture();
        using var request = CreateRequest(body, webhookId);

        try
        {
            using var response = await _httpClients.CreateClient(ConformanceHttpClients.Sender).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Recorded by the handler with its reason.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout of the request — recorded by the handler with its reason.
        }

        if (capture.Last is null)
        {
            throw new ConformanceSetupException(SeamMissingMessage);
        }

        return capture;
    }

    /// <summary>
    /// The request of a post: the body as JSON, as the wrapper sends it, and the three Standard Webhooks
    /// headers when it is signed.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="webhookId">The <c>webhook-id</c>; <c>null</c> — no signature headers.</param>
    /// <returns>The request.</returns>
    private HttpRequestMessage CreateRequest(ReadOnlyMemory<byte> body, string? webhookId)
    {
        var content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json, Encoding.UTF8.WebName);

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl)
        {
            Content = content
        };

        if (webhookId is null)
        {
            return request;
        }

        try
        {
            foreach (var (name, value) in SignatureHeadersOf(body.Span, webhookId, TimeProvider.System.GetUtcNow()))
            {
                request.Headers.Add(name, value);
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }
}
