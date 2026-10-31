// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net;
using System.Text.Json;

using Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// Judges a call the wrapper made against what the wire requires, from the exchange the recording
/// handler saw and from what the wrapper made of it. A verdict is a reason of failure or <c>null</c>;
/// a reason names the status, the schema place or the error code — never a value of a body
/// (SPEC-042 N58).
/// </summary>
internal static class WrapperCallVerdict
{
    /// <summary>
    /// Member of the answer envelope carrying the result.
    /// </summary>
    private const string ResultMember = "result";

    /// <summary>
    /// Member of the answer envelope carrying the error.
    /// </summary>
    private const string ErrorMember = "error";

    /// <summary>
    /// Member of the error carrying its code.
    /// </summary>
    private const string CodeMember = "code";

    /// <summary>
    /// Whether a call succeeded as the wire requires: the adapter answered 200 with a result that matches
    /// the schema, and the wrapper took it.
    /// </summary>
    /// <param name="capture">What the call did on the wire.</param>
    /// <param name="refusal">The refusal of the wrapper; <c>null</c> — the wrapper took the answer, or no wrapper made the call.</param>
    /// <param name="document">What the answer is, for the schema.</param>
    /// <param name="schema">Schema of the wire.</param>
    /// <returns><c>null</c> — it did; otherwise why not.</returns>
    /// <exception cref="ConformanceSetupException">The recording handler of the suite is not in the chain.</exception>
    public static string? Succeeded(WireCallCapture capture, TransactionError? refusal, WireDocument document, WireSchema schema)
    {
        if (Answered(capture) is { } missing)
        {
            return refusal is null ? missing : missing + "; the wrapper refused the call with " + refusal.Code;
        }

        if (ConformingAnswer(capture.Last!, document, schema, out var body) is { } nonConforming)
        {
            return nonConforming;
        }

        if (ErrorCodeOf(body) is { } code)
        {
            return "the adapter answered with the error " + code + " instead of a result";
        }

        // The answer is a result of the schema; what is left is a rule of the wrapper itself — for
        // example a deep link it does not admit. Its message is the wrapper's own text.
        return refusal is null ? null : "the wrapper refused the answer with " + refusal.Code + ": " + refusal.Message;
    }

    /// <summary>
    /// Whether the adapter answered a call with the error the wire requires: 200 with an error of the
    /// expected code, the answer matching the schema.
    /// </summary>
    /// <param name="capture">What the call did on the wire.</param>
    /// <param name="expectedCode">The error code of the wire the adapter owes.</param>
    /// <param name="document">What the answer is, for the schema.</param>
    /// <param name="schema">Schema of the wire.</param>
    /// <returns><c>null</c> — it did; otherwise why not.</returns>
    /// <exception cref="ConformanceSetupException">The recording handler of the suite is not in the chain.</exception>
    public static string? AnsweredWithError(WireCallCapture capture, string expectedCode, WireDocument document, WireSchema schema)
    {
        if (Answered(capture) is { } missing)
        {
            return missing;
        }

        if (ConformingAnswer(capture.Last!, document, schema, out var body) is { } nonConforming)
        {
            return nonConforming;
        }

        return ErrorCodeOf(body) switch
        {
            null => "the adapter answered with a result instead of the error " + expectedCode,
            var code when !string.Equals(code, expectedCode, StringComparison.Ordinal) =>
                "the adapter answered with the error " + code + " instead of " + expectedCode,
            _ => null
        };
    }

    /// <summary>
    /// Whether the answer to a document that is not a call — an event envelope sent to the input of the
    /// adapter — is not a success: anything but 200 with a result. An error envelope in a 200 answer is
    /// checked against the schema.
    /// </summary>
    /// <param name="capture">What the call did on the wire.</param>
    /// <param name="schema">Schema of the wire.</param>
    /// <returns><c>null</c> — it is not a success; otherwise why it is, or why the answer is wrong.</returns>
    /// <exception cref="ConformanceSetupException">The recording handler of the suite is not in the chain.</exception>
    public static string? NotASuccess(WireCallCapture capture, WireSchema schema)
    {
        if (Answered(capture) is { } missing)
        {
            return missing;
        }

        var exchange = capture.Last!;
        if (exchange.StatusCode is not (int)HttpStatusCode.OK || exchange.ResponseBody is not { } body)
        {
            return null;
        }

        var members = EnvelopeMembersOf(body);
        if (members.HasResult)
        {
            return "the adapter took an event envelope for a call and answered it with a result";
        }

        return members.HasError && schema.FindViolation(WireDocument.ResponseEnvelope, body) is { } violation
            ? "the answer does not match the wire schema: " + violation
            : null;
    }

