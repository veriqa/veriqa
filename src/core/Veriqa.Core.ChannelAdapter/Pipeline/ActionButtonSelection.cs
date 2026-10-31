// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The one home of the choice of the buttons an adapter draws under a message (SPEC-012 §4.9,
/// SPEC-003 §4.9.3) and of fitting a button label or the text of the message into the limit of the
/// platform. Every adapter passes its <see cref="ActionButtonLimits"/> here instead of checking the
/// buttons on its own.
/// </summary>
/// <remarks>
/// Nothing a button carries is logged: the address may hold a link handle or a claim form token
/// (SPEC-041 N78), the payload and the label are the operator's data. A warning names the channel,
/// the lengths, the limits and the counts only.
/// </remarks>
internal static class ActionButtonSelection
{
    /// <summary>
    /// Mark a cut label or text ends with (U+2026), one UTF-16 code unit long.
    /// </summary>
    private const string TruncationMark = "…";

    /// <summary>
    /// Smallest limit that leaves room for at least one character before the truncation mark.
    /// Every limit a platform names today is far above it.
    /// </summary>
    private const int MinFittedLength = 2;

    /// <summary>
    /// Picks the buttons of a message that can be drawn, in the order they are listed, with their
    /// labels fitted into the limit. A button is left out with a warning when it is malformed —
    /// neither a link nor a callback, or both — when it is a link and the form draws none, or when its
    /// address or payload is longer than the platform allows. Once the list is walked, the buttons
    /// beyond the largest number the form holds are dropped with one warning that counts them.
    /// </summary>
    /// <param name="content">Buttons of the message.</param>
    /// <param name="limits">What the form the adapter draws the buttons in allows.</param>
    /// <param name="channelType">Channel the warnings are written for.</param>
    /// <param name="logger">Logger of the adapter.</param>
    /// <returns>Buttons to draw, labels already fitted; empty — nothing can be drawn.</returns>
    public static IReadOnlyList<ChannelActionButton> SelectDrawable(
        ActionButtonsContent content,
        ActionButtonLimits limits,
        string channelType,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);

