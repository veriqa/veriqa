// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// One value a field of the <see cref="ClaimFieldFormat.Choice"/> format offers (SPEC-041 C95): what the
/// claim receives when the user picks it, and what the user reads in the list.
/// </summary>
public sealed class ClaimCompletionOption
{
    /// <summary>
    /// Value the claim receives. Must not be blank, carries no white space at its edges, is at most
    /// <see cref="ClaimCompletionDefinition.DefaultMaxLength"/> characters long and is stated once in its
    /// list (compared ordinally).
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Caption of the value in the list — a text setting resolved as a Natural Key through the host
    /// locale files, the way <see cref="ClaimCompletionDefinition.Label"/> is. Must not be blank.
    /// </summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Description of the step that completes a claim through a simple field (SPEC-041 C31, C95): what the
/// field is labelled with, how long its value may be, what format it takes and — for a choice — which
/// values it offers.
/// </summary>
public sealed class ClaimCompletionDefinition
{
    /// <summary>
    /// Maximum length of a completed value the product ships.
    /// </summary>
    public const int DefaultMaxLength = 256;

    /// <summary>
    /// Label of the field — a text setting, resolved as a Natural Key through the host locale files
    /// (SPEC-012 CFG-241). Must not be blank.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Maximum length of the value, in characters; must be positive. Applies to the
    /// <see cref="ClaimFieldFormat.Text"/> format only — a date bounds its length by its format, a
    /// choice by its list.
    /// </summary>
    public int MaxLength { get; set; } = DefaultMaxLength;

    /// <summary>
    /// Format of the field.
    /// </summary>
    public ClaimFieldFormat Format { get; set; } = ClaimFieldFormat.Text;

    /// <summary>
    /// Values a field of the <see cref="ClaimFieldFormat.Choice"/> format offers, in the order they are
    /// listed. A list rather than a map: the order is part of the setting, and a configuration provider
    /// keeps the order of a list but sorts the keys of a map. Required for a choice and refused for any
    /// other format.
    /// </summary>
    public IList<ClaimCompletionOption> Options { get; set; } = [];
}

/// <summary>
/// Definition of a single claim within a scope (SPEC-012 §6.6, SPEC-041 C31).
/// </summary>
public sealed class ClaimDefinition
{
    /// <summary>
    /// Claim name as it appears in a token (e.g. phone_number). One name — one row of the catalog.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Requirement level of the claim stated by the owner of the catalog (SPEC-041 R79). An application
    /// may raise it but never lower it. A level above <see cref="ClaimRequirementLevel.IfAvailable"/>
    /// needs a completion path (SPEC-041 L92): the phone number and the email have one of their own,
    /// any other claim has one only through <see cref="Completion"/>.
    /// </summary>
    public ClaimRequirementLevel Level { get; set; } = ClaimRequirementLevel.IfAvailable;

    /// <summary>
    /// Claim source: Channel (from the channel), External (from an external system) or Collected
    /// (collected by Veriqa — only from the store or through completion).
    /// </summary>
    public ClaimSource Source { get; set; } = ClaimSource.Channel;

    /// <summary>
    /// Description of the completion step; null — the claim has none. Required for a
    /// <see cref="ClaimSource.Collected"/> claim, admitted for a <see cref="ClaimSource.Channel"/> one
    /// (the form completes what the channel did not give), and refused for an
    /// <see cref="ClaimSource.External"/> claim, for the phone number, the email and the claims the core
    /// issues itself (SPEC-041 C31).
    /// </summary>
    public ClaimCompletionDefinition? Completion { get; set; }
}

/// <summary>
/// Definition of a single scope (SPEC-012 §6.6).
/// </summary>
public sealed class ScopeDefinition
{
    /// <summary>
    /// Scope name (e.g. profile).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// List of claims included in the scope.
    /// </summary>
    public IReadOnlyList<ClaimDefinition> Claims { get; set; } = [];
}

/// <summary>
/// Scopes and claims settings (SPEC-012 §6.6, SPEC-041 C30).
/// Configuration section: Veriqa:ScopesClaims.
/// </summary>
public sealed class ScopesClaimsOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:ScopesClaims";

    /// <summary>
    /// Catalog of scopes with their claims — the core level of the catalog. Empty — the product's own
    /// set of scopes is registered unchanged; a non-empty catalog REPLACES that set as a whole, so every
    /// scope the deployment serves has to be listed in it (<c>openid</c> is registered in any case).
    /// </summary>
    public IReadOnlyList<ScopeDefinition> Scopes { get; set; } = [];

    /// <summary>
    /// Require the phone_number claim — a shorthand for the <see cref="ClaimRequirementLevel.Required"/>
    /// level of that claim, combined with the level of its catalog row by seniority (SPEC-041 L40).
    /// Default: false.
    /// </summary>
    public bool RequirePhone { get; set; }

    /// <summary>
    /// Require the email claim — the same shorthand as <see cref="RequirePhone"/>, for the email.
    /// Default: false.
    /// </summary>
    public bool RequireEmail { get; set; }
}
