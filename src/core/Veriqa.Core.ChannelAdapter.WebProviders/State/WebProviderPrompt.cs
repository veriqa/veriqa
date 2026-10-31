// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WebProviders.State;

/// <summary>
/// The question the core put to one account of a transaction, as the page shows it.
/// </summary>
/// <param name="ChannelUserId">The account the question is put to; another account signed in for the
/// same transaction is not shown it.</param>
/// <param name="Text">Rendered text of the question.</param>
/// <param name="Answered">The account has sent its answer: the page waits for the outcome instead of
/// asking again.</param>
internal sealed record WebProviderPrompt(
    string ChannelUserId,
    string Text,
    bool Answered);
