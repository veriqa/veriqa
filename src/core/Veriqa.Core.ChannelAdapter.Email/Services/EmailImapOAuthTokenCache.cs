// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// The access token one mailbox is signed in to with (OAuth 2.0): obtained from the token endpoint
/// by one of the two standard grants (RFC 6749 §6 and §4.4) and kept in memory until it expires.
/// </summary>
/// <remarks>
/// An instance belongs to the polling loop of ONE tenant and is used by that loop only, so it needs
/// no locking and the token of one tenant can never be handed to another. Neither the token, nor
/// the client secret, nor the refresh token, nor the body of an answer of the authorization server
/// reaches a log or the message of an exception from here.
/// </remarks>
internal sealed class EmailImapOAuthTokenCache
{
    /// <summary>
    /// Name of the grant type parameter of a token request.
    /// </summary>
    private const string GrantTypeField = "grant_type";

    /// <summary>
    /// Grant type of a request by a refresh token (RFC 6749 §6); also the name of its parameter and
    /// of the field a rotated refresh token arrives in.
    /// </summary>
    private const string RefreshTokenGrant = "refresh_token";

    /// <summary>
    /// Grant type of a request by the credentials of the application (RFC 6749 §4.4).
    /// </summary>
    private const string ClientCredentialsGrant = "client_credentials";

    /// <summary>
    /// Name of the application identifier parameter.
    /// </summary>
    private const string ClientIdField = "client_id";

    /// <summary>
    /// Name of the application secret parameter.
    /// </summary>
    private const string ClientSecretField = "client_secret";

    /// <summary>
    /// Name of the requested scope parameter.
    /// </summary>
    private const string ScopeField = "scope";

    /// <summary>
    /// Name of the access token field of an answer.
    /// </summary>
    private const string AccessTokenField = "access_token";

    /// <summary>
    /// Name of the token lifetime field of an answer, in seconds.
    /// </summary>
    private const string ExpiresInField = "expires_in";

    /// <summary>
    /// Name of the error code field of a refusal (RFC 6749 §5.2).
    /// </summary>
    private const string ErrorField = "error";

    /// <summary>
    /// What the log says in place of an error code the answer did not carry in a usable form.
    /// </summary>
    private const string UnknownErrorCode = "unknown";

    /// <summary>
    /// Factory of the client the token endpoint is called with.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Clock the expiry of a token is judged by.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger of the polling loop that owns this cache.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Fingerprint of the OAuth settings the state below was obtained with; a change of the settings
    /// makes all of it void.
    /// </summary>
    private string? _obtainedWith;

    /// <summary>
    /// The access token in hand; null — there is none to reuse.
    /// </summary>
    private string? _accessToken;

    /// <summary>
    /// The moment <see cref="_accessToken"/> stops being reused.
    /// </summary>
    private DateTimeOffset _reusableUntil;

    /// <summary>
    /// A refresh token the authorization server returned in place of the configured one. It lives in
    /// the memory of the process only and is never written back to the configuration.
    /// </summary>
    private string? _rotatedRefreshToken;

    /// <summary>
    /// Creates the token cache of one tenant's mailbox.
    /// </summary>
    /// <param name="httpClientFactory">Factory of the client the token endpoint is called with.</param>
    /// <param name="timeProvider">Clock the expiry of a token is judged by.</param>
    /// <param name="logger">Logger of the owning polling loop.</param>
    public EmailImapOAuthTokenCache(
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Computes the fingerprint of the OAuth settings: equal settings give an equal value, and the
    /// secrets among them are not kept in clear text for the sake of a comparison.
    /// </summary>
    /// <param name="oauth">OAuth settings of the mailbox.</param>
    /// <returns>Hex digest of the settings.</returns>
    public static string Fingerprint(EmailImapOAuthOptions oauth)
    {
        ArgumentNullException.ThrowIfNull(oauth);

        var identity = string.Join(
            '\0',
            oauth.TokenEndpoint,
            oauth.ClientId,
            oauth.ClientSecret,
            oauth.RefreshToken,
            oauth.Scope);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>
    /// Returns an access token for a sign-in to the mailbox: the one in hand while it is still
    /// good, otherwise a new one from the token endpoint.
    /// </summary>
    /// <param name="oauth">OAuth settings of the mailbox, already judged by the validator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The access token.</returns>
    /// <exception cref="InvalidOperationException">
    /// The authorization server issued no access token.
    /// </exception>
    public async Task<string> GetAccessTokenAsync(
        EmailImapOAuthOptions oauth,
        CancellationToken cancellationToken)
    {
        // The method reuses the token in hand until its expiry and asks for a new one otherwise
        ArgumentNullException.ThrowIfNull(oauth);

        var fingerprint = Fingerprint(oauth);

        if (!string.Equals(fingerprint, _obtainedWith, StringComparison.Ordinal))
        {
            // Other settings — another application or another user: nothing obtained so far applies.
            _obtainedWith = fingerprint;
            _accessToken = null;
            _rotatedRefreshToken = null;
        }

        if (_accessToken is not null && _timeProvider.GetUtcNow() < _reusableUntil)
        {
            return _accessToken;
        }

        _accessToken = null;

        var refreshToken = _rotatedRefreshToken ?? oauth.RefreshToken;
        var byRefreshToken = !string.IsNullOrWhiteSpace(refreshToken);

        using var answer = await RequestAsync(oauth, byRefreshToken ? refreshToken : null, cancellationToken);
        var root = answer.RootElement;

        var accessToken = ReadString(root, AccessTokenField);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "The authorization server answered the token request of the mailbox without an access token.");
        }

        // A server that rotates refresh tokens returns the next one with the access token.
        var rotatedRefreshToken = ReadString(root, RefreshTokenGrant);
        if (!string.IsNullOrWhiteSpace(rotatedRefreshToken))
        {
            _rotatedRefreshToken = rotatedRefreshToken;
        }

        // Without a stated lifetime the token is good for the sign-in it was asked for and is not
        // kept: the next sign-in asks again. The same goes for a lifetime inside the safety margin.
        var reusableSeconds = ReadLifetimeSeconds(root) - EmailAdapterConstants.ImapOAuthExpiryMarginSeconds;
        if (reusableSeconds > 0)
        {
            _accessToken = accessToken;
            _reusableUntil = _timeProvider.GetUtcNow().AddSeconds(reusableSeconds);
        }

        _logger.LogDebug(
            "Email IMAP polling: access token obtained from the authorization server ({GrantType}); reusable for {ReusableSeconds} s",
            byRefreshToken ? RefreshTokenGrant : ClientCredentialsGrant,
            Math.Max(reusableSeconds, 0));

        return accessToken;
    }

