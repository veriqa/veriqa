// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.Json;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The inline script of the launch page (SPEC-025 HOP-066, HOP-066c): the automatic launch of the channel
/// and, when the server puts them in, the client signals back to the core and the browser fingerprint.
/// The page of the channel buttons takes the fingerprint part alone (<see cref="BuildFingerprintOnly"/>),
/// or — the page of one channel whose hop URL launches the channel (SPEC-025 HOP-195) — the launch itself,
/// whose transition its own script reports as a tap on its button (<see cref="HopSignalScript.FingerprintOnly"/>,
/// <see cref="PressEventName"/>).
/// <para>
/// The launch happens once per tab. It navigates in the tab of the page, so where the operating system
/// does not take the link over, the document is unloaded and the person comes back to the page by
/// "Back". A page that launched again then would throw them out a second time. The script therefore
/// leaves a mark in the session storage of the tab when a transition begins — by the automatic launch or
/// by a press of the button — under a key made of <see cref="TransitionMarkKeyPrefix"/> and the path of
/// the document; the token of a hop link is per channel, so the mark is about this channel of this
/// transaction in this tab. A document loaded with the mark standing or by a back/forward navigation, and
/// one restored from the back/forward cache, does not launch: it announces the state "the transition was
/// made" instead (<see cref="TransitionEventName"/>), and the script of the status line shows what
/// follows a transition at once. The button and the signal of the opened page work as on any load. A
/// storage that is missing or refuses leaves no mark and the page behaves as on a first load. The server
/// takes no part in this: its answer does not depend on how many times the link was presented.
/// </para>
/// <para>
/// Every value the script carries goes in as a JSON literal, so no value can end the script or the element.
/// The script loads nothing and evaluates no string (SPEC-007 UI-040, UI-042). It never carries the hop
/// token: a signal goes to the address of the document itself, read by the page, without its query and
/// fragment.
/// </para>
/// </summary>
internal static class HopInterstitialScript
{
    /// <summary>
    /// Id of the button into the channel — the element a tap on which sends
    /// <see cref="HopChannelStatusKeys.ChannelLaunched"/>: again after the automatic launch has sent it, or
    /// in its place when the tap comes first.
    /// </summary>
    public const string LaunchButtonId = "veriqa-hop-launch";

    /// <summary>
    /// Leading part of the key of the mark of a begun transition in the session storage of the tab; the
    /// path of the document follows it. The path is read by the page, so the script never carries the hop
    /// token.
    /// </summary>
    internal const string TransitionMarkKeyPrefix = "veriqa-hop-transition:";

    /// <summary>
    /// Name of the document event the script announces a transition by: one that begins now, or one made
    /// before this document was shown (<see cref="TransitionReturnedField"/>). The script of the status
    /// line listens to it; the two scripts share nothing else.
    /// </summary>
    internal const string TransitionEventName = "veriqa-hop-transition";

    /// <summary>
    /// Field of the detail of <see cref="TransitionEventName"/>: true — the transition was made before
    /// this document was shown, and the person is back on the page.
    /// </summary>
    internal const string TransitionReturnedField = "returned";

    /// <summary>
    /// Name of the document event a page announces a press of its own button into the channel by — a
    /// button that is not <see cref="LaunchButtonId"/>, the mail-client button of the page of the channel
    /// buttons of one channel. The press takes the place of the automatic launch, as a press of
    /// <see cref="LaunchButtonId"/> does, but the script neither announces it (<see cref="TransitionEventName"/>)
    /// nor signals it: the page reacts to its own press and reports it itself.
    /// </summary>
    internal const string PressEventName = "veriqa-hop-press";

    /// <summary>
    /// Value of the mark of a begun transition; only its presence is read.
    /// </summary>
    private const string TransitionMarkValue = "1";

    /// <summary>
    /// Type of a navigation through the history of the tab, as the Navigation Timing interface names it.
    /// </summary>
    private const string BackForwardNavigationType = "back_forward";

