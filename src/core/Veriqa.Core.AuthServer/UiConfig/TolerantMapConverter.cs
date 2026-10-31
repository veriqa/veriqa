// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veriqa.Core.AuthServer.UiConfig;

/// <summary>
/// JSON converter for a map of the ui_config record keyed by a dimension value — a value per theme,
/// per channel (SPEC-012 CFG-203, evolution discipline). Each entry is read on its own by the
/// serializer, with the options the record is read with, so an entry reads exactly as it would inside
/// a map read whole. An entry the value type cannot take — a text where a number belongs, a number out
/// of its range, an object where a text belongs, a null — costs that entry alone (CFG-246): it is
/// skipped, and the entries beside it and the rest of the record are read, so a theme or a channel
/// without a readable entry takes the plain member of the same record. A value of the member that is
/// not an object states no map at all. Serialization writes the map as an object, the shape a
/// dictionary is written in.
/// <para>
/// Keys are kept as the record spells them, compared ordinally, the way a dictionary read by the
/// serializer keeps them: the case-insensitive match of a theme or a channel belongs to the reading
/// of the record by the resolution, and a map that merged two spellings here would decide which of
/// them wins in a second place.
/// </para>
/// </summary>
/// <typeparam name="TMap">
/// Type of the member — a map interface a <see cref="Dictionary{TKey, TValue}"/> of the same entries
/// implements.
/// </typeparam>
/// <typeparam name="TValue">Type of an entry's value.</typeparam>
public sealed class TolerantMapConverter<TMap, TValue> : JsonConverter<TMap>
    where TMap : class, IEnumerable<KeyValuePair<string, TValue>>
{
    /// <summary>
    /// Creates the converter.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TMap"/> is not a type the map read here can be handed out as.
    /// </exception>
    public TolerantMapConverter()
    {
        if (!typeof(TMap).IsAssignableFrom(typeof(Dictionary<string, TValue>)))
        {
            throw new InvalidOperationException(
                $"'{typeof(TMap)}' is not implemented by '{typeof(Dictionary<string, TValue>)}'.");
        }
    }

    /// <inheritdoc />
    public override TMap? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            // TrySkip, not Skip: Skip refuses a reader off its final block (a payload read as a stream).
            _ = reader.TrySkip();
            return null;
        }

        var map = new Dictionary<string, TValue>(StringComparer.Ordinal);

        while (reader.Read() && reader.TokenType is not JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();

            if (TryReadEntry(reader, options, out var value))
            {
                map[key] = value;
            }

            // The entry is read from a copy of the reader, so the reader itself is moved past the
            // entry here whether the read succeeded or not.
            // TrySkip, not Skip: Skip refuses a reader off its final block (a payload read as a stream).
            _ = reader.TrySkip();
        }

        return (TMap)(object)map;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TMap value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        foreach (var (key, entry) in value)
        {
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, entry, options);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads the value of one entry, standing at its first token. The read goes through a COPY of the
    /// reader: a read that fails part way leaves the copy wherever it stopped, and the caller moves the
    /// reader itself past the entry.
    /// </summary>
    /// <param name="reader">Reader standing at the value of the entry.</param>
    /// <param name="options">Options the record is read with.</param>
    /// <param name="value">Value read.</param>
    /// <returns>true when the entry holds a value of the type.</returns>
    private static bool TryReadEntry(
        Utf8JsonReader reader,
        JsonSerializerOptions options,
        [MaybeNullWhen(false)] out TValue value)
    {
        try
        {
            if (JsonSerializer.Deserialize<TValue>(ref reader, options) is { } read)
            {
                value = read;
                return true;
            }
        }
        catch (JsonException)
        {
            // The entry is not a value of the type; it is skipped, and the map keeps the rest.
        }

        value = default;
        return false;
    }
}
