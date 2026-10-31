// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.Configuration;

/// <summary>
/// Keys of the Logging axis — the audit mode and the retention of audit records.
/// <para>
/// The home of these two keys is the mechanism assembly, and that is a deliberate exception rather
/// than a new norm: this assembly is otherwise a mechanism without product keys. It is the only
/// place both sides of the axis already depend on — the auth server, which owns the section their
/// core level is read from, and the audit trail, which is the consumer of the resolved values.
/// Putting them in either of those two would make one a dependency of the other, which is exactly
/// backwards for a journal: an audit sink is cross-cutting, not a feature of an OIDC server.
/// </para>
/// <para>
/// Declaring a key and owning its home are different acts (CFG-203). These keys are declared into
/// the schema of a deployment by ANY owner that registers the catalogs — the call that registers a
/// catalog brings this axis with it, so the auth server and the contour of the channels alike carry
/// it — and not by the audit trail: the journal is opt-in, and if it carried the declaration, a key that
/// is read with or without it would disappear from the schema of a deployment that never wired the
/// journal. The price of that arrangement is stated here once, because everything the two keys below
/// promise rests on it: a host that composes the journal onto the transaction engine ALONE registers
/// no catalog of this axis, so its keys are outside that host's schema — no level defines them, the
/// resolution answers with the default of the type, and the strict domains below have nothing to hold
/// there. Both consumers of the values guard against exactly that on their own.
/// </para>
/// <para>
/// The SECTION of the core level and the values the product ships with are stated here, at the keys,
/// and the options class of the auth server takes from here what it still carries: the section is
/// one, and a second literal of it in the other assembly would be a second answer able to drift. The
/// members inside the section are written as text for the mirror reason — the options class lives in
/// the assembly that references this one, not the other way round.
/// </para>
/// </summary>
public static class LoggingConfigKeys
{
    /// <summary>
    /// Section of the application configuration the CORE level of this axis lives in (self-hosted ≡
    /// core, SPEC-012 §10.1). The auth server names the same section through
    /// <c>LoggingOptions.SectionName</c>, which takes its value from here.
    /// </summary>
    public const string CoreSectionName = "Veriqa:Logging";

    /// <summary>
    /// Audit mode the product ships with — the value the core level states where the section states
    /// none. It is declared at the key alone: the section of the auth server is no longer bound into an
    /// object for this setting, so there is no options member left to take a default from.
    /// </summary>
    public const LoggingMode DefaultMode = LoggingMode.System;

    /// <summary>
    /// Retention of audit records the product ships with, in days (SPEC-012 §6.5) — the value the core
    /// level states where the section states none. <c>LoggingOptions.RetentionDays</c> takes its
    /// default from here.
    /// </summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>
    /// Catalog of the declarations of this axis — what the one registrar of the deployment declares and
    /// binds. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Member of the section carrying the audit mode.
    /// </summary>
    private const string ModeMember = "Mode";

    /// <summary>
    /// Member of the section carrying the retention of audit records.
    /// </summary>
    private const string RetentionDaysMember = "RetentionDays";

