// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Security.Cryptography;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// The start by a code (SPEC-042 C39): the user sends the bot the code shown on the sign-in page, and
/// the adapter hands it to the core as <c>auth_start_by_code</c>. The suite plays the core, so the code
/// is one it makes up: no store of codes stands behind it, and what is checked is the event alone — the
/// code as the user sent it, without the wrapper of the template, and the user who sent it.
/// </summary>
internal static partial class EventFlowScenarios
{
    /// <summary>
    /// For the first tenant of the run, only for a registration that declares the start by a code: the
    /// user sends a made-up code to the bot of a deep link, and <c>auth_start_by_code</c> with that code
    /// comes from that user.
    /// </summary>
    public const string FlowCodeStart = "flow.code-start";

    /// <summary>
    /// Number of digits of the made-up code.
    /// </summary>
    private const int MadeUpCodeLength = 8;

    /// <summary>
    /// The characters the made-up code is drawn from.
    /// </summary>
    private const string CodeDigits = "0123456789";

    /// <summary>
    /// Character users put between the groups of digits of a code besides whitespace; dropped, like
    /// whitespace, before the code of the event is compared.
    /// </summary>
    private const char CodeGroupSeparator = '-';

    /// <summary>
    /// Reason of a declared message template the core could not put a code into: the suite does not
    /// send a message the core would never show.
    /// </summary>
    private static readonly string UnusableTemplateReason =
        "the message template (Http:Capabilities:CodeStart:MessageTemplate) cannot carry a code: it is blank, longer than "
        + ChannelCodeStart.MaxMessageTemplateLength.ToString(CultureInfo.InvariantCulture) + " characters, or holds "
        + ChannelCodeStart.CodePlaceholder + " not exactly once; Veriqa shows no code by such a template";

    /// <summary>
    /// <see cref="FlowCodeStart"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> CodeStartFlowAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var tenant = context.Options.PrimaryTenant;

