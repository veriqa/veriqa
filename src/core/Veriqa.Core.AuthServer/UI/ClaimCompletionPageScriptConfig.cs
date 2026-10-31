// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Everything the server tells the watcher script of a page — the page of the claims completion or the
/// question page of the core (<see cref="ClaimCompletionPageScript"/>): where to poll, where to
/// go, which hub to listen to, how long the sign-in has left, and which step of the completion and which state
/// of the transaction the page on screen was rendered for. Property names of the JSON
/// are these names in camelCase (<see cref="AuthPageScript.ConfigSerializerOptions"/>); the script reads
/// them by those names, and the compiler does not check the pair.
/// </summary>
internal sealed record ClaimCompletionPageScriptConfig
{
    /// <summary>External session identifier the page tracks — the group of the hub it joins.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Application path base (empty — the site root); the script applies it to the root-relative URLs below.
    /// </summary>
    public required string BasePath { get; init; }

    /// <summary>Root-relative URL of the status of the transaction.</summary>
    public required string StatusUrl { get; init; }

    /// <summary>
    /// Root-relative URL the page reloads through: the callback, or, on the question page of a confirmation,
    /// which has no callback, the own address of that page.
    /// </summary>
    public required string CallbackUrl { get; init; }

    /// <summary>Interval of the polling, in milliseconds — the one of the sign-in window.</summary>
    public required int PollIntervalMs { get; init; }

    /// <summary>
    /// Milliseconds left of the transaction's lifetime when the page was built, never negative. The
    /// countdown adds it to the browser's own clock reading, as the one of the sign-in window does
    /// (<see cref="AuthPageScriptConfig.RemainingMs"/>). Null — the page counts nothing down: the web
    /// confirmation page of a sign-in its deadline no longer decides, which carries no countdown element.
    /// </summary>
    public required long? RemainingMs { get; init; }

    /// <summary>
    /// Name of the state the transaction was in when the page was built, as the status names it; null — not
    /// stated. A final state the page was rendered in is not a change by itself: such a page — the web
    /// confirmation page of a sign-in the channel completed before the question — leaves once the
    /// authorization code of the sign-in is issued, or once the state is another one.
    /// </summary>
    public string? RenderedState { get; init; }

    /// <summary>
    /// Name of the step of the completion the page was rendered for, as the status names it; null — the
    /// page was rendered for no step (every missing claim has an outcome, the sign-in is being finished, or
    /// the page is the web confirmation page, which precedes the completion).
    /// </summary>
    public required string? RenderedStep { get; init; }

    /// <summary>
    /// Name of the surface of the web step the page was rendered for, as the status names it; null — no
    /// surface was chosen yet.
    /// </summary>
    public required string? RenderedSurface { get; init; }

    /// <summary>
    /// Names of the states in which the sign-in is over — the page then reloads through the callback.
    /// </summary>
    public required IReadOnlyList<string> FinalStates { get; init; }

    /// <summary>The hub the page listens to for the status of its transaction.</summary>
    public required ClaimCompletionPageScriptHub Hub { get; init; }

    /// <summary>
    /// Whether the page watches the status of its transaction — polls it, asks it when the hub announces a
    /// change, reloads through <see cref="CallbackUrl"/> and counts down. False on the page of the claims
    /// completion on the device that confirmed the sign-in: the callback and the status are the business of
    /// the device the sign-in was started on, so that page only listens to the hub for the action of its
    /// email block (<see cref="ChannelAction"/>, <see cref="EmailBlock"/>), and the addresses above are not
    /// read.
    /// </summary>
    public bool WatchesStatus { get; init; } = true;

    /// <summary>
    /// The action of the page the script performs, as if pressed, when the hub announces the channel status
    /// that action waits for; null — the script performs none. Only the page of the claims completion with the
    /// email block sets it; the web confirmation page leaves it out, so its answers stay the user's own.
    /// </summary>
    public ClaimCompletionPageScriptChannelAction? ChannelAction { get; init; }

    /// <summary>
    /// The elements of the email block of the form the script drives; null — the page shows no email block.
    /// </summary>
    public ClaimCompletionPageScriptEmailBlock? EmailBlock { get; init; }

