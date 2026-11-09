// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Start-up validation of the short code section: a code length outside the supported range, or a
/// budget of unmatched codes that is not positive, stops the host.
/// </summary>
/// <remarks>
/// Judged at start because the value fails late otherwise: a length below the range makes a code a
/// handful of guesses wide, and one above it — or a non-positive one — surfaces only when the first
/// page of a channel started by a code is rendered; a budget of zero would silence every start by a
/// code on the route of a tenant, and a non-positive window cannot be built at all.
/// </remarks>
internal sealed class ChannelCodeStartOptionsValidator : IValidateOptions<ChannelCodeStartOptions>
{
    /// <summary>
    /// Address of the code length inside the host configuration.
    /// </summary>
    private static readonly string CodeLengthPath =
        ChannelCodeStartOptions.SectionName + ":" + nameof(ChannelCodeStartOptions.CodeLength);

    /// <summary>
    /// Address of the budget of unmatched codes inside the host configuration.
    /// </summary>
    private static readonly string UnmatchedCodePermitLimitPath =
        ChannelCodeStartOptions.SectionName + ":" + nameof(ChannelCodeStartOptions.UnmatchedCodePermitLimit);

    /// <summary>
    /// Address of the window of the budget of unmatched codes inside the host configuration.
    /// </summary>
    private static readonly string UnmatchedCodeWindowSecondsPath =
        ChannelCodeStartOptions.SectionName + ":" + nameof(ChannelCodeStartOptions.UnmatchedCodeWindowSeconds);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ChannelCodeStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.CodeLength is < ChannelCodeStartOptions.MinCodeLength or > ChannelCodeStartOptions.MaxCodeLength)
        {
            return ValidateOptionsResult.Fail(
                $"{CodeLengthPath}: {options.CodeLength} is outside the supported range. State a whole "
                + $"number from {ChannelCodeStartOptions.MinCodeLength} to {ChannelCodeStartOptions.MaxCodeLength} "
                + $"or leave the setting out (the shipped value is {ChannelCodeStartOptions.DefaultCodeLength}).");
        }

        if (options.UnmatchedCodePermitLimit <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{UnmatchedCodePermitLimitPath}: {options.UnmatchedCodePermitLimit} is not a positive number. "
                + "State a whole number above zero or leave the setting out.");
        }

        if (options.UnmatchedCodeWindowSeconds <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{UnmatchedCodeWindowSecondsPath}: {options.UnmatchedCodeWindowSeconds} is not a positive number. "
                + "State a whole number of seconds above zero or leave the setting out.");
        }

        return ValidateOptionsResult.Success;
    }
}
