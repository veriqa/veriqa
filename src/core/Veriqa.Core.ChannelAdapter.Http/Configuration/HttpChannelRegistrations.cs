// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Configuration;

/// <summary>
/// What the HTTP channel sections of the configuration declared at start: the name of every HTTP
/// registration, enabled or not, and the full model of the enabled ones.
/// </summary>
/// <param name="ChannelTypes">
/// Channel types of every HTTP registration, disabled ones included: a disabled registration still
/// declares its channel to the core (SPEC-003 §17.7), so its name takes part in the collision checks.
/// </param>
/// <param name="Enabled">Registrations that are enabled at start and get an adapter.</param>
internal sealed record HttpChannelRegistrations(
    IReadOnlyList<string> ChannelTypes,
    IReadOnlyList<HttpChannelRegistration> Enabled);
