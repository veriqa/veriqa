// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text.Json;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Hubs;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Access to the client script that watches a page of the callback, shipped as an embedded resource of
/// this assembly and inlined into the page under its CSP nonce — the same arrangement as the script of the
/// sign-in window (<see cref="AuthPageScript"/>). Two pages carry it: the page of the claims completion,
/// rendered for its step and surface — on the device the sign-in was started on, and, for its email block
/// only, without watching the status, on the device that confirmed it
/// (<see cref="ClaimCompletionPageScriptConfig.WatchesStatus"/>) — and the question page of the core (SPEC-007 UI-123) of a sign-in and
/// of a confirmation, rendered for no step — so for that page a step of the completion that appears is a
/// change like any other, and the one rule below covers both. The question page also states the state of
/// the transaction it was rendered in, which matters for one of them: <c>Completed</c>. Where the page goes
/// is its configuration's to say (<see cref="ClaimCompletionPageScriptConfig.CallbackUrl"/>): the callback,
/// or the own address of the question page of a confirmation, which has no callback (SPEC-039 C19).
/// <para>
/// Norms the script implements. They are named here and not in the .js file, whose comments are inlined
/// into the page and reach the end user's browser (see <see cref="CorePageEmbeddedText"/>):
/// </para>
/// <list type="bullet">
/// <item><description>the page learns the step of the claims completion from the status surface — the
/// polling fallback of the sign-in window, at its address and interval (SPEC-007 §5.4, SPEC-041 L91);
/// </description></item>
/// <item><description>the page also joins the group of its transaction on the hub of the sign-in window and
/// joins it again after an automatic reconnect (SPEC-007 UI-125). A status message is not read: it
/// makes the page ask the status surface at once, so what a status means is decided in one place. The
/// polling never stops and does not depend on the connection — without the library, a start or a join, the
/// page watches by polling alone;</description></item>
/// <item><description>a status naming another step or another surface of the web step than the page
/// shows, and a finished transaction, take the browser to the address of the configuration once, by
/// ordinary navigation: the page there decides what to show — the new state of the page, the sign-in
/// itself, the "session expired" page (SPEC-041 L44, L52) or, for a confirmation, the neutral page
/// (SPEC-039 R51);</description></item>
/// <item><description>the script sends no form and makes no request but reading the status and the hub
/// connection: the answers of a page stay explicit posts of the user, and the only way it takes the
/// browser anywhere is a GET of the address of the configuration, which checks the request again. The one
/// exception is a page that names an action for a channel status
/// (<see cref="ClaimCompletionPageScriptConfig.ChannelAction"/>): the form of the claims completion with the
/// email block presses its "Check status" when the hub announces
/// <see cref="ClaimCompletionChannelStatusKeys.EmailConfirmed"/> (SPEC-007 UI-125), a press the user could
/// make, sending the form as the button does — and a press of Enter in the address input of the block,
/// which the script turns into the press of the button of the address step, or of "Continue" while the
/// input is empty. The question page names no action, so its script sends nothing (SPEC-007
/// UI-123);</description></item>
/// <item><description>the page that does not watch the status — the claims completion on the device that
/// confirmed the sign-in — neither polls, nor reloads, nor counts down: the callback and the status are
/// the business of the device the sign-in was started on. It carries the script only while its form has
/// simple fields or its email block shows the address input or "Check status", and the library of the
/// hub — so a connection — only while it shows "Check status";</description></item>
/// <item><description>the values typed into the simple fields are kept in the session storage of the tab,
/// under a key of the transaction (<see cref="SimpleFieldsStorageKeyPrefix"/>), and put back into the fields
/// the page renders empty, so a reload — after an action of the email block or by the user — loses none of
/// them; "Continue" and "Cancel sign-in" forget them, unless "Continue" goes with a code or an address typed
/// into the block, which the server takes for an action of the block. Nothing is kept in the local storage,
/// which would outlive the tab;</description></item>
/// <item><description>"Check status" of the email block is rendered hidden and stays hidden while the
/// connection to the hub is alive — started and in the group of the transaction — and is shown once it has
/// not been alive for <see cref="HubSilenceBeforeCheckButtonMs"/> on end: it never started, it was lost or it
/// is reconnecting, or the join failed; alive again, it is hidden again. Since a status announced while the
/// connection was down is lost, the page presses the button after an automatic reconnect and when its tab
/// comes back after <see cref="RecheckAfterAwayMs"/> or more in the background;</description></item>
/// <item><description>the button of the address step of the email block is enabled only while the address
/// input holds an address — rendered so by the server and kept so as the user types; the server judges the
/// address;</description></item>
/// <item><description>a confirmed sign-in whose details are all given is not a change: the page waits for
/// its completion instead of reloading into the finalization;</description></item>
/// <item><description>a final state the page was rendered in
/// (<see cref="ClaimCompletionPageScriptConfig.RenderedState"/>) is not a change by itself: the web
/// confirmation page of a sign-in a channel completed before the question is rendered in <c>Completed</c>,
/// and it leaves once the status reports the authorization code of the sign-in issued
/// (<c>authorization_code_issued</c>), the state is another one or the transaction is gone — so a page
/// brought back after "Yes" never stays a live question (SPEC-007 UI-123);</description></item>
/// <item><description>a page whose form has been sent does not leave on its own: between the moment the
/// server finishes the sign-in and the moment the browser receives the redirect to the relying party, the
/// status surface already answers "gone", and a navigation of the script there would abort that redirect
/// and land on a callback the sign-in has left;</description></item>
/// <item><description>a tab brought back to the screen and a page restored from the back/forward cache
/// check at once, the latter with its latches reset (SPEC-007 UI-087);</description></item>
/// <item><description>the countdown runs from the server's remainder of the TTL, drawn by the function the
/// sign-in window uses (<see cref="PageCountdown"/>). Zero on the clock is a question for the server
/// (SPEC-007 UI-125): the status is asked at once and then at the interval of the polling, never faster; a
/// transaction that is gone or over takes the browser to the address of the configuration, whose page names
/// the outcome — the page has
/// no "expired" text of its own; a live one with time left restarts the countdown from the server's
/// remainder, and a live one with none keeps the zero on screen. A page its deadline no longer decides —
/// the web confirmation page in <c>Completed</c> — carries no countdown and watches the status alone.
/// </description></item>
/// </list>
/// </summary>
internal static class ClaimCompletionPageScript
{
    /// <summary>
    /// Resource name of the script (<c>UI/Scripts/veriqa-claim-completion.js</c>).
    /// </summary>
    private const string ResourceName = "Veriqa.Core.AuthServer.UI.Scripts.veriqa-claim-completion.js";

