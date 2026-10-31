// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The address of a message of a transaction, as the resolution reads it: the kind and the surface, plus
/// the type and the action type of the transaction and the channel — the dimensions the wording of the
/// action is chosen by (SPEC-036 TPL-130). One home for every point that addresses its message by the
/// transaction, on any surface, so that a dimension added here reaches all of them: the senders into a
/// channel (<see cref="InChannelMessageAddress"/>) and the lines of a channel on the core pages.
/// </summary>
internal static class TransactionMessageAddress
{
    /// <summary>
    /// Builds the point of the fallback chain. A dimension without a value is left out: a blank value
    /// would ask about the value "" rather than about no value, and the coarser step answers instead.
    /// </summary>
    /// <param name="messageKind">Kind of the message.</param>
    /// <param name="surface">Surface the message is shown on.</param>
    /// <param name="transaction">Transaction the message is about.</param>
    /// <param name="channelType">Channel the message goes through or speaks of; null — none.</param>
    /// <returns>The point of the fallback chain.</returns>
    internal static ConfigDimensionValues Of(
        string messageKind,
        string surface,
        Transaction transaction,
        string? channelType)
    {
        var values = new List<(string Dimension, string? Value)>(5)
        {
            (MessageTemplateConfigKeys.KindDimensionName, messageKind),
            (MessageTemplateConfigKeys.SurfaceDimensionName, surface)
        };

        Add(values, MessageTemplateConfigKeys.TransactionTypeDimensionName, transaction.Type);
        Add(
            values,
            MessageTemplateConfigKeys.ActionTypeDimensionName,
            transaction.ConfirmationSnapshot?.ActionType);
        Add(values, MessageTemplateConfigKeys.ChannelDimensionName, channelType);

        return ConfigDimensionValues.Of(values.ToArray().AsSpan());
    }

    /// <summary>
    /// Adds a dimension that has a value.
    /// </summary>
    /// <param name="values">Accumulator of the point.</param>
    /// <param name="dimension">Dimension name.</param>
    /// <param name="value">Value; null or empty — none.</param>
    private static void Add(List<(string Dimension, string? Value)> values, string dimension, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values.Add((dimension, value));
        }
    }
}
