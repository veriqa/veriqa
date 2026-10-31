// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Contracts;

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Internal error codes for the OIDC integration (SPEC-002, section 11.2).
/// Used for structured logging and diagnostics.
/// </summary>
public static class OidcErrorCodes
{
    /// <summary>
    /// Invalid OIDC request (required parameters are missing).
    /// </summary>
    public const string OidcRequestInvalid = "oidc_request_invalid";

    /// <summary>
    /// PKCE is required but not provided (a public client sent no <c>code_challenge</c>).
    /// </summary>
    public const string PkceRequired = "pkce_required";

    /// <summary>
    /// Failed to create the transaction.
    /// </summary>
    public const string TransactionCreationFailed = "transaction_creation_failed";

    /// <summary>
    /// Transaction is not completed (not in the Completed state).
    /// </summary>
    public const string TransactionNotCompleted = "transaction_not_completed";

    /// <summary>
    /// Browser nonce does not match.
    /// </summary>
    public const string BrowserNonceMismatch = "browser_nonce_mismatch";

    /// <summary>
    /// Claims mapping error.
    /// </summary>
    public const string ClaimsMappingFailed = "claims_mapping_failed";

    /// <summary>
    /// OpenIddict client seeding error.
    /// </summary>
    public const string ClientSeedingFailed = "client_seeding_failed";

    /// <summary>
    /// Invalid UI configuration.
    /// </summary>
    public const string UiConfigInvalid = "ui_config_invalid";

    /// <summary>
    /// Transaction not found or expired. Same value and same meaning as the SPI code, so it
    /// aliases <see cref="VeriqaErrorCodes"/> rather than carrying a third copy of the literal.
    /// </summary>
    public const string TransactionNotFound = VeriqaErrorCodes.TransactionNotFound;

    /// <summary>
    /// Error preparing channel data for the UI.
    /// </summary>
    public const string ChannelDisplayFailed = "channel_display_failed";

    /// <summary>
    /// The phone number is required of the sign-in, but no channel available to the request can provide
    /// it (SPEC-041 L48): the request is refused before a transaction is created.
    /// </summary>
    public const string NoChannelForRequiredPhone = "no_channel_for_required_phone";

    /// <summary>
    /// The collected claims store, or the resolution of the tenant of the calling client, failed to answer
    /// (SPEC-041 C82), and the request may be repeated. A failed tenant resolution deleted nothing. When the
    /// subject stands for several core subjects, those deleted before the store failed stay deleted and
    /// recorded in the audit journal; the repeated request completes the deletion.
    /// </summary>
    public const string CollectedClaimsStoreUnavailable = "collected_claims_store_unavailable";
}
