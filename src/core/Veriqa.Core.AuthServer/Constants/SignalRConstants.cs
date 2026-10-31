// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Authentication SignalR hub constants (SPEC-007 §5).
/// </summary>
public static class SignalRConstants
{
    /// <summary>
    /// Path of the authentication SignalR hub.
    /// </summary>
    public const string AuthHubPath = "/hubs/auth";

    /// <summary>
    /// Name of the server method for subscribing to a transaction.
    /// </summary>
    public const string JoinTransactionMethod = "JoinTransactionAsync";

    /// <summary>
    /// Name of the server method for unsubscribing from a transaction.
    /// </summary>
    public const string LeaveTransactionMethod = "LeaveTransactionAsync";

    /// <summary>
    /// Name of the client method that notifies about a status change.
    /// </summary>
    public const string OnStatusChangedMethod = "OnStatusChanged";

    /// <summary>
    /// Name of the client method that notifies about a channel-specific intermediate status
    /// (SPEC-016 §10.1). A separate method — an additive contract: existing clients
    /// subscribed only to <see cref="OnStatusChangedMethod"/> are not affected.
    /// </summary>
    public const string OnChannelStatusChangedMethod = "OnChannelStatusChanged";

    /// <summary>
    /// Name of the server method a page asks for the replacement of its hop QR codes by (SPEC-025
    /// HOP-152). It answers the caller alone and writes nothing.
    /// </summary>
    public const string ReissueHopQrMethod = "ReissueHopQrAsync";

    /// <summary>
    /// Relative path to the locally served SignalR JavaScript client (@microsoft/signalr 8.0.7).
    /// The file ships as a static web asset inside the Veriqa.Core.AuthServer package (RCL),
    /// so it is available from any host by the <c>/_content/{PackageId}/...</c> convention without
    /// manually copying it into the host's wwwroot.
    /// </summary>
    public const string SignalRClientPath = "/_content/Veriqa.Core.AuthServer/lib/signalr/signalr.min.js";
}
