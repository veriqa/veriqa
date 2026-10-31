// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Body of the server-to-server request creating a confirmation transaction (SPEC-039 C14).
/// Field names are snake_case, as on the status surface.
/// <para>
/// The transaction TYPE is not a field: this entry fixes it (C19). Neither is the idempotency SCOPE:
/// it is derived from the authenticated client, so a relying party cannot address someone else's
/// scope (R12). Unknown fields are ignored — the ordinary behaviour of the deserializer, and no
/// "unexpected field" check of our own.
/// </para>
/// </summary>
internal sealed class ConfirmationTransactionRequest
{
    /// <summary>
    /// Identifier of the declared confirmation message kind being confirmed. Required (R4).
    /// </summary>
    [JsonPropertyName("action_type")]
    public string? ActionType { get; init; }

    /// <summary>
    /// Caller slot name → value in the canonical textual form of its declared type. Optional; which
    /// individual slots are required is stated by the contract of the message kind, not here.
    /// </summary>
    [JsonPropertyName("slot_values")]
    public IReadOnlyDictionary<string, string>? SlotValues { get; init; }

    /// <summary>
    /// Language of the request (BCP 47). Optional; absent — the base language of the product.
    /// </summary>
    [JsonPropertyName("locale")]
    public string? Locale { get; init; }

    /// <summary>
    /// Time zone the moments of the message are shown in (IANA identifier, for example
    /// <c>Europe/Berlin</c>). Optional; absent — the default of the deployment, and without one the
    /// moments are shown in UTC with the marker that says so.
    /// </summary>
    [JsonPropertyName("time_zone")]
    public string? TimeZone { get; init; }

    /// <summary>
    /// UI configuration code. Optional; absent — the client's default record.
    /// </summary>
    [JsonPropertyName("ui_config")]
    public string? UiConfig { get; init; }

    /// <summary>
    /// Preferred channel type. Optional, existing creation semantics.
    /// </summary>
    [JsonPropertyName("requested_channel_type")]
    public string? RequestedChannelType { get; init; }

    /// <summary>
    /// Allowed channel types. Optional, existing creation semantics.
    /// </summary>
    [JsonPropertyName("allowed_channel_types")]
    public IReadOnlyList<string>? AllowedChannelTypes { get; init; }

    /// <summary>
    /// TTL override in seconds. Optional, within the existing TTL rules.
    /// </summary>
    [JsonPropertyName("ttl_seconds")]
    public int? TtlSeconds { get; init; }

    /// <summary>
    /// Idempotency key. Optional; a network retry of the relying party must not create a second
    /// transaction.
    /// </summary>
    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Client request identifier for end-to-end tracing. Optional.
    /// </summary>
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Arbitrary client data, not interpreted. Optional, existing size limit.
    /// </summary>
    [JsonPropertyName("client_context")]
    public string? ClientContext { get; init; }

    /// <summary>
    /// Expectations of the relying party about who is going to confirm: declared type name → expected
    /// value (SPEC-039 C23). Optional. A MAP rather than a list, because the contract admits no second
    /// value of the same type — which is also why there is no cap on the number of candidates.
    /// </summary>
    /// <remarks>
    /// Every name has to be a type the effective configuration declares for this application (C22),
    /// and every value has to have a canonical form under the normalization rule of that type; a
    /// refusal on either count rejects the whole operation before anything is written. What is stored
    /// is the protected canonical form, in the comparison container of the transaction and never in
    /// the snapshot — these values are shown to nobody and returned by nothing.
    /// </remarks>
    [JsonPropertyName("expected_identities")]
    public IReadOnlyDictionary<string, string>? ExpectedIdentities { get; init; }

    /// <summary>
    /// Whether the answer carries, besides the single way in, the list of deep-link entries of every
    /// channel offered (SPEC-039 C52). Optional; absent or <see langword="false"/> — no list.
    /// </summary>
    /// <remarks>
    /// It shapes the ANSWER and not the transaction, so it never reaches the creation request and
    /// takes no part in comparing the parameters of an idempotent repeat: a repeat gets the list by
    /// its own flag.
    /// </remarks>
    [JsonPropertyName("include_channel_entries")]
    public bool? IncludeChannelEntries { get; init; }
}

