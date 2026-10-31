// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration.Enums;

/// <summary>
/// Format of the simple field a claim is completed through (SPEC-041 C95). The set is closed on
/// purpose: completion is not a general-purpose questionnaire, and the phone number and the email have
/// paths of their own rather than a format of this field. A choice is the one format that takes its
/// values from the configuration rather than from the user.
/// </summary>
public enum ClaimFieldFormat
{
    /// <summary>
    /// Free text, bounded by <see cref="ClaimCompletionDefinition.MaxLength"/>.
    /// </summary>
    Text = 0,

    /// <summary>
    /// A calendar date; the format bounds the length itself.
    /// </summary>
    Date = 1,

    /// <summary>
    /// One value out of the list the catalog row states (<see cref="ClaimCompletionDefinition.Options"/>),
    /// offered as a drop-down list; the list bounds the value itself.
    /// </summary>
    Choice = 2
}