    /// <summary>
    /// Domain of the audit mode — which values the setting admits at all, and what a value outside it
    /// costs (SPEC-012 §10.6, CFG-240/CFG-246). The members are asked for rather than listed, so a mode
    /// added to the enum brings itself into the boundary the operator is shown.
    /// <para>
    /// The policy is the strict one because the loss here is SILENT and total: a mode nothing can read
    /// leaves the step unset, the shipped answer of the core level takes its place, and the receiver of
    /// the journal — which writes only in the Audit mode — stops writing without a word. A deployment
    /// that asked for an audit trail and is served none learns of it from the absence of records, which
    /// is the one place it must not learn of it from. WHAT the policy costs is not restated here: it
    /// turns on the level the inadmissible value lies at, on whether the walk of the snapshot reaches
    /// that level at all, and on whether the host is starting or reloading — and all of that is the
    /// decision of the policy itself, stated once at
    /// <see cref="ConfigValueRejectionPolicy.FailStart"/> (CFG-246). A second telling of it here would
    /// be a second answer able to drift from the first.
    /// </para>
    /// </summary>
    private static readonly ConfigValueDomain<LoggingMode> ModeDomain =
        new(
            static mode => Enum.IsDefined(mode),
            "one of the audit modes the product ships: " + string.Join(", ", Enum.GetNames<LoggingMode>()),
            CoreSectionName + ":" + ModeMember,
            ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Domain of the retention — a positive number of days. Zero and a negative number are outside it
    /// rather than treated as "keep nothing": the journal is deleted through one channel only, and a
    /// cutoff computed from a non-positive retention would put it at "now" and take every record ever
    /// written with it.
    /// <para>
    /// The policy is the strict one for the mirror reason the mode has: a retention nothing can read
    /// leaves the core level of this protective key with the shipped answer, so a deployment that wrote a
    /// shorter retention on purpose — the direction this axis narrows in — would go on keeping records
    /// for the shipped ninety days believing its own value is in effect.
    /// </para>
    /// </summary>
    private static readonly ConfigValueDomain<int> RetentionDaysDomain =
        new(
            static days => days > 0,
            "a positive number of days",
            CoreSectionName + ":" + RetentionDaysMember,
            ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Logging.Mode (SPEC-011 R8, SPEC-012 CFG-051): a plain value over two levels — the TENANT, who
    /// owns the mode, and the core. The application is not among them and does NOT override the key,
    /// and there is no level above the tenant, so nothing can weaken the mode the owner set. A tenant
    /// that states nothing cedes to the core through the ordinary resolution contract (SPEC-012
    /// §10.6); no branch of resolution of its own appears.
    /// The consumer of the resolved value is the audit trail receiver (SPEC-011): audit records are
    /// written only in the Audit mode, and Disabled/System are equivalent for it — "do not write".
    /// <para>
    /// The shipped value below stays a statement of the CORE level: the default of a key is reached
    /// where the record of the core level said nothing, and above the core a level that stated
    /// nothing simply cedes to the one below (<see cref="DeclaredConfigKey{T}.StatedAtStep"/>). So a tenant
    /// that never wrote the setting does not answer with the shipped mode in place of its own core.
    /// </para>
    /// <para>
    /// The mode is read BY PATH and by nothing else: the section carrying it is not bound into an input
    /// member anywhere, so a spelling the read cannot take is judged by the policy below rather than by
    /// the binder of the platform, which used to stop the host before the mechanism saw the value.
    /// </para>
    /// <para>
    /// The domain is what keeps a mode the deployment WROTE from being read as a mode it never wrote
    /// (see <see cref="ModeDomain"/>): a value outside it, and a value no read can take into the type at
    /// all, are never replaced in silence by the shipped answer of this very key. What such a value
    /// costs instead is the answer of <see cref="ConfigValueRejectionPolicy.FailStart"/> (CFG-246) and
    /// is stated there, not at this key. Both are the guarantee of a host that registers
    /// <see cref="Catalog"/>, which is where it lives. A host that registers none reads no mode here at
    /// all (see the remarks on this class).
    /// </para>
    /// </summary>
    public static ConfigKey<LoggingMode> Mode { get; } = Declared
        .Of<LoggingMode>("Logging.Mode")
        .At(ConfigLevel.Core, CoreSectionName + ":" + ModeMember)

        // The tenant level is addressed by the name of the key alone (Logging:Mode) rather than by a
        // section of its own: the record of that level is a document of the owner's area, and an
        // address derived from the name reads there without carrying the section of the host's
        // application configuration into it.
        .At(ConfigLevel.Tenant)
        .Domain(ModeDomain)
        .Default(DefaultMode)
        .Declare();

    /// <summary>
    /// Logging.RetentionDays (SPEC-012 §6.5/§9): retention of audit records. Protective ceiling —
    /// "stricter" = a smaller value (records live shorter), so a level below the owning one may only
    /// shorten the retention, never extend it.
    /// <para>
    /// Where <see cref="Catalog"/> is registered the core level always states a value, and the domain
    /// is what makes that promise hold for the sweep of the journal (see
    /// <see cref="RetentionDaysDomain"/>): a deployment writing a non-positive retention, or a
    /// retention no read can take into the type, does not start rather than run with the shipped
    /// ninety days under a value of its own. Where it is not registered the promise is not made at
    /// all — the sweep meets the default of the type, and its own guard says so.
    /// </para>
    /// </summary>
    public static ConfigKey<int> RetentionDays { get; } = Declared
        .Of<int>("Logging.RetentionDays")
        .At(ConfigLevel.Core, CoreSectionName + ":" + RetentionDaysMember)
        .ProtectiveCeiling(static (u, l) => Math.Min(u, l))
        .Domain(RetentionDaysDomain)
        .Default(DefaultRetentionDays)
        .Declare();

    /// <summary>
    /// Declarations of this axis — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;
}
