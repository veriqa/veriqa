// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// What an authorization request writes onto the transaction it creates about the claims (SPEC-041 C35,
/// L48): the requirement set and the allowed channel types that set leaves.
/// </summary>
/// <param name="Requirements">Requirement set of the transaction; empty when nothing is required.</param>
/// <param name="AllowedChannelTypes">
/// Allowed channel types of the transaction: those of <c>acr_values</c> as they came, or — when the phone
/// number is required — the explicit list of channels able to provide it. Null admits every channel.
/// </param>
internal sealed record ClaimRequirementsDecision(
    IReadOnlyList<ClaimRequirement> Requirements,
    IReadOnlyList<string>? AllowedChannelTypes);
