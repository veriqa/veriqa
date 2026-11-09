// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veriqa.Sample.Quickstart.AgentApproval;

/// <summary>
/// Where the backend finds Veriqa and how it authenticates. Every value comes from an environment
/// variable; on the local stand none of them is needed: the secret is read from the stand's .env and
/// the certificate from its certs/.
/// </summary>
/// <param name="Issuer">VERIQA_ISSUER — the Veriqa issuer.</param>
/// <param name="ClientId">VERIQA_CLIENT_ID — the client registered on the issuer.</param>
/// <param name="ClientSecret">VERIQA_CLIENT_SECRET, or CLIENT_SECRET of ../../built-in/.env.</param>
/// <param name="CaFile">VERIQA_CA_FILE — the certificate to trust for the issuer; null — the system trust store.</param>
public sealed record VeriqaSettings(Uri Issuer, string ClientId, string ClientSecret, string? CaFile)
{
    private const string DefaultIssuer = "https://localhost:8443";
    private const string StandEnvSecretKey = "CLIENT_SECRET";

    /// <summary>
    /// Reads the settings. Paths are relative to <paramref name="clientDirectory"/>, the directory of
    /// this project. Answers null with <paramref name="error"/> set when no client secret is found.
    /// </summary>
    public static VeriqaSettings? FromEnvironment(string clientDirectory, string defaultClientId, out string? error)
    {
        var standDirectory = Path.GetFullPath(Path.Combine(clientDirectory, "..", "..", "built-in"));
        var envFile = Path.Combine(standDirectory, ".env");
        var caFile = Environment.GetEnvironmentVariable("VERIQA_CA_FILE") is { Length: > 0 } configuredCa
            ? configuredCa
            : Path.Combine(standDirectory, "certs", "localhost.crt");

        var secret = Environment.GetEnvironmentVariable("VERIQA_CLIENT_SECRET") is { Length: > 0 } configured
            ? configured
            : ReadEnvFile(envFile, StandEnvSecretKey);

        if (string.IsNullOrEmpty(secret))
        {
            error = $"No client secret: set VERIQA_CLIENT_SECRET, or {StandEnvSecretKey} in {envFile}.";
            return null;
        }

        error = null;
        return new VeriqaSettings(
            new Uri(Environment.GetEnvironmentVariable("VERIQA_ISSUER") is { Length: > 0 } issuer ? issuer : DefaultIssuer),
            Environment.GetEnvironmentVariable("VERIQA_CLIENT_ID") is { Length: > 0 } clientId ? clientId : defaultClientId,
            secret,
            File.Exists(caFile) ? caFile : null);
    }

    // Plain KEY=VALUE lines with # comments: the format of the stand's .env, no quoting.
    private static string? ReadEnvFile(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (trimmed.StartsWith('#') || separator <= 0)
            {
                continue;
            }

            if (trimmed[..separator].Trim() == key)
            {
                return trimmed[(separator + 1)..].Trim();
            }
        }

        return null;
    }
}

/// <summary>Veriqa refused a call: the status and the body as Veriqa sent them.</summary>
public sealed class VeriqaCallException(int statusCode, string body)
    : Exception($"Veriqa answered {statusCode}: {body}")
{
    /// <summary>HTTP status of the refusal.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>Problem Details or OAuth error JSON.</summary>
    public string Body { get; } = body;
}

/// <summary>Where the person answers: the link and its QR code (a PNG data URI, or null).</summary>
public sealed record ChannelEntry(string Url, string? Qr, DateTimeOffset ValidUntil);

/// <summary>A created confirmation.</summary>
public sealed record CreatedConfirmation(string TransactionId, ChannelEntry ChannelEntry);

/// <summary>The state of a confirmation: outcome, and the identity type that matched the confirmer.</summary>
public sealed record ConfirmationResult(string Outcome, string? MatchedType)
{
    /// <summary>The one outcome that is a yes.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Nobody has answered yet.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// The backend's side of the Veriqa confirmation API: a token of its own (client_credentials), creating
/// a confirmation, reading its result and, once confirmed, learning who confirmed.
/// </summary>
public sealed class VeriqaClient : IDisposable
{
    private const string TokenPath = "/connect/token";
    private const string CreatePath = "/api/transaction/confirmation";
    private const string ConfirmationGrantType = "urn:veriqa:params:oauth:grant-type:confirmation";
    private const string ExchangeScope = "openid channel";
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue _clientAuthentication;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _validUntil;

