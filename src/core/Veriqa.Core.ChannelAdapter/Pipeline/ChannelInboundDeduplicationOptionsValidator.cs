// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Start-up validation of the inbound deduplication section: a negative window stops the host.
/// </summary>
/// <remarks>
/// Judged at start because a negative window would otherwise pass unnoticed: it reads as "off", while
/// the operator who wrote it meant some window.
/// </remarks>
internal sealed class ChannelInboundDeduplicationOptionsValidator : IValidateOptions<ChannelInboundDeduplicationOptions>
{
    /// <summary>
    /// Address of the window inside the host configuration.
    /// </summary>
    private static readonly string WindowPath =
        ChannelInboundDeduplicationOptions.SectionName + ":" + nameof(ChannelInboundDeduplicationOptions.Window);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ChannelInboundDeduplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Window < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{WindowPath}: {options.Window} is negative. State a non-negative time span — "
                + $"{TimeSpan.Zero} turns the deduplication off — or leave the setting out (the shipped "
                + $"value is {ChannelInboundDeduplicationOptions.DefaultWindow}).");
        }

        return ValidateOptionsResult.Success;
    }
}
