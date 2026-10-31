// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// What the web provider channel sections of the configuration declared at start.
/// </summary>
/// <param name="ChannelTypes">Names of every web provider channel, enabled or not.</param>
/// <param name="Enabled">The enabled channels, read and checked.</param>
internal sealed record WebProviderRegistrations(
    IReadOnlyList<string> ChannelTypes,
    IReadOnlyList<WebProviderRegistration> Enabled);
