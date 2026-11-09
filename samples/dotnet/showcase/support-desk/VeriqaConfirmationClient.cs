// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veriqa.Sample.DotNet.Showcase.SupportDesk;

/// <summary>
/// Settings of the relying-party backend: where Veriqa is and how this backend authenticates to it.
/// </summary>
public sealed class SampleOptions
{
    /// <summary>Configuration section of these settings.</summary>
    public const string SectionName = "SupportDeskSample";

    /// <summary>Base address of the Veriqa issuer.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Public client the customer's browser signs in through (Authorization Code + PKCE).</summary>
    public string WebClientId { get; set; } = string.Empty;

    /// <summary>Confidential client allowed the Client Credentials grant.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Secret of that client.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Action type of "resolved", declared in the MessageTemplates of that client entry.</summary>
    public string ResolveActionType { get; set; } = string.Empty;

    /// <summary>Action type of "delete", declared in the MessageTemplates of that client entry.</summary>
    public string DeleteActionType { get; set; } = string.Empty;
}

/// <summary>
/// The three calls a relying-party backend makes to Veriqa for a server-to-server confirmation:
/// create the transaction, read its result, and exchange a confirmed transaction for an id_token.
/// Every call authenticates as the client itself; no user token is involved.
/// </summary>
public sealed class VeriqaConfirmationClient(HttpClient http, SampleOptions options, VeriqaAccessTokenCache tokens)
{
    // Relative to the issuer's address (BaseAddress, with a trailing slash), never from the root: a path
    // from the root drops the path of an issuer published under a prefix, such as https://example.com/id.
    private const string TokenPath = "connect/token";
    private const string CreatePath = "api/transaction/confirmation";
    private const string ResultPathFormat = "api/transaction/{0}/result";
    private const string ConfirmationGrantType = "urn:veriqa:params:oauth:grant-type:confirmation";
    private const string ClientCredentialsGrantType = "client_credentials";
    private const string BearerScheme = "Bearer";
    private const string BasicScheme = "Basic";
    private const string ExchangeScope = "openid channel";

    /// <summary>The outcome after which the transaction may be exchanged for an id_token.</summary>
    public const string ConfirmedOutcome = "confirmed";

    /// <summary>
    /// Creates a confirmation transaction. The body names the declared action type and fills the
    /// caller slots of its contract; the answer carries the way in for the user (deep link + QR).
    /// </summary>
    /// <param name="actionType">The declared action type this confirmation asks about. Unlike the
    /// step-up sample this one has TWO of them — resolving a ticket and deleting it are different
    /// questions to the customer — so the type travels per call rather than sitting in the options.</param>
    /// <param name="slotValues">Values of the caller slots.</param>
    /// <param name="expectedIdentities">Who is expected to confirm, by declared comparable type;
    /// null — anyone may confirm, and the result then carries no match.</param>
    /// <param name="requestedChannelType">The channel to confirm in — the one the session came
    /// through. Named, the answer carries a deep link straight into it; unnamed, and several channels
    /// enabled, it carries the address of the channel-choice page instead.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<CreateConfirmationResponse> CreateAsync(
        string actionType,
        IReadOnlyDictionary<string, string> slotValues,
        IReadOnlyDictionary<string, string>? expectedIdentities,
        string? requestedChannelType,
        CancellationToken cancellationToken)
    {
        // A retry with the same key answers with the same transaction instead of a second one, so
        // the key is made once and reused when the call has to be sent again.
        var idempotencyKey = Guid.NewGuid().ToString("N");

        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Post, CreatePath)
            {
                Content = JsonContent.Create(new CreateConfirmationRequest
                {
                    ActionType = actionType,
                    SlotValues = slotValues,
                    ExpectedIdentities = expectedIdentities,
                    RequestedChannelType = requestedChannelType,
                    IdempotencyKey = idempotencyKey
                })
            },
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return (await response.Content.ReadFromJsonAsync<CreateConfirmationResponse>(cancellationToken))!;
    }

    /// <summary>
    /// Reads the outcome: <c>pending</c>, <c>confirmed</c>, <c>declined</c>, <c>expired</c> or <c>failed</c>.
    /// </summary>
    public async Task<ConfirmationResultResponse> GetResultAsync(
        string transactionId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, ResultPathFormat, Uri.EscapeDataString(transactionId))),
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return (await response.Content.ReadFromJsonAsync<ConfirmationResultResponse>(cancellationToken))!;
    }

    /// <summary>
    /// Exchanges a confirmed transaction, once, for the id_token of the person who confirmed and
    /// returns the claims of its payload.
    /// </summary>
    public async Task<JsonElement> ExchangeForIdTokenClaimsAsync(
        string transactionId,
        CancellationToken cancellationToken)
    {
        var token = await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = ConfirmationGrantType,
                ["transaction_id"] = transactionId,
                ["scope"] = ExchangeScope
            },
            cancellationToken);

        // The id_token came straight from the issuer's token endpoint over TLS, so the payload is read
        // without validating the signature (OpenID Connect Core §3.1.3.7, item 6). A token received
        // any other way must be validated against the issuer's JWKS.
        var payload = token.IdToken!.Split('.')[1];
        var padded = payload.Replace('-', '+').Replace('_', '/')
            .PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
    }

    // Sends a request authenticated as this backend. The access token is kept until shortly before
    // expires_in, as the confirmation API asks: waiting for a person means polling the result every
    // couple of seconds, and a fresh token per poll runs into the rate limit of /connect/token long
    // before the person has answered. A held token can still stop being accepted before that moment:
    // Veriqa issues reference tokens and keeps them in the OpenIddict store, which Program.cs here
    // sets up with UseInMemoryOpenIddictStore() — not the transaction store of
    // ConfigureTransactionEngine(te => te.UseInMemoryStore()). A restart leaves the held token
    // unknown to Veriqa, and it is that OpenIddict registration a durable deployment replaces.
    // A 401 drops the token and the request is sent once more with a fresh one, instead of failing
    // until the held one expires.
    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        var accessToken = await AccessTokenAsync(cancellationToken);

        using (var request = createRequest())
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);

            var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is not HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
        }

        await tokens.DropAsync(accessToken, cancellationToken);

        using var retryRequest = createRequest();
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue(
            BearerScheme,
            await AccessTokenAsync(cancellationToken));

        return await http.SendAsync(retryRequest, cancellationToken);
    }

    private Task<string> AccessTokenAsync(CancellationToken cancellationToken) =>
        tokens.GetAsync(
            ct => RequestTokenAsync(
                new Dictionary<string, string> { ["grant_type"] = ClientCredentialsGrantType },
                ct),
            cancellationToken);

    private async Task<TokenResponse> RequestTokenAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            BasicScheme,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(options.ClientId)}:{Uri.EscapeDataString(options.ClientSecret)}")));

        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
    }

    // Veriqa answers a refusal with Problem Details (title = error code) or an OAuth error body;
    // both are surfaced whole so the sample shows what went wrong.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new VeriqaCallException((int)response.StatusCode, body);
        }
    }
}

