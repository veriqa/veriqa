// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Channel account the form of the claims completion shows above its fields (SPEC-041 L109), so the user
/// can tell which account the sign-in is being completed for — and cancel a sign-in someone else confirmed.
/// </summary>
/// <param name="ChannelName">Name of the channel, as the sign-in window labels it.</param>
/// <param name="Identifier">
/// Identifier the owner of the account does not choose at will (an email address, <c>@username</c>, a
/// masked phone number); null — the channel gave none.
/// </param>
/// <param name="DisplayName">Display name of the account, shown in addition only; null — none, or it repeats the identifier.</param>
internal sealed record ClaimCompletionAccountView(string ChannelName, string? Identifier, string? DisplayName);
