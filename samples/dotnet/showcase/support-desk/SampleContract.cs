// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Authentication;

using Veriqa.Core.Contracts;

namespace Veriqa.Sample.DotNet.Showcase.SupportDesk;

/// <summary>Addresses the sample serves, in one place instead of as literals along the routes.</summary>
public static class SampleRoutes
{
    /// <summary>Where the OpenID Connect handler receives the authorization response.</summary>
    public const string CallbackPath = "/signin-oidc";

    /// <summary>Starts the sign-in.</summary>
    public const string SignIn = "/signin";

    /// <summary>Starts the authorize request that confirms the customer's phone number.</summary>
    public const string VerifyPhone = "/verify-phone";

    /// <summary>Starts the authorize request that confirms the customer's e-mail address.</summary>
    public const string VerifyEmail = "/verify-email";

    /// <summary>Ends this application's session (not the issuer's).</summary>
    public const string SignOut = "/signout";

    /// <summary>
    /// Where a sign-in that ended in an error returns to: the page itself, with the mark its script
    /// reads to say so. The page is anonymous and starts no sign-in by itself.
    /// </summary>
    public const string SignInFailed = "/?signin=failed";

    /// <summary>Who the customer is.</summary>
    public const string Session = "/api/session";

    /// <summary>The tickets of this customer.</summary>
    public const string Tickets = "/api/tickets";

    /// <summary>Starts resolving or deleting a ticket.</summary>
    public const string Resolution = "/api/tickets/{ticketId}/resolution";

    /// <summary>State of an operation in flight.</summary>
    public const string ResolutionResult = "/api/tickets/resolutions/{transactionId}";

    /// <summary>Asks the desk to get in touch about a ticket, by a callback or by e-mail.</summary>
    public const string ContactRequest = "/api/tickets/{ticketId}/contact-request";

    /// <summary>The single page of the interface, inside the web root.</summary>
    public const string IndexFileName = "index.html";

    /// <summary>Content type of that page.</summary>
    public const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>Content type a refusal of Veriqa is passed through with.</summary>
    public const string JsonContentType = "application/json";
}

/// <summary>
/// Standard OIDC scopes the sample asks for beyond the ones the handler adds itself. Neither is part
/// of the sign-in: each travels only in the request that confirms its own contact.
/// </summary>
public static class SampleScopes
{
    /// <summary>Standard OIDC scope gating the <c>phone_number</c> claim.</summary>
    public const string Phone = "phone";

    /// <summary>Standard OIDC scope gating the <c>email</c> claim.</summary>
    public const string Email = "email";
}

/// <summary>
/// Names of the <c>claims</c> request parameter (OIDC Core 1.0 §5.5) and of the members the sample
/// writes into it: <c>claims={"id_token":{"phone_number":{"essential":true}}}</c>.
/// </summary>
public static class SampleAuthorizeParameters
{
    /// <summary>The authorize parameter that requests individual claims.</summary>
    public const string Claims = "claims";

    /// <summary>Member of the parameter naming the claims wanted in the ID token.</summary>
    public const string IdToken = "id_token";

    /// <summary>Member of one requested claim that marks it as required rather than voluntary.</summary>
    public const string Essential = "essential";
}

/// <summary>
/// The two <c>ui_config</c> records of the sign-in client, declared in <c>appsettings.json</c>
/// (<c>Veriqa:UiConfigurations:Records</c>) and listed in the client's <c>AllowedUiConfigs</c>. They
/// differ in one member, <c>Channels</c>: which channels the sign-in page offers, and in what order.
/// The names say nothing about a language on purpose — the code is visible in the address. A contact request names
/// the same record as the sign-in.
/// </summary>
public static class SampleUiConfigs
{
    /// <summary>The authorize parameter that names the record.</summary>
    public const string ParameterName = "ui_config";

    /// <summary>The record with MAX: named when the browser's first language is <see cref="SignInALanguage"/>.</summary>
    public const string SignInA = "helio-sign-in-a";

    /// <summary>The record with Instagram and Messenger: the client's default, named in every other case.</summary>
    public const string SignInB = "helio-sign-in-b";

    /// <summary>Primary language subtag that selects <see cref="SignInA"/>.</summary>
    public const string SignInALanguage = "ru";

    /// <summary>Separates the primary subtag of a language tag from the rest: <c>ru-RU</c>.</summary>
    public const char SubtagSeparator = '-';
}