    /// <summary>
    /// Forgets the access token in hand: the mailbox refused it, so the next sign-in asks for a new one.
    /// </summary>
    public void Invalidate() => _accessToken = null;

    /// <summary>
    /// Sends one token request and returns the JSON object of a successful answer.
    /// </summary>
    /// <param name="oauth">OAuth settings of the mailbox.</param>
    /// <param name="refreshToken">The refresh token to use; null — the grant by application credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed answer; the caller disposes it.</returns>
    private async Task<JsonDocument> RequestAsync(
        EmailImapOAuthOptions oauth,
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        // The method posts the form of the grant and turns a refusal into an exception that names
        // the error code only
        var form = new List<KeyValuePair<string?, string?>>();

        if (refreshToken is not null)
        {
            form.Add(new(GrantTypeField, RefreshTokenGrant));
            form.Add(new(RefreshTokenGrant, refreshToken));
            form.Add(new(ClientIdField, oauth.ClientId));

            // A public client has no secret, and the scope of a refresh is the granted one unless narrowed.
            if (!string.IsNullOrWhiteSpace(oauth.ClientSecret))
            {
                form.Add(new(ClientSecretField, oauth.ClientSecret));
            }

            if (!string.IsNullOrWhiteSpace(oauth.Scope))
            {
                form.Add(new(ScopeField, oauth.Scope));
            }
        }
        else
        {
            form.Add(new(GrantTypeField, ClientCredentialsGrant));
            form.Add(new(ClientIdField, oauth.ClientId));
            form.Add(new(ClientSecretField, oauth.ClientSecret));
            form.Add(new(ScopeField, oauth.Scope));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        // The client is taken per request: this cache lives as long as the polling loop does.
        var client = _httpClientFactory.CreateClient(EmailAdapterConstants.ImapOAuthHttpClientName);

        using var response = await client.SendAsync(request, cancellationToken);
        var answer = await ReadJsonObjectAsync(response, cancellationToken);

        if (response.IsSuccessStatusCode && answer is not null)
        {
            return answer;
        }

        using (answer)
        {
            // Only the error code and the status go further: the body may echo what was sent.
            var errorCode = answer is null ? UnknownErrorCode : ReadErrorCode(answer.RootElement);

            throw new InvalidOperationException(
                "The authorization server issued no access token for the mailbox. "
                + $"Error: {errorCode}; HTTP status: {(int)response.StatusCode}.");
        }
    }

    /// <summary>
    /// Reads the body of an answer as a JSON object; anything else gives null.
    /// </summary>
    /// <param name="response">Answer of the token endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed object, or null when the body is not one.</returns>
    private static async Task<JsonDocument?> ReadJsonObjectAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        JsonDocument document;

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document.RootElement.ValueKind is JsonValueKind.Object)
        {
            return document;
        }

        document.Dispose();
        return null;
    }

    /// <summary>
    /// Reads a string property of an answer.
    /// </summary>
    /// <param name="root">The answer object.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The value, or null when the property is absent or is not a string.</returns>
    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads the lifetime of the token in seconds. Servers state it as a number, some as a string of digits.
    /// </summary>
    /// <param name="root">The answer object.</param>
    /// <returns>The lifetime, or zero when the answer states no usable one.</returns>
    private static long ReadLifetimeSeconds(JsonElement root)
    {
        if (!root.TryGetProperty(ExpiresInField, out var value))
        {
            return 0;
        }

        if (value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return Math.Max(number, 0);
        }

        if (value.ValueKind is JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(parsed, 0);
        }

        return 0;
    }

    /// <summary>
    /// Reads the error code of a refusal in a form that is safe to log: the code is a short run of
    /// printable ASCII by its definition (RFC 6749 §5.2), and anything else is reported as unknown.
    /// </summary>
    /// <param name="root">The answer object.</param>
    /// <returns>The error code, or the placeholder of an unknown one.</returns>
    private static string ReadErrorCode(JsonElement root)
    {
        var code = ReadString(root, ErrorField);

        if (string.IsNullOrEmpty(code) || code.Length > EmailAdapterConstants.ImapOAuthMaxErrorCodeLength)
        {
            return UnknownErrorCode;
        }

        foreach (var character in code)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))
            {
                return UnknownErrorCode;
            }
        }

        return code;
    }
}
