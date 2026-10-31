// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Xml;
using System.Xml.Linq;

namespace Veriqa.Core.ChannelAdapter.WeChat;

/// <summary>
/// The fields of an inbound message of the Official Account the adapter reads. The platform posts a
/// message as an XML document in plaintext mode; a body encrypted by the account's safe mode
/// carries none of these fields and reads as a message without a sender.
/// </summary>
/// <param name="FromUserName">OpenID of the user the message comes from.</param>
/// <param name="MsgType">Type of the message.</param>
/// <param name="Event">Name of the event — set for a message of the event type.</param>
/// <param name="EventKey">Key of the event; for a scanned QR code — its scene.</param>
/// <param name="BizMsgMenuId">
/// Identifier of the menu option the user pressed — set for the text message a pressed option of a
/// menu message arrives as.
/// </param>
/// <param name="MsgId">
/// Identifier of the message; a repeated delivery of the message carries the same one — set for a
/// message a user sent, not for an event.
/// </param>
internal sealed record WeChatInboundMessage(
    string? FromUserName,
    string? MsgType,
    string? Event,
    string? EventKey,
    string? BizMsgMenuId,
    string? MsgId)
{
    /// <summary>
    /// Element carrying the OpenID of the sender.
    /// </summary>
    private const string FromUserNameElement = "FromUserName";

    /// <summary>
    /// Element carrying the type of the message.
    /// </summary>
    private const string MsgTypeElement = "MsgType";

    /// <summary>
    /// Element carrying the name of the event.
    /// </summary>
    private const string EventElement = "Event";

    /// <summary>
    /// Element carrying the key of the event.
    /// </summary>
    private const string EventKeyElement = "EventKey";

    /// <summary>
    /// Element carrying the identifier of the pressed menu option.
    /// </summary>
    private const string BizMsgMenuIdElement = "bizmsgmenuid";

    /// <summary>
    /// Element carrying the identifier of the message.
    /// </summary>
    private const string MsgIdElement = "MsgId";

    /// <summary>
    /// Reader settings of an inbound body. The body is external input, so a document type
    /// definition is refused and nothing outside the document is resolved.
    /// </summary>
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    /// <summary>
    /// Reads an inbound body.
    /// </summary>
    /// <param name="body">Body of the request as received.</param>
    /// <returns>The message, or null for a body that is not a well-formed XML document.</returns>
    public static WeChatInboundMessage? TryParse(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var stream = new MemoryStream(body.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, ReaderSettings);

            if (XDocument.Load(reader).Root is not { } root)
            {
                return null;
            }

            return new WeChatInboundMessage(
                root.Element(FromUserNameElement)?.Value,
                root.Element(MsgTypeElement)?.Value,
                root.Element(EventElement)?.Value,
                root.Element(EventKeyElement)?.Value,
                root.Element(BizMsgMenuIdElement)?.Value,
                root.Element(MsgIdElement)?.Value);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
