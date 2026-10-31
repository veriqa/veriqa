// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Instagram.Configuration;

/// <summary>
/// Instagram tenant-credential settings group (SPEC-003 §17.3, CFG-202): the tenant level
/// (in self-hosted ≡ core). The Meta app credentials and the identity of the professional account —
/// per tenant (CA-160/CA-162). Instagram has no core-transport group: the channel is webhook-only.
/// </summary>
/// <param name="AppSecret">Meta app secret the signature of an inbound webhook is verified with.</param>
/// <param name="WebhookVerifyToken">Verify token compared during the webhook <c>GET</c> handshake.</param>
/// <param name="AccessToken">Access token of the Instagram API calls.</param>
/// <param name="AccountId">Identifier of the Instagram professional account that sends the messages.</param>
/// <param name="Username">Username of that account, as it stands in an <c>ig.me</c> link.</param>
public sealed record InstagramTenantCredentials(
    string AppSecret,
    string WebhookVerifyToken,
    string AccessToken,
    string AccountId,
    string Username);
