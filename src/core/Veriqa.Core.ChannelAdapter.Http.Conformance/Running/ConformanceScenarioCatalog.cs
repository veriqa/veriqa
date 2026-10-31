// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

/// <summary>
/// Every scenario of the suite, in the order they run. A group of scenarios joins the suite by adding
/// its list here.
/// </summary>
internal static class ConformanceScenarioCatalog
{
    /// <summary>
    /// The scenarios of the suite. The sender follows the calls of the wrapper, whose bodies it mutates;
    /// the presses of an item of the menu the suite hands to the wrapper itself need no receiver and come
    /// before the flows; the flows come before the scenarios of the platform, whose switches last until a
    /// reset; the checks over the events that came — the phone number of the starts of the flows, then all
    /// events — come last, when every event has come.
    /// </summary>
    private static readonly IReadOnlyList<ConformanceScenario> Scenarios =
    [
        .. WrapperCallScenarios.All,
        .. OutOfWrapperScenarios.All,
        .. MenuItemPressedScenarios.All,
        .. EventFlowScenarios.Flows,
        .. PlatformScenarios.All,
        EventFlowScenarios.PhoneAutomatic,
        EventFlowScenarios.AllValid
    ];

    /// <summary>
    /// The scenarios a run carries: those of its mode that check no way of obtaining the phone number,
    /// or one the registration declares; that check the start by a code only when the registration
    /// declares it; that check a kind of message only when the registration declares that kind; and that
    /// follow the deep link of a transaction only when that link carries the transaction — a link that
    /// only opens the chat cannot start one. A scenario left out is not reported at all — the report of a
    /// registration that declares neither a phone number, nor the start by a code, nor a link to the claim
    /// form or buttons is the report it had before the scenarios of any of them existed.
    /// </summary>
    /// <param name="mode">Mode of the run.</param>
    /// <param name="capabilities">Facts of the registration under test.</param>
    /// <returns>The scenarios, in order.</returns>
    public static IEnumerable<ConformanceScenario> For(ConformanceModes mode, ChannelCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var declared = capabilities.PhoneNumber.Acquisition;
        var declaresCodeStart = capabilities.CodeStart is not null;
        var linkCarriesTransaction = capabilities.CodeStart is not { LinkCarriesTransaction: false };
        var kinds = capabilities.SupportedMessageKinds;

        return Scenarios.Where(scenario =>
            (scenario.Modes & mode) == mode
            && (declared & scenario.PhoneNumberAcquisition) == scenario.PhoneNumberAcquisition
            && (declaresCodeStart || !scenario.RequiresCodeStart)
            && (linkCarriesTransaction || !scenario.FollowsTransactionLink)
            && (scenario.RequiredMessageKind is not { } kind || kinds.Contains(kind)));
    }
}
