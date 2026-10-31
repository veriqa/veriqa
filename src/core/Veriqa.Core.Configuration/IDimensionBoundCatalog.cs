// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.Configuration;

/// <summary>
/// Says WHY a catalog that answers <see cref="IConfigSnapshotCatalog.CanWalk"/> with false stays out
/// of the walk, when the obstacle is the ADDRESS of the level and not the source behind it
/// (SPEC-012 §10.6, CFG-244). The two are fixed in different places — the source is a property of the
/// deployed composition, the address a property of the declaration of the key — so the report names
/// which of them it met instead of telling an operator about a source that enumerates its records
/// perfectly well.
/// <para>
/// It is internal and non-generic on purpose: the catalog built from a declaration is generic over the
/// value of its setting, while the report speaks about a pair "setting + level" and never about that
/// type. Nothing outside the mechanism implements it — a catalog written by the owner of a level is
/// kept out by its source, which is what the report says when nothing here answers.
/// </para>
/// </summary>
internal interface IDimensionBoundCatalog
{
    /// <summary>
    /// Address of the level that carries a substitution OUTSIDE its groups, and therefore states
    /// nothing at an address a walk could read; null when the address has a step that narrows nothing,
    /// or when the level is the core one and is walked by the points of that dimension — then the walk
    /// is kept out by the source of the level, if it is kept out at all.
    /// </summary>
    string? AddressRequiringDimensionValue { get; }
}
