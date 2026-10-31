// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;

using LateApexEarlySpeed.Json.Schema;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;

/// <summary>
/// The machine-readable schema of the wire, version 1 (JSON Schema draft 2020-12), and the check of a
/// document against one of its entry points. The schema is a resource of this assembly — the same file
/// the image ships for authors. A violation is reported by its place, never by the values of the
/// document: a document of the wire may carry what must not reach the output (SPEC-042 N58).
/// </summary>
internal sealed class WireSchema
{
    /// <summary>
    /// Name of the resource of the schema.
    /// </summary>
    private const string ResourceName = "http-channel-wire-v1.schema.json";

    /// <summary>
    /// Pointer from the root of the schema to its definitions.
    /// </summary>
    private const string DefinitionsPointer = "#/$defs/";

    /// <summary>
    /// Keyword of a reference.
    /// </summary>
    private const string RefKeyword = "$ref";

    /// <summary>
    /// The entry point of each document in the definitions of the schema.
    /// </summary>
    private static readonly FrozenDictionary<WireDocument, string> EntryPoints = new Dictionary<WireDocument, string>
    {
        [WireDocument.SendPromptResponse] = "send_prompt_response",
        [WireDocument.SendMessageResponse] = "send_message_response",
        [WireDocument.ReportOutcomeResponse] = "report_outcome_response",
        [WireDocument.DeepLinkResponse] = "deep_link_response",
        [WireDocument.HealthResponse] = "health_response",
        [WireDocument.ResponseEnvelope] = "response_envelope",
        [WireDocument.EventEnvelope] = "event_envelope"
    }.ToFrozenDictionary();

    /// <summary>
    /// A validator per document.
    /// </summary>
    private readonly FrozenDictionary<WireDocument, JsonValidator> _validators;

    /// <summary>
    /// Creates the checks.
    /// </summary>
    /// <param name="validators">A validator per document.</param>
    private WireSchema(FrozenDictionary<WireDocument, JsonValidator> validators) => _validators = validators;

    /// <summary>
    /// Loads the schema from the resource of the assembly.
    /// </summary>
    /// <returns>The checks.</returns>
    /// <exception cref="ConformanceSetupException">The resource is missing or is not a schema.</exception>
    public static WireSchema Load()
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(ReadResource())?.AsObject()
                ?? throw new JsonException("The schema is not a JSON object.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new ConformanceSetupException("The wire schema of the suite cannot be read: " + exception.Message, exception);
        }

        // Each entry point is the whole schema with a root reference to its definition: the root of the
        // schema constrains nothing else, and every reference inside keeps resolving in its own document.
        var validators = EntryPoints.ToFrozenDictionary(
            static entry => entry.Key,
            entry =>
            {
                var entrySchema = root.DeepClone().AsObject();
                entrySchema[RefKeyword] = DefinitionsPointer + entry.Value;
                return new JsonValidator(entrySchema.ToJsonString());
            });

        return new WireSchema(validators);
    }

    /// <summary>
    /// Checks a document against its entry point.
    /// </summary>
    /// <param name="document">What the document is.</param>
    /// <param name="json">The document.</param>
    /// <returns><c>null</c> — the document conforms; otherwise the place of the first violation.</returns>
    public string? FindViolation(WireDocument document, ReadOnlyMemory<byte> json)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return "the body is not JSON";
        }

        using (parsed)
        {
            var result = _validators[document].Validate(parsed);
            if (result.IsValid)
            {
                return null;
            }

            var error = result.ValidationErrors.FirstOrDefault();
            return error is null
                ? "the document does not match " + EntryPoints[document]
                : "the document does not match " + EntryPoints[document] + ": keyword '" + error.Keyword
                    + "' at '" + error.InstanceLocation + "' (schema location '" + error.RelativeKeywordLocation + "')";
        }
    }

    /// <summary>
    /// The text of the schema resource.
    /// </summary>
    /// <returns>The text.</returns>
    private static string ReadResource()
    {
        using var stream = typeof(WireSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new ConformanceSetupException("The wire schema is missing from the suite: resource '" + ResourceName + "'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
