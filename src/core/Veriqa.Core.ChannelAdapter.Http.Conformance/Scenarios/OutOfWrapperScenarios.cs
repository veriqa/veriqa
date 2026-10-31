// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

using Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Sending;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// The scenarios of the calls the wrapper does not make by construction (SPEC-042 R23): a call without
/// a signature, the repetition of <c>send-message</c> under one key, the unknown field, variant and
/// operation, the foreign major version, and an event envelope reflected onto the input of the adapter;
/// and a <c>deep-link</c> without the prefill text, which the wrapper sends only under a template
/// configuration the run does not have.
/// Each takes the body a scenario of the wrapper calls recorded and changes one thing — the
/// <c>deep-link</c> without the text is also asked for a new transaction, so the answer cannot be the
/// link the adapter keeps for the source call; only the
/// reflected event is built here. Every scenario runs in every mode; in the automatic mode the platform
/// log shows the effect of the calls that must have none.
/// </summary>
internal static class OutOfWrapperScenarios
{
    /// <summary>
    /// A call without the signature headers is refused with 403 and no envelope (SPEC-042 R14).
    /// </summary>
    public const string AuthUnsigned = "auth.unsigned";

    /// <summary>
    /// Two <c>send-message</c> under one key return equal results and send one message (SPEC-042 R15).
    /// </summary>
    public const string SendMessageRepeatSameKey = "send-message.repeat-same-key";

    /// <summary>
    /// A <c>deep-link</c> without the prefill text is still answered with a link (SPEC-042 C37).
    /// </summary>
    public const string DeepLinkWithoutRendered = "deep-link.without-rendered";

    /// <summary>
    /// An unknown field is ignored (SPEC-042 L42).
    /// </summary>
    public const string WireUnknownField = "wire.unknown-field";

    /// <summary>
    /// An unknown variant of the prompt is taken as a prompt without details (SPEC-042 L42).
    /// </summary>
    public const string WireUnknownVariant = "wire.unknown-variant";

    /// <summary>
    /// An unknown operation is answered with <c>unsupported_operation</c> (SPEC-042 L42).
    /// </summary>
    public const string WireUnknownOperation = "wire.unknown-operation";

    /// <summary>
    /// A foreign major version is answered with <c>unsupported_wire_version</c> (SPEC-042 L42).
    /// </summary>
    public const string WireForeignMajorVersion = "wire.foreign-major-version";

    /// <summary>
    /// An event envelope sent to the input of the adapter is not a success (SPEC-042 E50).
    /// </summary>
    public const string WireReflectedEvent = "wire.reflected-event";

    /// <summary>
    /// The scenarios, in the order they run.
    /// </summary>
    public static IReadOnlyList<ConformanceScenario> All { get; } =
    [
        new(AuthUnsigned, ConformanceModes.Any, UnsignedIsRefusedAsync),
        new(SendMessageRepeatSameKey, ConformanceModes.Any, SendMessageRepeatsUnderOneKeyAsync),
        new(DeepLinkWithoutRendered, ConformanceModes.Any, DeepLinkWithoutRenderedSucceedsAsync),
        new(WireUnknownField, ConformanceModes.Any, UnknownFieldIsIgnoredAsync),
        new(WireUnknownVariant, ConformanceModes.Any, UnknownVariantIsTakenAsync),
        new(WireUnknownOperation, ConformanceModes.Any, UnknownOperationIsRefusedAsync),
        new(WireForeignMajorVersion, ConformanceModes.Any, ForeignMajorVersionIsRefusedAsync),
        new(WireReflectedEvent, ConformanceModes.Any, ReflectedEventIsNotASuccessAsync)
    ];