    /// <summary>
    /// Longest wait for the fingerprint, in milliseconds (SPEC-025 HOP-066c): past it the signal of the
    /// opened page goes without one, and the automatic launch waits no longer than this.
    /// </summary>
    private const int FingerprintTimeLimitMilliseconds = 300;

    /// <summary>
    /// The function <c>send(signal, fingerprint)</c> every script of this class queues a signal by: the
    /// address is the path of the document itself, so the hop token is never written into the script.
    /// Built once per process.
    /// </summary>
    private static readonly string SendFunction = $$"""
        function send(signal, fingerprint) {
            var body = {};
            body[{{Literal(HopClientSignal.SignalField)}}] = signal;
            if (fingerprint) { body[{{Literal(HopClientSignal.FingerprintField)}}] = fingerprint; }
            try { navigator.sendBeacon(window.location.pathname, JSON.stringify(body)); } catch (e) { }
          }
        """;

    /// <summary>
    /// Body of <c>opened()</c> without a fingerprint. Built once per process.
    /// </summary>
    private static readonly string OpenedWithoutFingerprintBody = OpenedWithoutFingerprint();

    /// <summary>
    /// Body of <c>opened()</c> with the fingerprint. Built once per process.
    /// </summary>
    private static readonly string OpenedWithFingerprintBody = OpenedWithFingerprint();

    /// <summary>
    /// The script of <see cref="BuildFingerprintOnly"/>. Built once per process.
    /// </summary>
    private static readonly string FingerprintOnly = $$"""
        (function () {
          {{SendFunction}}
          function opened() {
            {{OpenedWithFingerprintBody}}
          }
          try {
            if (typeof navigator.sendBeacon === 'function') { opened(); }
          } catch (e) { }
        })();
        """;

    /// <summary>
    /// Builds the script.
    /// </summary>
    /// <param name="target">The deep link the page launches.</param>
    /// <param name="signalScript">What the page sends back to the core.</param>
    /// <returns>The text of the script, to be put into a <c>script</c> element with the nonce of the request.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind of the signal script is not a member of the enumeration.</exception>
    public static string Build(string target, HopSignalScript signalScript)
    {
        ArgumentNullException.ThrowIfNull(target);

        // The default JSON encoder escapes '<', '>', '&' and quotes, so the literal cannot close the
        // script element whatever the adapter returned.
        var targetLiteral = JsonSerializer.Serialize(target);

        return signalScript switch
        {
            HopSignalScript.None => Launch(targetLiteral, opened: null, signalsLaunch: false),
            HopSignalScript.Signals => Launch(targetLiteral, OpenedWithoutFingerprintBody, signalsLaunch: true),
            HopSignalScript.SignalsWithFingerprint => Launch(targetLiteral, OpenedWithFingerprintBody, signalsLaunch: true),
            HopSignalScript.FingerprintOnly => Launch(targetLiteral, OpenedWithFingerprintBody, signalsLaunch: false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(signalScript), signalScript, "Unknown kind of the signal script of the launch page.")
        };
    }

    /// <summary>
    /// Builds the script of a page that sends the client signals and does NOT launch the channel by itself —
    /// the page of a channel whose link carries no transaction: the person has a message to read and to
    /// copy there, so the page stays until they press its button. The signal of the opened page goes at
    /// once, or after the fingerprint when it is computed; a press of the button
    /// (<see cref="LaunchButtonId"/>) sends <see cref="HopChannelStatusKeys.ChannelLaunched"/> and does not
    /// hold the navigation. Without <c>sendBeacon</c> there are no signals.
    /// </summary>
    /// <param name="signalScript">What the page sends back to the core.</param>
    /// <returns>
    /// The text of the script, to be put into a <c>script</c> element with the nonce of the request; empty —
    /// the page sends nothing.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind of the signal script is not a member of the enumeration.</exception>
    public static string BuildWithoutLaunch(HopSignalScript signalScript)
    {
        return signalScript switch
        {
            HopSignalScript.None => string.Empty,
            HopSignalScript.Signals => SignalsOnly(OpenedWithoutFingerprintBody),
            HopSignalScript.SignalsWithFingerprint => SignalsOnly(OpenedWithFingerprintBody),
            HopSignalScript.FingerprintOnly => FingerprintOnly,
            _ => throw new ArgumentOutOfRangeException(
                nameof(signalScript), signalScript, "Unknown kind of the signal script of the launch page.")
        };
    }

