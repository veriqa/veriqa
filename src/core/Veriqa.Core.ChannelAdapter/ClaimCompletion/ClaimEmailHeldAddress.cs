// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// An address the email completion obtained while other fields of the web step were still open. The mail
/// side holds it, and the transaction records it only when the form is finished, so the user can still change
/// it, and the recorded outcomes of the transaction only ever grow.
/// </summary>
/// <param name="Address">The address, as it is to be recorded: the one the code or the link was sent to, or
/// the one the user's mail came from, never an input of a later request (SPEC-041 L50); shown only on the page
/// of a holder of the right to the form, never logged.</param>
/// <param name="Verified">Whether the address was confirmed.</param>
/// <param name="Method">The method that obtained it.</param>
internal sealed record ClaimEmailHeldAddress(string Address, bool Verified, EmailCompletionMethod Method);
