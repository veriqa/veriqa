// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The inline script by which the launch page answers the start of a transition into the channel
/// (SPEC-025 HOP-186): the page says where the person went and what to do there, and starts following the
/// outcome of the transaction, which then takes the place of the heading and the button. A transition
/// starts by the automatic launch of the page or by a press of its button; the script of the launch announces both
/// (<see cref="HopInterstitialScript.TransitionEventName"/>), and a press of the button is heard here
/// directly as well, so the page follows the outcome even where that script did not run. A confirmed
/// transaction is not an outcome yet — it may still fail before it is completed — so the page
/// takes the heading and the button away, the channel having done its part, shows the line of the processing
/// and goes on following; the line of the success is shown for a completed transaction only. A transaction the core
/// asks its confirming question about on its own page (SPEC-039 E29) takes the browser to that page, as
/// the page of the channel buttons does: the watch ends there and the status line is left as it is —
/// nothing has been confirmed yet. A sign-in whose question the core asks on the page of the sign-in
/// window has no such page to go to (SPEC-025 HOP-186): the page puts the line that sends the person back
/// to that window (<see cref="HopInterstitialWatchTexts.ReturnToWindow"/>) in place of its heading and
/// controls, as an outcome would, and goes on following — only a line of the outcome replaces it.
/// <para>
/// The reaction to those states is not this script's own: the shared function of the status block
/// (<see cref="PageStatusBlock"/>), inlined ahead of this script, writes the status line and takes the
/// heading and the controls away, as it does on the scanner page, and leaves the focus where it is — the
/// status line of this page takes none. This script only tells it which state came, and marks the page
/// settled so that no line of the transition replaces the line of the state. The lines of a transition and
/// of the return to the page leave the heading on screen.
/// </para>
/// <para>
/// The spare start code of the page. A channel whose link carries the transaction may still open its chat
/// with nothing started in it, and the message with the code is the way on from there. The page keeps it
/// out of sight until a transition has had time to fail: the element named by
/// <see cref="SpareRevealElementId"/> — the link that opens the code — appears a delay after the transition
/// began, and at once on a page the person came back to after a transition. The link opens the block (<see cref="SpareElementId"/>) in place and takes the
/// button of the page out of sight: the block carries its own button into the channel, and the page offers
/// one way in, not two. With scripts off the block is on screen from the load, the link is not and the
/// button stays: hiding the block is this script's doing. The outcome of the transaction takes both away
/// with the button. Once the status says the answer is awaited in the channel, the chat has started and
/// the code leads nowhere: the link and the block go out of sight, and neither the delay nor a return
/// brings them back; the button of the page and the status line are left as they are (SPEC-025 HOP-194).
/// </para>
/// <para>
/// The line of a begun transition gives way to the line of the return
/// (<see cref="HopInterstitialWatchTexts.Returned"/>) once the person is back on the page: a page the
/// person came back to after a transition shows it at once, and a page whose tab was hidden after the
/// transition began and became visible again shows it then (Page Visibility API). A line of the outcome
/// and the line that sends the person back to the sign-in window are never replaced by it.
/// </para>
/// <para>
/// The outcome is followed by the polling of the status surface
/// (<see cref="TransactionOutcomeWatchScript"/>), which asks only while the page is visible; the page
/// joins no hub. The listener neither holds nor cancels the navigation of the button. The script is a
/// separate element from the script of the launch and of the client signals
/// (<see cref="HopInterstitialScript"/>), so a failure of one does not stop the other.
/// Every value goes in as a JSON literal; the script loads nothing and evaluates no string
/// (SPEC-007 UI-040, UI-042).
/// </para>
/// </summary>
internal static class HopInterstitialWatchScript
{
    /// <summary>
    /// Id of the status line of the launch page — the live region the script fills.
    /// </summary>
    public const string StatusElementId = "veriqa-hop-status";

    /// <summary>
    /// Attribute of the elements of the launch page the outcome of the transaction takes the place of.
    /// </summary>
    public const string ControlAttribute = "data-veriqa-hop-control";

    /// <summary>
    /// Id of the block of the spare start code of the launch page.
    /// </summary>
    public const string SpareElementId = "veriqa-hop-spare";

    /// <summary>
    /// Id of the element that holds the link opening the spare start code.
    /// </summary>
    public const string SpareRevealElementId = "veriqa-hop-trouble";

    /// <summary>
    /// Id of the link that opens the spare start code.
    /// </summary>
    public const string SpareLinkId = "veriqa-hop-trouble-link";

    /// <summary>
    /// Selector of the elements carrying <see cref="ControlAttribute"/>.
    /// </summary>
    private const string ControlSelector = "[" + ControlAttribute + "]";

    /// <summary>
    /// Selector of the heading of the channel. The launch page carries one heading, so its class finds it;
    /// the heading takes no id, its markup being shared with the scanner page, which shows one per channel.
    /// </summary>
    private const string HeadingSelector = "." + HopLaunchPageHeading.TitleClass;

    /// <summary>
    /// The literal handed to the watch in place of a callback the page does not have.
    /// </summary>
    private const string NoCallback = "null";