    /// <summary>
    /// Identifier of the data block carrying <see cref="ClaimCompletionPageScriptConfig"/> into the page.
    /// The script reads the block back by a bare literal of the same value
    /// (<c>getElementById("veriqa-claim-completion-config")</c>) — nothing checks the pair, so the two are
    /// renamed together.
    /// </summary>
    public const string ConfigElementId = "veriqa-claim-completion-config";

    /// <summary>
    /// How long the connection of the page to the hub may stay not alive — not started, lost, reconnecting
    /// or out of the group of the transaction — before the email block shows "Check status", in
    /// milliseconds. While the connection is alive the button stays hidden: the page presses it by itself
    /// when the hub says the address was confirmed.
    /// </summary>
    public const int HubSilenceBeforeCheckButtonMs = 15_000;

    /// <summary>
    /// How long the tab of the page must stay in the background for its return to press the button of the
    /// confirmation made elsewhere, in milliseconds: long enough that switching tabs for a moment does not
    /// reload the page, short enough that a trip to the mail app does. A mobile browser drops the connection
    /// of a tab in the background, and the message of the hub may be lost meanwhile.
    /// </summary>
    public const int RecheckAfterAwayMs = 3_000;

    /// <summary>
    /// Prefix of the key under which the script keeps the values of the simple fields in the session storage
    /// of the tab; the session identifier of the transaction completes it.
    /// </summary>
    public const string SimpleFieldsStorageKeyPrefix = "veriqa-claim-fields:";

    /// <summary>
    /// Source of the script, ready to be inlined.
    /// </summary>
    public static string Js => CorePageEmbeddedText.Load(typeof(ClaimCompletionPageScript).Assembly, ResourceName);

