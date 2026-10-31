// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.Meta;

/// <summary>
/// The button template of the Messenger Platform family: a text with up to three buttons under it,
/// each a link the platform opens or a postback whose payload comes back as a webhook delivery. The
/// wire form is the same for Messenger and Instagram, so the post-sign-in buttons and the claim form
/// link of both are built here.
/// </summary>
/// <remarks>
/// The builder passes the data as it gets it: fitting the text into the limit of the template and
/// choosing the buttons (<see cref="Pipeline.ActionButtonSelection"/>) are the caller's job.
/// </remarks>
internal static class MetaButtonTemplate
{
    /// <summary>
    /// Largest number of buttons of a button template, as Meta documents it for Messenger and for
    /// Instagram.
    /// </summary>
    public const int MaxButtons = 3;

    /// <summary>
    /// Type of an attachment that carries a template.
    /// </summary>
    private const string AttachmentTypeTemplate = "template";

    /// <summary>
    /// Template type of a text with buttons under it.
    /// </summary>
    private const string TemplateTypeButton = "button";

    /// <summary>
    /// Button type of a link the platform opens.
    /// </summary>
    private const string ButtonTypeWebUrl = "web_url";

    /// <summary>
    /// Button type whose payload comes back to the sender as a webhook delivery.
    /// </summary>
    private const string ButtonTypePostback = "postback";

    /// <summary>
    /// Builds the <c>message</c> object of the Send API request: a template attachment with the text
    /// and the buttons in the order they are listed — a link button for a button with an address, a
    /// postback for a button with callback data.
    /// </summary>
    /// <param name="text">Text of the template, already fitted into its limit.</param>
    /// <param name="buttons">Buttons to draw, already chosen and labelled for the platform.</param>
    /// <returns>The <c>message</c> object, in its wire form.</returns>
    /// <exception cref="ArgumentException">A button carries neither an address nor callback data.</exception>
    public static object BuildMessage(string text, IReadOnlyList<ChannelActionButton> buttons)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(buttons);

        var wireButtons = new object[buttons.Count];

        for (var i = 0; i < buttons.Count; i++)
        {
            wireButtons[i] = ToWireButton(buttons[i]);
        }

        return new
        {
            attachment = new
            {
                type = AttachmentTypeTemplate,
                payload = new
                {
                    template_type = TemplateTypeButton,
                    text,
                    buttons = wireButtons
                }
            }
        };
    }

    /// <summary>
    /// Builds the wire form of one button.
    /// </summary>
    /// <param name="button">Button to draw.</param>
    /// <returns>The button object of the template.</returns>
    private static object ToWireButton(ChannelActionButton button)
    {
        if (!string.IsNullOrEmpty(button.Url))
        {
            return new
            {
                type = ButtonTypeWebUrl,
                url = button.Url,
                title = button.Label
            };
        }

        if (string.IsNullOrEmpty(button.CallbackData))
        {
            throw new ArgumentException(
                "A button of the template must carry either an address or callback data.", nameof(button));
        }

        return new
        {
            type = ButtonTypePostback,
            payload = button.CallbackData,
            title = button.Label
        };
    }
}
