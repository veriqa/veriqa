// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Viber.Configuration;

/// <summary>
/// Viber tenant-credential settings group (SPEC-003 §17.3, CFG-202): the tenant level
/// (in self-hosted ≡ core). The bot — its token, its URI and the name its messages are signed
/// with — is per tenant (CA-160/CA-162). Viber has no core-transport group: the channel is
/// webhook-only.
/// </summary>
/// <param name="AuthToken">
/// Authentication token of the bot: it authorizes the Bot API calls and is the key the signature
/// of an inbound webhook is verified with.
/// </param>
/// <param name="BotUri">URI of the bot, as it stands in a <c>viber://pa</c> link.</param>
/// <param name="SenderName">Sender name shown on the messages of the bot.</param>
public sealed record ViberTenantCredentials(
    string AuthToken,
    string BotUri,
    string SenderName);
