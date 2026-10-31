// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// A hop URL just issued. The URL is a capability of the transaction and never reaches a log.
/// </summary>
/// <param name="Url">Hop URL — <c>{base}{HopPath}/{token}</c>.</param>
/// <param name="ExpiresAt">Own expiry of the token the URL carries (SPEC-025 HOP-149).</param>
internal sealed record HopIssuedUrl(string Url, DateTimeOffset ExpiresAt);
