// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Configuration;

/// <summary>
/// Resolver key registry of the email completion (SPEC-041 C96, E61) and of the collected claims store
/// (SPEC-041 C84): the ordered list of the ways the email may be obtained, the limits of the mail the
/// completion sends, and the automatic removal of the collected claims. Declared in the engine, so that the
/// auth server, the Email adapter, the planner and the removal read the one declaration.
/// <para>
/// The keys of the email live at the core, tenant and application levels; the removal — at the core and
/// tenant levels, because the store is kept per tenant and not per application. The core level is written in
/// the global section <c>Veriqa:ClaimCompletion</c>; the tenant and the application state a member of their
/// record named after its subject, because the record is flat and shared by every owner writing into it.
/// </para>
/// </summary>
public static class ClaimCompletionConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this registry — what the one registrar of the deployment declares
    /// and binds. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Address of the list of methods in the global section of the core level.
    /// </summary>
    public const string CoreEmailMethodsAddress = "Veriqa:ClaimCompletion:EmailMethods";

    /// <summary>
    /// Member of the record of a tenant or an application the list of methods is stated at.
    /// </summary>
    public const string EmailMethodsRecordMember = "ClaimCompletionEmailMethods";

    /// <summary>
    /// Address of the limits of the completion mail in the global section of the core level.
    /// </summary>
    private const string CoreEmailLimitsAddress = "Veriqa:ClaimCompletion:EmailLimits";

    /// <summary>
    /// Member of the record of a tenant or an application the limits of the completion mail are stated
    /// under.
    /// </summary>
    private const string EmailLimitsRecordMember = "ClaimCompletionEmailLimits";

    /// <summary>
    /// Address of the retention of the collected claims in the global section of the core level.
    /// </summary>
    private const string CoreCollectedClaimsRetentionDaysAddress = "Veriqa:ClaimCompletion:CollectedClaims:RetentionDays";

    /// <summary>
    /// Member of the record of a tenant the retention of the collected claims is stated at.
    /// </summary>
    private const string CollectedClaimsRetentionDaysRecordMember = "ClaimCompletionCollectedClaimsRetentionDays";

    /// <summary>
    /// Where a correct retention of the collected claims is written, as a rejection names it.
    /// </summary>
    private const string CollectedClaimsRetentionDaysCorrectAt =
        CoreCollectedClaimsRetentionDaysAddress + " of the core level, or '" + CollectedClaimsRetentionDaysRecordMember
        + "' of the record of the tenant";

    /// <summary>
    /// Retention of the collected claims the product ships: off — nothing is removed automatically.
    /// </summary>
    private const int ShippedCollectedClaimsRetentionDays = 0;

    /// <summary>
    /// Where a correct list of methods is written, as a rejection names it.
    /// </summary>
    private const string EmailMethodsCorrectAt =
        CoreEmailMethodsAddress + " of the core level, or '" + EmailMethodsRecordMember
        + "' of the record of the level stating it";

    /// <summary>
    /// Where a correct limit is written, as a rejection names it.
    /// </summary>
    private const string EmailLimitsCorrectAt =
        CoreEmailLimitsAddress + " of the core level, or '" + EmailLimitsRecordMember
        + "' of the record of the level stating it";

    /// <summary>
    /// The list the product ships (SPEC-041 C96, decision of 2026-09-26). <c>Code</c> and
    /// <c>MagicLink</c> are one way for the user — "confirm via email": the page of the initiating device
    /// offers that way alone, with a mail carrying both the code and the link; the page of the confirming
    /// device offers the mail of the user (<c>Push</c>) first and that way after it.
    /// </summary>
    private static readonly IReadOnlyList<EmailCompletionMethod> ShippedEmailMethods =
    [
        EmailCompletionMethod.Push,
        EmailCompletionMethod.Code,
        EmailCompletionMethod.MagicLink
    ];

    /// <summary>
    /// The limits of the completion mail the product ships (SPEC-041 E61).
    /// </summary>
    private static class ShippedLimits
    {
        /// <summary>Attempts at entering one code.</summary>
        public const int CodeAttempts = 5;

        /// <summary>Mails of one transaction.</summary>
        public const int MailsPerTransaction = 3;

        /// <summary>Mails of one channel account within its window.</summary>
        public const int MailsPerSubject = 10;

        /// <summary>Mails to one recipient address within its window.</summary>
        public const int MailsPerAddress = 5;

        /// <summary>Mails to one recipient domain within its window: off.</summary>
        public const int MailsPerDomain = 0;
    }

    /// <summary>
    /// Domain of the list of methods on ANY level: at least one method, no method twice, every one a
    /// member of the enumeration.
    /// </summary>
    /// <remarks>
    /// The empty list is refused on every level alike. While the tenant is silent the list of the core
    /// level is the base an application narrows only when it names at least one method; an empty core
    /// list would therefore not narrow anything, and an application would STATE the list instead of
    /// narrowing it. Switching the email completion off is a matter of the requirement level of the
    /// claim, not of an empty list. The policy is the strict one: a list silently replaced by the one of
    /// the level below would leave an operator believing a method is excluded that is offered.
    /// </remarks>
    private static readonly ConfigValueDomain<IReadOnlyList<EmailCompletionMethod>> EmailMethodsDomain = new(
        IsWellFormedList,
        "at least one method, each of Push, Code, MagicLink and Unverified, none named twice",
        EmailMethodsCorrectAt,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Domain of a limit that is always on: a positive number.
    /// </summary>
    private static readonly ConfigValueDomain<int> PositiveLimitDomain = new(
        static limit => limit >= 1,
        "a whole number of at least 1",
        EmailLimitsCorrectAt,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Domain of the limit per recipient domain, which zero switches off.
    /// </summary>
    private static readonly ConfigValueDomain<int> SwitchableLimitDomain = new(
        static limit => limit >= 0,
        "a whole number of at least 0, where 0 switches the limit off",
        EmailLimitsCorrectAt,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Domain of the retention of the collected claims: a number of days, where 0 switches the removal off.
    /// </summary>
    private static readonly ConfigValueDomain<int> CollectedClaimsRetentionDaysDomain = new(
        static days => days >= 0,
        "a whole number of days of at least 0, where 0 switches the automatic removal off",
        CollectedClaimsRetentionDaysCorrectAt,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Ordered list of the methods the email may be obtained by (SPEC-041 C96). <c>Code</c> and
    /// <c>MagicLink</c> stand for one way of the user, placed where the first of them is; the first way
    /// available on the surface is the main one, the other available ones are the alternatives.
    /// </summary>
    /// <remarks>
    /// Set semantics without the core level in the intersection (SPEC-012 CFG-162, CFG-248): a tenant that
    /// states a list replaces the core one; while the tenant is silent the core list is the base, and an
    /// application only narrows the base it is given. The narrowing keeps the methods of the level above
    /// that the level below names, IN THE ORDER OF THE LEVEL ABOVE: an application chooses which methods
    /// it admits, not which one comes first. A narrowing to nothing is a legitimate outcome — the email
    /// then has no completion path for that application.
    /// </remarks>
    public static ConfigKey<IReadOnlyList<EmailCompletionMethod>> EmailMethods { get; } = Declared
        .Of<IReadOnlyList<EmailCompletionMethod>>("ClaimCompletion.EmailMethods")
        .At(ConfigLevel.Core, CoreEmailMethodsAddress)
        .At(ConfigLevel.Tenant, EmailMethodsRecordMember)
        .At(ConfigLevel.Application, EmailMethodsRecordMember)
        .Set(NarrowInOrderOfUpper, coreParticipatesInSetIntersection: false)
        .Domain(EmailMethodsDomain)
        .Default(ShippedEmailMethods)
        .Declare();

    /// <summary>
    /// Attempts at entering one code of a completion mail (SPEC-041 E61); the code is spent when they end.
    /// </summary>
    public static ConfigKey<int> EmailCodeAttempts { get; } = PositiveLimit(
        "ClaimCompletion.EmailCodeAttempts", "CodeAttempts", ShippedLimits.CodeAttempts);

    /// <summary>
    /// Completion mails of one transaction, with a code and with a link together (SPEC-041 E61).
    /// </summary>
    public static ConfigKey<int> EmailMailsPerTransaction { get; } = PositiveLimit(
        "ClaimCompletion.EmailMailsPerTransaction", "MailsPerTransaction", ShippedLimits.MailsPerTransaction);

    /// <summary>
    /// Completion mails of one channel account over its transactions within a fixed window of 24 hours
    /// (SPEC-041 E61).
    /// </summary>
    public static ConfigKey<int> EmailMailsPerSubject { get; } = PositiveLimit(
        "ClaimCompletion.EmailMailsPerSubject", "MailsPerSubject", ShippedLimits.MailsPerSubject);

    /// <summary>
    /// Completion mails to one recipient address within a fixed window of one hour (SPEC-041 E61). A
    /// refusal by this limit does not exhaust a method: another address may be entered.
    /// </summary>
    public static ConfigKey<int> EmailMailsPerAddress { get; } = PositiveLimit(
        "ClaimCompletion.EmailMailsPerAddress", "MailsPerAddress", ShippedLimits.MailsPerAddress);

    /// <summary>
    /// Completion mails to one recipient domain within a fixed window of one hour (SPEC-041 E61);
    /// <c>0</c> — the limit is off, which is what the product ships: one counter per domain would block
    /// the mass domains of mailbox providers, so an operator switches it on explicitly.
    /// </summary>
    /// <remarks>
    /// Stricter is the smaller POSITIVE value, and <c>0</c> yields to any positive one: an application can
    /// switch the limit on or lower it, never switch off the limit its tenant set.
    /// </remarks>
    public static ConfigKey<int> EmailMailsPerDomain { get; } = Declared
        .Of<int>("ClaimCompletion.EmailMailsPerDomain")
        .At(ConfigLevel.Core, CoreEmailLimitsAddress + ":MailsPerDomain")
        .At(ConfigLevel.Tenant, EmailLimitsRecordMember + ":MailsPerDomain")
        .At(ConfigLevel.Application, EmailLimitsRecordMember + ":MailsPerDomain")
        .ProtectiveCeiling(StricterSwitchableLimit)
        .Domain(SwitchableLimitDomain)
        .Default(ShippedLimits.MailsPerDomain)
        .Declare();

    /// <summary>
    /// Automatic removal of the collected claims (SPEC-041 C84): the number of days without a sign-in after
    /// which the records of a subject are deleted from the collected claims store; <c>0</c> — off, which is
    /// what the product ships. A plain value over the core and tenant levels: a tenant stating it replaces
    /// the core value, so a tenant can switch the removal on while the core keeps it off. Read only where a
    /// collected claims store is registered.
    /// </summary>
    public static ConfigKey<int> CollectedClaimsRetentionDays { get; } = Declared
        .Of<int>("ClaimCompletion.CollectedClaimsRetentionDays")
        .At(ConfigLevel.Core, CoreCollectedClaimsRetentionDaysAddress)
        .At(ConfigLevel.Tenant, CollectedClaimsRetentionDaysRecordMember)
        .Domain(CollectedClaimsRetentionDaysDomain)
        .Default(ShippedCollectedClaimsRetentionDays)
        .Declare();

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Declares a limit that is always on: the core, tenant and application levels under the member of
    /// the limits, the smaller value being the stricter one.
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="member">Member of the limits the value is written at.</param>
    /// <param name="shipped">Limit the product ships with.</param>
    /// <returns>Setting key.</returns>
    private static ConfigKey<int> PositiveLimit(string name, string member, int shipped) =>
        Declared
            .Of<int>(name)
            .At(ConfigLevel.Core, CoreEmailLimitsAddress + ":" + member)
            .At(ConfigLevel.Tenant, EmailLimitsRecordMember + ":" + member)
            .At(ConfigLevel.Application, EmailLimitsRecordMember + ":" + member)
            .ProtectiveCeiling(static (upper, lower) => Math.Min(upper, lower))
            .Domain(PositiveLimitDomain)
            .Default(shipped)
            .Declare();

    /// <summary>
    /// The stricter of two limits a zero switches off: the smaller positive one; a zero yields to any
    /// positive value.
    /// </summary>
    /// <param name="upper">Limit of the level higher in ownership.</param>
    /// <param name="lower">Limit of the level lower in ownership.</param>
    /// <returns>The stricter limit.</returns>
    private static int StricterSwitchableLimit(int upper, int lower)
    {
        if (upper == 0)
        {
            return lower;
        }

        return lower == 0 ? upper : Math.Min(upper, lower);
    }

    /// <summary>
    /// Narrowing of two lists of methods: the methods of the upper list the lower one names, in the order
    /// of the upper list.
    /// </summary>
    /// <param name="upper">List of the level higher in ownership — the base.</param>
    /// <param name="lower">List of the level lower in ownership.</param>
    /// <returns>The narrowed list.</returns>
    private static IReadOnlyList<EmailCompletionMethod> NarrowInOrderOfUpper(
        IReadOnlyList<EmailCompletionMethod> upper,
        IReadOnlyList<EmailCompletionMethod> lower) =>
        [.. upper.Where(method => lower.Contains(method)).Distinct()];

    /// <summary>
    /// Whether a list of methods is one the setting admits on any level.
    /// </summary>
    /// <param name="methods">List stated by a level.</param>
    /// <returns><c>true</c> when the setting admits the list.</returns>
    private static bool IsWellFormedList(IReadOnlyList<EmailCompletionMethod> methods) =>
        methods is { Count: > 0 }
        && methods.All(static method => Enum.IsDefined(method))
        && methods.Distinct().Count() == methods.Count;
}
