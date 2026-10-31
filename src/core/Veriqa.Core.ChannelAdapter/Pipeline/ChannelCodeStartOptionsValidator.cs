// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Start-up validation of the short code section: a code length outside the supported range stops the
/// host.
/// </summary>
/// <remarks>
/// Judged at start because the value fails late otherwise: a length below the range makes a code a
/// handful of guesses wide, and one above it — or a non-positive one — surfaces only when the first
/// page of a channel started by a code is rendered.
/// </remarks>
internal sealed class ChannelCodeStartOptionsValidator : IValidateOptions<ChannelCodeStartOptions>
{
    /// <summary>
    /// Address of the code length inside the host configuration.
    /// </summary>
    private static readonly string CodeLengthPath =
        ChannelCodeStartOptions.SectionName + ":" + nameof(ChannelCodeStartOptions.CodeLength);

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

        return ValidateOptionsResult.Success;
    }
}
