// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// A live link of a claims-completion mail found by its token: what the confirmation page shows and
/// what its use records (SPEC-041 L50).
/// </summary>
/// <param name="TransactionId">Transaction the link was sent for.</param>
/// <param name="Address">Address the link was sent to.</param>
internal sealed record EmailClaimLinkUse(string TransactionId, string Address);
