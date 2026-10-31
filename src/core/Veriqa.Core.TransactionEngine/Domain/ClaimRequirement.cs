// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// One element of the requirement set of a transaction (SPEC-041 C35): a claim, the level required
/// of it and the source of that level, fixed when the transaction is created.
/// </summary>
/// <param name="Claim">Name of the claim, exactly as it appears in a token.</param>
/// <param name="Level">Level of the requirement — the senior one among all the sources that stated it.</param>
/// <param name="Source">
/// Source of the senior level; when the request and the configuration state the same level, the
/// configuration.
/// </param>
/// <param name="HasCompletionPath">
/// Whether the claim could be completed by the user when the transaction was created (SPEC-041 L92).
/// It is computed once, at creation, where the scopes-and-claims catalog is known, so that the
/// consumers of the set read the flag instead of the catalog.
/// </param>
public sealed record ClaimRequirement(
    string Claim,
    ClaimRequirementLevel Level,
    ClaimRequirementSource Source,
    bool HasCompletionPath);