    /// <summary>
    /// Writes the page configuration as the JSON the data block carries, with the encoder that makes the
    /// block safe to put into the document (<see cref="AuthPageScript.ConfigSerializerOptions"/>).
    /// </summary>
    /// <param name="config">Configuration of the page being rendered.</param>
    /// <returns>JSON safe to place inside the data block as it is.</returns>
    public static string SerializeConfig(ClaimCompletionPageScriptConfig config) =>
        JsonSerializer.Serialize(config, AuthPageScript.ConfigSerializerOptions);

    /// <summary>
    /// Builds the configuration of the watcher of a page of the transaction: the status surface and the
    /// interval of the sign-in window, the callback of the transaction, the states that end the sign-in and
    /// the hub of the sign-in window. A page with another address to leave for states it over the callback
    /// (<see cref="ClaimCompletionPageScriptConfig.CallbackUrl"/>).
    /// </summary>
    /// <param name="sessionId">External session identifier of the transaction.</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <param name="remainingMs">Milliseconds left of the transaction's lifetime (<see cref="PageCountdown.RemainingMs"/>);
    /// null — the page counts nothing down.</param>
    /// <param name="renderedStep">Step of the completion the page was rendered for; null — none.</param>
    /// <param name="renderedSurface">Surface of the web step the page was rendered for; null — none.</param>
    /// <returns>The configuration of the page.</returns>
    public static ClaimCompletionPageScriptConfig BuildConfig(
        string sessionId,
        string pathBase,
        long? remainingMs,
        string? renderedStep,
        string? renderedSurface) =>
        new()
        {
            SessionId = sessionId,
            BasePath = pathBase,
            StatusUrl = Endpoints.TransactionStatusEndpoint.BuildStatusPath(sessionId),
            CallbackUrl = TransactionStatusMessageFactory.BuildCallbackUrl(sessionId),
            PollIntervalMs = DefaultAuthPageRenderer.PollingIntervalMs,
            RemainingMs = remainingMs,
            RenderedStep = renderedStep,
            RenderedSurface = renderedSurface,
            FinalStates =
            [
                nameof(TransactionState.Completed),
                nameof(TransactionState.Failed),
                nameof(TransactionState.Expired)
            ],
            Hub = new ClaimCompletionPageScriptHub(
                Path: SignalRConstants.AuthHubPath,
                StatusMethod: SignalRConstants.OnStatusChangedMethod,
                JoinMethod: SignalRConstants.JoinTransactionMethod)
        };

    /// <summary>
    /// Builds the watcher of a page: its configuration block, the link to the SignalR client library, the
    /// drawing function of the countdown and the script, every one under the CSP nonce of the request. The
    /// page puts the element and the rules of the countdown (<see cref="PageCountdown"/>) in with it — the
    /// three go out together or not at all, so a configuration without a remainder
    /// (<see cref="ClaimCompletionPageScriptConfig.RemainingMs"/>) leaves the drawing function out. A page
    /// that needs no hub goes out without the library, and its script then opens no connection.
    /// </summary>
    /// <param name="config">Configuration of the page (<see cref="BuildConfig"/>).</param>
    /// <param name="signalRClientPath">Address of the SignalR client library, as
    /// <see cref="SignalRClientLocation"/> picked it for the transaction; not yet HTML-encoded. Null — the page
    /// needs no hub: the library is left out.</param>
    /// <param name="cspNonce">CSP nonce of this request; without one the scripts go out without the
    /// attribute, as every inline script of the contour does.</param>
    /// <returns>Markup to put at the end of the body.</returns>
    public static string BuildWatcher(
        ClaimCompletionPageScriptConfig config,
        string? signalRClientPath,
        string? cspNonce)
    {
        ArgumentNullException.ThrowIfNull(config);

        var nonceAttribute = string.IsNullOrEmpty(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        var countdown = config.RemainingMs is null
            ? string.Empty
            : $"<script{nonceAttribute}>{PageCountdown.Js}</script>";

        var library = signalRClientPath is null
            ? string.Empty
            : $"<script src=\"{WebUtility.HtmlEncode(signalRClientPath)}\"{nonceAttribute}></script>";

        return $$"""
            <script type="application/json" id="{{ConfigElementId}}"{{nonceAttribute}}>{{SerializeConfig(config)}}</script>
              {{library}}
              {{countdown}}
              <script{{nonceAttribute}}>{{Js}}</script>
            """;
    }
}
