// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.Contracts;

namespace Veriqa.Sample.DotNet.Showcase.StreamingTv;

/// <summary>Addresses the sample serves, in one place instead of as literals along the routes.</summary>
public static class SampleRoutes
{
    /// <summary>Where the OpenID Connect handler receives the authorization response.</summary>
    public const string CallbackPath = "/signin-oidc";

    /// <summary>Starts the sign-in.</summary>
    public const string SignIn = "/signin";

    /// <summary>Ends this application's session (not the issuer's).</summary>
    public const string SignOut = "/signout";

    /// <summary>
    /// Where a sign-in that ended in an error returns to: the page itself, with the mark its script
    /// reads to say so. The page is anonymous and starts no sign-in by itself.
    /// </summary>
    public const string SignInFailed = "/?signin=failed";

    /// <summary>Who is watching, and whether they have been welcomed.</summary>
    public const string Session = "/api/session";

    /// <summary>Marks the welcome screen as seen.</summary>
    public const string WelcomeSeen = "/api/session/welcome-seen";

    /// <summary>The catalogue as this viewer sees it.</summary>
    public const string Catalog = "/api/catalog";

    /// <summary>Starts buying a title.</summary>
    public const string Purchase = "/api/catalog/{titleId}/purchase";

    /// <summary>State of a purchase in flight.</summary>
    public const string PurchaseResult = "/api/purchases/{transactionId}";

    /// <summary>The single page of the interface, inside the web root.</summary>
    public const string IndexFileName = "index.html";

    /// <summary>Content type of that page.</summary>
    public const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>Content type a refusal of Veriqa is passed through with.</summary>
    public const string JsonContentType = "application/json";
}

/// <summary>Scopes the sample asks for beyond the ones the handler adds itself.</summary>
public static class SampleScopes
{
    /// <summary>Standard OIDC scope gating the <c>phone_number</c> claim.</summary>
    public const string Phone = "phone";

    /// <summary>
    /// The sample's own scope, declared in the <c>Veriqa:ScopesClaims</c> catalog of
    /// <c>appsettings.json</c>: it gates <see cref="SampleClaims.PreferredGenre"/>.
    /// </summary>
    public const string Preferences = "preferences";
}

/// <summary>
/// The two <c>ui_config</c> records of the sign-in client, declared in <c>appsettings.json</c>
/// (<c>Veriqa:UiConfigurations:Records</c>) and listed in the client's <c>AllowedUiConfigs</c>. They
/// differ in one member, <c>Channels</c>: which channels the sign-in page offers, and in what order.
/// The names say nothing about a language on purpose — the code is visible in the address.
/// </summary>
public static class SampleUiConfigs
{
    /// <summary>The authorize parameter that names the record.</summary>
    public const string ParameterName = "ui_config";

    /// <summary>The record with MAX: named when the browser's first language is <see cref="SignInALanguage"/>.</summary>
    public const string SignInA = "nova-sign-in-a";

    /// <summary>The record with Instagram and Messenger: the client's default, named in every other case.</summary>
    public const string SignInB = "nova-sign-in-b";

    /// <summary>Primary language subtag that selects <see cref="SignInA"/>.</summary>
    public const string SignInALanguage = "ru";

    /// <summary>Separates the primary subtag of a language tag from the rest: <c>ru-RU</c>.</summary>
    public const char SubtagSeparator = '-';
}

/// <summary>Claim names the sample reads that are not part of the Veriqa claim contract.</summary>
public static class SampleClaims
{
    /// <summary>Standard OIDC subject claim — the viewer this session belongs to.</summary>
    public const string Subject = "sub";

    /// <summary>
    /// Standard OIDC date of birth, <c>YYYY-MM-DD</c> (OIDC Core §5.1). No channel gives it: the
    /// catalog of <c>appsettings.json</c> declares it under <c>profile</c> and Veriqa collects it on
    /// its form inside the sign-in.
    /// </summary>
    public const string Birthdate = "birthdate";

    /// <summary>
    /// The sample's own claim: the genre the viewer named as their favourite, collected the same way
    /// under the <see cref="SampleScopes.Preferences"/> scope. Absent when the viewer skipped it.
    /// </summary>
    public const string PreferredGenre = "preferred_genre";
}

