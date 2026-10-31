// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;

/// <summary>
/// An inbound request the receiver got, as the wrapper read it, with the first rule of the wire it
/// broke. The body is not kept (SPEC-042 N58).
/// </summary>
/// <param name="RouteTenant">Tenant of the route it came on; <c>null</c> — the route without a tenant segment.</param>
/// <param name="WebhookId">Its <c>webhook-id</c>; <c>null</c> — none, or the request was not read that far.</param>
/// <param name="Result">What the wrapper read; <c>null</c> — the request was refused or not read.</param>
/// <param name="Violation">The first rule broken; <c>null</c> — none.</param>
internal sealed record ReceivedEvent(
    string? RouteTenant,
    string? WebhookId,
    ChannelInboundResult? Result,
    string? Violation)
{
    /// <summary>
    /// The transaction the event names; <c>null</c> — the event names none (<c>auth_start_by_code</c>
    /// among them: the core finds its transaction by the code), or it was not read.
    /// </summary>
    public TransactionId? TransactionId => Result switch
    {
        ChannelAuthStartResult start => start.TransactionId,
        ChannelAuthConfirmResult confirm => confirm.TransactionId,
        ChannelAuthDeclineResult decline => decline.TransactionId,
        _ => null
    };

    /// <summary>
    /// The name of the event on the wire, as the wrapper read it; an unknown event reads as
    /// <c>unrelated</c>.
    /// </summary>
    public string Name => Result switch
    {
        ChannelAuthStartResult => HttpChannelWire.Events.AuthStart,
        ChannelAuthStartByCodeResult => HttpChannelWire.Events.AuthStartByCode,
        ChannelAuthConfirmResult => HttpChannelWire.Events.AuthConfirm,
        ChannelAuthDeclineResult => HttpChannelWire.Events.AuthDecline,
        ChannelPhoneSharedResult => HttpChannelWire.Events.PhoneShared,
        ChannelPhoneStepActionResult => HttpChannelWire.Events.PhoneStepAction,
        ChannelMenuItemPressedResult => HttpChannelWire.Events.MenuItemPressed,
        ChannelUnaddressedResult => HttpChannelWire.Events.Unaddressed,
        ChannelUnrelatedResult => HttpChannelWire.Events.Unrelated,
        _ => "a request"
    };

    /// <summary>
    /// The event in a message of the suite: its name and its route.
    /// </summary>
    /// <returns>For example <c>auth_start on the route of tenant 'shop-a'</c>.</returns>
    public string Describe() => Name + " on the route of " + TenantNames.Describe(RouteTenant);
}