    /// <summary>
    /// Whether a call signed in a way the adapter must refuse was refused as the wire requires
    /// (SPEC-042 R14): status 403 with no envelope in the body, and the call failed for the wrapper.
    /// </summary>
    /// <param name="capture">What the call did on the wire.</param>
    /// <param name="refusal">The refusal of the wrapper; <c>null</c> — the wrapper took the answer.</param>
    /// <returns><c>null</c> — it was; otherwise why not.</returns>
    /// <exception cref="ConformanceSetupException">The recording handler of the suite is not in the chain.</exception>
    public static string? RefusedUnauthenticated(WireCallCapture capture, TransactionError? refusal) =>
        RefusedWithoutEnvelope(capture)
        ?? (refusal is null ? "the wrapper took the answer 403 as a success" : null);

    /// <summary>
    /// Whether a call without a valid signature was refused as the wire requires (SPEC-042 R14): status
    /// 403 with no envelope in the body.
    /// </summary>
    /// <param name="capture">What the call did on the wire.</param>
    /// <returns><c>null</c> — it was; otherwise why not.</returns>
    /// <exception cref="ConformanceSetupException">The recording handler of the suite is not in the chain.</exception>
    public static string? RefusedWithoutEnvelope(WireCallCapture capture)
    {
        if (Answered(capture) is { } missing)
        {
            return missing;
        }

        var exchange = capture.Last!;

        if (exchange.StatusCode is (int)HttpStatusCode.OK)
        {
            return "a call with an invalid signature was accepted";
        }

        if (exchange.StatusCode is not (int)HttpStatusCode.Forbidden)
        {
            return "the adapter answered with status " + StatusOf(exchange) + " instead of 403";
        }

        if (exchange.ResponseBody is { } body && EnvelopeMembersOf(body) is { HasResult: true } or { HasError: true })
        {
            return "the answer 403 carries a wire envelope";
        }

        return null;
    }

    /// <summary>
    /// The result of a call that succeeded — the <c>result</c> member of its answer.
    /// </summary>
    /// <param name="capture">A call <see cref="Succeeded"/> passed.</param>
    /// <returns>A copy of the result.</returns>
    public static JsonElement ResultOf(WireCallCapture capture)
    {
        using var document = JsonDocument.Parse(capture.Last!.ResponseBody!.Value);
        return document.RootElement.GetProperty(ResultMember).Clone();
    }

    /// <summary>
    /// Whether the answer of an exchange is a 200 of the wire whose body matches the schema.
    /// </summary>
    /// <param name="exchange">The exchange.</param>
    /// <param name="document">What the answer is, for the schema.</param>
    /// <param name="schema">Schema of the wire.</param>
    /// <param name="body">The body of the answer, when it is one.</param>
    /// <returns><c>null</c> — it is; otherwise why not.</returns>
    private static string? ConformingAnswer(RecordedExchange exchange, WireDocument document, WireSchema schema, out ReadOnlyMemory<byte> body)
    {
        body = default;

        if (exchange.StatusCode is not (int)HttpStatusCode.OK)
        {
            return "the adapter answered with status " + StatusOf(exchange) + " instead of 200";
        }

        if (exchange.ResponseExceedsLimit || exchange.ResponseBody is not { } answer)
        {
            return "the body of the answer exceeds the limit of the channel contour";
        }

        if (schema.FindViolation(document, answer) is { } violation)
        {
            return "the answer does not match the wire schema: " + violation;
        }

        body = answer;
        return null;
    }

    /// <summary>
    /// Whether an answer of the adapter reached the wrapper at all.
    /// </summary>
    /// <param name="capture">What the call did on the wire.</param>
    /// <returns><c>null</c> — it did; otherwise why not.</returns>
    /// <exception cref="ConformanceSetupException">The recording handler of the suite is not in the chain.</exception>
    private static string? Answered(WireCallCapture capture)
    {
        capture.EnsureSeamPresent();

        return capture.Last switch
        {
            null => "the wrapper made no call to the adapter",
            { TransportFailure: { } failure } => "no connection to the adapter: " + failure,
            _ => null
        };
    }

    /// <summary>
    /// The status of an exchange, for a reason.
    /// </summary>
    /// <param name="exchange">The exchange.</param>
    /// <returns>The status.</returns>
    private static string StatusOf(RecordedExchange exchange) =>
        exchange.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none";

    /// <summary>
    /// Which members of a wire envelope a body has — none when it is not a JSON object.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <returns>The members.</returns>
    private static EnvelopeMembers EnvelopeMembersOf(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return root.ValueKind is JsonValueKind.Object
                ? new EnvelopeMembers(root.TryGetProperty(ResultMember, out _), root.TryGetProperty(ErrorMember, out _))
                : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// The error code of an answer that matches the schema; <c>null</c> — the answer is a result.
    /// </summary>
    /// <param name="body">The body of the answer.</param>
    /// <returns>The code.</returns>
    private static string? ErrorCodeOf(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty(ErrorMember, out var error)
            && error.TryGetProperty(CodeMember, out var code)
                ? code.GetString()
                : null;
    }

    /// <summary>
    /// The members of a wire envelope a body has.
    /// </summary>
    /// <param name="HasResult">The body has <c>result</c>.</param>
    /// <param name="HasError">The body has <c>error</c>.</param>
    private readonly record struct EnvelopeMembers(bool HasResult, bool HasError);
}