    /// <summary>
    /// Builds the script.
    /// </summary>
    /// <param name="statusPath">The address of the polled status of the transaction, path base included.</param>
    /// <param name="questionPath">
    /// The address of the page of the core that asks the confirming question, path base included; null —
    /// no page of the core asks about this transaction, and the page never leaves itself.
    /// </param>
    /// <param name="confirmedWatchLimit">How long the page goes on asking after it saw its transaction confirmed.</param>
    /// <param name="texts">The lines of the status line.</param>
    /// <param name="spareRevealDelayMilliseconds">
    /// How long after the start of a transition the spare start code is offered, in milliseconds.
    /// </param>
    /// <returns>The text of the script, to be put into a <c>script</c> element with the nonce of the request.</returns>
    public static string Build(
        string statusPath,
        string? questionPath,
        TimeSpan confirmedWatchLimit,
        HopInterstitialWatchTexts texts,
        int spareRevealDelayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(statusPath);
        ArgumentNullException.ThrowIfNull(texts);

        // Without a question to leave for the watch is handed no destination and goes on as it did.
        var onQuestion = questionPath is null
            ? NoCallback
            : $"function () {{ window.location.assign({HopInterstitialScript.Literal(questionPath)}); }}";

        // A transaction that ran out of time, one the status no longer knows and a sign-in replaced by a
        // newer one end alike on this page, as they do on the page of the channel buttons: none of them
        // is a failure to report. A refusal is told from a fault by the reason code alone.
        return $$"""
            (function () {
              var button = document.getElementById({{HopInterstitialScript.Literal(HopInterstitialScript.LaunchButtonId)}});
              var statusEl = document.getElementById({{HopInterstitialScript.Literal(StatusElementId)}});
              if (!button || !statusEl || typeof window.veriqaStatusBlock !== 'function') { return; }

              var settled = false;

              var block = window.veriqaStatusBlock(
                {
                  status: statusEl,
                  heading: document.querySelector({{HopInterstitialScript.Literal(HeadingSelector)}}),
                  controls: Array.prototype.slice.call(document.querySelectorAll({{HopInterstitialScript.Literal(ControlSelector)}})),
                  close: document.getElementById({{HopInterstitialScript.Literal(OutcomeCloseAction.ElementId)}})
                },
                {
                  returnToWindow: {{HopInterstitialScript.Literal(texts.ReturnToWindow)}},
                  processing: {{HopInterstitialScript.Literal(texts.Processing)}},
                  completed: {{HopInterstitialScript.Literal(texts.Completed)}},
                  expired: {{HopInterstitialScript.Literal(texts.Expired)}},
                  declined: {{HopInterstitialScript.Literal(texts.Declined)}},
                  error: {{HopInterstitialScript.Literal(texts.Error)}}
                });

              function showTransition(text) {
                if (!text) { return; }
                statusEl.textContent = text;
                statusEl.className = 'veriqa-status';
                statusEl.hidden = false;
              }

              {{TransactionOutcomeWatchScript.Declaration}}

              var watch = {{TransactionOutcomeWatchScript.FunctionName}}(
                {{HopInterstitialScript.Literal(statusPath)}},
                {{TransactionOutcomeWatchScript.MillisecondsLiteral(confirmedWatchLimit)}},
                {{onQuestion}},
                function () { settled = true; block.show('returnToWindow'); },
                withdrawSpare,
                function () { settled = true; block.show('processing'); },
                function () { settled = true; block.show('completed'); },
                function (state, reasonCode) {
                  settled = true;
                  if (state === null
                      || state === {{HopInterstitialScript.Literal(TransactionState.Expired.ToString())}}
                      || reasonCode === {{HopInterstitialScript.Literal(TransactionErrorCodes.SupersededByNewSignIn)}}) {
                    block.show('expired');
                  } else if (reasonCode === {{HopInterstitialScript.Literal(TransactionErrorCodes.DeclinedByUser)}}) {
                    block.show('declined');
                  } else {
                    block.show('error');
                  }
                });

              var spareEl = document.getElementById({{HopInterstitialScript.Literal(SpareElementId)}});
              var spareLink = document.getElementById({{HopInterstitialScript.Literal(SpareLinkId)}});
              var revealEl = document.getElementById({{HopInterstitialScript.Literal(SpareRevealElementId)}});
              var revealTimer = null;
              var revealed = false;
              var withdrawn = false;
              var begun = false;
              var away = false;

              if (spareEl) { spareEl.hidden = true; }
              if (spareEl && spareLink) {
                spareLink.addEventListener('click', function (event) {
                  event.preventDefault();
                  spareEl.hidden = false;
                  spareLink.setAttribute('aria-expanded', 'true');
                  button.style.display = 'none';
                });
              }

              function reveal() {
                clearTimeout(revealTimer);
                if (revealed || withdrawn || !revealEl) { return; }
                revealed = true;
                revealEl.hidden = false;
              }

              function withdrawSpare() {
                clearTimeout(revealTimer);
                withdrawn = true;
                if (revealEl) { revealEl.hidden = true; }
                if (spareEl) { spareEl.hidden = true; }
              }

              function transition(returned) {
                if (!settled) {
                  showTransition(returned
                    ? {{HopInterstitialScript.Literal(texts.Returned)}}
                    : {{HopInterstitialScript.Literal(texts.Launched)}});
                }
                watch.start();
                if (returned) {
                  reveal();
                } else if (!begun) {
                  revealTimer = setTimeout(reveal, {{spareRevealDelayMilliseconds.ToString(CultureInfo.InvariantCulture)}});
                }
                begun = true;
              }

              document.addEventListener({{HopInterstitialScript.Literal(HopInterstitialScript.TransitionEventName)}}, function (event) {
                transition(!!(event.detail && event.detail[{{HopInterstitialScript.Literal(HopInterstitialScript.TransitionReturnedField)}}]));
              });
              button.addEventListener('click', function () { transition(false); });
              document.addEventListener('visibilitychange', function () {
                if (!begun) { return; }
                if (document.visibilityState === 'hidden') {
                  away = true;
                } else if (away && !settled) {
                  showTransition({{HopInterstitialScript.Literal(texts.Returned)}});
                }
              });
            })();
            """;
    }
}
