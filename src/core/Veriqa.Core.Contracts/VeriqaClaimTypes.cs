// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;

namespace Veriqa.Core.Contracts;

/// <summary>
/// Well-known OIDC claim names (standard and Veriqa-specific) emitted by the core and channel adapters.
/// Consolidates claim-name string literals that were previously scattered across the core
/// so external adapters and consumers reference a single stable contract. Values are stable
/// (part of the public wire/OIDC contract) and must not change.
///
/// The names are also split into declared groups that say who owns which name when an adapter
/// supplies free-form claims: <see cref="MandatoryClaims"/> and <see cref="CoreReservedClaims"/>
/// belong to the core and are never taken from an adapter, <see cref="VendorClaims"/> is the closed
/// list of vendor names accepted as an exception, and everything else belongs to the adapter under
/// its own <c>{ChannelType}_</c> prefix.
///
/// Every comparison of a claim name — ownership checks, claim sets, token destinations — goes
/// through <see cref="NameComparison"/> / <see cref="NameComparer"/>. See their documentation for
/// why the contract compares names case-insensitively. That is the identity of a name, not a
/// licence to spell it freely: a name an adapter may issue is taken into the claim set only in the
/// spelling declared here, and a name that differs from it in letter case is dropped with a warning
/// like any other name the adapter does not own.
/// </summary>
public static class VeriqaClaimTypes
{
    /// <summary>
    /// Bot flag marking whether the channel identity originates from a bot. The claim is issued as a
    /// JSON boolean — it is a member of <see cref="BooleanClaims"/>; the string form the resolver and
    /// the snapshots carry it in is the internal transport, not the shape a relying party reads.
    /// </summary>
    public const string IsBot = "is_bot";

    /// <summary>
    /// Channel type through which the identity was confirmed (for example "telegram").
    /// </summary>
    public const string ChannelType = "channel_type";

    /// <summary>
    /// User identifier within the originating channel.
    /// </summary>
    public const string ChannelUserId = "channel_user_id";

    /// <summary>
    /// Standard OIDC email address claim.
    /// </summary>
    public const string Email = "email";

    /// <summary>
    /// Standard OIDC claim indicating whether the email address has been verified.
    /// </summary>
    public const string EmailVerified = "email_verified";

    /// <summary>
    /// Veriqa channel claim carrying the channel type of the Email adapter (SPEC-016 §6.3).
    /// </summary>
    public const string VeriqaChannel = "veriqa_channel";

    /// <summary>
    /// Veriqa Email-channel mode claim: <see cref="VeriqaEmailModes.Pull"/> or <see cref="VeriqaEmailModes.Push"/> (SPEC-016 §6.3).
    /// </summary>
    public const string VeriqaEmailMode = "veriqa_email_mode";

    /// <summary>
    /// Standard OIDC display-name claim.
    /// </summary>
    public const string Name = "name";

    /// <summary>
    /// Standard OIDC given-name claim.
    /// </summary>
    public const string GivenName = "given_name";

    /// <summary>
    /// Standard OIDC family-name claim.
    /// </summary>
    public const string FamilyName = "family_name";

    /// <summary>
    /// Standard OIDC preferred-username claim.
    /// </summary>
    public const string PreferredUsername = "preferred_username";

    /// <summary>
    /// Standard OIDC locale claim.
    /// </summary>
    public const string Locale = "locale";

    /// <summary>
    /// Standard OIDC time zone claim (OIDC Core 1.0 section 5.1): the IANA identifier of the zone the
    /// user's moments are stated in.
    /// </summary>
    public const string Zoneinfo = "zoneinfo";

    /// <summary>
    /// Standard OIDC phone-number claim.
    /// </summary>
    public const string PhoneNumber = "phone_number";

    /// <summary>
    /// Standard OIDC claim indicating whether the phone number has been verified — a JSON boolean on
    /// the wire, a member of <see cref="BooleanClaims"/>.
    /// </summary>
    public const string PhoneNumberVerified = "phone_number_verified";

    /// <summary>
    /// Standard OIDC picture (avatar URL) claim.
    /// </summary>
    public const string Picture = "picture";

