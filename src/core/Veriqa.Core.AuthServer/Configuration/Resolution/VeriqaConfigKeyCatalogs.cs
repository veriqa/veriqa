// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// The catalogs of declared keys the AUTH SERVER ships (SPEC-012 §10.6, CFG-244) — the ONE place that
/// names them, and what its composition registers into the container.
/// <para>
/// The list holds the catalogs THIS composition registers and no other. A catalog belongs to the
/// owner that declares into it, and every owner registers its own at its own composition: the engine
/// of transactions when the engine is composed, the contour of the channels when the channels are, a
/// channel satellite when its channel is. An owner named from here would be declared twice over —
/// once by itself and once by us — and an owner outside the core could not be named here at all: it
/// does not reference this assembly and cannot (TASK-133).
/// </para>
/// <para>
/// All but one of the catalogs are declared in this assembly; the odd one — the inbound-verification
/// axis (<see cref="ChannelInboundVerificationConfigKeys"/>) — in the assembly of the mechanism. That is
/// the NORM and not a deviation from it: declaring a key, owning its home and registering its catalog
/// are three different acts (CFG-203). The home of that axis is the mechanism assembly because its
/// two readers — the channel contour and the audit trail — depend on neither one another, and this
/// composition registers it because the contour that reads it registers nothing for it. A host that
/// composes the channels WITHOUT this server names that catalog at its own composition root instead:
/// the contour refuses to start a deployment that enabled a channel and declared no value, so an
/// undeclared key is not the harmless absence it is for the keys of any other owner such a host did
/// not compose.
/// </para>
/// <para>
/// A key that is NOT declared through a catalog is absent from here by construction: the handwritten
/// registrars declare their keys themselves, and this list says nothing about them.
/// </para>
/// </summary>
public static class VeriqaConfigKeyCatalogs
{
    /// <summary>
    /// Every catalog of the auth server, in no particular order — a consumer that prints them orders
    /// by what it prints and not by the order they are written in here.
    /// </summary>
    public static IReadOnlyList<ConfigKeyCatalog> All { get; } =
    [
        AuthServerConfigKeys.Catalog,
        RateLimitConfigKeys.Catalog,
        ConfirmationSubjectDisplayConfigKeys.Catalog,
        HopModeConfigKeys.Catalog,
        ScopesClaimsConfigKeys.Catalog,
        ChannelInboundVerificationConfigKeys.Catalog
    ];
}
