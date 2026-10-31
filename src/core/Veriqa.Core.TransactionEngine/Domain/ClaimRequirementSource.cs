// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Where the level recorded for a claim in the requirement set of a transaction came from
/// (SPEC-041 C35).
/// <para>
/// The numeric values are part of the storage format of a transaction.
/// </para>
/// </summary>
public enum ClaimRequirementSource
{
    /// <summary>
    /// The authorization request of the relying party: a requested scope or the <c>claims</c> parameter.
    /// </summary>
    Request = 0,

    /// <summary>
    /// The configuration: the scopes-and-claims catalog, the application's raise of a claim level or
    /// the phone/email requirement flags.
    /// </summary>
    Configuration = 1
}
