// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

using Veriqa.Core.Contracts;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// User data received from the channel upon confirmation.
/// Filled in by Channel Adapter → Engine on the transition to Confirmed.
/// Immutable once written. Maximum size: 128 KB (validated in TransactionService).
/// Corresponds to SPEC-003 §5.2.
/// </summary>
public sealed class ChannelIdentitySnapshot
{
    /// <summary>
    /// Frozen copy of the additional claims (see <see cref="AdditionalClaims"/>).
    /// </summary>
    private readonly IReadOnlyDictionary<string, string>? _additionalClaims;

    /// <summary>
    /// Backing fields of the required string properties, whose init accessors reject a null
    /// (see <see cref="RequireValue"/>). The <c>null!</c> initializer stands in for the compiler's
    /// "field never assigned" analysis: assignment is guaranteed by <c>required</c> on the property.
    /// </summary>
    private readonly string _channelType = null!;
    private readonly string _channelUserId = null!;
    private readonly string _displayName = null!;
    private readonly string _adapterVersion = null!;

    /// <summary>
    /// Avatar accepted on assignment (see <see cref="AvatarUrl"/>).
    /// </summary>
    private readonly string? _avatarUrl;

    /// <summary>
    /// Tenant the confirming channel belongs to. Null — the default implicit tenant of a self-hosted
    /// installation. It is the third dimension of the identity key: two tenants seeing the same
    /// <see cref="ChannelUserId"/> keep separate records and separate rate-limit budgets, because they
    /// are separate resources — not because the tenant is an access boundary.
    /// The core sets it on every path an inbound event reaches the core by, and the value the adapter
    /// wrote is not used on any of them (SPEC-003 CA-164). In today's delivery the core takes the tenant
    /// of the event — the owner of the channel registration, otherwise the tenant of the webhook route
    /// segment or of the polling loop; without one, the tenant of the transaction the event addresses,
    /// from the moment that transaction is read; without either, null. An adapter value that differs
    /// from the one chosen is reported by a warning in the log. Additive field: old serialized snapshots
    /// deserialize with null.
    /// </summary>
    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }

    /// <summary>
    /// Channel type through which the confirmation happened.
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.ChannelType)]
    public required string ChannelType
    {
        get => _channelType;
        init => _channelType = RequireValue(value, nameof(ChannelType));
    }

    /// <summary>
    /// Bot flag: true if the source is a bot rather than a real user.
    /// Used for protective filtering in the Transaction Engine.
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.IsBot)]
    public required bool IsBot { get; init; }

    /// <summary>
    /// User identifier in the channel.
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.ChannelUserId)]
    public required string ChannelUserId
    {
        get => _channelUserId;
        init => _channelUserId = RequireValue(value, nameof(ChannelUserId));
    }

    /// <summary>
    /// Display name of the user in the channel.
    /// </summary>
    [JsonPropertyName("display_name")]
    public required string DisplayName
    {
        get => _displayName;
        init => _displayName = RequireValue(value, nameof(DisplayName));
    }

    /// <summary>
    /// User's first name (if available from the channel).
    /// </summary>
    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    /// <summary>
    /// User's last name (if available from the channel).
    /// </summary>
    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }

    /// <summary>
    /// User's username in the channel (if available).
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>
    /// User's email address (if available from the channel).
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.Email)]
    public string? Email { get; init; }

    /// <summary>
    /// Whether the channel verified that the user controls <see cref="Email"/>.
    /// A typed field rather than a free-form claim: relying parties make security decisions on
    /// <c>email_verified</c>, so the fact is stated by the contract and visible on review.
    /// Null — the channel makes no statement, and the core issues no claim (CA-021). Stated without a
    /// non-empty <see cref="Email"/>, it verifies no address, and the core issues no claim either.
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.EmailVerified)]
    public bool? EmailVerified { get; init; }

    /// <summary>
    /// Phone number (if available from the channel).
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.PhoneNumber)]
    public string? PhoneNumber { get; init; }

    /// <summary>
    /// Whether the channel proved that <see cref="PhoneNumber"/> belongs to the user (OIDC Core 1.0
    /// section 5.1, <c>phone_number_verified</c>). A typed field for the reason
    /// <see cref="EmailVerified"/> is one: relying parties make security decisions on it.
    /// Null — the channel makes no statement, and the core states <c>false</c> next to a non-empty
    /// <see cref="PhoneNumber"/> (SPEC-041 R10). Stated without a non-empty number, it verifies nothing.
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.PhoneNumberVerified)]
    public bool? PhoneNumberVerified { get; init; }

    /// <summary>
    /// User's avatar (if available from the channel): either the image itself as a <c>data:</c> URI
    /// in the format of <see cref="AvatarDataUri"/> (build it with <see cref="AvatarDataUri.TryCreate"/>),
    /// or an absolute https URL. Any other value is dropped by the core and never reaches a token.
    /// </summary>
    /// <remarks>
    /// Dropped on assignment: a value <see cref="AvatarDataUri.IsAcceptedAvatar"/> refuses — an image
    /// over <see cref="AvatarDataUri.MaxImageBytes"/> among them — is stored as null. An oversized
    /// image would otherwise push the snapshot over its size limit, and the core would reject the
    /// whole confirmation instead of signing the user in without an avatar. The check lives in the
    /// accessor for the same reason as the other checks of this type: every construction path goes
    /// through it, deserialization included, so an adapter cannot bypass it.
    /// </remarks>
    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl
    {
        get => _avatarUrl;
        init => _avatarUrl = AvatarDataUri.IsAcceptedAvatar(value) ? value : null;
    }

    /// <summary>
    /// User's language/locale (if available from the channel).
    /// </summary>
    [JsonPropertyName(VeriqaClaimTypes.Locale)]
    public string? Locale { get; init; }

    /// <summary>
    /// Arbitrary metadata from the channel. Maximum size: 4 KB (CA-005).
    /// </summary>
    [JsonPropertyName("raw_metadata")]
    public JsonElement? RawMetadata { get; init; }

    /// <summary>
    /// Additional channel-specific OIDC claims issued by the adapter: its own names under the
    /// adapter's <c>{ChannelType}_</c> prefix, plus the closed standard-profile list — a web provider
    /// channel issues "birthdate" this way — and the closed vendor list — the Email channel issues
    /// "veriqa_channel" and "veriqa_email_mode" this way (SPEC-016 §6.3).
    /// An additive field: merged with the standard claims during identity resolution.
    /// Null — no additional claims.
    /// </summary>
    /// <remarks>
    /// Only names the adapter owns survive the merge: names the core issues or reserves
    /// (<see cref="VeriqaClaimTypes.MandatoryClaims"/>, <see cref="VeriqaClaimTypes.CoreReservedClaims"/>
    /// — <c>email_verified</c> among them, stated by <see cref="EmailVerified"/> instead) are
    /// dropped with a warning, as is any other name outside the adapter's prefix. The closed lists
    /// <see cref="VeriqaClaimTypes.StandardProfileClaims"/> and <see cref="VeriqaClaimTypes.VendorClaims"/>
    /// are the exceptions. Ownership is decided
    /// by <see cref="VeriqaClaimTypes.NameComparer"/>, so a reserved name is refused in any letter
    /// case; a name the adapter may issue is taken only in the spelling the contract declares, and
    /// two names differing only in letter case are one claim — one of them is merged, the rest are
    /// dropped with a warning like any other rejected name (CA-188). Which spelling survives is not
    /// defined: supplying such a pair is a contract violation, not a way to pick the winner.
    ///
    /// Frozen on assignment: the caller may pass a live mutable dictionary cast to
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/>, and a later mutation of it must not leak into
    /// a snapshot that is immutable by contract (CA-008). Freezing in the accessor rather than in a
    /// copying method leaves nothing to forget and nothing to bypass — every construction path,
    /// deserialization from the store included, goes through it. Null stays null: an empty
    /// dictionary would claim claims that were never issued.
    ///
    /// The freeze keeps the payload exactly as the adapter supplied it — key for key, spelling for
    /// spelling. It is a copy, not a merge: re-keying it by claim-name identity here would collapse
    /// a pair of case-variant names before the merge ever sees them, and the warning CA-188
    /// promises would have nothing left to report.
    /// </remarks>
    [JsonPropertyName("additional_claims")]
    public IReadOnlyDictionary<string, string>? AdditionalClaims
    {
        get => _additionalClaims;
        init => _additionalClaims = value?.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Time the data was captured from the channel (UTC). Required field (CA-006).
    /// </summary>
    [JsonPropertyName("captured_at")]
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Channel adapter version in semver format (CA-007).
    /// </summary>
    [JsonPropertyName("adapter_version")]
    public required string AdapterVersion
    {
        get => _adapterVersion;
        init => _adapterVersion = RequireValue(value, nameof(AdapterVersion));
    }

    /// <summary>
    /// Rejects a null in a required string property, naming the property.
    /// </summary>
    /// <remarks>
    /// <c>required</c> is a contract of the C# compiler: it makes a caller state the property, but
    /// says nothing about a value that arrives already built. A snapshot deserialized from the
    /// transaction store or from an inbound payload carries whatever the JSON said, and an explicit
    /// <c>"display_name": null</c> satisfies <c>required</c> — the property is present. The core
    /// then consumes these values unconditionally (subject, mandatory claims), so a null surfaces
    /// far from its source, as a failure inside token issuance. The check lives in the accessor for
    /// the same reason the additional claims are frozen there: every construction path goes through
    /// it, deserialization included, and there is nothing to forget and nothing to bypass.
    /// </remarks>
    private static string RequireValue(string? value, string propertyName)
        => value ?? throw new ArgumentNullException(propertyName);
}
