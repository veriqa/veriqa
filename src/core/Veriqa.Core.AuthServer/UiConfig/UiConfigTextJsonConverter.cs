// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veriqa.Core.AuthServer.UiConfig;

/// <summary>
/// JSON form of <see cref="UiConfigText"/>: a string is the form "replacement key", an object of strings
/// the form "texts by language". Reading follows the evolution discipline of the record: a member of the
/// object that is not a string is skipped, and a value of any other kind reads as no entry at all, rather
/// than failing the whole record.
/// </summary>
internal sealed class UiConfigTextJsonConverter : JsonConverter<UiConfigText>
{
    /// <inheritdoc />
    public override UiConfigText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.String)
        {
            return new UiConfigText(reader.GetString()!);
        }

        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            // TrySkip, not Skip: Skip refuses a reader off its final block (a payload read as a stream).
            _ = reader.TrySkip();
            return null;
        }

        var texts = new UiConfigText();

        while (reader.Read() && reader.TokenType is not JsonTokenType.EndObject)
        {
            var language = reader.GetString()!;
            reader.Read();

            if (reader.TokenType is JsonTokenType.String)
            {
                texts[language] = reader.GetString()!;
            }
            else
            {
                // TrySkip, not Skip: Skip refuses a reader off its final block (a payload read as a stream).
                _ = reader.TrySkip();
            }
        }

        return texts;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, UiConfigText value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.ReplacementKey is { } replacementKey)
        {
            writer.WriteStringValue(replacementKey);
            return;
        }

        writer.WriteStartObject();

        foreach (var (language, text) in value)
        {
            writer.WriteString(language, text);
        }

        writer.WriteEndObject();
    }
}
