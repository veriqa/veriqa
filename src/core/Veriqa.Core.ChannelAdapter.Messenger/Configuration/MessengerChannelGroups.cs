// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Messenger.Configuration;

/// <summary>
/// Messenger tenant-credential settings group (SPEC-003 §17.3, CFG-202): the tenant level
/// (in self-hosted ≡ core). The identity of the Facebook Page and the Meta app credentials —
/// per tenant (CA-160/CA-162). Messenger has no core-transport group: the channel is webhook-only.
/// </summary>
/// <param name="PageId">Identifier of the Facebook Page that sends the messages.</param>
/// <param name="PageName">Username of that Page, as it stands in an <c>m.me</c> link.</param>
/// <param name="PageAccessToken">Page access token of the Graph API calls.</param>
/// <param name="AppSecret">Meta app secret the signature of an inbound webhook is verified with.</param>
/// <param name="WebhookVerifyToken">Verify token compared during the webhook <c>GET</c> handshake.</param>
public sealed record MessengerTenantCredentials(
    string PageId,
    string PageName,
    string PageAccessToken,
    string AppSecret,
    string WebhookVerifyToken);
