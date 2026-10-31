// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// Makes the client secret Sign in with Apple expects at its token endpoint: a JWT signed with the
/// private key of the developer account (ES256), naming the team as the issuer, the Services ID as
/// the subject and Apple as the audience. A fresh short-lived one is made for every token request,
/// so nothing with a six-month life sits in configuration.
/// </summary>
internal sealed class AppleClientSecret
{
    /// <summary>
    /// Services ID the secret is made for.
    /// </summary>
    private readonly string _clientId;

    /// <summary>
    /// Team ID of the developer account.
    /// </summary>
    private readonly string _teamId;

    /// <summary>
    /// Signing key together with its identifier.
    /// </summary>
    private readonly SigningCredentials _credentials;

    /// <summary>
    /// Clock.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Token writer. Default times are off: the token carries exactly the claims Apple names.
    /// </summary>
    private readonly JsonWebTokenHandler _tokenHandler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>
    /// Reads the key and the identifiers.
    /// </summary>
    /// <param name="settings">Apple credentials.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <exception cref="InvalidOperationException">The provider is switched on, but the team, the
    /// key identifier or the private key is missing or unreadable.</exception>
    public AppleClientSecret(AppleSettings settings, TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(settings.TeamId)
            || string.IsNullOrWhiteSpace(settings.KeyId)
            || string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            throw new InvalidOperationException(
                $"Sign in with Apple is switched on ({PseudoChannelsSettings.SectionName}:Apple:ClientId is set), " +
                "so TeamId, KeyId and PrivateKey of the same section are required: the client secret is a token " +
                "signed with that key.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(settings.PrivateKey);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            key.Dispose();

            throw new InvalidOperationException(
                $"{PseudoChannelsSettings.SectionName}:Apple:PrivateKey is not a PEM-encoded private key. " +
                "Pass the content of the .p8 file, line breaks included.",
                exception);
        }

        _clientId = settings.ClientId;
        _teamId = settings.TeamId;
        _credentials = new SigningCredentials(
            new ECDsaSecurityKey(key) { KeyId = settings.KeyId },
            SecurityAlgorithms.EcdsaSha256);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Makes a client secret valid from now for <see cref="PseudoHostConstants.Apple.ClientSecretLifetime"/>.
    /// </summary>
    /// <returns>The signed token.</returns>
    public string Create()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        return _tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _teamId,
            Audience = PseudoHostConstants.Apple.Authority,
            Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Sub] = _clientId },
            IssuedAt = now,
            Expires = now + PseudoHostConstants.Apple.ClientSecretLifetime,
            SigningCredentials = _credentials
        });
    }
}
