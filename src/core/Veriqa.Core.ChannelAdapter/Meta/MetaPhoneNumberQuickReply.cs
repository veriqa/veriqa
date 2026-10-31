// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Contracts;

namespace Veriqa.Core.ChannelAdapter.Meta;

/// <summary>
/// The phone number request of the Messenger Platform family (SPEC-041 R6): a text message with the
/// quick reply the platform fills with the number of the user's profile, and the reading of the
/// delivery that answers it. The wire form is the same for every channel of the family.
/// </summary>
internal static class MetaPhoneNumberQuickReply
{
    /// <summary>
    /// Content type of a quick reply that offers the phone number of the user's profile: the
    /// platform draws the button itself and sends the number back when it is pressed.
    /// </summary>
    public const string QuickReplyContentTypePhoneNumber = "user_phone_number";

    /// <summary>
    /// Content type of a quick reply with a caption and a payload of the sender's own.
    /// </summary>
    public const string QuickReplyContentTypeText = "text";

    /// <summary>
    /// Maximum length (characters) of the caption of a quick reply, as Meta documents it.
    /// </summary>
    public const int QuickReplyTitleLimit = 20;

    /// <summary>
    /// Mark a caption cut to <see cref="QuickReplyTitleLimit"/> ends with.
    /// </summary>
    private const string TruncationMark = "…";

    /// <summary>
    /// Marker of a pressed quick reply in the raw body of a webhook delivery: the name of the
    /// member a message carries only when it was sent by a quick reply button. It tells that a
    /// delivery may answer the phone number request, not that it does: quick replies of other code
    /// on the same page or account carry the member too.
    /// </summary>
    public static ReadOnlySpan<byte> QuickReplyMarker => "\"quick_reply\""u8;

    /// <summary>
    /// Builds the <c>message</c> object of the Send API request that asks the user to share their
    /// phone number: the text with two quick replies under it — the button of the platform that
    /// sends the number of the user's profile, and the button of the secondary action of the step,
    /// whose payload names the transaction (SPEC-041 L73).
    /// </summary>
    /// <remarks>
    /// The first button is drawn by the platform, caption included, so the caption the caller
    /// supplies for it has nowhere to go. When the platform shows that button is a fact of the
    /// channel; saying so is the business of the request text, which arrives here already worded.
    /// </remarks>
    /// <param name="text">Text of the request.</param>
    /// <param name="content">Content of the request.</param>
    /// <returns>The <c>message</c> object, in its wire form.</returns>
    public static object BuildRequestMessage(string text, PhoneNumberRequestContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new
        {
            text,
            quick_replies = new object[]
            {
                new
                {
                    content_type = QuickReplyContentTypePhoneNumber
                },
                new
                {
                    content_type = QuickReplyContentTypeText,
                    title = LimitTitle(content.SecondaryButtonLabel),
                    payload = PhoneStepButtonPayload.Of(content.SecondaryAction, content.TransactionId)
                }
            }
        };
    }

    /// <summary>
    /// Tells whether a delivery carries a pressed quick reply whose payload has an E.164 form — the
    /// only shape the answer of the platform's phone number button has. The body is parsed, so the
    /// caller asks only for a delivery that names a quick reply at all
    /// (<see cref="QuickReplyMarker"/>).
    /// </summary>
    /// <param name="body">Raw body of a webhook delivery.</param>
    /// <returns>False as well for a body that is not a webhook delivery.</returns>
    public static bool CarriesSharedPhoneNumber(ReadOnlySpan<byte> body)
    {
        MetaWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MetaWebhookPayload>(body);
        }
        catch (JsonException)
        {
            return false;
        }

        foreach (var entry in payload?.Entry ?? [])
        {
            foreach (var messagingEvent in entry?.Messaging ?? [])
            {
                if (messagingEvent?.Message is { IsEcho: false, QuickReply: { } quickReply }
                    && E164PhoneNumber.TryNormalize(quickReply.Payload, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Cuts the caption of a quick reply to the limit the platform sets.
    /// </summary>
    /// <param name="title">Caption to send.</param>
    /// <returns>The caption as is, or its beginning followed by the truncation mark.</returns>
    private static string LimitTitle(string title)
    {
        if (title.Length <= QuickReplyTitleLimit)
        {
            return title;
        }

        var keep = QuickReplyTitleLimit - TruncationMark.Length;

        // A cut between the two halves of a surrogate pair would leave a broken character behind.
        if (char.IsHighSurrogate(title[keep - 1]))
        {
            keep--;
        }

        return string.Concat(title.AsSpan(0, keep), TruncationMark);
    }
}
