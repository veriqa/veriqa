// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// A claim missing at the moment the user confirmed the sign-in (SPEC-041 C90): the one element of the
/// list the completion collects.
/// </summary>
/// <param name="Claim">Name of the claim, as the requirement set of the transaction spells it.</param>
/// <param name="Level">Level required of the claim — <c>Required</c> or <c>Optional</c>.</param>
/// <param name="Step">Step the claim is collected on.</param>
public sealed record ClaimCompletionItem(
    string Claim,
    ClaimRequirementLevel Level,
    ClaimCompletionStep Step);
