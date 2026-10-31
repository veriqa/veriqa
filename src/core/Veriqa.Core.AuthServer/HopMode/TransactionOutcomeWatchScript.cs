// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.Json;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The script fragment by which a page follows the outcome of its transaction without a hub: it polls
/// the status surface of the core (SPEC-007 §5.4) and hands the outcome to the page. The launch page of
/// the hop is the page that includes it (<see cref="HopInterstitialWatchScript"/>); the rules of the
/// polling are kept apart from what that page does with an outcome.
/// <para>
/// The status is asked for only while somebody waits for an outcome on the page: from the moment the page
/// starts the watch and only while the page is visible; a page that comes back into view asks at once.
/// A refused or failed request — a rate limit, a network error — leaves the page as it is, and the next
/// tick asks again: the budget of the status requests is shared by every page of the deployment. A
/// transaction the status no longer knows has run out.
/// </para>
/// <para>
/// A confirmed transaction is not an outcome yet: it still has to be completed, and may fail on the way.
/// The page is told once and the watch goes on, but not without end: the lifetime of a transaction no
/// longer bounds a confirmed one, and not every store of this delivery ends a stalled one by a sweep.
/// After the limit the page is handed (<see cref="ConfirmedWatchLimit"/>) it stops asking and keeps the
/// line it shows; the limit is judged after an answer, so a page back in view past it asks once more. The watch ends on a completed transaction, on one that failed or ran out, and on that
/// limit.
/// </para>
/// <para>
/// A transaction the core asks its confirming question about on its own page (SPEC-039 E29) is still
/// pending, and the status says so by a field of its own. A page that has that question to leave for is
/// told once and the watch ends: the answer is given on the page it leaves for, and a later tick may not
/// repeat the navigation. A page with nowhere to go — a sign-in, whose question waits in the sign-in
/// window (SPEC-025 HOP-186) — is told so once, and its watch goes on: the outcome is still to come.
/// </para>
/// <para>
/// A pending transaction whose answer is awaited in the channel says so by a field of its own as well —
/// the predicate under which the hub tells a subscribed page that the channel answered. The page is told
/// once and the watch goes on: the outcome is still to come (SPEC-025 HOP-194).
/// </para>
/// </summary>
internal static class TransactionOutcomeWatchScript
{
    /// <summary>
    /// Name of the function the fragment declares.
    /// </summary>
    public const string FunctionName = "watchTransactionOutcome";

    /// <summary>
    /// How often a watching page asks for the status of its transaction, in milliseconds.
    /// </summary>
    private const int PollIntervalMilliseconds = 3000;

    /// <summary>
    /// Field of the status by which a pending transaction says the core is asking its confirming question
    /// on its own page.
    /// </summary>
    private const string AwaitingWebConfirmationField = "awaiting_web_confirmation";

    /// <summary>
    /// Field of the status that tells the question takes its answer from one other browser only — the one
    /// the link of the letter was opened in: the page then does not leave for it.
    /// </summary>
    private const string AnswerBrowserBoundField = "answer_browser_bound";

    /// <summary>
    /// Field of the status by which a pending transaction says its answer is awaited in the channel.
    /// </summary>
    private const string AwaitingAnswerInChannelField = "awaiting_answer_in_channel";

    /// <summary>
    /// How long a page goes on asking after it saw its transaction confirmed, by the effective settings
    /// of the engine: a stalled confirmed transaction is ended by the background sweep, which takes one
    /// that has been confirmed for the finalization timeout and runs once per cleanup interval — so the
    /// sweep has ended it no later than the two together after the confirmation. One polling step is
    /// added for the request that reads that end.
    /// </summary>
    /// <param name="engineOptions">Effective settings of the transaction engine.</param>
    /// <returns>The limit of the watch after a confirmation.</returns>
    public static TimeSpan ConfirmedWatchLimit(TransactionEngineOptions engineOptions)
    {
        ArgumentNullException.ThrowIfNull(engineOptions);

        return TimeSpan.FromSeconds(engineOptions.ConfirmationFinalizationTimeoutSeconds)
            + TimeSpan.FromSeconds(engineOptions.CleanupIntervalSeconds)
            + TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
    }

