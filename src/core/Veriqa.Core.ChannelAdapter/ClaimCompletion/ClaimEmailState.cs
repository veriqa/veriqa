// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// State of the email completion of a transaction, as the block of the web step shows it.
/// </summary>
/// <param name="SelectedMethod">
/// Method the user chose; null — none was chosen, the main one is shown. Either of
/// <see cref="EmailCompletionMethod.Code"/> and <see cref="EmailCompletionMethod.MagicLink"/> stands for the
/// one way "confirm via email" (<see cref="ClaimEmailMailContents"/>).
/// </param>
/// <param name="CodeAddressMasked">Masked address the last code was sent to; null — no code was sent.</param>
/// <param name="CodeLive">Whether the last code has attempts left and has not expired.</param>
/// <param name="LinkAddressMasked">Masked address the last link was sent to; null — no link was sent.</param>
/// <param name="LinkLive">Whether the last link is unused and has not expired.</param>
/// <param name="MailBudgetLeft">
/// Whether the limits of the transaction and of the channel account allow another mail.
/// </param>
/// <param name="LastAddress">
/// Address of the last mail, in full — the address step of the block offers it again, so a new mail goes
/// out without retyping it; null — no mail was sent. It is shown only on the page of a holder of the right
/// to the form, never logged.
/// </param>
internal sealed record ClaimEmailState(
    EmailCompletionMethod? SelectedMethod,
    string? CodeAddressMasked,
    bool CodeLive,
    string? LinkAddressMasked,
    bool LinkLive,
    bool MailBudgetLeft,
    string? LastAddress);
