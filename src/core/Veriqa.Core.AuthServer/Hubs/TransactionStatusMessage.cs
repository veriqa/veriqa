// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Hubs;

/// <summary>
/// Transaction status change message model (SPEC-007 §5.3).
/// Sent to clients via SignalR when the transaction state changes.
/// Every holder of the session_id may receive it, so it carries the transaction's process state only —
/// never the channel user's identity, claims or snapshot contents (SPEC-007 UI-095).
/// </summary>
public sealed class TransactionStatusMessage
{
    /// <summary>
    /// External session identifier (session_id) used to subscribe to updates.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Current status: Pending, Confirmed, Completed, Expired, Failed.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Redirect URL (on completion).
    /// </summary>
    public string? RedirectUrl { get; init; }

    /// <summary>
    /// Error code (on failure).
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Current step of the claims completion — the name of a <c>ClaimCompletionStep</c> member
    /// (<c>Phone</c>, <c>WebStep</c>); set only when <see cref="Status"/> is <c>ClaimCompletion</c>
    /// (SPEC-041 L91).
    /// </summary>
    public string? CompletionStep { get; init; }

    /// <summary>
    /// Surface of the web step — the name of a <c>WebStepSurface</c> member (<c>InitiatingPage</c>,
    /// <c>CrossDevicePage</c>); null while the web step has not begun, and whenever
    /// <see cref="Status"/> is not <c>ClaimCompletion</c>.
    /// </summary>
    public string? CompletionSurface { get; init; }

    /// <summary>
    /// Whether the answer on the core question page is taken only from the browser the channel identity
    /// was attached from; set only when <see cref="Status"/> is <c>AwaitingWebConfirmation</c>. The
    /// question is then already open in that browser, so a page following the transaction stays where it
    /// is instead of leaving for a question it would not be let to answer.
    /// </summary>
    public bool AnswerBrowserBound { get; init; }
}
