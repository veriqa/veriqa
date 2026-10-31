// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http;

/// <summary>
/// One outbound connection attempt of the HTTP channel wrapper as the egress policy sees it
/// (SPEC-042 L48).
/// </summary>
/// <param name="Tenant">
/// Tenant the call is made for — the tenant of the envelope (SPEC-042 L47); <c>null</c> — the implicit
/// tenant. A health probe is always made with <c>null</c>.
/// </param>
/// <param name="ChannelType">Channel type of the HTTP registration.</param>
/// <param name="Operation">Wire operation of the call — a value of <see cref="HttpChannelWire.Operations"/>.</param>
/// <param name="Target">URL the connection would be opened to — the base URL of the registration.</param>
public sealed record HttpChannelEgressRequest(string? Tenant, string ChannelType, string Operation, Uri Target);
