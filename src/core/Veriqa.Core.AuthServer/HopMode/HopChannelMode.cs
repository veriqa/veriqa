// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The hop mode of ONE channel for one act of projection or resolve, as the canonical resolver answers
/// it for the caller's full resolution context (SPEC-025 HOP-120, HOP-129a).
/// </summary>
/// <param name="IsEnabled">
/// Hop is on for the channel: the effective switch of the channel is on AND the channel is available to
/// the layer (<c>channels_enabled</c>, SPEC-025 HOP-124) — for a channel outside that set hop is a no-op.
/// </param>
/// <param name="RedirectStyle">Effective redirect style of the channel.</param>
/// <param name="BaseUrl">
/// Effective base of a hop URL — the issuer, with no trailing slash (SPEC-025 HOP-033a, HOP-123); null
/// when the issuer is not stated or is no absolute HTTPS URL.
/// </param>
/// <param name="HopPath">
/// Normalized base path of the resolve endpoint — the value of the node read once for the process, not
/// resolved per act (SPEC-025 HOP-128).
/// </param>
internal sealed record HopChannelMode(
    bool IsEnabled,
    HopRedirectStyle RedirectStyle,
    string? BaseUrl,
    string HopPath);
