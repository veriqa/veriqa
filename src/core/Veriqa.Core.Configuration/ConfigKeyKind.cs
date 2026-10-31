// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.Configuration;

/// <summary>
/// Setting semantics defining the level-combining rules during resolution
/// (SPEC-012 §10.3). Declared declaratively when creating a
/// <see cref="ConfigKey{T}"/>, not hardcoded by setting name.
/// </summary>
public enum ConfigKeyKind
{
    /// <summary>
    /// Plain value: the first level specified by precedence wins (SPEC-012 §10.3).
    /// </summary>
    Value = 0,

    /// <summary>
    /// Set (narrowing, SPEC-012 §10.3): a lower level does not widen what the upper one allows.
    /// The effective value is the intersection of the levels that PARTICIPATE — which is not the
    /// same as the levels the key declares: the CORE level opts out of the intersection per key
    /// (<see cref="ConfigKey{T}.CoreParticipatesInSetIntersection"/>) and is then no ceiling over the
    /// tenant — a tenant set replaces it — but, while the tenant is silent, the base the levels below
    /// the tenant narrow, provided it allows at least one member; with no level stating a set it is the
    /// value as it is. A key's own semantics therefore follow from its declaration (levels plus that
    /// flag), not from this sentence alone.
    /// </summary>
    Set = 1,

    /// <summary>
    /// Protective setting with a ceiling (SPEC-012 §10.3): downwards only stricter.
    /// Relaxation from below is allowed only when the upper level's gate is set
    /// (see <see cref="ConfigKey{T}.Stricter"/> and the level's gate flag).
    /// </summary>
    ProtectiveCeiling = 2,

    /// <summary>
    /// Value with controlled override (SPEC-012 §10.3, decision #3): a level freely overrides
    /// the upper one IF the level below it by ownership has opened the gate (AllowLowerOverride);
    /// with the gate closed, the upper level's value is kept. Used where a value has no "stricter"
    /// relation to order it by, so the override is permitted by a gate rather than by strictness —
    /// today that is <c>AuthPageDesign.CustomJs</c>, whose application level opens only through the
    /// gate of the owner of the global section.
    /// </summary>
    GatedValue = 3
}
