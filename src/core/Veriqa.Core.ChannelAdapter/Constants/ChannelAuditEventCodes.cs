// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Codes of the channel audit events the channel contour publishes about its own decisions — the
/// occurrences an operator has to learn about although the user notices nothing. The codes travel on
/// the channel audit event and land in the integrator's journal.
/// </summary>
/// <remarks>
/// A dictionary of its own next to <see cref="ChannelInteractionAuditCodes"/>: that one names the
/// refusals of the interaction surface, and a decision of the core about what a channel is asked to
/// do refuses nobody.
/// </remarks>
public static class ChannelAuditEventCodes
{
    /// <summary>
    /// The post-completion action the deployment asked for is outside the set the channel can render,
    /// and the channel's default was applied instead (SPEC-012 §4.9). The details of the event name
    /// both actions.
    /// </summary>
    public const string CompletionBehaviorClamped = "channel_completion_behavior_clamped";
}