/// <summary>Claim names the sample reads that are not part of the Veriqa claim contract.</summary>
public static class SampleClaims
{
    /// <summary>Standard OIDC subject claim — the customer this session belongs to.</summary>
    public const string Subject = "sub";

    /// <summary>
    /// The phone number this session has accepted. A claim of the sample's own cookie, never issued
    /// by Veriqa: its presence means the number came back from a contact request together with
    /// <c>phone_number_verified: true</c>.
    /// </summary>
    public const string VerifiedPhone = "helio_verified_phone";

    /// <summary>The e-mail address this session has accepted; the counterpart of <see cref="VerifiedPhone"/>.</summary>
    public const string VerifiedEmail = "helio_verified_email";
}

/// <summary>Values of the <c>error</c> member the API answers refusals with.</summary>
public static class SampleErrors
{
    /// <summary>Nobody is signed in.</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>The body named an operation the desk does not have.</summary>
    public const string UnknownAction = "unknown_action";

    /// <summary>The ticket is closed already.</summary>
    public const string AlreadyResolved = "already_resolved";

    /// <summary>The session carries no channel identity to address a step-up to.</summary>
    public const string ChannelIdentityUnavailable = "channel_identity_unavailable";

    /// <summary>The body named a way of getting in touch the desk does not have.</summary>
    public const string UnknownMethod = "unknown_method";

    /// <summary>The contact the chosen way of getting in touch needs has not been confirmed in this session.</summary>
    public const string ContactUnverified = "contact_unverified";
}

/// <summary>
/// The two operations, as the browser spells them in a request body and as they travel on the wire.
/// The same two strings are what <see cref="TicketAction"/> serialises to — one spelling, declared
/// once, instead of a literal in the parser and another in the enumeration.
/// </summary>
public static class SampleActionNames
{
    /// <summary>Close the ticket.</summary>
    public const string Resolve = "resolve";

    /// <summary>Remove the ticket from the list for good.</summary>
    public const string Delete = "delete";
}

/// <summary>
/// The two ways the desk can get in touch, as the browser spells them in a request body and as they
/// travel on the wire — the same strings <see cref="ContactMethod"/> serialises to.
/// </summary>
public static class SampleContactMethods
{
    /// <summary>Call the customer back.</summary>
    public const string Callback = "callback";

    /// <summary>Answer the customer by e-mail.</summary>
    public const string Email = "email";
}

/// <summary>
/// One contact the customer can confirm, and everything that differs between the two: what the
/// authorize request asks for, what the answer has to carry, and where the session keeps it.
/// </summary>
/// <param name="Scope">The scope the contact request adds to the scopes of the sign-in.</param>
/// <param name="ClaimType">The claim requested as essential, and read from the answer.</param>
/// <param name="VerifiedClaimType">The claim that has to say <c>true</c> for the value to be accepted.</param>
/// <param name="SessionClaimType">The claim of the sample's cookie the accepted value is kept under.</param>
public sealed record VerifiableContact(
    string Scope,
    string ClaimType,
    string VerifiedClaimType,
    string SessionClaimType);

/// <summary>The two contacts, and the mark by which a return is recognised as a contact request.</summary>
public static class SampleContacts
{
    /// <summary>
    /// Key of the authentication-properties item that marks an authorize request as a contact
    /// request; its value is the <see cref="VerifiableContact.ClaimType"/> asked for. The item
    /// travels in the protected <c>state</c>, so it comes back with the answer and cannot be forged.
    /// </summary>
    public const string RequestedItem = "helio.contact";

    /// <summary>The phone number: opens the callback.</summary>
    public static readonly VerifiableContact Phone = new(
        SampleScopes.Phone,
        VeriqaClaimTypes.PhoneNumber,
        VeriqaClaimTypes.PhoneNumberVerified,
        SampleClaims.VerifiedPhone);

    /// <summary>The e-mail address: opens the answer by e-mail.</summary>
    public static readonly VerifiableContact Email = new(
        SampleScopes.Email,
        VeriqaClaimTypes.Email,
        VeriqaClaimTypes.EmailVerified,
        SampleClaims.VerifiedEmail);

