// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Claims;

using OpenIddict.Abstractions;

using Veriqa.Core.ChannelAdapter.WebProviders.Constants;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Identity;

/// <summary>
/// Builds the identity snapshot of an account from the principal the OpenIddict client produced at the
/// address of return. OpenIddict has already merged what the provider stated — the identity token and
/// the userinfo answer — and mapped the identifier, the name and the email of every provider of the
/// package to the WS-Federation claim types (a Yandex <c>default_email</c>, a Huawei
/// <c>display_name</c>, a VK ID <c>user_id</c>…); the snapshot reads those first and the standard
/// OpenID Connect claims after them. Nothing the provider issued to the client itself, and no raw answer
/// of the provider, enters the snapshot.
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
    /// <item>the phone number is never taken: the channel declares none, and a number goes through the
    /// claim form.</item>
    /// </list>
    /// </summary>
    /// <param name="channelType">The channel the account signed in on.</param>
    /// <param name="principal">The principal of the OpenIddict client.</param>
    /// <param name="tenantId">Tenant of the transaction the sign-in is for; <c>null</c> — the default
    /// tenant.</param>
    /// <param name="capturedAt">Moment the snapshot is taken.</param>
    /// <returns>The snapshot; <c>null</c> — the provider stated no identifier of the account.</returns>
    public static ChannelIdentitySnapshot? Build(
        string channelType,
        ClaimsPrincipal principal,
        string? tenantId,
        DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(principal);

        var channelUserId = First(principal, ClaimTypes.NameIdentifier, OpenIddictConstants.Claims.Subject);
        if (channelUserId is null)
        {
            return null;
        }

        var name = First(principal, ClaimTypes.Name, OpenIddictConstants.Claims.Name);
        var email = First(principal, ClaimTypes.Email, OpenIddictConstants.Claims.Email);

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
            PhoneNumber = null,
            RawMetadata = null,
            CapturedAt = capturedAt,
            AdapterVersion = WebProviderAdapterConstants.AdapterVersion
        };
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
