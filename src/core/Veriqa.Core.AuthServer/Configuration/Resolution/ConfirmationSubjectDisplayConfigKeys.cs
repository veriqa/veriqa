// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// The resolver key of the "surfaces the subject of a confirmation is shown on" axis
/// (SPEC-012 §4.11, CFG-104). One key, one semantics, and no ladder of levels of its own: which
/// level wins and how two of them combine is stated by this declaration and applied by the canonical
/// resolver (CFG-235).
/// </summary>
public static class ConfirmationSubjectDisplayConfigKeys
{
    /// <summary>
    /// Canonical name of the subject-display surfaces key.
    /// </summary>
    public const string SurfacesKeyName = "ConfirmationSubjectDisplay.Surfaces";

    /// <summary>
    /// Catalog of the declarations of this axis — what the composition of the auth server registers,
    /// and what the schema of a deployment carries because of it. It is initialized before the
    /// declarations that fill it, which is the order the initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// The set AS THE PRODUCT SHIPS IT — a default-constructed options object, read for one thing only:
    /// the value the core level yields when the section states nothing. Taking it off the options class
    /// rather than restating it here keeps the two from drifting.
    /// </summary>
    private static readonly ConfirmationSubjectDisplayOptions Shipped = new();

    /// <summary>
    /// Address of the set in the global section of the core level.
    /// </summary>
    private const string CoreAddress =
        ConfirmationSubjectDisplayOptions.SectionName
        + ":" + nameof(ConfirmationSubjectDisplayOptions.Surfaces);

    /// <summary>
    /// Domain of the surface set — which sets the setting admits on ANY level, and what a set outside
    /// it costs (SPEC-012 §10.6, CFG-240/CFG-246). It is declared once, on the key, so the global
    /// section and the entry of an OIDC client are judged by one statement instead of by a check per
    /// reader.
    /// <para>
    /// The EMPTY set is admitted and is the value the product ships (CFG-105): "the subject is shown
    /// nowhere but the page that asks" is a statement a deployment makes, and refusing it here would
    /// refuse the core level's own default.
    /// </para>
    /// <para>
    /// The policy is the strict one, and it is what carries the fail-fast of CFG-108 to every level of
    /// the axis: at the core level an inadmissible set stops the start — where the startup validation
    /// of the options class already stops it — and above the core level the RECORD stating it is left
    /// out of the effective configuration whole (CFG-246), instead of the set of that level being lost
    /// in silence. Being served the surfaces of the level above in place of a set an operator wrote
    /// wrong is exactly the quiet substitution the default policy would allow here.
    /// </para>
    /// <para>
    /// The DIRECTION of the cost is stated rather than left to be discovered: a discarded record leaves
    /// with the narrowing it declared, so this axis — and any neighbouring set axis of the same
    /// record — is resolved from the level below, WIDER than the record stated. What the policy buys is
    /// not a narrower set but a substitution that is loud and whole instead of silent and piecemeal
    /// (SPEC-012 §10.3).
    /// </para>
    /// </summary>
    private static readonly ConfigValueDomain<IReadOnlyCollection<ConfirmationSubjectSurface>> SurfacesDomain =
        new(
            static surfaces => surfaces is not null
                && surfaces.All(static surface => Enum.IsDefined(surface)),
            "every surface of the set is a member of the closed set the product ships; the empty set, "
            + "which shows the subject nowhere but the page that asks, is one of them",
            CoreAddress + " of the core level, or '" + ConfirmationSubjectDisplayOptions.RecordMember
            + "' of the record of the level stating it",
            ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// The set of surfaces the subject may be shown on (CFG-104, CFG-211): a set with narrowing. The
    /// tenant states it and an application may only narrow what the tenant allows — the requirement the
    /// application level is here for.
    /// </summary>
    /// <remarks>
    /// The core level is opted OUT of the intersection
    /// (<c>coreParticipatesInSetIntersection: false</c>) for the same reason
    /// <see cref="ChannelAdapter.MultiTenancy.ChannelConfigKeys.ChannelsEnabled"/> is: the global set
    /// here is the degenerate N=1 default of a self-hosted deployment, not a ceiling over the tenant,
    /// and as a ceiling it would zero out a tenant's set through the intersection whenever the global
    /// one is empty — which is the shipped default (CFG-105). A tenant set replaces the global one;
    /// while the tenant is silent, a global set naming at least one surface is the base an application
    /// narrows (CFG-248), so an application cannot show the subject on a surface the global section does
    /// not allow. The empty global set is no base: it leaves an application's set as the application
    /// states it.
    /// <para>
    /// Above the core level the set is read inside the RECORD of the level, at the member the options
    /// class names, so an application states it as a member of its OIDC client entry and no type of the
    /// entry changes. The core level is the one addressed from the root of the host configuration
    /// (SPEC-012 §10.1) — the very section the options class is bound from, so the resolver and a
    /// direct read of the section cannot part.
    /// </para>
    /// <para>
    /// What the domain reaches has ONE boundary, and it belongs to the source of a level rather than
    /// to this declaration: on a level read from the host configuration the binder of the platform
    /// drops an element it cannot convert and hands back the rest, so a MISSPELT surface name never
    /// becomes a value the domain is asked about — the level states the smaller set instead. A set
    /// stated in a store record is read as one value and fails whole, and a numeric member outside the
    /// enumeration is converted and refused here on every level alike. The spelling of a surface at the
    /// core level is caught by the startup validation of the options class, which reads the text the
    /// operator wrote; above the core level it is a known limitation of the axis (SPEC-012 §10.3).
    /// </para>
    /// </remarks>
    public static ConfigKey<IReadOnlyCollection<ConfirmationSubjectSurface>> Surfaces { get; } = Declared
        .Of<IReadOnlyCollection<ConfirmationSubjectSurface>>(SurfacesKeyName)
        .At(ConfigLevel.Core, CoreAddress)
        .At(ConfigLevel.Tenant, ConfirmationSubjectDisplayOptions.RecordMember)
        .At(ConfigLevel.Application, ConfirmationSubjectDisplayOptions.RecordMember)
        .Set(IntersectSurfaceSets, coreParticipatesInSetIntersection: false)
        .Domain(SurfacesDomain)
        .Default(Shipped.Surfaces)
        .Declare();

    /// <summary>
    /// Intersection of two surface sets (CFG-211): the result is what both layers allow. Narrowing,
    /// never widening — a lower layer cannot add a surface the upper one did not allow.
    /// </summary>
    /// <param name="upper">Set of the level higher in ownership.</param>
    /// <param name="lower">Set of the level lower in ownership.</param>
    /// <returns>Intersection of the sets.</returns>
    private static IReadOnlyCollection<ConfirmationSubjectSurface> IntersectSurfaceSets(
        IReadOnlyCollection<ConfirmationSubjectSurface> upper,
        IReadOnlyCollection<ConfirmationSubjectSurface> lower)
    {
        var upperSet = new HashSet<ConfirmationSubjectSurface>(upper);
        return lower.Where(upperSet.Contains).Distinct().ToArray();
    }

    /// <summary>
    /// Declarations of this axis — what the composition of the auth server registers
    /// (<see cref="VeriqaConfigKeyCatalogs"/>).
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;
}
