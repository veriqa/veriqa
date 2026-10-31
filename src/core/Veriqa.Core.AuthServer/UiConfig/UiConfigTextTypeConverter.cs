// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.Globalization;

namespace Veriqa.Core.AuthServer.UiConfig;

/// <summary>
/// Reads the form "replacement key" of <see cref="UiConfigText"/> from a text — the way the configuration
/// binder reads a value stated as a text rather than as a section: an entry
/// <c>Veriqa:UiConfigurations:Records:&lt;code&gt;:texts:&lt;text&gt;</c> holding a string.
/// </summary>
internal sealed class UiConfigTextTypeConverter : TypeConverter
{
    /// <inheritdoc />
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc />
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string replacementKey
            ? new UiConfigText(replacementKey)
            : base.ConvertFrom(context, culture, value);
}
