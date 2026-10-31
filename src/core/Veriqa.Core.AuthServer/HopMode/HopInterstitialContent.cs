// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.UI.Services;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// What the launch page of one presentation shows: where it leads, which channel that is and how it is
/// named and drawn, what kind of transaction it belongs to and, when the deployment shows it here, the
/// subject of the confirmation.
/// </summary>
/// <param name="Target">
/// The deep link of the channel, as the adapter derived it for this very presentation — the only source of
/// the target (SPEC-025 HOP-005, HOP-040).
/// </param>
/// <param name="ChannelType">Channel type the target leads into.</param>
/// <param name="ChannelDisplayName">Label a third-party adapter declared for the channel; null — none.</param>
/// <param name="SubjectText">Subject of the confirmation as plain text; null — the page shows none.</param>
/// <param name="TransactionType">
/// Type of the transaction (<see cref="Veriqa.Core.TransactionEngine.Domain.TransactionTypes"/>): the texts
/// that speak of signing in are shown for a sign-in only.
/// </param>
/// <param name="IconSvgPath">Glyph markup a third-party adapter declared for the channel; null — none.</param>
/// <param name="BotName">Name under which the person sees the chat of the channel; null — the channel states none.</param>
/// <param name="StartMessage">
/// The message with the code of the transaction a channel whose link carries the transaction keeps as a
/// spare way in; null — the channel has none, or the code could not be read.
/// </param>
internal sealed record HopInterstitialContent(
    string Target,
    string ChannelType,
    string? ChannelDisplayName,
    string? SubjectText,
    string TransactionType,
    string? IconSvgPath,
    string? BotName,
    ChannelStartMessageText? StartMessage);