    /// <summary>
    /// Standard OIDC claim of the time the information of the user was last updated (OIDC Core 1.0 section
    /// 5.1) — a JSON number of seconds since the epoch on the wire, a member of <see cref="NumericDateClaims"/>.
    /// The core states it when a sign-in hands out values Veriqa kept from an earlier one (SPEC-041 C38).
    /// </summary>
    public const string UpdatedAt = "updated_at";

    /// <summary>
    /// Separator between an adapter's channel type and the rest of its own claim name:
    /// a free-form adapter claim must be named <c>{ChannelType}</c> + this + anything.
    /// The prefix makes collisions between adapters impossible by construction.
    /// </summary>
    public const string AdapterClaimPrefixSeparator = "_";

    /// <summary>
    /// The single comparison for claim names across Veriqa — name ownership, claim sets and token
    /// destinations all use it, so a name has one meaning end to end. It is case-insensitive on
    /// purpose: relying parties read claims case-insensitively, so <c>email_Verified</c> and
    /// <c>email_verified</c> are the same claim to the consumer. A case-sensitive ownership check
    /// against case-insensitive consumers would let a changed letter case walk a reserved name
    /// past the gate.
    /// </summary>
    public const StringComparison NameComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Comparer form of <see cref="NameComparison"/>, for claim-name sets and dictionaries.
    /// Derived from it rather than declared separately: the two cannot drift apart.
    /// </summary>
    public static readonly StringComparer NameComparer = StringComparer.FromComparison(NameComparison);

    /// <summary>
    /// Group 1 — claims the core always issues, derived from required snapshot fields.
    /// Never accepted from an adapter's free-form claims.
    /// </summary>
    public static readonly IReadOnlySet<string> MandatoryClaims = new[]
    {
        ChannelType,
        ChannelUserId,
        Name
    }.ToFrozenSet(NameComparer);

    /// <summary>
    /// Group 2 — claims reserved for the core: it derives them from typed snapshot fields, so an
    /// adapter that has the value states it in the field rather than in a free-form claim, or states them
    /// itself (<see cref="UpdatedAt"/>). Never accepted from an adapter's free-form claims.
    /// </summary>
    public static readonly IReadOnlySet<string> CoreReservedClaims = new[]
    {
        GivenName,
        FamilyName,
        PreferredUsername,
        Locale,
        PhoneNumber,
        PhoneNumberVerified,
        Email,
        EmailVerified,
        Picture,
        IsBot,
        UpdatedAt
    }.ToFrozenSet(NameComparer);

    /// <summary>
    /// Claim names the core issues with a JSON boolean value on the wire (OIDC Core 1.0 section 5.1
    /// states <c>email_verified</c> and <c>phone_number_verified</c> as booleans, and <c>is_bot</c>
    /// follows the same shape).
    /// <para>
    /// This is the single point that knows WHICH claim is boolean: the mapper types a claim by
    /// membership here rather than by naming any of them, so a boolean claim added to the core is
    /// typed by adding its name to this set and nothing else. The internal transport stays a string —
    /// the resolver and the snapshots carry <c>"true"</c>/<c>"false"</c>, which is the spelling of the
    /// JSON literal, and the typed claim is what turns it into a boolean on the wire.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> BooleanClaims = new[]
    {
        EmailVerified,
        PhoneNumberVerified,
        IsBot
    }.ToFrozenSet(NameComparer);

    /// <summary>
    /// Claim names the core issues with a JSON number of seconds since the epoch on the wire (OIDC Core 1.0
    /// section 5.1 states <c>updated_at</c> as a number). Like <see cref="BooleanClaims"/>, the single point
    /// that knows which claim has this shape: the mapper types a claim by membership here, and the internal
    /// transport carries the number as its invariant decimal string.
    /// </summary>
    public static readonly IReadOnlySet<string> NumericDateClaims = new[]
    {
        UpdatedAt
    }.ToFrozenSet(NameComparer);

    /// <summary>
    /// Vendor exception — Veriqa's own product claims, accepted from an adapter's free-form claims
    /// despite carrying a foreign prefix. The exception is a closed list of names, deliberately not
    /// the <c>veriqa_</c> prefix: a prefix rule would hand the vendor's namespace to any adapter.
    /// </summary>
    public static readonly IReadOnlySet<string> VendorClaims = new[]
    {
        VeriqaChannel,
        VeriqaEmailMode
    }.ToFrozenSet(NameComparer);
}
