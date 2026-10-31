// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.ChannelAdapter.MultiTenancy;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// The press of an item of the menu (SPEC-042 C39): the suite sends the user a message with a button of
/// an item of the menu, the author presses it, and the adapter hands the press to the core as
/// <c>menu_item_pressed</c>. The value of the item is new for every run, so a press of a button of an
/// earlier run is not taken for the press of this one.
/// </summary>
internal static partial class EventFlowScenarios
{
    /// <summary>
    /// In the interactive mode alone, for the first tenant of the run and only for a registration that
    /// declares buttons: the user presses the button of an item of the menu of a message of the suite,
    /// and <c>menu_item_pressed</c> with the value of the item comes from that user, on the route of the
    /// tenant the message was sent for.
    /// </summary>
    public const string FlowMenuItemPress = "flow.menu-item-press";

    /// <summary>
    /// What the flow waits for, as the reason of a timeout names it.
    /// </summary>
    private const string PressOfTheRun = HttpChannelWire.Events.MenuItemPressed + " with the value of the run";

    /// <summary>
    /// <see cref="FlowMenuItemPress"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> MenuItemPressFlowAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var tenant = context.Options.PrimaryTenant;

        return await RunMenuItemPressAsync(context, tenant, cancellationToken) is { } reason
            ? ScenarioOutcome.Failed(TenantNames.Describe(tenant) + ": " + reason)
            : ScenarioOutcome.Passed;
    }

    /// <summary>
    /// Plays the press of an item of the menu for one tenant: the message with buttons of the suite with a
    /// new value of the item, the author asked to press its button, and the event of the adapter awaited.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> — the event came as the wire requires; otherwise why not.</returns>
    private static async Task<string?> RunMenuItemPressAsync(ConformanceContext context, string? tenant, CancellationToken cancellationToken)
    {
        var value = WrapperCallScenarios.NewMenuItemValue();
        var message = WrapperCallScenarios.ActionButtonsMessage(context, value);

        // What came to the receiver before the flow is not an event of the flow.
        var receivedBefore = context.Receiver.Events.Count;
        var flow = new ObservedMenuItemPress(value, context.Options.ChannelUserId, tenant);
        using var observation = context.Receiver.ObserveUntied(flow.OnEventAsync);

        if (await SendMenuMessageAsync(context, message, tenant, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        await Console.Out.WriteLineAsync(
            ActionLinePrefix + " " + FlowMenuItemPress + ": as the user of " + TenantNames.Describe(tenant)
            + ", press the button \"" + WrapperCallScenarios.MenuItemButtonLabel + "\" of the message \""
            + WrapperCallScenarios.ActionButtonsText + "\" the bot has just sent");

        return await WaitAsync(context, flow.Pressed, PressOfTheRun, receivedBefore, cancellationToken);
    }

    /// <summary>
    /// The message of the flow, sent in the scope of a tenant through the wrapper.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="message">The message.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> — the message is out; otherwise why not.</returns>
    private static async Task<string?> SendMenuMessageAsync(
        ConformanceContext context,
        ChannelMessage message,
        string? tenant,
        CancellationToken cancellationToken)
    {
        using var tenantScope = ChannelTenantContext.BeginScope(tenant);

        var adapter = context.Compositions.Working.Adapter;
        var (sent, capture) = await WrapperCallScenarios.CallAsync(context, () => adapter.SendMessageAsync(message, cancellationToken));

        return WrapperCallVerdict.Succeeded(capture, WrapperCallScenarios.RefusalOf(sent), WireDocument.SendMessageResponse, context.Schema) is { } reason
            ? "send-message: " + reason
            : null;
    }

    /// <summary>
    /// The events that name no transaction, as they come while the press is awaited. The first
    /// <c>menu_item_pressed</c> with the value of the run ends the wait; a press with another value — a
    /// button of an earlier message — and every other such event are left to <see cref="EventsAllValid"/>.
    /// </summary>
    /// <param name="value">The value of the item of the run.</param>
    /// <param name="channelUserId">The user of the run.</param>
    /// <param name="tenant">The tenant the message with the button was sent for.</param>
    private sealed class ObservedMenuItemPress(string value, string channelUserId, string? tenant)
    {
        /// <summary>
        /// The wait for the press.
        /// </summary>
        private readonly TaskCompletionSource<string?> _pressed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The wait for the press: <c>null</c> — the event came as the wire requires; otherwise why not.
        /// </summary>
        public Task<string?> Pressed => _pressed.Task;

        /// <summary>
        /// Takes an event that names no transaction.
        /// </summary>
        /// <param name="received">The event.</param>
        /// <param name="cancellationToken">Cancellation token of the request of the adapter.</param>
        /// <returns>A completed task.</returns>
        public Task OnEventAsync(ReceivedEvent received, CancellationToken cancellationToken)
        {
            if (received.Result is ChannelMenuItemPressedResult press
                && string.Equals(press.Value, value, StringComparison.Ordinal))
            {
                _pressed.TrySetResult(MismatchOf(received, press));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// What in the press of the run departs from the wire. The value itself is never part of the
        /// reason.
        /// </summary>
        /// <param name="received">The event.</param>
        /// <param name="press">The press, as the wrapper read it.</param>
        /// <returns><c>null</c> — nothing; otherwise what.</returns>
        private string? MismatchOf(ReceivedEvent received, ChannelMenuItemPressedResult press)
        {
            if (received.Violation is { } violation)
            {
                return "the " + received.Name + " event is not valid: " + violation;
            }

            if (!string.Equals(press.PressId, received.WebhookId, StringComparison.Ordinal))
            {
                return "the wrapper read menu_item_pressed with a press identifier other than the webhook-id of the event";
            }

            // SPEC-042 C39: the tenant of a press is the route it comes on, so the adapter posts it on the
            // route of the tenant of the send-message that drew the button; the core cannot check it.
            if (!string.Equals(received.RouteTenant, tenant, StringComparison.Ordinal))
            {
                return "menu_item_pressed of a button sent for " + TenantNames.Describe(tenant)
                    + " arrived on the route of " + TenantNames.Describe(received.RouteTenant);
            }

            return string.Equals(press.Identity.ChannelUserId, channelUserId, StringComparison.Ordinal)
                ? null
                : "menu_item_pressed came with an identity.channel_user_id other than the user of the run ("
                    + ConformanceConfigKeys.PathOf(ConformanceConfigKeys.ChannelUserId) + ")";
        }
    }
}
