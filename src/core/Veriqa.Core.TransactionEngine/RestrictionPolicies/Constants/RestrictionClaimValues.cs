// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// String form of the boolean claim fields: a rule compares strings, so a boolean field is read as
/// one of the two values below and the configuration states them the same way.
/// </summary>
internal static class RestrictionClaimValues
{
    /// <summary>
    /// The field is true.
    /// </summary>
    public const string True = "true";

    /// <summary>
    /// The field is false.
    /// </summary>
    public const string False = "false";
}
