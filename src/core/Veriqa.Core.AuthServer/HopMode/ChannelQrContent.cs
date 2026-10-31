// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The content of the QR code of one channel, with the own expiry of the hop token when the content is a
/// hop URL. The content is a capability of the transaction when it is a hop URL and never reaches a log.
/// </summary>
/// <param name="Content">Content of the QR code: a hop URL or the deep link of the channel.</param>
/// <param name="HopTokenExpiresAt">
/// Own expiry of the hop token the content carries (SPEC-025 HOP-149); null — the content is the deep link.
/// </param>
internal sealed record ChannelQrContent(string Content, DateTimeOffset? HopTokenExpiresAt);
