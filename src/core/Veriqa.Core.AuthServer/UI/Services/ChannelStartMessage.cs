// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;
using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.AuthServer.UI.Services;

/// <summary>
/// The message a person sends to a channel that starts the sign-in by a code, in its two forms.
/// </summary>
/// <param name="Text">The message with the code written without separators — what goes to the clipboard.</param>
/// <param name="DisplayText">The same message with the digits of the code set in groups — what a page shows.</param>
internal readonly record struct ChannelStartMessageText(string Text, string DisplayText);

/// <summary>
/// Builds the message of the start by a code from the template the channel declared and the code the
/// core issued. Every surface that shows the message asks here, so one channel never shows two
/// different messages for one transaction.
/// <para>
/// The format of the message belongs to the channel and is not interpreted: the template is only
/// checked for the one slot the code goes into. The length of the code belongs to the core, and
/// neither is restated here.
/// </para>
/// </summary>
internal static class ChannelStartMessage
{
    /// <summary>
    /// Number of digits in a group of the displayed code.
    /// </summary>
    private const int DisplayGroupSize = 3;

    /// <summary>
    /// Separator of the groups of the displayed code. A person who retypes it is not wrong: the core
    /// drops whitespace from a code it receives.
    /// </summary>
    private const char DisplayGroupSeparator = ' ';

    /// <summary>
    /// Puts the code into the message template of the channel.
    /// </summary>
    /// <param name="codeStart">The start by a code as the channel declared it.</param>
    /// <param name="code">Code of the transaction: digits only.</param>
    /// <returns>
    /// The message; null — the declared template cannot carry the code: it is blank, longer than
    /// <see cref="ChannelCodeStart.MaxMessageTemplateLength"/>, or holds
    /// <see cref="ChannelCodeStart.CodePlaceholder"/> not exactly once.
    /// </returns>
    public static ChannelStartMessageText? Compose(ChannelCodeStart codeStart, string code)
    {
        ArgumentNullException.ThrowIfNull(codeStart);
        ArgumentException.ThrowIfNullOrEmpty(code);

        var displayCode = GroupForDisplay(code);
        var template = codeStart.MessageTemplate;

        // No template: the person sends the code itself.
        if (template is null)
        {
            return new ChannelStartMessageText(code, displayCode);
        }

        if (string.IsNullOrWhiteSpace(template) || template.Length > ChannelCodeStart.MaxMessageTemplateLength)
        {
            return null;
        }

        var slotIndex = template.IndexOf(ChannelCodeStart.CodePlaceholder, StringComparison.Ordinal);
        if (slotIndex < 0
            || template.IndexOf(
                ChannelCodeStart.CodePlaceholder,
                slotIndex + ChannelCodeStart.CodePlaceholder.Length,
                StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        return new ChannelStartMessageText(
            template.Replace(ChannelCodeStart.CodePlaceholder, code, StringComparison.Ordinal),
            template.Replace(ChannelCodeStart.CodePlaceholder, displayCode, StringComparison.Ordinal));
    }

    /// <summary>
    /// Sets the digits of a code in groups, left to right; the last group takes what is left.
    /// </summary>
    /// <param name="code">Code of the transaction.</param>
    /// <returns>The code as a person reads it.</returns>
    private static string GroupForDisplay(string code)
    {
        var grouped = new StringBuilder(code.Length + code.Length / DisplayGroupSize);

        for (var index = 0; index < code.Length; index++)
        {
            if (index > 0 && index % DisplayGroupSize is 0)
            {
                grouped.Append(DisplayGroupSeparator);
            }

            grouped.Append(code[index]);
        }

        return grouped.ToString();
    }
}
