// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WebProviders.Constants;

/// <summary>
/// Constants of the adapter of the web provider channels: its version, the claims of a provider read
/// beside the standard ones and the messages of its refusals. A message names no value of the user.
/// </summary>
internal static class WebProviderAdapterConstants
{
    /// <summary>
    /// Version of the adapter recorded in the identity snapshot.
    /// </summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>
    /// Claim of the first name of the providers that state it under this name (VK ID, Yandex) rather
    /// than as <c>given_name</c>.
    /// </summary>
    public const string FirstNameClaim = "first_name";

    /// <summary>
    /// Claim of the last name of the providers that state it under this name (VK ID, Yandex) rather
    /// than as <c>family_name</c>.
    /// </summary>
    public const string LastNameClaim = "last_name";

    /// <summary>
    /// Claim of the address of the avatar of the providers that state it under this name (VK ID)
    /// rather than as <c>picture</c>.
    /// </summary>
    public const string AvatarClaim = "avatar";

    /// <summary>
    /// Value type OpenIddict gives a claim it made of a JSON object of the answer of the provider; the
    /// value of such a claim is the raw JSON. OpenIddict publishes no constant of it.
    /// </summary>
    public const string JsonObjectClaimValueType = "JSON";

    /// <summary>
    /// Value type OpenIddict gives a claim it made of a JSON array nested in an array of the answer.
    /// </summary>
    public const string JsonArrayClaimValueType = "JSON_ARRAY";

    /// <summary>
    /// Value type OpenIddict gives a claim it made of a JSON <c>null</c> of the answer.
    /// </summary>
    public const string JsonNullClaimValueType = "JSON_NULL";

    /// <summary>
    /// Message of the refusal of an event the channel has no webhook for.
    /// </summary>
    public const string NoInboundEventsMessage = "A web provider channel takes no webhook events: its events come from its own routes.";

    /// <summary>
    /// Message of the refusal of a question for a transaction the store of transactions does not hold.
    /// </summary>
    public const string TransactionNotFoundMessage = "The transaction of the question is not found.";

    /// <summary>
    /// Message of the refusal of a message to an account without a live session of the page.
    /// </summary>
    public const string NoLivePageSessionMessage = "The account has no live session of the page of the channel; the message is not delivered.";

    /// <summary>
    /// Message of the refusal of a link to the claim form without the content of the link.
    /// </summary>
    public const string ClaimFormLinkWithoutContentMessage = "The message is a link to the claim form without the content of the link.";

    /// <summary>
    /// Message of the refusal of a link to the claim form whose address is not an absolute web one.
    /// </summary>
    public const string ClaimFormLinkNotWebAddressMessage = "The message is a link to the claim form whose address is not an absolute http or https one.";

    /// <summary>
    /// Message of the refusal of an action message without a button the page can draw.
    /// </summary>
    public const string ActionButtonsWithoutButtonsMessage = "The message is an action message without a button that is a web link or an item of the menu.";

    /// <summary>
    /// Message of the refusal of a message of a kind the channel does not declare.
    /// </summary>
    public const string UnsupportedMessageKindMessage = "The channel does not carry messages of this kind.";

    /// <summary>
    /// Message of the refusal of an operation the state of the channel could not be stored or read for.
    /// </summary>
    public const string StateStoreFailedMessage = "The state of the page of the channel could not be stored.";

    /// <summary>
    /// Opening of the details of an unhealthy channel; the reason follows.
    /// </summary>
    public const string ServerConfigurationUnavailableMessage = "The configuration of the provider could not be retrieved: ";
}