/// <summary>Values of the <c>error</c> member the API answers refusals with.</summary>
public static class SampleErrors
{
    /// <summary>Nobody is signed in.</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>This viewer already owns the title.</summary>
    public const string AlreadyOwned = "already_owned";

    /// <summary>The title has a minimum age this viewer is below, or their age is not known.</summary>
    public const string AgeRestricted = "age_restricted";

    /// <summary>The session carries no channel identity to address a step-up to.</summary>
    public const string ChannelIdentityUnavailable = "channel_identity_unavailable";
}

/// <summary>Caller slots of the <c>purchase-title</c> action, as its contract declares them.</summary>
public static class SampleSlots
{
    /// <summary>Name of the title being bought.</summary>
    public const string Title = "title";

    /// <summary>Its price, currency included.</summary>
    public const string Price = "price";
}

/// <summary>
/// The comparable identity types the backend client declares in <c>IdentityMatchComparableTypes</c>,
/// one per channel the sample registers. All six compare the same claim — <c>channel_user_id</c> —
/// and differ only in the normalisation rule that suits the channel's way of writing an identity.
/// <para>
/// The map lives here, as one table, because a confirmation that names a type the client did not
/// declare is refused with <c>candidate_type_undeclared</c>: the names in this file and the names in
/// <c>appsettings.json</c> are two halves of one declaration.
/// </para>
/// </summary>
public static class SampleIdentityTypes
{
    /// <summary>Telegram: the channel user id, compared exactly.</summary>
    public const string TelegramUserId = "telegram_user_id";

    /// <summary>WhatsApp: the business-scoped user id (BSUID), compared exactly.</summary>
    public const string WhatsAppUserId = "whatsapp_user_id";

    /// <summary>MAX: the channel user id, compared exactly.</summary>
    public const string MaxUserId = "max_user_id";

    /// <summary>Instagram: the Instagram-scoped user id, compared exactly.</summary>
    public const string InstagramUserId = "instagram_user_id";

    /// <summary>Messenger: the page-scoped user id, compared exactly.</summary>
    public const string MessengerUserId = "messenger_user_id";

    /// <summary>Email: the address, compared with a case-folded domain.</summary>
    public const string EmailAddress = "email_address";

    private static readonly Dictionary<string, string> ByChannel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["telegram"] = TelegramUserId,
        ["whatsapp"] = WhatsAppUserId,
        ["max"] = MaxUserId,
        ["instagram"] = InstagramUserId,
        ["messenger"] = MessengerUserId,
        ["email"] = EmailAddress
    };

    /// <summary>The declared type for a channel, or null for a channel this sample does not register.</summary>
    /// <param name="channelType">Value of the <see cref="VeriqaClaimTypes.ChannelType"/> claim.</param>
    public static string? ForChannel(string channelType) => ByChannel.GetValueOrDefault(channelType);
}

/// <summary>Answer of <c>GET /api/session</c>.</summary>
/// <param name="Authenticated">Whether anybody is signed in.</param>
/// <param name="WelcomeSeen">Whether this viewer has already seen the welcome screen.</param>
/// <param name="Profile">The card of the viewer, or null when nobody is signed in.</param>
public sealed record SessionResponse(bool Authenticated, bool WelcomeSeen, ViewerProfile? Profile);

/// <summary>Answer of <c>GET /api/catalog</c>.</summary>
/// <param name="Titles">The catalogue as this viewer sees it: the titles of their favourite genre first,
/// their own titles marked as owned, the ones their age closes marked as restricted.</param>
public sealed record CatalogResponse(IReadOnlyList<CatalogTitleView> Titles);

/// <summary>Answer of <c>POST /api/catalog/{titleId}/purchase</c>: the way into the channel.</summary>
/// <param name="TransactionId">The confirmation transaction to poll.</param>
/// <param name="Qr">The way into the channel as a PNG data URI — put it straight into an img src. The
/// deep link itself is not passed on: the page offers the QR alone, not a link to open the bot.</param>
/// <param name="ChannelType">Channel the confirmation was addressed to.</param>
public sealed record PurchaseStartedResponse(string TransactionId, string? Qr, string? ChannelType);

/// <summary>A refusal the page can act on.</summary>
/// <param name="Error">One of <see cref="SampleErrors"/>.</param>
public sealed record ErrorResponse(string Error);
