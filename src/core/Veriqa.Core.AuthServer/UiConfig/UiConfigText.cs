// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Veriqa.Core.AuthServer.UiConfig;

/// <summary>
/// One entry of the texts of a <c>ui_config</c> record (<see cref="UiConfigRecord.Texts"/>), in one of
/// the two forms of the key <c>AuthPage.Texts</c> (SPEC-012 §4.3):
/// <list type="bullet">
/// <item><description>
/// a JSON string — a replacement KEY (<see cref="ReplacementKey"/>): a Natural Key the page localizes
/// through the host locale files in place of its own;
/// </description></item>
/// <item><description>
/// a JSON object — ready texts by language tag (the entries of this map): the page shows the text of
/// its content language as it is, and its own text for a language the map does not state.
/// </description></item>
/// </list>
/// <para>
/// Why a map with a key beside it rather than a plain value: the record is read from two sources, a
/// JSON store and the application configuration, and the configuration binder reads a text into a type
/// only through its type converter and a section only into a type that is a dictionary. This one type is
/// both, so either form reads from either source; the JSON form is spelled by its own converter.
/// </para>
/// </summary>
[JsonConverter(typeof(UiConfigTextJsonConverter))]
[TypeConverter(typeof(UiConfigTextTypeConverter))]
public sealed class UiConfigText : Dictionary<string, string>
{
    /// <summary>
    /// Creates the form "texts by language", empty — the configuration binder fills it.
    /// </summary>
    public UiConfigText()
        : base(StringComparer.Ordinal)
    {
    }

    /// <summary>
    /// Creates the form "replacement key".
    /// </summary>
    /// <param name="replacementKey">Natural Key the page localizes in place of its own.</param>
    public UiConfigText(string replacementKey)
        : this()
    {
        ReplacementKey = replacementKey ?? throw new ArgumentNullException(nameof(replacementKey));
    }

    /// <summary>
    /// Replacement key of the form "replacement key"; null — the entry is the form "texts by language",
    /// and its texts are the entries of this map.
    /// </summary>
    public string? ReplacementKey { get; }
}
