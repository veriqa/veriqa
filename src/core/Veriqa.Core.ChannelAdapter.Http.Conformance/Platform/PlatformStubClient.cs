// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

using Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Platform;

/// <summary>
/// Client of the platform stub of the stand in the automatic mode — the service channel through which
/// the suite plays the user and reads what the adapter did on the platform. HTTP and JSON in snake_case,
/// no signature: the stand runs in an isolated network. Every call must be answered with a 2xx status,
/// otherwise it is a <see cref="PlatformStubException"/> naming the call.
/// </summary>
/// <param name="httpClients">The HTTP clients of the working composition.</param>
/// <param name="baseUrl">Base address of the stub, ending with <c>/</c>.</param>
internal sealed class PlatformStubClient(IHttpClientFactory httpClients, Uri baseUrl)
{
    /// <summary>
    /// Path of the reset of the platform log and switches.
    /// </summary>
    private const string ResetPath = "conformance/reset";

    /// <summary>
    /// Path of the switches of the platform.
    /// </summary>
    private const string ScenarioPath = "conformance/scenario";

    /// <summary>
    /// Path of the user opening a deep link.
    /// </summary>
    private const string FollowLinkPath = "conformance/follow-link";

    /// <summary>
    /// Path of the user sending a text message to a bot.
    /// </summary>
    private const string SendTextPath = "conformance/send-text";

    /// <summary>
    /// Path of the platform log.
    /// </summary>
    private const string PlatformLogPath = "conformance/platform-log";

    /// <summary>
    /// Clears the platform log and the switches.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the stub has answered.</returns>
    /// <exception cref="PlatformStubException">The call did not succeed.</exception>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, ResetPath, content: null, cancellationToken);
    }

    /// <summary>
    /// Sets the switches of the platform until the next reset.
    /// </summary>
    /// <param name="platformCannotConfirm"><c>true</c> — the platform refuses to deliver a prompt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the stub has answered.</returns>
    /// <exception cref="PlatformStubException">The call did not succeed.</exception>
    public async Task SetScenarioAsync(bool platformCannotConfirm, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(
            new PlatformScenarioRequest { PlatformCannotConfirm = platformCannotConfirm },
            PlatformStubJsonContext.Default.PlatformScenarioRequest);
        using var response = await SendAsync(HttpMethod.Post, ScenarioPath, content, cancellationToken);
    }

    /// <summary>
    /// Makes the user of the run open a deep link on the platform and press a button of the prompt it
    /// gets.
    /// </summary>
    /// <param name="url">The deep link.</param>
    /// <param name="answer"><c>confirm</c> or <c>decline</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the stub has accepted the request.</returns>
    /// <exception cref="PlatformStubException">The call did not succeed.</exception>
    public async Task FollowLinkAsync(string url, string answer, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(
            new FollowLinkRequest { Url = url, Answer = answer },
            PlatformStubJsonContext.Default.FollowLinkRequest);
        using var response = await SendAsync(HttpMethod.Post, FollowLinkPath, content, cancellationToken);
    }

    /// <summary>
    /// Makes the user of the run send a plain text message to the bot a deep link names. The stub only
    /// chooses the bot by the link and does not open it.
    /// </summary>
    /// <param name="url">The deep link that names the bot.</param>
    /// <param name="text">The text of the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the stub has accepted the request.</returns>
    /// <exception cref="PlatformStubException">The call did not succeed.</exception>
    public async Task SendTextAsync(string url, string text, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(
            new SendTextRequest { Url = url, Text = text },
            PlatformStubJsonContext.Default.SendTextRequest);
        using var response = await SendAsync(HttpMethod.Post, SendTextPath, content, cancellationToken);
    }

    /// <summary>
    /// What the adapter did on the platform since the last reset, in order.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The actions.</returns>
    /// <exception cref="PlatformStubException">The call did not succeed or its body is not the platform log.</exception>
    public async Task<IReadOnlyList<PlatformAction>> GetPlatformLogAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, PlatformLogPath, content: null, cancellationToken);

        try
        {
            var log = await response.Content.ReadFromJsonAsync(PlatformStubJsonContext.Default.PlatformLog, cancellationToken);
            return log?.Actions
                ?? throw new PlatformStubException(CallOf(HttpMethod.Get, PlatformLogPath) + " of the platform stub answered with a body that is not the platform log.");
        }
        catch (JsonException exception)
        {
            throw new PlatformStubException(
                CallOf(HttpMethod.Get, PlatformLogPath) + " of the platform stub answered with a body that is not the platform log: " + exception.Message,
                exception);
        }
    }

    /// <summary>
    /// Makes one call of the stub.
    /// </summary>
    /// <param name="method">Method of the call.</param>
    /// <param name="path">Path of the call under the base address.</param>
    /// <param name="content">Body; <c>null</c> — none. The request of the call owns and disposes it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer, with a 2xx status; the caller disposes it.</returns>
    /// <exception cref="PlatformStubException">No answer, or a status outside 2xx.</exception>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, path))
        {
            Content = content
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(ConformanceHttpClients.PlatformStub).SendAsync(request, cancellationToken);
        }
        catch (Exception exception)
            when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new PlatformStubException(
                CallOf(method, path) + " of the platform stub brought no answer: " + exception.GetType().Name + ": " + exception.Message,
                exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            response.Dispose();
            throw new PlatformStubException(CallOf(method, path) + " of the platform stub answered with status " + status + " instead of 2xx.");
        }

        return response;
    }

    /// <summary>
    /// How a call of the stub is named in a message, for example <c>POST /conformance/reset</c>.
    /// </summary>
    /// <param name="method">Method of the call.</param>
    /// <param name="path">Path of the call.</param>
    /// <returns>The name.</returns>
    private static string CallOf(HttpMethod method, string path) => method.Method + " /" + path;
}
