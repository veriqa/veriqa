// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// OIDC request context passed when creating a transaction.
/// It is a request parameter (value object), not a domain object.
/// Stored in the transaction for use in the callback flow.
/// </summary>
public sealed class OidcContext
{
    /// <summary>
    /// Client redirect URI.
    /// </summary>
    [JsonPropertyName("redirect_uri")]
    public string? RedirectUri { get; init; }

    /// <summary>
    /// OIDC state parameter.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>
    /// PKCE code challenge.
    /// </summary>
    [JsonPropertyName("code_challenge")]
    public string? CodeChallenge { get; init; }

    /// <summary>
    /// PKCE method (S256).
    /// </summary>
    [JsonPropertyName("code_challenge_method")]
    public string? CodeChallengeMethod { get; init; }

    /// <summary>
    /// OIDC nonce.
    /// </summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; init; }

    /// <summary>
    /// Requested scopes.
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>
    /// OIDC client identifier.
    /// </summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    /// <summary>
    /// Tenant attribution of the request, resolved from <see cref="ClientId"/> at creation.
    /// Null — the tenant level is not stated (the default implicit tenant of a self-hosted install).
    /// Additive field: old serialized transactions deserialize with null (behavior 1:1).
    /// </summary>
    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }

    /// <summary>
    /// Display name of the application, taken at creation from the entry of <see cref="ClientId"/> (its
    /// <c>DisplayName</c>, else the identifier itself) — what the application slot of the texts shows.
    /// Additive field: old serialized transactions deserialize with null, and readers fall back to the
    /// identifier.
    /// </summary>
    [JsonPropertyName("application_name")]
    public string? ApplicationName { get; init; }

    /// <summary>
    /// Brand name the levels of the transaction stated at creation (<c>AuthPageDesign.BrandName</c>), as
    /// configured — a Natural Key, translated where a text shows it. Null — no level states one. Additive field.
    /// </summary>
    [JsonPropertyName("brand_name")]
    public string? BrandName { get; init; }

    /// <summary>
    /// Whether the resolution of the tenant of <see cref="ClientId"/> failed at creation: the resolver threw,
    /// and <see cref="TenantId"/> is null because nothing answered, not because the client states no tenant.
    /// The sign-in goes on as before; only the collected claims store stays untouched on this transaction
    /// (SPEC-041 E62), since a null tenant would key the records of another tenant.
    /// Additive field: old serialized transactions deserialize with false (behavior 1:1).
    /// </summary>
    [JsonPropertyName("tenant_resolution_failed")]
    public bool TenantResolutionFailed { get; init; }

    /// <summary>
    /// Response type (code).
    /// </summary>
    [JsonPropertyName("response_type")]
    public string? ResponseType { get; init; }

    /// <summary>
    /// Response mode the relying party asked the authorization response to be delivered in
    /// (<c>query</c>, <c>fragment</c> or <c>form_post</c>); null — the party stated none and the
    /// default of the response type applies. The value is carried on the transaction because the
    /// callback returns the browser to <c>/connect/authorize</c> by rebuilding the request from this
    /// context: a parameter absent here is a parameter the second pass never sees.
    /// Additive field: old serialized transactions deserialize with null (behavior 1:1).
    /// </summary>
    [JsonPropertyName("response_mode")]
    public string? ResponseMode { get; init; }

    /// <summary>
    /// CSRF protection for the OIDC callback.
    /// </summary>
    [JsonPropertyName("browser_nonce")]
    public string? BrowserNonce { get; init; }

    /// <summary>
    /// UI configuration code selecting the template and channel set (SPEC-002 section 4.6).
    /// </summary>
    [JsonPropertyName("ui_config_code")]
    public string? UiConfigCode { get; init; }

    /// <summary>
    /// Locale of the sign-in page (IETF tag) resolved at /connect/authorize: the language the page was
    /// rendered in, carrying whatever subtags the relying party stated for it — a region (<c>en-DE</c>),
    /// a script (<c>zh-Hans</c>) or none at all (<c>en</c>), a region only when one was asked for
    /// (<c>ui_locales</c> first, else <c>Accept-Language</c>). Fallback source for the recipient-locale
    /// chain (channel → this → base locale). A reader that needs the LANGUAGE alone clamps the tag
    /// through the language registry and gets that same page language back; a reader that formats a
    /// moment keeps the tag whole, because a region among its subtags is what decides the conventions.
    /// Additive field: old serialized transactions deserialize with null (behavior 1:1).
    /// </summary>
    [JsonPropertyName("ui_locale")]
    public string? UiLocale { get; init; }

    /// <summary>
    /// Time zone the moments of this sign-in are shown in (IANA identifier, for example
    /// <c>Europe/Berlin</c>); null — the zone is not known and a moment is shown in UTC, said aloud
    /// by its marker.
    /// Additive field: old serialized transactions deserialize with null (behavior 1:1).
    /// </summary>
    [JsonPropertyName("ui_time_zone")]
    public string? UiTimeZone { get; init; }
}
