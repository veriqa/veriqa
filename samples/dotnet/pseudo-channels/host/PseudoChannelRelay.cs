// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Sample.PseudoChannels.Adapter;

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// The "platform" half of a pseudo-channel. A channel tells the core about an event through its
/// webhook and nothing else, and an identity provider posts no webhook — so the host, having seen
/// the sign-in at the provider or the answer on the phone page, posts the signed webhook to itself.
/// </summary>
internal sealed class PseudoChannelRelay
{
    /// <summary>
    /// Client whose base address is this very host.
    /// </summary>
    private readonly HttpClient _http;

    /// <summary>
    /// Settings of the sample.
    /// </summary>
    private readonly PseudoChannelsSettings _settings;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<PseudoChannelRelay> _logger;

    /// <summary>
    /// Creates the relay.
    /// </summary>
    /// <param name="http">Client whose base address is this very host.</param>
    /// <param name="settings">Settings of the sample.</param>
    /// <param name="logger">Logger.</param>
    public PseudoChannelRelay(HttpClient http, PseudoChannelsSettings settings, ILogger<PseudoChannelRelay> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Posts one event of a pseudo-channel to the webhook of that channel. The identity is taken
    /// from the principal the provider handler validated — never from anything the browser sent.
    /// </summary>
    /// <param name="channelType">Channel type the event belongs to.</param>
    /// <param name="intent">One of <see cref="PseudoChannelConstants.Intents"/>.</param>
    /// <param name="transactionId">Veriqa transaction.</param>
    /// <param name="providerUser">Principal built from the provider's ID token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Whether the webhook accepted the request. The webhook answers 200 to a request it accepted
    /// and could not act upon as well, so <see langword="true"/> means "delivered", not "the
    /// transaction moved".
    /// </returns>
    public async Task<bool> SendAsync(
        string channelType,
        string intent,
        TransactionId transactionId,
        ClaimsPrincipal providerUser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_settings.RelaySecret))
        {
            _logger.LogWarning(
                "The relay secret is not configured — the {Intent} event of the {ChannelType} pseudo-channel is not sent",
                intent,
                channelType);

            return false;
        }

        var subject = providerUser.FindFirstValue(PseudoChannelConstants.Fields.Subject);
        if (string.IsNullOrEmpty(subject))
        {
            _logger.LogWarning(
                "The {ChannelType} provider returned no subject — the {Intent} event is not sent",
                channelType,
                intent);

            return false;
        }

        // The field names of the webhook body are the standard OpenID Connect claim names, so one
        // set of constants reads the claims and writes the body.
        var body = JsonSerializer.SerializeToUtf8Bytes(new PseudoChannelWebhookPayload
        {
            Intent = intent,
            TransactionId = transactionId.ToString(),
            Subject = subject,
            Email = providerUser.FindFirstValue(PseudoChannelConstants.Fields.Email),
            // A claim is always a string; the webhook body wants a JSON boolean, and the adapter
            // rejects anything else.
            EmailVerified = bool.TryParse(
                providerUser.FindFirstValue(PseudoChannelConstants.Fields.EmailVerified),
                out var emailVerified)
                ? emailVerified
                : null,
            Name = providerUser.FindFirstValue(PseudoChannelConstants.Fields.Name),
            GivenName = providerUser.FindFirstValue(PseudoChannelConstants.Fields.GivenName),
            FamilyName = providerUser.FindFirstValue(PseudoChannelConstants.Fields.FamilyName),
            Picture = providerUser.FindFirstValue(PseudoChannelConstants.Fields.Picture),
            Locale = providerUser.FindFirstValue(PseudoChannelConstants.Fields.Locale)
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            CustomChannelConstants.BuildWebhookPath(channelType));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(PseudoHostConstants.JsonContentType);

        // The signature covers the very bytes that are sent.
        request.Headers.Add(
            PseudoChannelConstants.SignatureHeader,
            PseudoChannelSignature.Compute(_settings.RelaySecret, body));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "The {ChannelType} webhook answered {StatusCode} to the {Intent} event",
                channelType,
                (int)response.StatusCode,
                intent);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A cancellation nobody asked for is the client's own timeout: the webhook did not
            // answer in time, which for the phone is the same as not being reached.
            _logger.LogWarning(
                exception,
                "The {ChannelType} webhook could not be reached for the {Intent} event — check {Setting}",
                channelType,
                intent,
                $"{PseudoChannelsSettings.SectionName}:{nameof(PseudoChannelsSettings.RelayBaseUrl)}");
        }

        return false;
    }
}
