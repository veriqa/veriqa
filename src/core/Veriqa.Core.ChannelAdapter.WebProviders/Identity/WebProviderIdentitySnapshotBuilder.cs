// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Claims;

using Microsoft.Extensions.Logging;

using OpenIddict.Abstractions;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.WebProviders.Configuration;
using Veriqa.Core.ChannelAdapter.WebProviders.Constants;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Identity;

/// <summary>
/// Builds the identity snapshot of an account from the principal the OpenIddict client produced at the
/// address of return. OpenIddict has already merged what the provider stated — the identity token and
/// the userinfo answer — and mapped the identifier, the name and the email of every provider of the
/// package to the WS-Federation claim types (a Yandex <c>default_email</c>, a Huawei
/// <c>display_name</c>, a VK ID <c>user_id</c>…); the snapshot reads those first and the standard
/// OpenID Connect claims after them. The phone number and the further claims are read where the
/// registration of the channel points — no name of a provider is known here. Nothing the provider issued
/// to the client itself, and no raw answer of the provider, enters the snapshot; no value the provider
/// stated is ever written to the log.
/// </summary>
internal static class WebProviderIdentitySnapshotBuilder
{
    /// <summary>
    /// Builds the snapshot.
    /// <list type="bullet">
    /// <item>the identifier is the one OpenIddict issued; without it there is no snapshot;</item>
    /// <item>the display name is the name, else the email, else the identifier;</item>
    /// <item>the avatar is taken only as an absolute <c>https</c> address;</item>
    /// <item>the email is verified or not only by an explicit statement of the provider beside the
    /// email; without one it stays unknown;</item>
    /// <item>the phone number is taken only by a channel that declares it, from the path its
    /// registration states, in E.164; with it goes its proof (<see cref="PhoneNumberVerifiedOf"/>);</item>
    /// <item>the further claims are taken by the entries of the registration, each under the name the
    /// entry gives it.</item>
    /// </list>
    /// </summary>
    /// <param name="registration">The channel the account signed in on.</param>
    /// <param name="principal">The principal of the OpenIddict client.</param>
    /// <param name="tenantId">Tenant of the transaction the sign-in is for; <c>null</c> — the default
    /// tenant.</param>
    /// <param name="capturedAt">Moment the snapshot is taken.</param>
    /// <param name="logger">Logger of the values of the provider that could not be taken.</param>
    /// <returns>The snapshot; <c>null</c> — the provider stated no identifier of the account.</returns>
    public static ChannelIdentitySnapshot? Build(
        WebProviderRegistration registration,
        ClaimsPrincipal principal,
        string? tenantId,
        DateTimeOffset capturedAt,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(logger);

        var channelType = registration.ChannelType;

        var channelUserId = First(principal, ClaimTypes.NameIdentifier, OpenIddictConstants.Claims.Subject);
        if (channelUserId is null)
        {
            return null;
        }

        var name = First(principal, ClaimTypes.Name, OpenIddictConstants.Claims.Name);
        var email = First(principal, ClaimTypes.Email, OpenIddictConstants.Claims.Email);
        var (phoneNumber, phoneNumberVerified) = PhoneNumberOf(registration, principal, logger);

        return new ChannelIdentitySnapshot
        {
            TenantId = tenantId,
            ChannelType = channelType,
            IsBot = false,
            ChannelUserId = channelUserId,
            DisplayName = name ?? email ?? channelUserId,
            FirstName = First(
                principal,
                ClaimTypes.GivenName,
                OpenIddictConstants.Claims.GivenName,
                WebProviderAdapterConstants.FirstNameClaim),
            LastName = First(
                principal,
                ClaimTypes.Surname,
                OpenIddictConstants.Claims.FamilyName,
                WebProviderAdapterConstants.LastNameClaim),
            Email = email,
            EmailVerified = email is null
                ? null
                : ExplicitFlag(First(principal, OpenIddictConstants.Claims.EmailVerified)),
            AvatarUrl = HttpsAddress(First(
                principal,
                OpenIddictConstants.Claims.Picture,
                WebProviderAdapterConstants.AvatarClaim)),
            Locale = First(principal, OpenIddictConstants.Claims.Locale),
            PhoneNumber = phoneNumber,
            PhoneNumberVerified = phoneNumberVerified,
            RawMetadata = null,
            AdditionalClaims = AdditionalClaimsOf(registration, principal, logger),
            CapturedAt = capturedAt,
            AdapterVersion = WebProviderAdapterConstants.AdapterVersion
        };
    }

