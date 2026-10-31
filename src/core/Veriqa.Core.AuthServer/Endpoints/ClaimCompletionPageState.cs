// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// What the page of the claims completion shows: the form of the web step, or one of its waiting states, and
/// the step it was rendered for — on the device the sign-in was started on, or on the device that confirmed
/// it (SPEC-041 L52).
/// </summary>
internal sealed record ClaimCompletionPageState
{
    /// <summary>
    /// External session identifier of the transaction — the page polls its status and reloads through its
    /// callback.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Current step of the claims completion the page was rendered for; null — no step is on (every missing
    /// claim has an outcome, the sign-in is being finished).
    /// </summary>
    public required ClaimCompletionStep? Step { get; init; }

    /// <summary>
    /// Surface of the web step the page was rendered for; null — no surface was chosen yet.
    /// </summary>
    public required WebStepSurface? Surface { get; init; }

    /// <summary>
    /// Whether the script of the page watches the status — reloads the page through the callback when the
    /// step changes or the sign-in ends, and counts down — the page of the device the sign-in was started on
    /// does; the page of the device that confirmed it has no callback to reload through and does not: it
    /// carries the script only for its email block.
    /// </summary>
    public required bool Watched { get; init; }

    /// <summary>
    /// Milliseconds left of the transaction's lifetime when the page was built, never negative — where the
    /// countdown of a page with the watcher script starts. A page without the script shows no countdown and
    /// does not read the value.
    /// </summary>
    public long RemainingMs { get; init; }

    /// <summary>
    /// Address a page with the script loads the SignalR client library from, as
    /// <see cref="Veriqa.Core.AuthServer.UI.SignalRClientLocation"/> picked it for the transaction. A page
    /// without the script loads no library and does not read the value, nor does a page that does not watch
    /// the status while its email block shows no "Check status".
    /// </summary>
    public string SignalRClientPath { get; init; } = string.Empty;

    /// <summary>
    /// The form to show; null — the page waits.
    /// </summary>
    public ClaimCompletionFormModel? Form { get; init; }

    /// <summary>
    /// The email block the form carries after its fields (SPEC-041 L108) — built from the transaction by the
    /// page that knows it; null — the email is not being obtained on this page.
    /// </summary>
    public ClaimCompletionEmailBlockView? EmailBlock { get; init; }

    /// <summary>
    /// The right of this browser to cancel the sign-in from a waiting state (SPEC-041 L109, N78) — the hidden
    /// fields the form of "Cancel sign-in" carries; null — the page offers no cancellation outside its form:
    /// the form is shown and carries the action itself, or the transaction has left the claims completion.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? CancelRightFields { get; init; }

    /// <summary>
    /// Name of the channel the phone number is asked in, shown while the page waits on the phone step;
    /// null on every other state.
    /// </summary>
    public string? PhoneChannelName { get; init; }
}