    /// <summary>
    /// A span as the number of milliseconds the script carries.
    /// </summary>
    /// <param name="span">The span.</param>
    /// <returns>The literal.</returns>
    public static string MillisecondsLiteral(TimeSpan span) =>
        ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The declaration of
    /// <c>watchTransactionOutcome(statusPath, confirmedWatchLimitMs, onQuestion, onQuestionElsewhere, onAnswerAwaited, onConfirmed, onCompleted, onEnded)</c>,
    /// to be put inside the function scope of the script of a page.
    /// <list type="bullet">
    /// <item><description><c>statusPath</c> — the address of the status of the transaction, path base included.</description></item>
    /// <item><description><c>confirmedWatchLimitMs</c> — how long the page goes on asking after it saw the
    /// transaction confirmed, in milliseconds (<see cref="ConfirmedWatchLimit"/>).</description></item>
    /// <item><description><c>onQuestion()</c> — the core is asking the confirming question on its own
    /// page, and the question takes its answer from this browser as well; called once, and the watch ends.
    /// <c>null</c> — the page has no such page to leave for, and the watch goes on.</description></item>
    /// <item><description><c>onQuestionElsewhere()</c> — the core is asking the confirming question on its
    /// own page, and the page does not leave for it: it has no such page to leave for (<c>onQuestion</c> is
    /// <c>null</c>), or the status says the answer is taken only from the browser the link of the letter
    /// was opened in (<c>answer_browser_bound</c>); called once, the watch goes on.</description></item>
    /// <item><description><c>onAnswerAwaited()</c> — the answer is awaited in the channel; called once, the
    /// watch goes on.</description></item>
    /// <item><description><c>onConfirmed()</c> — the transaction is confirmed and is being completed;
    /// called once, the watch goes on.</description></item>
    /// <item><description><c>onCompleted()</c> — the transaction is completed.</description></item>
    /// <item><description><c>onEnded(state, reasonCode)</c> — the transaction ended any other way; both
    /// arguments are <c>null</c> for a transaction the status no longer knows.</description></item>
    /// </list>
    /// It returns <c>{ start }</c>: <c>start()</c> begins the watch (once). Built once per process.
    /// </summary>
    public static string Declaration { get; } = $$"""
        function {{FunctionName}}(statusPath, confirmedWatchLimitMs, onQuestion, onQuestionElsewhere, onAnswerAwaited, onConfirmed, onCompleted, onEnded) {
          var pollIntervalMs = {{PollIntervalMilliseconds.ToString(CultureInfo.InvariantCulture)}};
          var confirmedState = {{JsonSerializer.Serialize(TransactionState.Confirmed.ToString())}};
          var completedState = {{JsonSerializer.Serialize(TransactionState.Completed.ToString())}};
          var endedStates = {{States(TransactionState.Expired, TransactionState.Failed)}};
          var ended = false;
          var watching = false;
          var asking = false;
          var confirmedAt = null;
          var questionElsewhereTold = false;
          var answerAwaitedTold = false;
          var timer = null;

          function apply(status) {
            if (!status || ended) { return; }
            if (status.state === completedState) {
              ended = true;
              onCompleted();
            } else if (endedStates.indexOf(status.state) !== -1) {
              ended = true;
              onEnded(status.state, status.reason_code);
            } else if (status.state === confirmedState && confirmedAt === null) {
              confirmedAt = Date.now();
              onConfirmed();
            } else if (status.{{AwaitingWebConfirmationField}} && onQuestion && !status.{{AnswerBrowserBoundField}}) {
              ended = true;
              onQuestion();
            } else if (status.{{AwaitingWebConfirmationField}} && !questionElsewhereTold) {
              questionElsewhereTold = true;
              onQuestionElsewhere();
            } else if (status.{{AwaitingAnswerInChannelField}} && !answerAwaitedTold) {
              answerAwaitedTold = true;
              onAnswerAwaited();
            }
          }

          function ask() {
            clearTimeout(timer);
            timer = null;
            if (ended || asking || document.hidden) { return; }
            asking = true;
            fetch(statusPath, { headers: { 'Accept': 'application/json' }, cache: 'no-store' })
              .then(function (r) {
                if (r.status === 404) { ended = true; onEnded(null, null); return null; }
                return r.ok ? r.json() : null;
              })
              .then(apply)
              .catch(function () { })
              .then(function () {
                asking = false;
                // The limit is judged after the answer, not before the request: a page that comes
                // back to view past it still asks once and shows the outcome reached meanwhile.
                if (confirmedAt !== null && Date.now() - confirmedAt >= confirmedWatchLimitMs) { ended = true; }
                if (!ended && watching && !document.hidden) { timer = setTimeout(ask, pollIntervalMs); }
              });
          }

          function start() {
            if (watching || ended) { return; }
            watching = true;
            timer = setTimeout(ask, pollIntervalMs);
          }

          document.addEventListener('visibilitychange', function () {
            if (!document.hidden && watching) { ask(); }
          });

          return { start: start };
        }
        """;

    /// <summary>
    /// States as the JSON array of the names the status spells them by.
    /// </summary>
    /// <param name="states">The states.</param>
    /// <returns>The array literal.</returns>
    private static string States(params TransactionState[] states) =>
        JsonSerializer.Serialize(states.Select(state => state.ToString()));
}
