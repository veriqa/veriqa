// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Answer of an <see cref="IChannelAdmissibilityCheck"/>: a refusal or "no objection". There is no
/// third answer — a check cannot admit a sender another check refused (RP-101).
/// </summary>
/// <remarks>
/// The default value is <see cref="Allowed"/>, so a refusal can only be stated explicitly, through
/// <see cref="Deny"/>.
/// </remarks>
public readonly record struct ChannelAdmissibilityVerdict
{
    /// <summary>
    /// Creates a refusal.
    /// </summary>
    /// <param name="reasonCode">Reason code of the check.</param>
    private ChannelAdmissibilityVerdict(string reasonCode)
    {
        IsDenied = true;
        ReasonCode = reasonCode;
    }

    /// <summary>
    /// Whether the check refuses the sender.
    /// </summary>
    public bool IsDenied { get; }

    /// <summary>
    /// Reason of the refusal in the check's own namespace of codes; null when the check does not
    /// object. The core writes it to the audit record as it is and does not interpret it (RP-103), so
    /// it must be a code and never a claim value.
    /// </summary>
    public string? ReasonCode { get; }

    /// <summary>
    /// The check does not object.
    /// </summary>
    public static ChannelAdmissibilityVerdict Allowed => default;

    /// <summary>
    /// The check refuses the sender.
    /// </summary>
    /// <param name="reasonCode">Reason code of the check, written to the audit record.</param>
    /// <returns>The refusal.</returns>
    public static ChannelAdmissibilityVerdict Deny(string reasonCode) => new(reasonCode);
}
