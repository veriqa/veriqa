// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Contracts;

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Channel adapter error codes (SPEC-003 §12.1, §21.2).
/// Codes that cross the free-SPI package boundary are declared once in
/// <see cref="VeriqaErrorCodes"/>; the members below alias them so that a single literal exists
/// per value and existing call sites keep their names. Codes that never leave the core stay
/// literals here.
/// </summary>
public static class ChannelAdapterErrorCodes
{
    /// <inheritdoc cref="VeriqaErrorCodes.WebhookValidationFailed" />
    public const string WebhookValidationFailed = VeriqaErrorCodes.WebhookValidationFailed;

    /// <summary>
    /// Channel is not available.
    /// </summary>
    public const string ChannelNotAvailable = VeriqaErrorCodes.ChannelNotAvailable;

    /// <summary>
    /// Failed to extract the user identity from the channel.
    /// </summary>
    public const string IdentityExtractionFailed = VeriqaErrorCodes.IdentityExtractionFailed;

    /// <summary>
    /// Deep link generation error.
    /// </summary>
    public const string DeepLinkGenerationFailed = VeriqaErrorCodes.DeepLinkGenerationFailed;

    /// <summary>
    /// Failed to send a message to the channel.
    /// </summary>
    public const string MessageSendFailed = VeriqaErrorCodes.MessageSendFailed;

    /// <summary>
    /// Invalid message or chat identifier.
    /// </summary>
    public const string InvalidMessageId = VeriqaErrorCodes.InvalidMessageId;

    /// <summary>
    /// Phone number extraction error.
    /// </summary>
    public const string PhoneExtractionFailed = VeriqaErrorCodes.PhoneExtractionFailed;

    /// <inheritdoc cref="VeriqaErrorCodes.UnsupportedEventType" />
    public const string UnsupportedEventType = VeriqaErrorCodes.UnsupportedEventType;

    /// <summary>
    /// The channel declares it cannot carry this confirmation out (SPEC-003 CA-191). Unlike every
    /// other refusal it ends the transaction instead of leaving it to a retry or its TTL.
    /// </summary>
    public const string ChannelCannotContinue = VeriqaErrorCodes.ChannelCannotContinue;

    /// <summary>
    /// The message content kind is outside the channel's declared
    /// <c>ChannelCapabilities.SupportedMessageKinds</c>, so the adapter was not called at all.
    /// </summary>
    public const string UnsupportedMessageKind = "unsupported_message_kind";

    /// <summary>
    /// The core could not give a transaction its start code: the transaction is past its deadline, or
    /// no free code was found. The caller treats it as a channel that failed to build its link.
    /// </summary>
    public const string StartCodeIssueFailed = "start_code_issue_failed";
}