    /// <summary>Creates the client; trusts the CA file of the settings when there is one.</summary>
    public VeriqaClient(VeriqaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _http = new HttpClient(CreateHandler(settings.CaFile)) { BaseAddress = settings.Issuer };
        _clientAuthentication = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(settings.ClientId)}:{Uri.EscapeDataString(settings.ClientSecret)}")));
    }

    /// <summary>Creates a confirmation of a declared action type.</summary>
    public async Task<CreatedConfirmation> CreateAsync(
        string actionType,
        IReadOnlyDictionary<string, string> slotValues,
        IReadOnlyDictionary<string, string>? expectedIdentities,
        CancellationToken cancellationToken)
    {
        // A retry with the same key answers with the same transaction instead of a second one.
        var body = new
        {
            ActionType = actionType,
            SlotValues = slotValues,
            ExpectedIdentities = expectedIdentities,
            IdempotencyKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
        };

        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Post, CreatePath) { Content = JsonContent.Create(body, options: Json) },
            cancellationToken);
        return await ReadAsync<CreatedConfirmation>(response, cancellationToken);
    }

    /// <summary>Reads the state of a confirmation.</summary>
    public async Task<ConfirmationResult> GetResultAsync(string transactionId, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/api/transaction/{Uri.EscapeDataString(transactionId)}/result"),
            cancellationToken);
        return await ReadAsync<ConfirmationResult>(response, cancellationToken);
    }

    /// <summary>
    /// Exchanges a confirmed transaction, once, for the id_token of the person who confirmed and
    /// answers its claims (sub, channel_type, channel_user_id).
    /// </summary>
    public async Task<JsonElement> ExchangeForClaimsAsync(string transactionId, CancellationToken cancellationToken)
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
        // without validating the signature (OpenID Connect Core 3.1.3.7, item 6). A token received any
        // other way must be validated against the issuer's JWKS.
        var payload = Base64Url.DecodeFromChars(token.IdToken!.Split('.')[1]);
        return JsonSerializer.Deserialize<JsonElement>(payload);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _tokenGate.Dispose();
    }

    // Sends a request with the backend's access token. A 401 means the held token is no longer
    // accepted (the stand keeps tokens in memory, a restart forgets them): the token is dropped and the
    // request is sent once more with a fresh one.
    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        var accessToken = await AccessTokenAsync(cancellationToken);
        using (var request = createRequest())
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is not HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
        }

        await DropTokenAsync(accessToken, cancellationToken);
        using var retry = createRequest();
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        return await _http.SendAsync(retry, cancellationToken);
    }

    // The token is held until shortly before it expires: waiting for a person means reading the result
    // every two seconds, and a new token per read runs into the rate limit of /connect/token.
    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _validUntil)
            {
                return _accessToken;
            }

            var token = await RequestTokenAsync(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" },
                cancellationToken);
            _validUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(token.ExpiresIn) - ExpiryMargin;

            // A token with less life left than the margin is used once and not held.
            _accessToken = _validUntil > DateTimeOffset.UtcNow ? token.AccessToken : null;
            return token.AccessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task DropTokenAsync(string staleToken, CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken == staleToken)
            {
                _accessToken = null;
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenPath) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = _clientAuthentication;
        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadAsync<TokenResponse>(response, cancellationToken);
    }

    // A refusal is surfaced whole — status and body — so the sample shows what Veriqa answered.
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new VeriqaCallException((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
    }

    // The stand's certificate is self-signed: with a CA file, the chain is built against that file
    // alone. A name mismatch still fails. Without one, the system trust store decides, as usual.
    private static SocketsHttpHandler CreateHandler(string? caFile)
    {
        var handler = new SocketsHttpHandler();
        if (caFile is null)
        {
            return handler;
        }

        var trusted = X509CertificateLoader.LoadCertificateFromFile(caFile);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0 || certificate is null)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trusted);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate as X509Certificate2 ?? new X509Certificate2(certificate));
        };
        return handler;
    }

    private sealed record TokenResponse(string AccessToken, string? IdToken, int ExpiresIn);
}
