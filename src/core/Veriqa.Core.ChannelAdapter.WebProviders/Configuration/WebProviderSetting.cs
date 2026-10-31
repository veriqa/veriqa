// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Reflection;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// One setting of the provider's own, stated by a member of <c>WebProvider:Settings</c> and applied to
/// the builder of the provider.
/// </summary>
/// <param name="Setter">The <c>Set{Name}(string)</c> of the builder of the provider.</param>
/// <param name="Value">The stated value.</param>
internal sealed record WebProviderSetting(MethodInfo Setter, string Value);
