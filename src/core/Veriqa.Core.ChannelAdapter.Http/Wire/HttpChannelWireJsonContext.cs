// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.Http.Wire;

/// <summary>
/// The only serializer of the wire (SPEC-042 R10): it knows the wire types of this namespace and
/// nothing else, so no type of the core can reach the wire "as it is" — every envelope is built from
/// these types by an explicit mapping.
/// <list type="bullet">
/// <item>names are <c>snake_case</c> (SPEC-042 §4);</item>
/// <item>a <c>required</c> member that is absent, and a member not annotated as nullable that is
/// <c>null</c>, fail the parse — the "missing required field" of SPEC-042 E52;</item>
/// <item>an unknown member is skipped (SPEC-042 L42);</item>
/// <item>a <c>null</c> member is written, not omitted: the adapter sees every field of its payload.</item>
/// </list>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(WireEventHead))]
[JsonSerializable(typeof(WireAuthStartEvent))]
[JsonSerializable(typeof(WireAuthStartByCodeEvent))]
[JsonSerializable(typeof(WireAuthAnswerEvent))]
[JsonSerializable(typeof(WirePhoneSharedEvent))]
[JsonSerializable(typeof(WirePhoneStepActionEvent))]
[JsonSerializable(typeof(WireMenuItemPressedEvent))]
[JsonSerializable(typeof(WireUnaddressedEvent))]
[JsonSerializable(typeof(WireCallEnvelope<WireSendPromptPayload>), TypeInfoPropertyName = "SendPromptCall")]
[JsonSerializable(typeof(WireCallEnvelope<WireSendMessagePayload>), TypeInfoPropertyName = "SendMessageCall")]
[JsonSerializable(typeof(WireCallEnvelope<WireReportOutcomePayload>), TypeInfoPropertyName = "ReportOutcomeCall")]
[JsonSerializable(typeof(WireCallEnvelope<WireDeepLinkPayload>), TypeInfoPropertyName = "DeepLinkCall")]
[JsonSerializable(typeof(WireCallEnvelope<WireHealthPayload>), TypeInfoPropertyName = "HealthCall")]
[JsonSerializable(typeof(WireResponseEnvelope<WireMessageResult>), TypeInfoPropertyName = "MessageResponse")]
[JsonSerializable(typeof(WireResponseEnvelope<WireReportOutcomeResult>), TypeInfoPropertyName = "ReportOutcomeResponse")]
[JsonSerializable(typeof(WireResponseEnvelope<WireDeepLinkResult>), TypeInfoPropertyName = "DeepLinkResponse")]
[JsonSerializable(typeof(WireResponseEnvelope<WireHealthResult>), TypeInfoPropertyName = "HealthResponse")]
internal sealed partial class HttpChannelWireJsonContext : JsonSerializerContext
{
}
