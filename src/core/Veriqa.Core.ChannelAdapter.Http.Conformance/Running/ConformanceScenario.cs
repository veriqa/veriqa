// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

/// <summary>
/// A scenario — the unit of the report. Its identifier is stable: the criteria of the reference
/// adapters refer to it. A scenario outside the mode of the run, one that checks a way of obtaining
/// the phone number, the start by a code or a kind of message the registration does not declare, and one
/// that follows a deep link the registration declares as carrying no transaction, is not run and not
/// reported; a condition of applicability within these is the scenario's own and ends in a not applicable
/// outcome.
/// </summary>
/// <param name="Id">Stable identifier, for example <c>send-prompt.success</c>.</param>
/// <param name="Modes">The modes the scenario belongs to.</param>
/// <param name="RunAsync">The scenario.</param>
internal sealed record ConformanceScenario(
    string Id,
    ConformanceModes Modes,
    Func<ConformanceContext, CancellationToken, Task<ScenarioOutcome>> RunAsync)
{
    /// <summary>
    /// The way of obtaining the phone number the scenario checks: the scenario belongs to a run only
    /// when the registration declares it. <see cref="PhoneNumberAcquisition.None"/> — the scenario
    /// checks none and belongs to every run of its modes.
    /// </summary>
    public PhoneNumberAcquisition PhoneNumberAcquisition { get; init; } = PhoneNumberAcquisition.None;

    /// <summary>
    /// The scenario checks the start by a code: it belongs to a run only when the registration declares
    /// <see cref="ChannelCapabilities.CodeStart"/>.
    /// </summary>
    public bool RequiresCodeStart { get; init; }

    /// <summary>
    /// The scenario starts a transaction by following its deep link: it belongs to a run only when the
    /// link of the registration carries the transaction — always, unless the registration declares the
    /// start by a code with a link that only opens the chat.
    /// </summary>
    public bool FollowsTransactionLink { get; init; }

    /// <summary>
    /// The kind of message the scenario checks: the scenario belongs to a run only when the registration
    /// declares it in <see cref="ChannelCapabilities.SupportedMessageKinds"/>. <c>null</c> — the scenario
    /// checks no kind of its own and belongs to every run of its modes.
    /// </summary>
    public ChannelMessageKind? RequiredMessageKind { get; init; }
}
