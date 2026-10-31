// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.State;

/// <summary>
/// A session of the page of a web provider channel: the browser that came back from the provider
/// holds its mark, and the mark ties that browser to one transaction and one account. The address of
/// return writes it before the sign-in reaches the core, so the answer and the presses that follow find
/// the account they act for. The mark itself carries nothing but its random value.
/// </summary>
/// <param name="ChannelType">Channel the account signed in on.</param>
/// <param name="ChannelUserId">Identifier of the account at the provider.</param>
/// <param name="TransactionId">The transaction the browser came for.</param>
/// <param name="Identity">The account as the provider described it — what the events after the
/// address of return carry to the core.</param>
/// <param name="ExpiresAt">Moment the session ends: the end of the transaction plus the retention of a
/// completed one (<see cref="WebProviderStateLifetime"/>).</param>
internal sealed record WebProviderPageSession(
    string ChannelType,
    string ChannelUserId,
    TransactionId TransactionId,
    ChannelIdentitySnapshot Identity,
    DateTimeOffset ExpiresAt);
