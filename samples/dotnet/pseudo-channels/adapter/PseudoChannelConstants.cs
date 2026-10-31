// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Sample.PseudoChannels.Adapter;

/// <summary>
/// String and numeric constants of the pseudo-channels: the channel types, the wire format of the
/// relay webhook and the shape of the channel link. The host reads the same constants, so the two
/// sides of the relay cannot drift apart.
/// </summary>
public static class PseudoChannelConstants
{
    /// <summary>
    /// Channel type of the Google pseudo-channel.
    /// </summary>
    public const string GoogleChannelType = "google-id";

    /// <summary>
    /// Name of the Google pseudo-channel on the sign-in button (a brand name — not localized).
    /// </summary>
    public const string GoogleDisplayName = "Google";

    /// <summary>
    /// Channel type of the Apple pseudo-channel.
    /// </summary>
    public const string AppleChannelType = "apple-id";

    /// <summary>
    /// Name of the Apple pseudo-channel on the sign-in button (a brand name — not localized).
    /// </summary>
    public const string AppleDisplayName = "Apple";

    /// <summary>
    /// Adapter version in semver format (recorded in the identity snapshot).
    /// </summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>
    /// Header carrying the signature of the relay webhook.
    /// </summary>
    public const string SignatureHeader = "X-Pseudo-Signature";

    /// <summary>
    /// Prefix of the signature header value; the lowercase hex of the HMAC-SHA256 follows it.
    /// </summary>
    public const string SignaturePrefix = "sha256=";

    /// <summary>
    /// First path segment of every address the sample serves for a pseudo-channel.
    /// </summary>
    public const string PathPrefix = "pseudo";

    /// <summary>
    /// Last path segment of the channel link: the address that sends the phone to the provider.
    /// </summary>
    public const string StartSegment = "start";

    /// <summary>
    /// Query parameter of the transaction in the channel link.
    /// </summary>
    public const string TransactionQueryParameter = "tx";

    /// <summary>
    /// Health-check response time reported by a pseudo-channel: there is no platform to measure.
    /// </summary>
    public static readonly TimeSpan HealthResponseTime = TimeSpan.Zero;

    /// <summary>
    /// Values of the <see cref="Fields.Intent"/> field of the relay webhook body.
    /// </summary>
    public static class Intents
    {
        /// <summary>
        /// The user signed in at the provider: the sign-in starts.
        /// </summary>
        public const string Start = "start";

        /// <summary>
        /// The user answered "yes" on the phone page.
        /// </summary>
        public const string Confirm = "confirm";

        /// <summary>
        /// The user answered "no" on the phone page.
        /// </summary>
        public const string Decline = "decline";
    }

    /// <summary>
    /// Field names of the relay webhook body.
    /// </summary>
    public static class Fields
    {
        /// <summary>
        /// What happened: one of <see cref="Intents"/>.
        /// </summary>
        public const string Intent = "intent";

        /// <summary>
        /// Veriqa transaction the event belongs to.
        /// </summary>
        public const string TransactionId = "transaction_id";

        /// <summary>
        /// Subject identifier the provider issued.
        /// </summary>
        public const string Subject = "sub";

        /// <summary>
        /// Email address.
        /// </summary>
        public const string Email = "email";

        /// <summary>
        /// Whether the provider verified the email address.
        /// </summary>
        public const string EmailVerified = "email_verified";

        /// <summary>
        /// Full name in a displayable form.
        /// </summary>
        public const string Name = "name";

        /// <summary>
        /// Given name.
        /// </summary>
        public const string GivenName = "given_name";

        /// <summary>
        /// Family name.
        /// </summary>
        public const string FamilyName = "family_name";

        /// <summary>
        /// URL of the profile picture.
        /// </summary>
        public const string Picture = "picture";

        /// <summary>
        /// Locale as a BCP 47 language tag.
        /// </summary>
        public const string Locale = "locale";
    }
}