        return await RunCodeStartAsync(context, tenant, cancellationToken) is { } reason
            ? ScenarioOutcome.Failed(TenantNames.Describe(tenant) + ": " + reason)
            : ScenarioOutcome.Passed;
    }

    /// <summary>
    /// Plays the start by a code for one tenant: the deep link of a new transaction names the bot, the
    /// user sends it the message of the template with a made-up code, and the event of the adapter is
    /// awaited. The link is not followed — whether it carries the transaction does not matter here.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> — the event came as the wire requires; otherwise why not.</returns>
    private static async Task<string?> RunCodeStartAsync(ConformanceContext context, string? tenant, CancellationToken cancellationToken)
    {
        var (url, refusal) = await DeepLinkAsync(context, TransactionId.NewId(), tenant, cancellationToken);

        if (url is null)
        {
            return refusal;
        }

        var code = RandomNumberGenerator.GetString(CodeDigits, MadeUpCodeLength);
        var template = context.Compositions.Working.Adapter.Capabilities.CodeStart?.MessageTemplate;

        if (template is not null && !CarriesCode(template))
        {
            return UnusableTemplateReason;
        }

        var text = template is null
            ? code
            : template.Replace(ChannelCodeStart.CodePlaceholder, code, StringComparison.Ordinal);

        // What came to the receiver before the flow is not an event of the flow.
        var receivedBefore = context.Receiver.Events.Count;
        var flow = new ObservedCodeStart(code, text, context.Options.ChannelUserId);
        using var observation = context.Receiver.ObserveUntied(flow.OnEventAsync);

        if (context.Options.Mode is ConformanceModes.Automatic)
        {
            await context.RequiredPlatformStub.SendTextAsync(url, text, cancellationToken);
        }
        else
        {
            await Console.Out.WriteLineAsync(
                ActionLinePrefix + " " + FlowCodeStart + ": as the user of " + TenantNames.Describe(tenant)
                + ", send the message \"" + text + "\" to the bot of the link: " + url);
        }

        return await WaitAsync(context, flow.Started, HttpChannelWire.Events.AuthStartByCode, receivedBefore, cancellationToken);
    }

    /// <summary>
    /// Whether a declared template can carry a code by the rule the core shows it by: not blank, not
    /// longer than <see cref="ChannelCodeStart.MaxMessageTemplateLength"/>, with
    /// <see cref="ChannelCodeStart.CodePlaceholder"/> exactly once (SPEC-003 CA-217).
    /// </summary>
    /// <param name="template">The template.</param>
    /// <returns><c>true</c> — it can.</returns>
    private static bool CarriesCode(string template) =>
        !string.IsNullOrWhiteSpace(template)
        && template.Length <= ChannelCodeStart.MaxMessageTemplateLength
        && template.Split(ChannelCodeStart.CodePlaceholder).Length == 2;

    /// <summary>
    /// The events that name no transaction, as they come while the start by a code is awaited. The first
    /// <c>auth_start_by_code</c> ends the wait, and so does an <c>unaddressed</c> that answers the message:
    /// from the user of the run, with the text sent. Any other such event — a bare <c>/start</c> of the
    /// author who opened the chat of the link among them — is not the answer to the message and is left
    /// to <see cref="EventsAllValid"/>.
    /// </summary>
    /// <param name="code">The made-up code the user sent.</param>
    /// <param name="text">The message the user sent.</param>
    /// <param name="channelUserId">The user of the run.</param>
    private sealed class ObservedCodeStart(string code, string text, string channelUserId)
    {
        /// <summary>
        /// The wait for the start.
        /// </summary>
        private readonly TaskCompletionSource<string?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The wait for the start: <c>null</c> — the event came as the wire requires; otherwise why not.
        /// </summary>
        public Task<string?> Started => _started.Task;

        /// <summary>
        /// Takes an event that names no transaction.
        /// </summary>
        /// <param name="received">The event.</param>
        /// <param name="cancellationToken">Cancellation token of the request of the adapter.</param>
        /// <returns>A completed task.</returns>
        public Task OnEventAsync(ReceivedEvent received, CancellationToken cancellationToken)
        {
            if (TryEnd(received, out var reason))
            {
                _started.TrySetResult(reason);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Whether an event ends the wait, and how. Neither the code sent nor the code that came is ever
        /// part of the reason — only that they differ, and their lengths.
        /// </summary>
        /// <param name="received">The event.</param>
        /// <param name="reason"><c>null</c> — the start came as the wire requires; otherwise why not.</param>
        /// <returns><c>true</c> — the event ends the wait.</returns>
        private bool TryEnd(ReceivedEvent received, out string? reason)
        {
            switch (received.Result)
            {
                case ChannelAuthStartByCodeResult start:
                    reason = received.Violation is { } violation
                        ? "the " + received.Name + " event is not valid: " + violation
                        : MismatchOf(start);
                    return true;

                case ChannelUnaddressedResult unaddressed when AnswersMessage(unaddressed):
                    reason = received.Name + " came instead of " + HttpChannelWire.Events.AuthStartByCode;
                    return true;

                default:
                    reason = null;
                    return false;
            }
        }

        /// <summary>
        /// Whether an <c>unaddressed</c> is the message the user sent, read as no start: from the user of
        /// the run, with its text up to whitespace and the group separator.
        /// </summary>
        /// <param name="unaddressed">The event.</param>
        /// <returns><c>true</c> — it is.</returns>
        private bool AnswersMessage(ChannelUnaddressedResult unaddressed) =>
            string.Equals(unaddressed.Identity.ChannelUserId, channelUserId, StringComparison.Ordinal)
            && unaddressed.Text is { } messageText
            && string.Equals(Compact(messageText), Compact(text), StringComparison.Ordinal);

        /// <summary>
        /// What in a start by a code differs from the message the user sent.
        /// </summary>
        /// <param name="start">The start.</param>
        /// <returns><c>null</c> — nothing; otherwise what.</returns>
        private string? MismatchOf(ChannelAuthStartByCodeResult start)
        {
            if (!string.Equals(start.Identity.ChannelUserId, channelUserId, StringComparison.Ordinal))
            {
                return "auth_start_by_code came with an identity.channel_user_id other than the user of the run "
                    + "(Conformance:ChannelUserId)";
            }

            if (string.Equals(Compact(start.Code), code, StringComparison.Ordinal))
            {
                return null;
            }

            return "the code of auth_start_by_code is not the code the user sent, without the wrapper of the "
                + "message template: " + start.Code.Length.ToString(CultureInfo.InvariantCulture) + " characters came for "
                + code.Length.ToString(CultureInfo.InvariantCulture) + " digits sent";
        }

        /// <summary>
        /// A code or a message with whitespace and the group separator dropped.
        /// </summary>
        /// <param name="value">The code or the message as the event carries it.</param>
        /// <returns>The value without them.</returns>
        private static string Compact(string value) =>
            string.Concat(value.Where(static character => !char.IsWhiteSpace(character) && character != CodeGroupSeparator));
    }
}