/// <summary>
/// The way in — what the relying party shows the person it is asking (SPEC-039 C20). A projection of
/// the entry material the sign-in window already builds, not a second kind of it: the contents of the
/// deep link, the binding of its lifetime to the transaction and the single confirmation it admits
/// are the ones that path has always had.
/// </summary>
internal sealed class ChannelEntry
{
    /// <summary>
    /// What the entry is — a value of <see cref="Constants.ChannelEntryKinds"/>.
    /// </summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>
    /// Channel the entry leads into; null when the channel is not chosen yet
    /// (<see cref="Constants.ChannelEntryKinds.PageUrl"/>).
    /// </summary>
    [JsonPropertyName("channel_type")]
    public string? ChannelType { get; init; }

    /// <summary>
    /// Name of the channel to label the entry with — the name the tab of the sign-in page shows: the
    /// label a third-party adapter declared, the brand name of a built-in channel, otherwise the raw
    /// channel type. Not localized. Null exactly when <see cref="ChannelType"/> is null.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    /// <summary>
    /// The address to show: the deep link of the channel, or the absolute URL of the entry page.
    /// </summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>
    /// The same address as a PNG data URI, for showing it to a second device. The image carries the
    /// attribution mark in a strip under the code (SPEC-015 §4.18), so it is not square. The contract
    /// admits null — nothing rendered for this entry; the shipped builder renders an image for both kinds
    /// of entry and leaves null only where the address does not fit into a QR code.
    /// </summary>
    [JsonPropertyName("qr")]
    public string? Qr { get; init; }

    /// <summary>
    /// Moment after which this entry must not be shown any more (C21).
    /// </summary>
    [JsonPropertyName("valid_until")]
    public required DateTimeOffset ValidUntil { get; init; }
}

/// <summary>
/// Body of the 201 answer of the confirmation creation entry (SPEC-039 C14).
/// </summary>
internal sealed class ConfirmationTransactionResponse
{
    /// <summary>
    /// Public identifier of the transaction — the same value the status surfaces accept.
    /// </summary>
    [JsonPropertyName("transaction_id")]
    public required string TransactionId { get; init; }

    /// <summary>
    /// Moment the transaction expires (ISO 8601, UTC).
    /// </summary>
    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Moment after which THIS ANSWER is no longer current: the earliest of the transaction's own
    /// deadline and the lifetimes of every entry it carries — the single way in and, when requested,
    /// each element of <see cref="ChannelEntries"/> (C21, C52).
    /// </summary>
    /// <remarks>
    /// A separate field from <see cref="ExpiresAt"/> and never a substitute for it. Today no entry
    /// material expires before its transaction does, so the two values coincide — which is a fact
    /// about today's entries and not a reason to collapse two different questions ("when does the
    /// operation end" and "when does what I am showing stop working") into one field.
    /// </remarks>
    [JsonPropertyName("response_valid_until")]
    public required DateTimeOffset ResponseValidUntil { get; init; }

    /// <summary>
    /// The way in, for the relying party to show (C20).
    /// </summary>
    [JsonPropertyName("channel_entry")]
    public required ChannelEntry ChannelEntry { get; init; }

    /// <summary>
    /// Deep-link entries of the channels offered, in the order of the channel display (SPEC-039 C52).
    /// An addition to <see cref="ChannelEntry"/>, never its replacement.
    /// </summary>
    /// <remarks>
    /// Null — not requested, and the field is absent from the JSON, so a relying party that does not
    /// ask keeps the field set it always had. Requested — the field is always present, an empty list
    /// included (no channel with an addressable point, or the question configured onto the core's page).
    /// </remarks>
    [JsonPropertyName("channel_entries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ChannelEntry>? ChannelEntries { get; init; }
}
