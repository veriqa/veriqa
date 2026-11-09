// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Canonical resolver keys owned by the Email channel (TASK-040, SPEC-012 §10.6). The channel owns
/// the CATALOG its keys are declared into and binds their core level itself
/// (<see cref="EmailConfigKeyRegistrar"/>), so no shared registry has to list the channels the
/// product happens to ship.
/// <para>
/// The catalog is registered where the channel is COMPOSED — <c>AddEmail()</c> — and that is what
/// carries the key into the schema of a deployment that added this channel and leaves it out of one
/// that did not. Declaring a key and binding a level of it are different acts (CFG-203): the
/// declaration lives here, the core-level getter in the registrar named above.
/// </para>
/// </summary>
public static class EmailConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this channel — what its registration puts into the schema of
    /// the deployment. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Email tenant-credential group of the layer (<c>Channels.email.Credentials</c>).
    /// </summary>
    public static ConfigKey<EmailTenantCredentials> Credentials { get; } =
        ChannelConfigKeys.CredentialKey<EmailTenantCredentials>(Declared, ChannelTypes.Email);

    /// <summary>
    /// Section of the record of a tenant or an application the sign-in settings of this channel are
    /// stated under. The record mirrors the root of the core level, as the per-channel hop settings
    /// do (<c>Channels:{Channel}:HopRedirectStyle</c>).
    /// </summary>
    internal const string RecordSection = "Channels:Email";

    /// <summary>
    /// Member of <see cref="RecordSection"/> the set of the sign-in modes is stated at.
    /// </summary>
    internal const string SignInModesMember = "SignInModes";

    /// <summary>
    /// Address of the set of the sign-in modes inside the record of a tenant or an application.
    /// </summary>
    internal const string SignInModesRecordAddress = RecordSection + ":" + SignInModesMember;

    /// <summary>
    /// Member the preferred sign-in mode is stated at: the member of <see cref="EmailOptions"/> at the
    /// core level and the member of <see cref="RecordSection"/> above it.
    /// </summary>
    private const string PreferredModeMember = "PreferredMode";

    /// <summary>
    /// Member the set of what the sign-in mail carries is stated at: the member of the global section at the
    /// core level and the member of <see cref="RecordSection"/> above it.
    /// </summary>
    internal const string PullMethodsMember = "PullMethods";

    /// <summary>
    /// Address of the set of what the sign-in mail carries inside the record of a tenant or an application.
    /// </summary>
    internal const string PullMethodsRecordAddress = RecordSection + ":" + PullMethodsMember;

    /// <summary>
    /// Member the attempts at entering one code of a sign-in mail are stated at: the member of the global
    /// section at the core level and the member of <see cref="RecordSection"/> above it.
    /// </summary>
    private const string SignInCodeAttemptsMember = "SignInCodeAttempts";

    /// <summary>
    /// Member the limit of the sign-in mails to one recipient address is stated at: the member of the global
    /// section at the core level and the member of <see cref="RecordSection"/> above it.
    /// </summary>
    private const string SignInMailsPerAddressMember = "SignInMailsPerAddress";

    /// <summary>
    /// Member the limit of the sign-in mails to one recipient domain is stated at: the member of the global
    /// section at the core level and the member of <see cref="RecordSection"/> above it.
    /// </summary>
    private const string SignInMailsPerDomainMember = "SignInMailsPerDomain";

    /// <summary>
    /// Canonical name of the key of the sign-in modes.
    /// </summary>
    private const string SignInModesKeyName = "Channels." + ChannelTypes.Email + "." + SignInModesMember;

    /// <summary>
    /// Canonical name of the key of the preferred sign-in mode.
    /// </summary>
    private const string PreferredModeKeyName = "Channels." + ChannelTypes.Email + "." + PreferredModeMember;

    /// <summary>
    /// Canonical name of the key of what the sign-in mail carries.
    /// </summary>
    private const string PullMethodsKeyName = "Channels." + ChannelTypes.Email + "." + PullMethodsMember;

    /// <summary>
    /// Canonical name of the key of the attempts at entering one code of a sign-in mail.
    /// </summary>
    private const string SignInCodeAttemptsKeyName = "Channels." + ChannelTypes.Email + "." + SignInCodeAttemptsMember;

    /// <summary>
    /// Canonical name of the key of the sign-in mails to one recipient address.
    /// </summary>
    private const string SignInMailsPerAddressKeyName = "Channels." + ChannelTypes.Email + "." + SignInMailsPerAddressMember;

    /// <summary>
    /// Canonical name of the key of the sign-in mails to one recipient domain.
    /// </summary>
    private const string SignInMailsPerDomainKeyName = "Channels." + ChannelTypes.Email + "." + SignInMailsPerDomainMember;

    /// <summary>
    /// Where a correct set of what the sign-in mail carries is written, as a rejection names it.
    /// </summary>
    private const string PullMethodsCorrectAt =
        "'" + EmailOptions.SectionName + ":" + PullMethodsMember + "' of the core level, or '" + PullMethodsRecordAddress
        + "' of the record of a tenant or an application";

    /// <summary>
    /// Where a correct number of attempts at entering one code of a sign-in mail is written, as a rejection
    /// names it.
    /// </summary>
    private const string SignInCodeAttemptsCorrectAt =
        "'" + EmailOptions.SectionName + ":" + SignInCodeAttemptsMember + "' of the core level, or '" + RecordSection
        + ":" + SignInCodeAttemptsMember + "' of the record of a tenant or an application";

    /// <summary>
    /// Where a correct limit of the sign-in mails to one recipient address is written, as a rejection names it.
    /// </summary>
    private const string SignInMailsPerAddressCorrectAt =
        "'" + EmailOptions.SectionName + ":" + SignInMailsPerAddressMember + "' of the core level, or '" + RecordSection
        + ":" + SignInMailsPerAddressMember + "' of the record of a tenant or an application";

    /// <summary>
    /// Where a correct limit of the sign-in mails to one recipient domain is written, as a rejection names it.
    /// </summary>
    private const string SignInMailsPerDomainCorrectAt =
        "'" + EmailOptions.SectionName + ":" + SignInMailsPerDomainMember + "' of the core level, or '" + RecordSection
        + ":" + SignInMailsPerDomainMember + "' of the record of a tenant or an application";

    /// <summary>
    /// Where a correct set of the sign-in modes is written, as a rejection names it.
    /// </summary>
    private const string SignInModesCorrectAt =
        "'" + SignInModesRecordAddress + "' of the record of a tenant or an application";

    /// <summary>
    /// Domain of the set of the sign-in modes: every element a member of <see cref="EmailMode"/>.
    /// </summary>
    /// <remarks>
    /// The empty set is admitted: it is the ceiling of the core level when both modes are switched off
    /// there, and the domain judges the getter of the core level as well. The policy is the lenient one:
    /// a level stating an unreadable set is skipped, and the level above it stands.
    /// </remarks>
    private static readonly ConfigValueDomain<IReadOnlyList<EmailMode>> SignInModesDomain = new(
        IsWellFormedModeSet,
        "a list of the modes Pull and Push",
        SignInModesCorrectAt,
        ConfigValueRejectionPolicy.SkipStep);

    /// <summary>
    /// Domain of the set of what the sign-in mail carries: at least one element, every element a member of
    /// <see cref="EmailPullMethod"/>.
    /// </summary>
    /// <remarks>
    /// The policy is the lenient one, as for the sign-in modes: a level stating a set that cannot be read, or
    /// that names nothing, is skipped, and the level above it stands — at the core level the shipped set
    /// (<see cref="EmailAdapterConfigDefaults.DefaultPullMethods"/>). On the core level the binder of the
    /// platform drops an element it cannot convert, so a core list naming only unknown methods reaches this
    /// domain empty and is refused here.
    /// </remarks>
    private static readonly ConfigValueDomain<IReadOnlyList<EmailPullMethod>> PullMethodsDomain = new(
        IsWellFormedPullMethods,
        "a list naming at least one of the methods MagicLink and Code",
        PullMethodsCorrectAt,
        ConfigValueRejectionPolicy.SkipStep);

    /// <summary>
    /// Domain of the attempts at entering one code of a sign-in mail: a positive number, judged as the
    /// attempts of the code of the claims completion are (SPEC-041 E61).
    /// </summary>
    private static readonly ConfigValueDomain<int> SignInCodeAttemptsDomain = new(
        static attempts => attempts >= 1,
        "a whole number of at least 1",
        SignInCodeAttemptsCorrectAt,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Domain of the limit of the sign-in mails to one recipient address: a positive number, judged as the limit
    /// of the mails of the claims completion to one address is (SPEC-041 E61).
    /// </summary>
    private static readonly ConfigValueDomain<int> SignInMailsPerAddressDomain = new(
        static limit => limit >= 1,
        "a whole number of at least 1",
        SignInMailsPerAddressCorrectAt,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Domain of the limit of the sign-in mails to one recipient domain: a number of at least 0, where 0 switches
    /// the limit off — judged as the limit of the mails of the claims completion to one domain is.
    /// </summary>
    private static readonly ConfigValueDomain<int> SignInMailsPerDomainDomain = new(
        static limit => limit >= 0,
        "a whole number of at least 0, where 0 switches the limit off",
        SignInMailsPerDomainCorrectAt,
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// The set of the modes a sign-in by Email is offered in (<see cref="SignInModesKeyName"/>). The
    /// core level is the CEILING — the modes the deployment serves (<c>PullEnabled</c>,
    /// <c>PushEnabled</c>) — and it is bound by a getter over the options of the channel
    /// (<see cref="EmailConfigKeyRegistrar"/>), so it carries no address. A tenant and an application
    /// narrow it at <see cref="SignInModesRecordAddress"/> of their record; neither can add a mode the
    /// level above does not allow.
    /// </summary>
    /// <remarks>
    /// Set semantics with the core level in the intersection, as its ceiling. A level whose set names no
    /// mode of the level above is skipped, and the level above stands (<see cref="NarrowOrKeepUpper"/>):
    /// there is no empty choice — switching Email off for an application is the axis of the enabled
    /// channels, not an empty set. A list written empty is kept by the configuration of the platform as a
    /// key without a value, which is a level stating nothing, so it does not narrow either.
    /// </remarks>
    internal static ConfigKey<IReadOnlyList<EmailMode>> SignInModes { get; } = Declared
        .Of<IReadOnlyList<EmailMode>>(SignInModesKeyName)
        .AtIdentity(ConfigLevel.Core)
        .At(ConfigLevel.Tenant, SignInModesRecordAddress)
        .At(ConfigLevel.Application, SignInModesRecordAddress)
        .Set(NarrowOrKeepUpper)
        .Domain(SignInModesDomain)
        .Declare();

    /// <summary>
    /// The sign-in mode shown first when the effective set holds both (<see cref="PreferredModeKeyName"/>):
    /// a plain value of the application, tenant and core levels, the first level stating it wins. A
    /// preferred mode outside the effective set does not apply — the mode left in the set does.
    /// </summary>
    internal static ConfigKey<EmailMode> PreferredSignInMode { get; } = Declared
        .Of<EmailMode>(PreferredModeKeyName)
        .At(ConfigLevel.Core, EmailOptions.SectionName + ":" + PreferredModeMember)
        .At(ConfigLevel.Tenant, RecordSection + ":" + PreferredModeMember)
        .At(ConfigLevel.Application, RecordSection + ":" + PreferredModeMember)
        .Default(EmailAdapterConfigDefaults.DefaultPreferredMode)
        .Declare();

    /// <summary>
    /// What the mail of a sign-in by an address typed into the page carries (<see cref="PullMethodsKeyName"/>):
    /// the magic link, the code, or both in one mail. A plain value of the core, tenant and application levels
    /// — the first level stating it wins — that applies only while Pull is in the effective set of the
    /// sign-in modes. Repeats and the order of the elements carry no meaning: the set is read as which
    /// methods it names (<see cref="EmailSignInModes"/>). A list written empty is kept by the configuration of
    /// the platform as a key without a value, which is a level stating nothing.
    /// </summary>
    internal static ConfigKey<IReadOnlyList<EmailPullMethod>> PullMethods { get; } = Declared
        .Of<IReadOnlyList<EmailPullMethod>>(PullMethodsKeyName)
        .At(ConfigLevel.Core, EmailOptions.SectionName + ":" + PullMethodsMember)
        .At(ConfigLevel.Tenant, PullMethodsRecordAddress)
        .At(ConfigLevel.Application, PullMethodsRecordAddress)
        .Domain(PullMethodsDomain)
        .Default(EmailAdapterConfigDefaults.DefaultPullMethods)
        .Declare();

    /// <summary>
    /// Attempts at entering one code of a sign-in mail (<see cref="SignInCodeAttemptsKeyName"/>); the code is
    /// spent when they end, and a new mail brings a new code. A key of its own, beside the attempts of the code
    /// of the claims completion and with their semantics (SPEC-041 E61): the core, tenant and application
    /// levels, the smaller value being the stricter one — so a setting of the claims completion does not
    /// change the sign-in.
    /// </summary>
    internal static ConfigKey<int> SignInCodeAttempts { get; } = Declared
        .Of<int>(SignInCodeAttemptsKeyName)
        .At(ConfigLevel.Core, EmailOptions.SectionName + ":" + SignInCodeAttemptsMember)
        .At(ConfigLevel.Tenant, RecordSection + ":" + SignInCodeAttemptsMember)
        .At(ConfigLevel.Application, RecordSection + ":" + SignInCodeAttemptsMember)
        .ProtectiveCeiling(static (upper, lower) => Math.Min(upper, lower))
        .Domain(SignInCodeAttemptsDomain)
        .Default(EmailAdapterConfigDefaults.DefaultSignInCodeAttempts)
        .Declare();

    /// <summary>
    /// Sign-in mails to one recipient address within a fixed window of one hour
    /// (<see cref="SignInMailsPerAddressKeyName"/>, SPEC-016 EM-064), counted per tenant over all the
    /// transactions of that tenant. A key of its own, beside the limit of the mails of the claims completion to
    /// one address and with its semantics (SPEC-041 E61): the core, tenant and application levels, the smaller
    /// value being the stricter one — so a setting of the claims completion does not change the sign-in, and
    /// the mails of the two are counted apart.
    /// </summary>
    internal static ConfigKey<int> SignInMailsPerAddress { get; } = Declared
        .Of<int>(SignInMailsPerAddressKeyName)
        .At(ConfigLevel.Core, EmailOptions.SectionName + ":" + SignInMailsPerAddressMember)
        .At(ConfigLevel.Tenant, RecordSection + ":" + SignInMailsPerAddressMember)
        .At(ConfigLevel.Application, RecordSection + ":" + SignInMailsPerAddressMember)
        .ProtectiveCeiling(static (upper, lower) => Math.Min(upper, lower))
        .Domain(SignInMailsPerAddressDomain)
        .Default(EmailAdapterConfigDefaults.DefaultSignInMailsPerAddress)
        .Declare();

    /// <summary>
    /// Sign-in mails to one recipient domain within a fixed window of one hour
    /// (<see cref="SignInMailsPerDomainKeyName"/>, SPEC-016 EM-064), counted per tenant; <c>0</c> — the limit
    /// is off, which is what the product ships. A key of its own beside the limit of the mails of the claims
    /// completion to one domain, with its semantics: the stricter is the smaller POSITIVE value and <c>0</c>
    /// yields to any positive one, so an application can switch the limit on or lower it, never switch off the
    /// limit its tenant set.
    /// </summary>
    internal static ConfigKey<int> SignInMailsPerDomain { get; } = Declared
        .Of<int>(SignInMailsPerDomainKeyName)
        .At(ConfigLevel.Core, EmailOptions.SectionName + ":" + SignInMailsPerDomainMember)
        .At(ConfigLevel.Tenant, RecordSection + ":" + SignInMailsPerDomainMember)
        .At(ConfigLevel.Application, RecordSection + ":" + SignInMailsPerDomainMember)
        .ProtectiveCeiling(StricterSwitchableLimit)
        .Domain(SignInMailsPerDomainDomain)
        .Default(EmailAdapterConfigDefaults.DefaultSignInMailsPerDomain)
        .Declare();

    /// <summary>
    /// Declarations of this channel — what <c>AddEmail()</c> registers into the container, and what
    /// the schema of a deployment carries because of it.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Whether a set of sign-in modes is one the setting admits on any level: every element a member of
    /// the enumeration. The empty set is admitted (see <see cref="SignInModesDomain"/>).
    /// </summary>
    /// <param name="modes">Set stated by a level.</param>
    /// <returns><c>true</c> when the setting admits the set.</returns>
    internal static bool IsWellFormedModeSet(IReadOnlyList<EmailMode> modes) =>
        modes.All(static mode => Enum.IsDefined(mode));

    /// <summary>
    /// Whether a set of what the sign-in mail carries is one the setting admits on any level: at least one
    /// element, every element a member of the enumeration.
    /// </summary>
    /// <param name="methods">Set stated by a level.</param>
    /// <returns><c>true</c> when the setting admits the set.</returns>
    internal static bool IsWellFormedPullMethods(IReadOnlyList<EmailPullMethod> methods) =>
        methods.Count > 0 && methods.All(static method => Enum.IsDefined(method));

    /// <summary>
    /// The stricter of two limits a zero switches off: the smaller positive one; a zero yields to any positive
    /// value.
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
    /// Narrowing of two sets of sign-in modes: the modes of the upper set the lower one names; when the
    /// lower set names none of them, the upper set unchanged — the lower level is skipped.
    /// </summary>
    /// <param name="upper">Set of the level higher in ownership (the core ceiling first).</param>
    /// <param name="lower">Set of the level lower in ownership.</param>
    /// <returns>The narrowed set, or the upper one when the narrowing would leave nothing.</returns>
    private static IReadOnlyList<EmailMode> NarrowOrKeepUpper(
        IReadOnlyList<EmailMode> upper,
        IReadOnlyList<EmailMode> lower)
    {
        IReadOnlyList<EmailMode> narrowed = [.. upper.Where(mode => lower.Contains(mode)).Distinct()];

        return narrowed.Count > 0 ? narrowed : upper;
    }
}
