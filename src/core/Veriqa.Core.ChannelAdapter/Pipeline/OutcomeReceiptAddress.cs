// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The ADDRESS of an outcome receipt: what a render point states about the receipt it is about to
/// show (SPEC-036 TPL-123, TPL-116). It carries no wording and no rule for picking one — the
/// wording of the address is chosen by the declared dimensions of the template key, level by level,
/// and the render point only says what it knows.
/// <para>
/// A value the point does not know is left null and is then not asked about at all: the step of the
/// ladder naming that dimension addresses nothing of its own, and the coarser step answers instead.
/// There is no "if the type is unknown" branch anywhere for that reason — the transaction of a
/// decision that arrived for a transaction the store no longer has (SPEC-003 CA-192) is exactly
/// this case, and it is answered by the step that names no transaction type.
/// </para>
/// </summary>
/// <param name="Outcome">What the receipt reports — one of the three terminal outcomes the kinds
/// are declared for, or the non-terminal state of a claims completion.</param>
/// <param name="Surface">Surface the receipt is shown on (<see cref="OutcomeReceiptSurfaces"/>).</param>
/// <param name="TransactionType">Transaction type (<see cref="TransactionTypes"/>); null — the
/// render point has no transaction to read it off.</param>
/// <param name="ActionType">Action being confirmed — stated by a confirmation transaction, null
/// otherwise.</param>
/// <param name="ChannelType">Channel the receipt travels through (<see cref="ChannelTypes"/>);
/// null — the receipt is shown outside a channel. The channel REFINES the surface and no step of
/// the ladder names it without one (SPEC-036 §4.6), so it is asked about only beside a stated
/// surface — see <see cref="ToDimensions"/>.</param>
/// <param name="DeviceType">Type of the device the transaction was started on, as the initiator
/// context of the transaction states it (<see cref="InitiatorContextFields.DeviceTypes"/>); null — the
/// render point has no transaction, the transaction carries no initiator context, or the context
/// does not collect the device type. Unlike the channel it refines any step and is asked about
/// with or without a surface.</param>
public readonly record struct OutcomeReceiptAddress(
    TransactionOutcome Outcome,
    string Surface,
    string? TransactionType = null,
    string? ActionType = null,
    string? ChannelType = null,
    string? DeviceType = null)
{
    /// <summary>
    /// Identifier of the message kind this outcome is reported by — the identity dimension of both
    /// keys. This is a mapping of an outcome onto an identifier, not a choice of wording: which text
    /// the kind renders is decided by the resolution, never here.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is none of the four the receipt
    /// kinds are declared for — <c>Failed</c> and <c>default</c> included: an error is reported by
    /// the wording of the channel that failed, which is not a message of this group
    /// (SPEC-036 §1.3).</exception>
    public string Kind => Outcome.Value switch
    {
        TransactionOutcomeCodes.Confirmed => MessageKinds.OutcomeReceiptConfirmed,
        TransactionOutcomeCodes.Declined => MessageKinds.OutcomeReceiptDeclined,
        TransactionOutcomeCodes.Expired => MessageKinds.OutcomeReceiptExpired,
        TransactionOutcomeCodes.AwaitingClaims => MessageKinds.OutcomeReceiptAwaitingClaims,
        _ => throw new ArgumentOutOfRangeException(
            nameof(Outcome),
            Outcome.Value,
            "An outcome receipt is declared for a confirmed, a declined and an expired outcome and "
            + "for the state of a claims completion. Any other outcome is reported by the error "
            + "wording of the channel, which is not a message of this group.")
    };

    /// <summary>
    /// The address as the resolution reads it: the kind, plus every dimension whose value this point
    /// actually knows. A dimension left out is not asked about — the step naming it drops that group
    /// from its address and behaves as the coarser step it became.
    /// <para>
    /// The channel is the one dimension that does not stand alone: it REFINES the surface and no step
    /// of the ladder addresses it without one (SPEC-036 §4.6), so a point naming a channel without a
    /// surface asks about no step at all and the resolution refuses it. A receipt answers the user —
    /// something was pressed, or the time ran out, and there is no second screen to learn the result
    /// on — so an address that names no surface leaves the channel out with it and is answered by
    /// the step that narrows by neither, exactly as any unnamed axis is answered.
    /// </para>
    /// </summary>
    /// <returns>The point of the fallback chain.</returns>
    public ConfigDimensionValues ToDimensions()
    {
        var values = new List<(string Dimension, string? Value)>(6)
        {
            (MessageTemplateConfigKeys.KindDimensionName, Kind)
        };

        Add(values, MessageTemplateConfigKeys.TransactionTypeDimensionName, TransactionType);
        Add(values, MessageTemplateConfigKeys.ActionTypeDimensionName, ActionType);
        Add(values, MessageTemplateConfigKeys.SurfaceDimensionName, Surface);
        Add(
            values,
            MessageTemplateConfigKeys.ChannelDimensionName,
            string.IsNullOrEmpty(Surface) ? null : ChannelType);
        Add(values, MessageTemplateConfigKeys.DeviceTypeDimensionName, DeviceType);

        return ConfigDimensionValues.Of(values.ToArray().AsSpan());
    }

    /// <summary>
    /// Formats the parts the render point STATED, and only them.
    /// <para>
    /// The printout a record synthesizes walks every readable property, <see cref="Kind"/> included —
    /// and that one deliberately refuses an outcome the receipts are not declared for. A log line
    /// carrying the address, or an inspection in a debugger, would then be the place where that
    /// programming error is reported, instead of the resolution that actually asks for the kind:
    /// formatting a value never throws. Nothing is lost by leaving it out, since the kind is a pure
    /// mapping of the outcome and the outcome is printed.
    /// </para>
    /// </summary>
    /// <param name="builder">Accumulator of the printout.</param>
    /// <returns><c>true</c> — the members are printed, so the type name is followed by them.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The printed list IS the list of stated parts: taking them apart binds it to the members of
        // the address, so a dimension added to the address cannot go missing from the printout.
        var (outcome, surface, transactionType, actionType, channelType, deviceType) = this;

        builder.Append("Outcome = ").Append(outcome)
            .Append(", Surface = ").Append(surface)
            .Append(", TransactionType = ").Append(transactionType)
            .Append(", ActionType = ").Append(actionType)
            .Append(", ChannelType = ").Append(channelType)
            .Append(", DeviceType = ").Append(deviceType);

        return true;
    }

    /// <summary>
    /// Adds a dimension the point knows a value of; a blank value is not a narrower question but a
    /// question about the value "", so it is left out instead.
    /// </summary>
    /// <param name="values">Accumulator of the point.</param>
    /// <param name="dimension">Dimension name.</param>
    /// <param name="value">Value the point knows, or null/blank when it knows none.</param>
    private static void Add(List<(string Dimension, string? Value)> values, string dimension, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values.Add((dimension, value));
        }
    }
}
