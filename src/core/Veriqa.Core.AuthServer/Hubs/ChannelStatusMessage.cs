// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Hubs;

/// <summary>
/// SignalR message about a channel-specific intermediate transaction status (SPEC-016 §10.1).
/// An additive contract: dispatched via a separate method
/// <see cref="Veriqa.Core.AuthServer.Constants.SignalRConstants.OnChannelStatusChangedMethod"/>
/// and does not change the semantics of the lifecycle messages <see cref="TransactionStatusMessage"/>.
/// </summary>
public sealed class ChannelStatusMessage
{
    /// <summary>
    /// Session identifier (transaction session_id).
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Type of the channel that published the status (for example, "email").
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Channel-specific status code (for example, "sent", "mail_received").
    /// </summary>
    public required string ChannelStatus { get; init; }
}
