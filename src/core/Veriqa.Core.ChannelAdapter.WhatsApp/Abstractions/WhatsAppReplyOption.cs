// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;

/// <summary>
/// One reply option of an interactive message — a reply button or a row of a list: the identifier the
/// press brings back and its caption. Both are already fitted into the limits of the form by the
/// adapter; a provider passes them to the platform as is.
/// </summary>
/// <param name="Id">Identifier the press brings back, the ownership marker included.</param>
/// <param name="Title">Caption of the option.</param>
public sealed record WhatsAppReplyOption(string Id, string Title);
