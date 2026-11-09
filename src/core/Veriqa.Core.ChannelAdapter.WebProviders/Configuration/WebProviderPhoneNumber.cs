// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.WebProviders.Identity;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// The phone number of the account as <c>WebProvider:PhoneNumber</c> states it, for a channel that
/// obtains one.
/// </summary>
/// <param name="Capability">The phone-number fact the channel declares to the core.</param>
/// <param name="Claim">Path of the number in the answer of the provider.</param>
/// <param name="VerifiedClaim">Path of the provider's statement that the number is verified;
/// <c>null</c> — none is read, and the proof alone decides.</param>
/// <param name="Scopes">Scopes asked of the provider for a sign-in that requires the number or asks
/// for it as optional.</param>
internal sealed record WebProviderPhoneNumber(
    PhoneNumberCapability Capability,
    WebProviderClaimPath Claim,
    WebProviderClaimPath? VerifiedClaim,
    IReadOnlyList<string> Scopes);