    /// <summary>The contact a request was started for, or null for an ordinary sign-in.</summary>
    /// <param name="properties">Properties of the authorize request or of its answer.</param>
    public static VerifiableContact? Requested(AuthenticationProperties? properties)
    {
        if (properties is null || !properties.Items.TryGetValue(RequestedItem, out var claimType))
        {
            return null;
        }

        if (string.Equals(claimType, Phone.ClaimType, StringComparison.Ordinal))
        {
            return Phone;
        }

        return string.Equals(claimType, Email.ClaimType, StringComparison.Ordinal) ? Email : null;
    }
}

/// <summary>Caller slots both ticket actions declare in their contracts.</summary>
public static class SampleSlots
{
    /// <summary>Identifier of the ticket, as the customer sees it.</summary>
    public const string Ticket = "ticket";

    /// <summary>Its one-line summary.</summary>
    public const string Subject = "subject";
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
/// <param name="Profile">The card of the customer, or null when nobody is signed in.</param>
/// <param name="Contacts">Which contacts this session has accepted; both false when nobody is signed in.</param>
public sealed record SessionResponse(bool Authenticated, ViewerProfile? Profile, SessionContacts Contacts);

/// <summary>
/// Which contacts the session has accepted. The page draws its buttons from these two flags; the
/// gate itself is the API, which refuses whatever the page showed.
/// </summary>
/// <param name="PhoneVerified">A phone number was accepted: the callback is open.</param>
/// <param name="EmailVerified">An e-mail address was accepted: the answer by e-mail is open.</param>
public sealed record SessionContacts(bool PhoneVerified, bool EmailVerified)
{
    /// <summary>Reads the flags from the session; an anonymous visitor has neither.</summary>
    /// <param name="user">The visitor.</param>
    public static SessionContacts FromClaims(ClaimsPrincipal user) =>
        new(ViewerProfile.Value(user, SampleContacts.Phone.SessionClaimType) is not null,
            ViewerProfile.Value(user, SampleContacts.Email.SessionClaimType) is not null);
}

/// <summary>Answer of <c>GET /api/tickets</c>.</summary>
/// <param name="Tickets">The tickets of this customer, newest first.</param>
public sealed record TicketsResponse(IReadOnlyList<Ticket> Tickets);

/// <summary>
/// Body of <c>POST /api/tickets/{ticketId}/resolution</c>. The action arrives as a STRING rather
/// than as the enumeration: a body naming an operation the desk does not have must be answered
/// <c>400 unknown_action</c>, and a binder told to parse an enumeration would refuse it first, with
/// a shape of its own.
/// </summary>
/// <param name="Action">Either <c>resolve</c> or <c>delete</c>.</param>
public sealed record ResolutionRequest([property: JsonPropertyName("action")] string? Action);

/// <summary>Answer of <c>POST /api/tickets/{ticketId}/resolution</c>: the way into the channel.</summary>
/// <param name="TransactionId">The confirmation transaction to poll.</param>
/// <param name="Qr">The way into the channel as a PNG data URI — put it straight into an img src. The
/// page shows it where the confirming phone is another device.</param>
/// <param name="Url">The same way in as a link. The page offers it as a button on a phone, where the
/// person confirms on the very device and a QR could not be scanned.</param>
/// <param name="Action">The operation awaiting confirmation.</param>
/// <param name="ChannelType">Channel the confirmation was addressed to.</param>
public sealed record ResolutionStartedResponse(
    string TransactionId,
    string? Qr,
    string Url,
    TicketAction Action,
    string? ChannelType);

/// <summary>
/// Body of <c>POST /api/tickets/{ticketId}/contact-request</c>. A string for the same reason as
/// <see cref="ResolutionRequest"/>: an unknown method is answered <c>400 unknown_method</c> by the
/// sample, not refused by the binder.
/// </summary>
/// <param name="Method">Either <c>callback</c> or <c>email</c>.</param>
public sealed record ContactRequestBody([property: JsonPropertyName("method")] string? Method);

/// <summary>Answer of <c>POST /api/tickets/{ticketId}/contact-request</c>.</summary>
/// <param name="TicketId">The ticket the desk will get in touch about.</param>
/// <param name="Method">How.</param>
/// <param name="Destination">Where to: the masked phone number for a callback, the address for an e-mail.</param>
public sealed record ContactRequestResponse(string TicketId, ContactMethod Method, string Destination);

/// <summary>A refusal the page can act on.</summary>
/// <param name="Error">One of <see cref="SampleErrors"/>.</param>
public sealed record ErrorResponse(string Error);
