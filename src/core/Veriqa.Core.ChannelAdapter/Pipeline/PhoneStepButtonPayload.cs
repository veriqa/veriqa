// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The payload of the secondary button of the phone step (SPEC-041 L73) in a channel whose buttons carry
/// one: the marker of the action followed by the transaction. One home for the pairing of
/// <see cref="PhoneStepAction"/> with <see cref="CallbackDataPrefixes.PhoneSkip"/> and
/// <see cref="CallbackDataPrefixes.PhoneCancel"/>, so the adapters that write the payload and read it back
/// cannot drift apart.
/// </summary>
internal static class PhoneStepButtonPayload
{
    /// <summary>
    /// Builds the payload of the button of an action.
    /// </summary>
    /// <param name="action">Secondary action the button stands for.</param>
    /// <param name="transactionId">Transaction the button belongs to.</param>
    /// <returns>The payload.</returns>
    public static string Of(PhoneStepAction action, TransactionId transactionId) =>
        PrefixOf(action) + transactionId.ToString();

    /// <summary>
    /// Reads the action and the raw transaction identifier off a button payload. The identifier is left
    /// unparsed: an adapter parses it its own way, and an identifier that does not parse makes the event
    /// unrelated.
    /// </summary>
    /// <param name="payload">Button payload.</param>
    /// <param name="action">The action the button stands for.</param>
    /// <param name="rawTransactionId">The raw transaction identifier that follows the marker.</param>
    /// <returns>true — the payload is a button of the phone step.</returns>
    public static bool TryRead(string payload, out PhoneStepAction action, out string rawTransactionId)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return TryReadAs(payload, PhoneStepAction.Skip, out action, out rawTransactionId)
            || TryReadAs(payload, PhoneStepAction.CancelSignIn, out action, out rawTransactionId);
    }

    /// <summary>
    /// Reads a payload as the button of one action.
    /// </summary>
    /// <param name="payload">Button payload.</param>
    /// <param name="candidate">The action the payload is tried as.</param>
    /// <param name="action">The candidate when the payload carries its marker.</param>
    /// <param name="rawTransactionId">The raw transaction identifier that follows the marker.</param>
    /// <returns>true — the payload carries the marker of the candidate.</returns>
    private static bool TryReadAs(
        string payload,
        PhoneStepAction candidate,
        out PhoneStepAction action,
        out string rawTransactionId)
    {
        var prefix = PrefixOf(candidate);
        if (payload.StartsWith(prefix, StringComparison.Ordinal))
        {
            action = candidate;
            rawTransactionId = payload[prefix.Length..];
            return true;
        }

        action = default;
        rawTransactionId = string.Empty;
        return false;
    }

    /// <summary>
    /// The marker of an action.
    /// </summary>
    /// <param name="action">Secondary action.</param>
    /// <returns>The marker the transaction identifier follows.</returns>
    private static string PrefixOf(PhoneStepAction action) =>
        action is PhoneStepAction.Skip ? CallbackDataPrefixes.PhoneSkip : CallbackDataPrefixes.PhoneCancel;
}