    /// <summary>
    /// The simple fields of the form whose typed values the script keeps in the tab; null — the form has none.
    /// </summary>
    public ClaimCompletionPageScriptSimpleFields? SimpleFields { get; init; }
}

/// <summary>
/// The simple fields of the form of the claims completion (<see cref="ClaimCompletionPageScriptConfig.SimpleFields"/>).
/// The script keeps what is typed into them in the session storage of the tab and puts it back into the
/// fields the page renders empty, so a reload of the page — after an action of the email block or by the
/// user — loses nothing; "Continue" and "Cancel sign-in" forget it. The storage is per tab and ends with
/// it; a browser that refuses it only loses the values on a reload.
/// </summary>
/// <param name="FormId">Element id of the form of the fields.</param>
/// <param name="NamePrefix">Prefix of the names of the inputs of the fields; the inputs of the email block,
/// which belong to the same form, carry other names.</param>
/// <param name="StorageKey">Key of the values in the session storage — one per transaction.</param>
internal sealed record ClaimCompletionPageScriptSimpleFields(
    string FormId,
    string NamePrefix,
    string StorageKey);

/// <summary>
/// The elements of the email block of the claims completion the script drives
/// (<see cref="ClaimCompletionPageScriptConfig.EmailBlock"/>). An element the page does not show — the block
/// is on another step — is simply not found, and the script leaves it alone.
/// </summary>
/// <param name="CheckButtonId">Element id of "Check status": rendered hidden and kept so while the connection
/// to the hub is alive, shown once it has not been alive for <paramref name="HubSilenceMs"/> on end.</param>
/// <param name="AddressInputId">Element id of the address input of the address step.</param>
/// <param name="AddressActionButtonId">Element id of the button of the address step: enabled while the address
/// input holds an address; pressed on Enter in that input.</param>
/// <param name="ContinueButtonId">Element id of "Continue" of the form: pressed on Enter in the empty address
/// input, and in the address input of the way without verification, which has no button of its own.</param>
/// <param name="HubSilenceMs">How long the connection to the hub may stay not alive before "Check
/// confirmation" is shown, in milliseconds.</param>
internal sealed record ClaimCompletionPageScriptEmailBlock(
    string CheckButtonId,
    string AddressInputId,
    string AddressActionButtonId,
    string ContinueButtonId,
    int HubSilenceMs);

/// <summary>
/// A button of the page the script presses when the hub announces one channel status
/// (<see cref="ClaimCompletionPageScriptConfig.ChannelAction"/>). The press is the one the user would make:
/// it sends the form of the button with whatever the user has typed into it, so the server answers it as it
/// answers the button. A page without the button, a page whose form is already on its way and a page that is
/// leaving ignore the status. The script also presses the button when the status may have been missed: after
/// an automatic reconnect to the hub, and when the tab comes back after <paramref name="RecheckAfterAwayMs"/>
/// or more in the background.
/// </summary>
/// <param name="Method">Hub method announcing the intermediate channel statuses.</param>
/// <param name="ChannelStatus">Code of the channel status the button waits for.</param>
/// <param name="ButtonId">Element id of the button.</param>
/// <param name="RecheckAfterAwayMs">How long the tab must stay in the background for its return to press the
/// button, in milliseconds.</param>
internal sealed record ClaimCompletionPageScriptChannelAction(
    string Method,
    string ChannelStatus,
    string ButtonId,
    int RecheckAfterAwayMs);

/// <summary>
/// The SignalR hub as the page of the claims completion uses it: where it is, how the page joins the group
/// of its transaction and which method tells it the status has changed. The page reads no field of the
/// message — every message is a reason to ask the status surface. Of the intermediate channel statuses it
/// listens to the one its <see cref="ClaimCompletionPageScriptConfig.ChannelAction"/> names, and to none
/// without it.
/// </summary>
/// <param name="Path">Root-relative path of the hub.</param>
/// <param name="StatusMethod">Hub method announcing a lifecycle status change.</param>
/// <param name="JoinMethod">Hub method the page calls to join the transaction's group.</param>
internal sealed record ClaimCompletionPageScriptHub(
    string Path,
    string StatusMethod,
    string JoinMethod);
