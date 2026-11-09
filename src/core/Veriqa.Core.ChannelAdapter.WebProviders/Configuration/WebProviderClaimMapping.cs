// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.WebProviders.Identity;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// One entry of <c>WebProvider:Claims</c>: a value of the answer of the provider the channel hands the
/// core under a claim name Veriqa issues.
/// </summary>
/// <param name="Source">Path of the value in the answer of the provider.</param>
/// <param name="Claim">Name of the claim Veriqa issues — one of the standard profile claims, or one
/// prefixed with the channel.</param>
/// <param name="Scopes">Scopes asked of the provider for a sign-in with a requirement to the claim.</param>
internal sealed record WebProviderClaimMapping(
    WebProviderClaimPath Source,
    string Claim,
    IReadOnlyList<string> Scopes);
