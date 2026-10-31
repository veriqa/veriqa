// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;

using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Sanitizes a value before it is substituted into a channel message (ICC-051, TPL-127): control
/// characters (including line breaks) become spaces, the value is truncated at a code-point boundary to
/// the cap the caller passes and trimmed. The cap is the caller's decision: a value the core computes
/// itself is passed with no cap, since a truncated link or code is no longer itself. Messages are sent
/// as plain text (no parse_mode/markup), so neutralizing control characters is sufficient — markup
/// escaping is the caller's concern (e.g. the HTML mail body). This is the single source of that logic,
/// shared by <see cref="ConfirmationPromptRenderer"/> and <see cref="MessageTextRenderer"/> — the
/// second echelon that runs on ALL value sources, including pre-validated caller values (defense in depth).
/// The same logic also cleans values that leave the adapter without being rendered into a message at all
/// (the Email sender display name on its way to a claim, SPEC-016 §6.3): control characters and an
/// unbounded length are a hazard in a token just as much as in a message.
/// </summary>
internal static class MessageValueSanitizer
{
    /// <summary>
    /// Sanitizes a field value; returns null when the value is empty/whitespace (an absent slot).
    /// </summary>
    /// <param name="value">Original value.</param>
    /// <param name="maxLength">
    /// Length cap in UTF-16 code units (default the global slot ceiling). A character outside the BMP that
    /// no longer fits whole is dropped rather than cut, so the result may stay one unit below the cap.
    /// </param>
    /// <returns>Safe value, or null when effectively empty.</returns>
    public static string? Sanitize(string? value, int maxLength = MessageTemplateLimits.MaxSlotValueLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));

        // The value is walked by code point, not by UTF-16 unit: a character outside the BMP is a surrogate
        // pair, and stopping between its halves would leave an ill-formed string. A lone surrogate is
        // enumerated as a one-unit replacement rune, so the position stays aligned with the source and
        // the source units are what gets copied.
        var position = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            var runeLength = rune.Utf16SequenceLength;

            if (builder.Length + runeLength > maxLength)
            {
                break;
            }

            // Replace control characters (including line breaks) with a space.
            if (Rune.IsControl(rune))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(value, position, runeLength);
            }

            position += runeLength;
        }

        var sanitized = builder.ToString().Trim();

        return sanitized.Length > 0 ? sanitized : null;
    }
}
