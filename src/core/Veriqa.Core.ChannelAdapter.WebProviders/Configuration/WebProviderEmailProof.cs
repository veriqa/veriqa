// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// Whether the email addresses a web provider states are taken as verified, as
/// <c>WebProvider:Email:Proof</c> declares it. The declaration is the integrator's statement of trust in
/// the addresses of the provider; an explicit <c>email_verified: false</c> of the provider is kept under
/// either value.
/// </summary>
internal enum WebProviderEmailProof
{
    /// <summary>
    /// The address is verified or not only by the provider's own <c>email_verified</c>; without it the
    /// proof stays unknown.
    /// </summary>
    NotProven = 0,

    /// <summary>
    /// The address is verified unless the provider states explicitly that it is not.
    /// </summary>
    Proven = 1
}
