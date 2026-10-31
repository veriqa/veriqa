// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.MessageTemplates;

/// <summary>
/// Values of the <c>link_purpose</c> dimension of the template key: WHERE the link a message is placed
/// in is going to be used. Purposes are constants, never magic strings — a render point names one the
/// same way it names its surface.
/// <para>
/// The purpose refines the channel and never appears without one, so a render point telling the
/// purposes of one kind apart names the surface, the channel and the purpose, in that order. A purpose
/// with no template stated cedes to the template of the channel.
/// </para>
/// </summary>
public static class MessageLinkPurposes
{
    /// <summary>
    /// A button on a page of the core, or the link handed to the relying party.
    /// </summary>
    public const string Button = "button";

    /// <summary>
    /// The target the hop resolve endpoint leads to after a hop QR code is scanned.
    /// </summary>
    public const string HopQr = "hop-qr";

    /// <summary>
    /// The content of a QR picture that carries the link itself, with no hop link in between.
    /// </summary>
    public const string DirectQr = "direct-qr";
}