    /// <summary>
    /// The phone number the registration points at, in E.164, with its proof. A value that is no phone
    /// number in any spelling counts as none.
    /// </summary>
    /// <param name="registration">The channel.</param>
    /// <param name="principal">The principal.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The number and whether it is verified; both <c>null</c> — the channel takes no number,
    /// or the provider stated none usable.</returns>
    private static (string? Number, bool? Verified) PhoneNumberOf(
        WebProviderRegistration registration,
        ClaimsPrincipal principal,
        ILogger logger)
    {
        if (registration.PhoneNumber is not { } phoneNumber
            || Read(phoneNumber.Claim, principal, registration.ChannelType, logger) is not { } value)
        {
            return (null, null);
        }

        if (!E164PhoneNumber.TryNormalize(value, out var normalized))
        {
            logger.LogWarning(
                "Web provider channel {ChannelType}: the value at {Path} is not a phone number; the account is taken without one",
                registration.ChannelType,
                phoneNumber.Claim.ToString());

            return (null, null);
        }

        return (normalized, PhoneNumberVerifiedOf(phoneNumber, principal, registration.ChannelType, logger));
    }

    /// <summary>
    /// The proof of the number. The registration is the ceiling: a number of a channel that does not
    /// declare it proven is not verified. A number of a channel that does is verified, unless the
    /// provider states explicitly beside it that it is not — the statement of the provider about this
    /// very number only lowers the proof, never raises it. A statement that is not a boolean is no
    /// statement.
    /// </summary>
    /// <param name="phoneNumber">The number of the channel.</param>
    /// <param name="principal">The principal.</param>
    /// <param name="channelType">The channel.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>Whether the number is verified.</returns>
    private static bool PhoneNumberVerifiedOf(
        WebProviderPhoneNumber phoneNumber,
        ClaimsPrincipal principal,
        string channelType,
        ILogger logger)
    {
        if (phoneNumber.Capability.Proof is not PhoneNumberProof.Proven)
        {
            return false;
        }

        if (phoneNumber.VerifiedClaim is not { } verifiedClaim
            || Read(verifiedClaim, principal, channelType, logger) is not { } statement)
        {
            return true;
        }

        if (bool.TryParse(statement, out var verified))
        {
            return verified;
        }

        logger.LogWarning(
            "Web provider channel {ChannelType}: the value at {Path} is not a boolean; the number is taken as the registration declares it",
            channelType,
            verifiedClaim.ToString());

        return true;
    }

    /// <summary>
    /// The further claims of the entries of the registration, each under its name. An entry whose value
    /// the provider did not state is left out.
    /// </summary>
    /// <param name="registration">The channel.</param>
    /// <param name="principal">The principal.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The claims; <c>null</c> — none.</returns>
    private static Dictionary<string, string>? AdditionalClaimsOf(
        WebProviderRegistration registration,
        ClaimsPrincipal principal,
        ILogger logger)
    {
        Dictionary<string, string>? claims = null;

        foreach (var mapping in registration.Claims)
        {
            if (Read(mapping.Source, principal, registration.ChannelType, logger) is { } value)
            {
                claims ??= new Dictionary<string, string>(StringComparer.Ordinal);
                claims[mapping.Claim] = value;
            }
        }

        return claims;
    }

    /// <summary>
    /// Reads the value at a path, and logs — by the path, never by the value — what the provider stated
    /// there that is not a leaf.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="principal">The principal.</param>
    /// <param name="channelType">The channel.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The value; <c>null</c> — none.</returns>
    private static string? Read(WebProviderClaimPath path, ClaimsPrincipal principal, string channelType, ILogger logger)
    {
        var value = path.Read(principal, out var unusable);

        if (unusable)
        {
            logger.LogWarning(
                "Web provider channel {ChannelType}: the value at {Path} is not a single string, number or boolean; it is not taken",
                channelType,
                path.ToString());
        }

        return value;
    }

    /// <summary>
    /// The first non-blank value among the claims of the given types, trimmed.
    /// </summary>
    /// <param name="principal">The principal.</param>
    /// <param name="claimTypes">Claim types, in the order of preference.</param>
    /// <returns>The value; <c>null</c> — none of the claims carries one.</returns>
    private static string? First(ClaimsPrincipal principal, params ReadOnlySpan<string> claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an explicit boolean statement of the provider: a JSON boolean or its string form, as Apple
    /// sends it.
    /// </summary>
    /// <param name="value">The value of the claim.</param>
    /// <returns><c>true</c> or <c>false</c> as stated; <c>null</c> — no statement, or one that is not a
    /// boolean.</returns>
    private static bool? ExplicitFlag(string? value) =>
        bool.TryParse(value, out var flag) ? flag : null;

    /// <summary>
    /// Keeps an address only when it is an absolute <c>https</c> one.
    /// </summary>
    /// <param name="value">The address the provider stated.</param>
    /// <returns>The address; <c>null</c> — none, or not an absolute <c>https</c> address.</returns>
    private static string? HttpsAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
}
