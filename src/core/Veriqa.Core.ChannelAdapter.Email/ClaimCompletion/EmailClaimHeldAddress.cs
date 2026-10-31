// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// The address the email completion obtained while other fields of the web step were still open, kept until
/// the form is finished — the stored form of <c>ClaimEmailHeldAddress</c>.
/// </summary>
/// <param name="Address">The address, as it is to be recorded.</param>
/// <param name="Verified">Whether the address was confirmed.</param>
/// <param name="Method">The method that obtained it.</param>
internal sealed record EmailClaimHeldAddress(string Address, bool Verified, EmailCompletionMethod Method);