/// <summary>
/// The client-credentials access token of this backend, held between calls. It belongs to the client
/// itself — not to a request and not to a user — so one instance serves the whole process; register it
/// as a singleton next to the client.
/// </summary>
public sealed class VeriqaAccessTokenCache : IDisposable
{
    /// <summary>
    /// How long before <c>expires_in</c> a held token is dropped. The margin covers the clock difference
    /// between the two sides and the call the token is about to be used for; with the hour-long token
    /// these samples issue (<c>AccessTokenLifetimeSeconds</c>) it costs one extra token request per hour.
    /// </summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _validUntil;

    /// <summary>
    /// Returns the held token, asking <paramref name="requestTokenAsync"/> for a new one when there is
    /// none left. One caller at a time asks, so a burst of calls shares a single token request.
    /// </summary>
    /// <param name="requestTokenAsync">Gets a fresh token from the issuer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> GetAsync(
        Func<CancellationToken, Task<TokenResponse>> requestTokenAsync,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _validUntil)
            {
                return _accessToken;
            }

            var token = await requestTokenAsync(cancellationToken);
            var validUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(token.ExpiresIn) - ExpiryMargin;

            // A token whose remaining life is shorter than the margin is used once and not held.
            _accessToken = validUntil > DateTimeOffset.UtcNow ? token.AccessToken : null;
            _validUntil = validUntil;

            return token.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the held token when it is still the one <paramref name="staleToken"/> names — a token the
    /// issuer has refused. A token another caller has already replaced is left alone, so one refusal
    /// does not throw away the replacement.
    /// </summary>
    /// <param name="staleToken">Token that was refused.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DropAsync(string staleToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(_accessToken, staleToken, StringComparison.Ordinal))
            {
                _accessToken = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the lock guarding the held token.</summary>
    public void Dispose() => _gate.Dispose();
}

/// <summary>A call to Veriqa was refused; the body is Veriqa's own answer.</summary>
public sealed class VeriqaCallException(int statusCode, string body)
    : Exception($"Veriqa answered {statusCode}: {body}")
{
    /// <summary>HTTP status of the answer.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>Body of the answer.</summary>
    public string Body { get; } = body;
}

/// <summary>Body of POST /api/transaction/confirmation (the fields this sample uses).</summary>
public sealed class CreateConfirmationRequest
{
    [JsonPropertyName("action_type")]
    public required string ActionType { get; init; }

    [JsonPropertyName("slot_values")]
    public IReadOnlyDictionary<string, string>? SlotValues { get; init; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("expected_identities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ExpectedIdentities { get; init; }

    [JsonPropertyName("requested_channel_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedChannelType { get; init; }
}

/// <summary>Answer of POST /api/transaction/confirmation.</summary>
public sealed class CreateConfirmationResponse
{
    [JsonPropertyName("transaction_id")]
    public required string TransactionId { get; init; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("response_valid_until")]
    public DateTimeOffset ResponseValidUntil { get; init; }

    [JsonPropertyName("channel_entry")]
    public required ChannelEntry ChannelEntry { get; init; }
}

/// <summary>The way in for the user: a deep link (or the entry page) and the same address as a QR.</summary>
public sealed class ChannelEntry
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("channel_type")]
    public string? ChannelType { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>PNG data URI — put it straight into an img src.</summary>
    [JsonPropertyName("qr")]
    public string? Qr { get; init; }

    [JsonPropertyName("valid_until")]
    public DateTimeOffset ValidUntil { get; init; }
}

/// <summary>Answer of GET /api/transaction/{id}/result.</summary>
public sealed class ConfirmationResultResponse
{
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("matched_type")]
    public string? MatchedType { get; init; }
}

/// <summary>The members of a token response the sample reads.</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    /// <summary>Lifetime of the access token, in seconds.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }
}
