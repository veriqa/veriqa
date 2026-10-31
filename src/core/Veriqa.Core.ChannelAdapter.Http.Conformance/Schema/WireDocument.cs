// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;

/// <summary>
/// A document of the wire the suite checks against the schema — each an entry point of its
/// <c>$defs</c>.
/// </summary>
internal enum WireDocument
{
    /// <summary>
    /// Answer to <c>send-prompt</c> — <c>send_prompt_response</c>.
    /// </summary>
    SendPromptResponse,

    /// <summary>
    /// Answer to <c>send-message</c> — <c>send_message_response</c>.
    /// </summary>
    SendMessageResponse,

    /// <summary>
    /// Answer to <c>report-outcome</c> — <c>report_outcome_response</c>.
    /// </summary>
    ReportOutcomeResponse,

    /// <summary>
    /// Answer to <c>deep-link</c> — <c>deep_link_response</c>.
    /// </summary>
    DeepLinkResponse,

    /// <summary>
    /// Answer to <c>health</c> — <c>health_response</c>.
    /// </summary>
    HealthResponse,

    /// <summary>
    /// Answer of the adapter to a call of no known operation — <c>response_envelope</c>.
    /// </summary>
    ResponseEnvelope,

    /// <summary>
    /// Event the adapter posts to the receiver — <c>event_envelope</c>.
    /// </summary>
    EventEnvelope
}
