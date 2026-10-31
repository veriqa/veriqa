// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Protocol constants of the Veriqa OIDC integration.
/// </summary>
public static class OidcConstants
{
    /// <summary>
    /// Cookie name for the browser nonce (protection against login CSRF / session fixation).
    /// </summary>
    public const string BrowserNonceCookieName = "veriqa_browser_nonce";

    /// <summary>
    /// Cookie name for intermediate authentication (callback → authorize).
    /// </summary>
    public const string AuthCookieName = "veriqa_auth";

    /// <summary>
    /// Name of the Cookie authentication scheme for the intermediate callback flow.
    /// </summary>
    public const string CookieAuthScheme = "Veriqa.Cookie";

    /// <summary>
    /// Name of the session_id parameter in the query string.
    /// </summary>
    public const string SessionIdParameterName = "session_id";

    /// <summary>
    /// Name of the ui_config parameter in the OIDC request.
    /// </summary>
    public const string UiConfigParameterName = "ui_config";

    /// <summary>
    /// Name of the time_zone parameter in the OIDC request: the zone the relying party states for
    /// the user, in which the moments of the messages of this sign-in are shown.
    /// </summary>
    public const string TimeZoneParameterName = "time_zone";

    /// <summary>
    /// Prefix of acr_values values for requesting a specific authentication channel (TASK-005).
    /// Example: "channel:telegram" — request authentication via Telegram.
    /// </summary>
    public const string AcrValuesChannelPrefix = "channel:";

    /// <summary>
    /// Prefix of OpenIddict internal claims (oi_*).
    /// Claims with this prefix are filtered out of the UserInfo response.
    /// </summary>
    public const string OpenIddictInternalClaimPrefix = "oi_";

    /// <summary>
    /// Lifetime of the intermediate authentication cookie (in minutes).
    /// Determines how long the cookie between callback and authorize remains valid.
    /// </summary>
    public const int AuthCookieExpireMinutes = 5;

    /// <summary>
    /// Name of the InMemory database for OpenIddict (used in dev mode).
    /// </summary>
    public const string InMemoryDatabaseName = "VeriqaOpenIddict";
}