    /// <summary>
    /// Builds the script of a page that sends the browser fingerprint and nothing else — the page of the
    /// channel buttons a hop URL is answered with (SPEC-025 HOP-090): the fingerprint is computed as on
    /// the launch page and sent with the signal of the opened page, which is its carrier there. The
    /// script launches nothing and listens to no button: the buttons of that page send their own signal,
    /// and a tap on one is not held by the computation. Without <c>sendBeacon</c> nothing is sent.
    /// </summary>
    /// <returns>The text of the script, to be put into a <c>script</c> element with the nonce of the request.</returns>
    public static string BuildFingerprintOnly() => FingerprintOnly;

    /// <summary>
    /// The client signals without a launch (see <see cref="BuildWithoutLaunch"/>). The whole body runs
    /// inside one guard: an error of the signals takes nothing else of the page away.
    /// </summary>
    /// <param name="opened">Body of the function <c>opened()</c>, as for the launch with the signals.</param>
    /// <returns>The script.</returns>
    private static string SignalsOnly(string opened) => $$"""
        (function () {
          {{SendFunction}}
          function opened() {
            {{opened}}
          }
          try {
            if (typeof navigator.sendBeacon === 'function') {
              var button = document.getElementById({{Literal(LaunchButtonId)}});
              if (button) {
                button.addEventListener('click', function () { send({{Literal(HopChannelStatusKeys.ChannelLaunched)}}); });
              }
              opened();
            }
          } catch (e) { }
        })();
        """;

