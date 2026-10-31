// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Veriqa.Core.Configuration;

/// <summary>
/// Read-only slice of the EXTRACTION BINDINGS of the deployment (SPEC-012 §10.6): at which levels each
/// key is actually bound in this contour — that is, which levels can answer it at all.
/// <para>
/// It is a third fact next to the two slices that already leave the mechanism, and it is not derivable
/// from either of them. The contour map (<see cref="ConfigSourceMapView"/>) says which levels a source
/// serves, and a served level may still hold no binding of a given key: a key that addresses a level
/// by identity is not bound over a reader of documents, even where that reader serves the level. The
/// schema (<see cref="ConfigKeySchemaView"/>) says which levels a key declares, which is what it may be
/// bound at rather than what it is bound at.
/// </para>
/// <para>
/// The slice exists for a composition step that decides ONCE at startup what the process exposes —
/// mapping an endpoint whose need depends on a setting — and has to know whether a level above the core
/// one can answer that setting later, per request, where startup cannot see it. It is data and carries
/// no way back to the registry; the registry itself stays internal. The facts are captured when the
/// slice is built, after every registrar of the container has been applied.
/// </para>
/// </summary>
public sealed class ConfigBindingView
{
    /// <summary>
    /// Creates the slice.
    /// </summary>
    /// <param name="boundLevels">Bound levels by setting name, each in resolution precedence order.</param>
    internal ConfigBindingView(FrozenDictionary<string, ImmutableArray<ConfigLevel>> boundLevels) =>
        BoundLevels = boundLevels;

    /// <summary>
    /// Levels each key is bound at in this contour, by setting name, in the order of resolution
    /// precedence (§10.3). A key with no binding at all is absent from the map.
    /// </summary>
    public FrozenDictionary<string, ImmutableArray<ConfigLevel>> BoundLevels { get; }
}
