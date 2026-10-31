// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veriqa.Core.AuthServer.UiConfig;

/// <summary>
/// JSON converter for nullable enums of the ui_config record (SPEC-012 CFG-203, evolution discipline).
/// Serializes the enum as a string; when deserializing an UNKNOWN string value it returns null
/// (rather than throwing) — the field then reads as not set (forward-compat).
/// Numeric values are also accepted tolerantly (an unknown number → null).
/// </summary>
/// <typeparam name="TEnum">Enum type.</typeparam>
public sealed class TolerantNullableEnumConverter<TEnum> : JsonConverter<TEnum?>
    where TEnum : struct, Enum
{
    /// <inheritdoc />
    public override TEnum? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // The method parses the enum tolerantly: an unknown value → null (fallback to the default at resolution)
        if (reader.TokenType is JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType is JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            // Case-insensitive parsing; an unknown name → null
            return Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : null;
        }

        if (reader.TokenType is JsonTokenType.Number && reader.TryGetInt64(out var numeric))
        {
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            return Enum.IsDefined(candidate) ? candidate : null;
        }

        // Any other token — ignore as unknown (graceful)
        // TrySkip, not Skip: Skip refuses a reader off its final block (a payload read as a stream).
        _ = reader.TrySkip();
        return null;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum? value, JsonSerializerOptions options)
    {
        // The enum is serialized as a string (readable, stable under evolution)
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString());
    }
}