        if (limits.MaxLabelLength is { } maxLabelLength)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLabelLength, MinFittedLength, nameof(limits));
        }

        var drawable = new List<ChannelActionButton>(content.Buttons.Count);

        foreach (var button in content.Buttons)
        {
            if (IsDrawable(button, limits, channelType, logger))
            {
                drawable.Add(button);
            }
        }

        if (limits.MaxButtons is { } maxButtons && drawable.Count > maxButtons)
        {
            var droppedCount = drawable.Count - maxButtons;

            logger.LogWarning(
                "{DroppedCount} action button(s) are not drawn in {ChannelType}: the platform shows at most {MaxButtons} buttons under a message, the first ones are kept",
                droppedCount,
                channelType,
                maxButtons);

            drawable.RemoveRange(maxButtons, droppedCount);
        }

        // Labels are fitted once the dropped buttons are gone, so a button that is not drawn costs no
        // warning about its label.
        for (var i = 0; i < drawable.Count; i++)
        {
            drawable[i] = drawable[i] with
            {
                Label = FitLabel(drawable[i].Label, limits.MaxLabelLength, channelType, logger)
            };
        }

        return drawable;
    }

    /// <summary>
    /// Fits a button label into the limit of the platform: a label within the limit is returned as is,
    /// a longer one is cut to its beginning followed by the truncation mark, the limit long in all. The
    /// cut never separates the two halves of a surrogate pair, so the result may be one character
    /// shorter than the limit.
    /// </summary>
    /// <param name="label">Label of the button.</param>
    /// <param name="maxLength">Limit of the platform; null — none is named, the label goes as is.</param>
    /// <param name="channelType">Channel the warning is written for.</param>
    /// <param name="logger">Logger of the adapter.</param>
    /// <returns>The label as is, or its cut form.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The limit leaves no room for a character before the mark.</exception>
    public static string FitLabel(string label, int? maxLength, string channelType, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(logger);

        if (maxLength is not { } limit)
        {
            return label;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, MinFittedLength, nameof(maxLength));

        if (label.Length <= limit)
        {
            return label;
        }

        var labelLength = label.Length;

        logger.LogWarning(
            "An action button label is cut in {ChannelType}: it is {LabelLength} characters long, the platform limit is {MaxLength}",
            channelType,
            labelLength,
            limit);

        return Cut(label, limit);
    }

    /// <summary>
    /// Fits the text of a message drawn with buttons into the limit of its form, the way
    /// <see cref="FitLabel"/> fits a label: a text within the limit is returned as is, a longer one is
    /// cut to its beginning followed by the truncation mark, the limit long in all, with a warning that
    /// names the lengths only. The cut never separates the two halves of a surrogate pair.
    /// </summary>
    /// <param name="text">Text of the message.</param>
    /// <param name="maxLength">Limit of the form, in UTF-16 code units.</param>
    /// <param name="channelType">Channel the warning is written for.</param>
    /// <param name="logger">Logger of the adapter.</param>
    /// <returns>The text as is, or its cut form.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The limit leaves no room for a character before the mark.</exception>
    public static string FitText(string text, int maxLength, string channelType, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, MinFittedLength, nameof(maxLength));

        if (text.Length <= maxLength)
        {
            return text;
        }

        var textLength = text.Length;

        logger.LogWarning(
            "The text of a message with buttons is cut in {ChannelType}: it is {TextLength} characters long, the platform limit is {MaxLength}",
            channelType,
            textLength,
            maxLength);

        return Cut(text, maxLength);
    }

    /// <summary>
    /// Cuts a value longer than the limit to its beginning followed by the truncation mark, the limit
    /// long in all, without separating the two halves of a surrogate pair.
    /// </summary>
    /// <param name="value">Value longer than the limit.</param>
    /// <param name="limit">Limit, at least <see cref="MinFittedLength"/>.</param>
    /// <returns>The cut value.</returns>
    private static string Cut(string value, int limit)
    {
        var keep = limit - TruncationMark.Length;

        // A cut between the two halves of a surrogate pair would leave a broken character behind.
        if (char.IsHighSurrogate(value[keep - 1]))
        {
            keep--;
        }

        return string.Concat(value.AsSpan(0, keep), TruncationMark);
    }

    /// <summary>
    /// Tells whether one button can be drawn in the form, writing the warning when it cannot.
    /// </summary>
    /// <param name="button">Button of the message.</param>
    /// <param name="limits">What the form allows.</param>
    /// <param name="channelType">Channel the warning is written for.</param>
    /// <param name="logger">Logger of the adapter.</param>
    /// <returns>true — the button goes to the platform.</returns>
    private static bool IsDrawable(
        ChannelActionButton button,
        ActionButtonLimits limits,
        string channelType,
        ILogger logger)
    {
        var hasUrl = !string.IsNullOrEmpty(button.Url);
        var hasCallbackData = !string.IsNullOrEmpty(button.CallbackData);

        if (hasUrl == hasCallbackData)
        {
            logger.LogWarning(
                "An action button is not drawn in {ChannelType}: it must carry either a link or callback data, and only one of them",
                channelType);

            return false;
        }

        if (hasUrl)
        {
            if (!limits.DrawsLinks)
            {
                logger.LogWarning(
                    "An action button is not drawn in {ChannelType}: the form the buttons are drawn in has no link buttons",
                    channelType);

                return false;
            }

            var urlLength = button.Url!.Length;

            if (limits.MaxUrlLength is { } maxUrlLength && urlLength > maxUrlLength)
            {
                logger.LogWarning(
                    "An action button is not drawn in {ChannelType}: its link is {UrlLength} characters long, the platform limit is {MaxLength}",
                    channelType,
                    urlLength,
                    maxUrlLength);

                return false;
            }

            return true;
        }

        var callbackDataLength = button.CallbackData!.Length;

        if (limits.MaxCallbackDataLength is { } maxCallbackDataLength && callbackDataLength > maxCallbackDataLength)
        {
            logger.LogWarning(
                "An action button is not drawn in {ChannelType}: its callback data is {CallbackDataLength} characters long, the platform limit is {MaxLength}",
                channelType,
                callbackDataLength,
                maxCallbackDataLength);

            return false;
        }

        return true;
    }
}
