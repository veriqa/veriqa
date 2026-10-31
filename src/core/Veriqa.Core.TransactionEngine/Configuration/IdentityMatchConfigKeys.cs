// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.TransactionEngine.Configuration;

/// <summary>
/// Resolver key registry of the comparable identity types (SPEC-012 §4.12, CFG-156). The set is
/// stated by the tenant and an application may only NARROW it (CFG-211); <c>ui_config</c> is
/// deliberately not among the levels — a record a request parameter selects carries wording and
/// styling, not comparison rights (CFG-161).
/// </summary>
public static class IdentityMatchConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this registry — what the one registrar of the deployment
    /// declares and binds.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// The set AS THE PRODUCT SHIPS IT — a default-constructed options object, read for one thing
    /// only: the value the core level yields when the section states nothing. Taking it off the
    /// options class rather than restating it here keeps the two from drifting.
    /// </summary>
    private static readonly IdentityMatchOptions Shipped = new();

    /// <summary>
    /// Address of the set in the global section of the core level.
    /// </summary>
    private const string CoreAddress =
        IdentityMatchOptions.SectionName + ":" + nameof(IdentityMatchOptions.ComparableTypes);

    /// <summary>
    /// Domain of the declared set — which sets the setting admits on ANY level, and what a set outside
    /// it costs (SPEC-012 §10.6, CFG-240/CFG-246). The predicate is the one CFG-160 already holds over
    /// the global section, declared once on the key so that it holds over the entry of an OIDC client
    /// and the record of a tenant as well.
    /// <para>
    /// The EMPTY set is admitted and is the value the product ships (CFG-157): "no comparison is
    /// performed" is a statement a deployment makes, and refusing it here would refuse the core level's
    /// own default.
    /// </para>
    /// <para>
    /// The policy is the strict one: a declaration that is silently ignored leaves an operator
    /// believing a comparison is in effect that is not, which is why CFG-160 makes it fatal at the core
    /// level. Above the core level the same reasoning gives the outcome of CFG-246 — the record that
    /// states the broken set is left out of the effective configuration whole, rather than the
    /// application being quietly served the types of the level above.
    /// </para>
    /// <para>
    /// The DIRECTION of the cost is stated rather than left to be discovered: a discarded record leaves
    /// with the narrowing it declared, so this axis — and any neighbouring set axis of the same
    /// record — is resolved from the level below, WIDER than the record stated. What the policy buys is
    /// not a narrower set but a substitution that is loud and whole instead of silent and piecemeal
    /// (SPEC-012 §10.3).
    /// </para>
    /// </summary>
    private static readonly ConfigValueDomain<IReadOnlyList<ComparableIdentityType>> ComparableTypesDomain =
        new(
            IsWellFormedSet,
            "every declared type states a name unique in the set, a claim name that is not blank and a "
            + "normalization rule the core ships; the empty set, which performs no comparison at all, "
            + "is admitted",
            CoreAddress + " of the core level, or '" + IdentityMatchOptions.RecordMember
            + "' of the record of the level stating it",
            ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Set of declared comparable types. Set semantics (CFG-211): a level below by ownership may only
    /// narrow what the level above allows.
    /// </summary>
    /// <remarks>
    /// The core level is no ceiling over the tenant (<c>coreParticipatesInSetIntersection: false</c>):
    /// its shipped set is empty (CFG-157), and as a ceiling an empty set would zero out everything a
    /// tenant or an application declares — the narrowing defect SPEC-012 §10.3 names. In a self-hosted
    /// installation the global section IS the set (CFG-162): a tenant, once it states one, replaces it;
    /// while the tenant is silent a global set that declares at least one type is the base an
    /// application narrows, so an application cannot compare by a type the global section does not
    /// declare, and a type both declare is compared by the global declaration — its claim and its
    /// normalization rule (<see cref="Narrow"/> keeps the upper one). The empty global set is no base:
    /// it leaves an application's set as the application states it.
    /// <para>
    /// What the domain reaches has ONE boundary, and it belongs to the source of a level rather than to
    /// this declaration: on a level read from the host configuration the binder of the platform drops
    /// an element it cannot convert and hands back the rest, so a declaration naming a MISSPELT
    /// normalization rule never becomes a value the domain is asked about — the level states the set
    /// without it. A set stated in a store record is read as one value and fails whole, and everything
    /// the binder does convert — a blank name, a repeated one, a rule outside the enumeration written
    /// as a number — is refused here on every level alike. The spelling of a rule at the core level is
    /// caught by the startup validation of the options class, which reads the text the operator wrote;
    /// above the core level it is a known limitation of the axis (SPEC-012 §10.3).
    /// </para>
    /// </remarks>
    public static ConfigKey<IReadOnlyList<ComparableIdentityType>> ComparableTypes { get; } = Declared
        .Of<IReadOnlyList<ComparableIdentityType>>("IdentityMatch.ComparableTypes")
        .At(ConfigLevel.Core, CoreAddress)
        .At(ConfigLevel.Tenant, IdentityMatchOptions.RecordMember)
        .At(ConfigLevel.Application, IdentityMatchOptions.RecordMember)
        .Set(Narrow, coreParticipatesInSetIntersection: false)
        .Domain(ComparableTypesDomain)
        .Default(Shipped.ComparableTypes)
        .Declare();

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Whether a declared set is one the axis admits (CFG-160): every type states a name unique within
    /// the set, a claim name that is not blank, and a normalization rule the core ships. The three are
    /// asked together because they are one question — whether the set can be COMPARED BY — and a set
    /// failing any of them would leave a relying party believing a comparison is in effect that is not.
    /// </summary>
    /// <param name="types">Set stated by a level.</param>
    /// <returns><c>true</c> when the axis admits the set.</returns>
    private static bool IsWellFormedSet(IReadOnlyList<ComparableIdentityType> types)
    {
        if (types is null)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declared in types)
        {
            if (declared is null
                || string.IsNullOrWhiteSpace(declared.Name)
                || string.IsNullOrWhiteSpace(declared.ClaimName)
                || !Enum.IsDefined(declared.Normalization)
                || !names.Add(declared.Name))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Narrowing of two declared sets: only the types the upper level allows survive, and the
    /// DECLARATION of the upper one is what survives with them.
    /// </summary>
    /// <remarks>
    /// The lower level selects names, it does not redefine what a name means: the claim a value is
    /// taken from and the rule it is compared by belong to the owner of the axis (CFG-158). Letting
    /// the lower level bring its own claim or its own weakening rule under a name the upper one
    /// declared would be exactly the widening the narrowing rule forbids.
    /// </remarks>
    /// <param name="upper">Set of the level higher in ownership.</param>
    /// <param name="lower">Set of the level lower in ownership.</param>
    /// <returns>The narrowed set.</returns>
    private static IReadOnlyList<ComparableIdentityType> Narrow(
        IReadOnlyList<ComparableIdentityType> upper,
        IReadOnlyList<ComparableIdentityType> lower)
    {
        var kept = new HashSet<string>(lower.Select(static type => type.Name), StringComparer.Ordinal);

        return [.. upper.Where(type => kept.Contains(type.Name))];
    }
}