    /// <summary>
    /// The launch, with the client signals when the server puts them in. One function starts every
    /// transition of the page — <c>begin()</c>: it leaves the mark and announces the transition, and it
    /// runs before the navigation of the automatic launch and on a press of the button. A page that came
    /// back after a transition announces the state through <c>returnedState()</c> and never navigates.
    /// <para>
    /// The signals run inside their own guard, so an error of theirs neither stops the launch nor the
    /// button; the launch starts once the signal of the opened page is queued — at once, or after the
    /// fingerprint when it is computed. Without <c>sendBeacon</c> there are no signals and the launch is
    /// not delayed.
    /// </para>
    /// <para>
    /// <see cref="HopChannelStatusKeys.ChannelLaunched"/> says the browser started the transition into the
    /// channel, by itself or by a tap (SPEC-025 HOP-066c): the automatic launch sends it once, right before
    /// the navigation, and the button sends it on every tap and does not hold the navigation. A tap made
    /// before the automatic launch — while the fingerprint is awaited — takes its place: the launch then
    /// neither navigates nor signals a second time. It does not say the channel's app opened, and nothing
    /// takes it back when the transition led nowhere.
    /// </para>
    /// <para>
    /// A press of a button of the page that is not <see cref="LaunchButtonId"/>, announced by
    /// <see cref="PressEventName"/>, takes the place of the automatic launch the same way and leaves the
    /// mark, without an announcement and without a signal: the page that announced it reacts to it and
    /// reports it itself.
    /// </para>
    /// </summary>
    /// <param name="targetLiteral">The target as a JSON literal.</param>
    /// <param name="opened">
    /// Body of the function <c>opened()</c>: queues the signal of the opened page and returns a promise
    /// that settles once it is queued, or nothing when it is queued already; null — the page sends no
    /// signals.
    /// </param>
    /// <param name="signalsLaunch">
    /// Whether the script sends <see cref="HopChannelStatusKeys.ChannelLaunched"/>; false — the transition is
    /// reported by the script of the page, which hears it by <see cref="TransitionEventName"/>, or the page
    /// sends no signals at all.
    /// </param>
    /// <returns>The script.</returns>
    private static string Launch(string targetLiteral, string? opened, bool signalsLaunch)
    {
        var launchedSignal = !signalsLaunch
            ? string.Empty
            : $"if (signals) {{ send({Literal(HopChannelStatusKeys.ChannelLaunched)}); }}";

        var start = opened is null
            ? "launch();"
            : $$"""
                {{SendFunction}}
                  function opened() {
                    {{opened}}
                  }
                  var pending = null;
                  try {
                    if (typeof navigator.sendBeacon === 'function') {
                      signals = true;
                      pending = opened();
                    }
                  } catch (e) { }
                  if (pending) { pending.then(launch, launch); } else { launch(); }
                """;

        return $$"""
            (function () {
              var markKey = {{Literal(TransitionMarkKeyPrefix)}} + window.location.pathname;
              function marked() {
                try { return window.sessionStorage.getItem(markKey) !== null; } catch (e) { return false; }
              }
              function mark() {
                try { window.sessionStorage.setItem(markKey, {{Literal(TransitionMarkValue)}}); } catch (e) { }
              }
              function cameByHistory() {
                try {
                  var entries = window.performance.getEntriesByType('navigation');
                  return entries.length > 0 && entries[0].type === {{Literal(BackForwardNavigationType)}};
                } catch (e) { return false; }
              }
              function announce(returned) {
                try {
                  var detail = {};
                  detail[{{Literal(TransitionReturnedField)}}] = returned;
                  document.dispatchEvent(new CustomEvent({{Literal(TransitionEventName)}}, { detail: detail }));
                } catch (e) { }
              }
              var launched = false;
              var signals = false;
              function begin() {
                launched = true;
                mark();
                announce(false);
              }
              function returnedState() {
                launched = true;
                announce(true);
              }
              function launch() {
                if (launched) { return; }
                begin();
                {{launchedSignal}}
                window.location.assign({{targetLiteral}});
              }
              var button = document.getElementById({{Literal(LaunchButtonId)}});
              if (button) {
                button.addEventListener('click', function () { begin(); {{launchedSignal}} });
              }
              document.addEventListener({{Literal(PressEventName)}}, function () { launched = true; mark(); });
              window.addEventListener('pageshow', function (event) { if (event.persisted) { returnedState(); } });
              if (marked() || cameByHistory()) { returnedState(); }
              {{start}}
            })();
            """;
    }

    /// <summary>
    /// The signal of the opened page without a fingerprint: queued at once.
    /// </summary>
    /// <returns>Body of the function <c>opened()</c>.</returns>
    private static string OpenedWithoutFingerprint() =>
        $"send({Literal(HopChannelStatusKeys.InterstitialOpened)}); return null;";

    /// <summary>
    /// The signal of the opened page with the browser fingerprint (SPEC-025 HOP-090b), computed by the
    /// functions of <see cref="CorePageBrowserFingerprint"/>. A failure, a browser without
    /// <c>crypto.subtle</c> or the time limit sends the signal without the fingerprint — the signal matters
    /// more than the value.
    /// </summary>
    /// <returns>Body of the function <c>opened()</c>.</returns>
    private static string OpenedWithFingerprint() => $$"""
        {{CorePageBrowserFingerprint.Functions}}
        return boundedFingerprint({{FingerprintTimeLimitMilliseconds.ToString(CultureInfo.InvariantCulture)}}).then(function (value) {
          send({{Literal(HopChannelStatusKeys.InterstitialOpened)}}, value);
        });
        """;

    /// <summary>
    /// A string as a JSON literal the script can carry.
    /// </summary>
    /// <param name="value">The string.</param>
    /// <returns>The literal.</returns>
    internal static string Literal(string value) => JsonSerializer.Serialize(value);
}