    /// <summary>
    /// <see cref="AuthUnsigned"/>: the body of <c>health.success</c> with no signature headers.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> UnsignedIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        PlatformEffects.WithoutEffectAsync(
            context,
            async () =>
            {
                if (context.SourceBodyOf(WrapperCallScenarios.HealthSuccess) is not { } body)
                {
                    return NoSource(WrapperCallScenarios.HealthSuccess);
                }

                var capture = await context.Sender.SendUnsignedAsync(body, cancellationToken);
                return WrapperCallScenarios.Judge(WrapperCallVerdict.RefusedWithoutEnvelope(capture));
            },
            cancellationToken);

    /// <summary>
    /// <see cref="SendMessageRepeatSameKey"/>: the body of <c>send-message.success</c> twice under one new
    /// <c>webhook-id</c> — the wrapper keys every <c>send-message</c> anew, so the repetition is the
    /// sender's (SPEC-042 L43).
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> SendMessageRepeatsUnderOneKeyAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        PlatformEffects.WithOneMessageSentAsync(
            context,
            async () =>
            {
                if (context.SourceBodyOf(WrapperCallScenarios.SendMessageSuccess) is not { } body)
                {
                    return NoSource(WrapperCallScenarios.SendMessageSuccess);
                }

                var webhookId = ConformanceInboundEventIds.NewId();
                var first = await context.Sender.SendSignedAsync(body, webhookId, cancellationToken);
                var second = await context.Sender.SendSignedAsync(body, webhookId, cancellationToken);

                if (WrapperCallScenarios.BothSucceeded(context, WireDocument.SendMessageResponse, (first, null), (second, null)) is { } failed)
                {
                    return failed;
                }

                return JsonElement.DeepEquals(WrapperCallVerdict.ResultOf(first), WrapperCallVerdict.ResultOf(second))
                    ? ScenarioOutcome.Passed
                    : ScenarioOutcome.Failed("the repeated call under the same key returned another result");
            },
            cancellationToken);

    /// <summary>
    /// <see cref="DeepLinkWithoutRendered"/>: the body of <c>deep-link.success</c> for a new transaction
    /// with <c>rendered</c> set to <c>null</c>. The adapter builds the link from its own text; the answer is judged as any
    /// answer to <c>deep-link</c> is, and the link is not printed — it is the adapter's.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> DeepLinkWithoutRenderedSucceedsAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        if (context.SourceBodyOf(WrapperCallScenarios.DeepLinkSuccess) is not { } body)
        {
            return NoSource(WrapperCallScenarios.DeepLinkSuccess);
        }

        var capture = await SendSignedAsync(context, WireBodies.WithoutRenderedPrefill(body), cancellationToken);
        return WrapperCallScenarios.Judge(WrapperCallVerdict.Succeeded(capture, refusal: null, WireDocument.DeepLinkResponse, context.Schema));
    }

    /// <summary>
    /// <see cref="WireUnknownField"/>: the body of <c>health.success</c> with an unknown field on the top
    /// level and in the payload.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> UnknownFieldIsIgnoredAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        if (context.SourceBodyOf(WrapperCallScenarios.HealthSuccess) is not { } body)
        {
            return NoSource(WrapperCallScenarios.HealthSuccess);
        }

        var capture = await SendSignedAsync(context, WireBodies.WithUnknownFields(body), cancellationToken);
        return WrapperCallScenarios.Judge(WrapperCallVerdict.Succeeded(capture, refusal: null, WireDocument.HealthResponse, context.Schema));
    }

    /// <summary>
    /// <see cref="WireUnknownVariant"/>: the body of <c>send-prompt.success</c> with an unknown variant of
    /// the prompt.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> UnknownVariantIsTakenAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        if (context.SourceBodyOf(WrapperCallScenarios.SendPromptSuccess) is not { } body)
        {
            return NoSource(WrapperCallScenarios.SendPromptSuccess);
        }

        var capture = await SendSignedAsync(context, WireBodies.WithUnknownVariant(body), cancellationToken);
        return WrapperCallScenarios.Judge(WrapperCallVerdict.Succeeded(capture, refusal: null, WireDocument.SendPromptResponse, context.Schema));
    }

    /// <summary>
    /// <see cref="WireUnknownOperation"/>: the body of <c>health.success</c> with an unknown operation.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> UnknownOperationIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        PlatformEffects.WithoutEffectAsync(
            context,
            async () =>
            {
                if (context.SourceBodyOf(WrapperCallScenarios.HealthSuccess) is not { } body)
                {
                    return NoSource(WrapperCallScenarios.HealthSuccess);
                }

                var capture = await SendSignedAsync(context, WireBodies.WithUnknownOperation(body), cancellationToken);
                return WrapperCallScenarios.Judge(WrapperCallVerdict.AnsweredWithError(
                    capture,
                    HttpChannelWire.ErrorCodes.UnsupportedOperation,
                    WireDocument.ResponseEnvelope,
                    context.Schema));
            },
            cancellationToken);

    /// <summary>
    /// <see cref="WireForeignMajorVersion"/>: the body of <c>health.success</c> in the next major version.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> ForeignMajorVersionIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        PlatformEffects.WithoutEffectAsync(
            context,
            async () =>
            {
                if (context.SourceBodyOf(WrapperCallScenarios.HealthSuccess) is not { } body)
                {
                    return NoSource(WrapperCallScenarios.HealthSuccess);
                }

                var capture = await SendSignedAsync(context, WireBodies.WithForeignMajorVersion(body), cancellationToken);
                return WrapperCallScenarios.Judge(WrapperCallVerdict.AnsweredWithError(
                    capture,
                    HttpChannelWire.ErrorCodes.UnsupportedWireVersion,
                    WireDocument.ResponseEnvelope,
                    context.Schema));
            },
            cancellationToken);

    /// <summary>
    /// <see cref="WireReflectedEvent"/>: a signed <c>auth_start</c> envelope of the user of the run.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> ReflectedEventIsNotASuccessAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        PlatformEffects.WithoutEffectAsync(
            context,
            async () =>
            {
                var capture = await SendSignedAsync(context, WireBodies.ReflectedAuthStart(context.Options.ChannelUserId), cancellationToken);
                return WrapperCallScenarios.Judge(WrapperCallVerdict.NotASuccess(capture, context.Schema));
            },
            cancellationToken);

    /// <summary>
    /// Posts a body signed under a new <c>webhook-id</c>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="body">The body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the post did on the wire.</returns>
    private static Task<WireCallCapture> SendSignedAsync(ConformanceContext context, byte[] body, CancellationToken cancellationToken) =>
        context.Sender.SendSignedAsync(body, ConformanceInboundEventIds.NewId(), cancellationToken);

    /// <summary>
    /// The failure of a scenario whose source scenario made no call to the adapter.
    /// </summary>
    /// <param name="sourceScenarioId">The source scenario.</param>
    /// <returns>The outcome.</returns>
    private static ScenarioOutcome NoSource(string sourceScenarioId) =>
        ScenarioOutcome.Failed("no source call: " + sourceScenarioId + " made no request to the adapter");
}
