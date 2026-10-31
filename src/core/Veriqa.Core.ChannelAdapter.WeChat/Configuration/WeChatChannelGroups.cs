// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WeChat.Configuration;

/// <summary>
/// WeChat tenant-credential settings group (SPEC-003 §17.3, CFG-202): the tenant level
/// (in self-hosted ≡ core). The identity of the Official Account and its secrets — per tenant
/// (CA-162). WeChat has no core-transport group: the channel is webhook-only.
/// </summary>
/// <param name="AppId">AppID of the Official Account.</param>
/// <param name="AppSecret">AppSecret of the Official Account; the access token is obtained with it.</param>
/// <param name="Token">
/// Token entered in the server configuration of the Official Account; the signature of an inbound
/// request is computed with it.
/// </param>
public sealed record WeChatTenantCredentials(
    string AppId,
    string AppSecret,
    string Token);
