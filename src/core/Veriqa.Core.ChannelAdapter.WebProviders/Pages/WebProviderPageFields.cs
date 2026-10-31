// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WebProviders.Pages;

/// <summary>
/// Names and values of the fields of the forms of the page of a web provider channel, and the ids its
/// script finds the page by.
/// </summary>
internal static class WebProviderPageFields
{
    /// <summary>
    /// Field of the answer form naming the transaction the question belongs to.
    /// </summary>
    public const string Transaction = "tx";

    /// <summary>
    /// Field of the answer form carrying the answer.
    /// </summary>
    public const string Answer = "answer";

    /// <summary>
    /// Value of <see cref="Answer"/> for "Yes".
    /// </summary>
    public const string AnswerConfirm = "confirm";

    /// <summary>
    /// Value of <see cref="Answer"/> for "No".
    /// </summary>
    public const string AnswerDecline = "decline";

    /// <summary>
    /// Field of the press form carrying the payload of the pressed item, as the button carries it.
    /// </summary>
    public const string Item = "item";

    /// <summary>
    /// Field of the press form carrying the identifier the press was given when its button was drawn.
    /// </summary>
    public const string PressId = "press";

    /// <summary>
    /// Longest identifier of a press taken from a form; a longer one is not ours and is dropped.
    /// </summary>
    public const int MaxPressIdLength = 64;

    /// <summary>
    /// Id of the card of the page; its data attributes configure the page script. The script spells the
    /// same id — rename both together.
    /// </summary>
    public const string PageElementId = "veriqa-web-provider-page";
}
