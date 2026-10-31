// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Constants;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Http.Configuration;

/// <summary>
/// Keys of an HTTP channel registration (SPEC-042 C30) — the subsection <c>Http</c> of a channel
/// section <c>Veriqa:Channels:{channel}</c>. Every key is declared ONCE, with the channel as its
/// identity dimension, by the example of <see cref="ChannelInboundVerificationConfigKeys"/>: the
/// channels are named by the operator in configuration, so there is no closed list of them to
/// declare a key per channel for.
/// <para>
/// Reading and using the values are two different acts. The registration is read once at start by
/// <see cref="HttpChannelRegistrationReader"/>, and every value except
/// <see cref="AdapterSettings"/> stays fixed until the host restarts (SPEC-042 R5): the declarations
/// below put the keys into the schema of the deployment and give the core level its address.
/// <see cref="AdapterSettings"/> is the one key resolved per call, for the tenant of the call.
/// </para>
/// <para>
/// <c>Enabled</c> and <c>InboundVerification</c> belong to the section of any channel and are not
/// declared here (SPEC-042 C30): the first is read by the registration the way every channel reads
/// it, the second is declared by the core and checked by the start-up pass over all channels.
/// </para>
/// </summary>
internal static class HttpChannelConfigKeys
{
    /// <summary>
    /// Name of the subsection that makes a channel section an HTTP channel registration.
    /// </summary>
    public const string HttpSectionName = "Http";

    /// <summary>
    /// Member of any channel section carrying its core-level <c>Enabled</c> flag (SPEC-003 §17.7).
    /// </summary>
    public const string EnabledMember = "Enabled";

    /// <summary>
    /// Member: absolute URL of the single entry of the external adapter.
    /// </summary>
    public const string BaseUrlMember = "BaseUrl";

    /// <summary>
    /// Member: the tenant that owns the registration.
    /// </summary>
    public const string TenantMember = "Tenant";

    /// <summary>
    /// Member: whether a base URL with the <c>http</c> scheme is admitted.
    /// </summary>
    public const string AllowInsecureHttpMember = "AllowInsecureHttp";

    /// <summary>
    /// Member: current transport secret.
    /// </summary>
    public const string TransportSecretMember = "TransportSecret";

    /// <summary>
    /// Member: previous transport secret, stated while a rotation is in progress.
    /// </summary>
    public const string PreviousTransportSecretMember = "PreviousTransportSecret";

    /// <summary>
    /// Member: deep-link schemes admitted beyond <c>https</c>.
    /// </summary>
    public const string DeepLinkSchemesMember = "DeepLinkSchemes";

    /// <summary>
    /// Member: opaque settings string forwarded to the external adapter.
    /// </summary>
    public const string AdapterSettingsMember = "AdapterSettings";

    /// <summary>
    /// Subsection carrying the channel facts, one member per property of <see cref="ChannelCapabilities"/>.
    /// </summary>
    public const string CapabilitiesSectionName = "Capabilities";

    /// <summary>
    /// Member: name of the channel on the sign-in button.
    /// </summary>
    public const string DisplayNameMember = "DisplayName";

    /// <summary>
    /// Member: inner markup of the channel glyph.
    /// </summary>
    public const string IconSvgPathMember = "IconSvgPath";

    /// <summary>
    /// Member: Natural Key of the status text of a processing error.
    /// </summary>
    public const string ErrorMessageTextMember = "ErrorMessageText";

    /// <summary>
    /// Member: Natural Key of the reply to a link that can no longer be served.
    /// </summary>
    public const string StaleLinkReplyTextMember = "StaleLinkReplyText";

    /// <summary>
    /// Member: Natural Key of the reply to a start by a code no live transaction answers to.
    /// </summary>
    public const string UnmatchedCodeReplyTextMember = "UnmatchedCodeReplyText";

    /// <summary>
    /// Member: Natural Key of the reply to a message that names no transaction.
    /// </summary>
    public const string UnaddressedReplyTextMember = "UnaddressedReplyText";

    /// <summary>
    /// Member: timeout of one attempt of an outbound call.
    /// </summary>
    public const string OperationTimeoutMember = "OperationTimeout";

    /// <summary>
    /// Member: timeout of the deep-link call.
    /// </summary>
    public const string DeepLinkTimeoutMember = "DeepLinkTimeout";

    /// <summary>
    /// Member: budget of all attempts of one outbound call.
    /// </summary>
    public const string RetryBudgetMember = "RetryBudget";

    /// <summary>
    /// Channel facts of a registration that states none (SPEC-042 R8): the safe defaults of
    /// <see cref="ChannelCapabilities"/>, with plain text as the only content kind.
    /// </summary>
    public static readonly ChannelCapabilities UnstatedCapabilities = new()
    {
        SupportedMessageKinds = new[] { ChannelMessageKind.PlainText }.ToFrozenSet()
    };

    /// <summary>
    /// Registration settings a registration that states no text of its own takes: the defaults of
    /// <see cref="ChannelRegistrationOptions"/> (SPEC-042 C30).
    /// </summary>
    public static readonly ChannelRegistrationOptions UnstatedRegistrationOptions = new();

    /// <summary>
    /// Prefix of the canonical names of the keys.
    /// </summary>
    private const string KeyNamePrefix = "Channels." + HttpSectionName + ".";

    /// <summary>
    /// Separator of the segments of a canonical key name.
    /// </summary>
    private const char KeyNameSeparator = '.';

    /// <summary>
    /// Core-level address of the <c>Http</c> subsection with the channel dimension in it.
    /// </summary>
    private const string CoreHttpSectionTemplate =
        ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName
        + ":{" + ConfigDimensionNames.Channel + "}:"
        + HttpSectionName + ":";

    /// <summary>
    /// Catalog of the declarations of the HTTP channels — what <c>AddHttpChannels()</c> registers. It is
    /// initialized before the declarations that fill it, which is the order the initializers of this
    /// class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// <c>Http:BaseUrl</c> — absolute URL of the external adapter. Core level only: a value written for
    /// a tenant is not read (SPEC-042 C30 footnote 1, E53). Obligatory for every enabled registration;
    /// the obligation is enforced by the registration reader, which is the one place that knows which
    /// channels are HTTP registrations.
    /// </summary>
    public static ConfigKey<string?> BaseUrl { get; } = Core<string?>(BaseUrlMember).Declare();

    /// <summary>
    /// <c>Http:Tenant</c> — the tenant that owns the registration (SPEC-042 R60): it alone is served by
    /// the registration — its route, its transactions, its entries into the channel and its outbound
    /// calls. Core level only, so a tenant cannot reassign it; unstated — the registration of the
    /// operator, serving every tenant and the implicit one. A value stated blank is refused by the
    /// registration reader (SPEC-042 E55).
    /// </summary>
    public static ConfigKey<string?> Tenant { get; } = Core<string?>(TenantMember).Declare();

    /// <summary>
    /// <c>Http:AllowInsecureHttp</c> — admits a base URL with the <c>http</c> scheme (SPEC-042 E55, N56).
    /// </summary>
    public static ConfigKey<bool> AllowInsecureHttp { get; } = Core<bool>(AllowInsecureHttpMember)
        .Default(false)
        .Declare();

    /// <summary>
    /// <c>Http:TransportSecret</c> — current transport secret in the <c>whsec_</c> serialization, core
    /// level only (SPEC-042 C30, C31).
    /// </summary>
    public static ConfigKey<string?> TransportSecret { get; } = Core<string?>(TransportSecretMember)
        .Secret()
        .Declare();

    /// <summary>
    /// <c>Http:PreviousTransportSecret</c> — previous transport secret, accepted and signed with while a
    /// rotation is in progress, core level only (SPEC-042 C30, C31).
    /// </summary>
    public static ConfigKey<string?> PreviousTransportSecret { get; } = Core<string?>(PreviousTransportSecretMember)
        .Secret()
        .Declare();

    /// <summary>
    /// <c>Http:DeepLinkSchemes</c> — deep-link schemes the registration may return beyond <c>https</c>
    /// (SPEC-042 C30, L45), a configuration array by index. None by default.
    /// </summary>
    public static ConfigKey<IReadOnlyList<string>> DeepLinkSchemes { get; } =
        Core<IReadOnlyList<string>>(DeepLinkSchemesMember)
            .Default(Array.Empty<string>())
            .Declare();

    /// <summary>
    /// <c>Http:AdapterSettings</c> — opaque string forwarded verbatim in every outbound call
    /// (SPEC-042 R12). The one key of the subsection with a tenant level: the value of a tenant
    /// replaces the value of the core whole. A secret, and bounded in length by the domain of the key
    /// on every level; the core level is also checked at start by the registration reader.
    /// </summary>
    public static ConfigKey<string?> AdapterSettings { get; } = Core<string?>(AdapterSettingsMember)

        // The tenant level is addressed by the identity of the key alone, like every tenant-level key
        // of a channel: its records are rows kept by the cloud contour, with no document to walk into.
        .AtIdentity(ConfigLevel.Tenant)
        .Secret()
        .Domain(new ConfigValueDomain<string?>(
            static settings => settings is null || settings.Length <= HttpChannelConstants.MaxAdapterSettingsLength,
            $"no longer than {HttpChannelConstants.MaxAdapterSettingsLength} characters",
            CoreHttpSectionTemplate + AdapterSettingsMember + " of the core level, or the record of the tenant stating it"))
        .Declare();

    /// <summary>
    /// <c>Http:Capabilities:SupportsInChannelConfirmation</c> (SPEC-042 R8).
    /// </summary>
    public static ConfigKey<bool> SupportsInChannelConfirmation { get; } =
        Capability<bool>(nameof(ChannelCapabilities.SupportsInChannelConfirmation))
            .Default(UnstatedCapabilities.SupportsInChannelConfirmation)
            .Declare();

    /// <summary>
    /// <c>Http:Capabilities:SupportedMessageKinds</c> — tokens of <see cref="ChannelMessageKinds"/>, a
    /// configuration array by index; plain text only by default (SPEC-042 R8, C30).
    /// </summary>
    public static ConfigKey<IReadOnlyList<string>> SupportedMessageKinds { get; } =
        Capability<IReadOnlyList<string>>(nameof(ChannelCapabilities.SupportedMessageKinds))
            .Default(new[] { ChannelMessageKinds.PlainText })
            .Declare();

    /// <summary>
    /// <c>Http:Capabilities:SupportsMessageUpdate</c> (SPEC-042 R8).
    /// </summary>
    public static ConfigKey<bool> SupportsMessageUpdate { get; } =
        Capability<bool>(nameof(ChannelCapabilities.SupportsMessageUpdate))
            .Default(UnstatedCapabilities.SupportsMessageUpdate)
            .Declare();

    /// <summary>
    /// <c>Http:Capabilities:DeliversOutcomeNotice</c> (SPEC-042 R8).
    /// </summary>
    public static ConfigKey<bool> DeliversOutcomeNotice { get; } =
        Capability<bool>(nameof(ChannelCapabilities.DeliversOutcomeNotice))
            .Default(UnstatedCapabilities.DeliversOutcomeNotice)
            .Declare();

    /// <summary>
    /// <c>Http:Capabilities:ProvidesRecipientLocale</c> (SPEC-042 R8).
    /// </summary>
    public static ConfigKey<bool> ProvidesRecipientLocale { get; } =
        Capability<bool>(nameof(ChannelCapabilities.ProvidesRecipientLocale))
            .Default(UnstatedCapabilities.ProvidesRecipientLocale)
            .Declare();

    /// <summary>
    /// <c>Http:Capabilities:RendersEmoji</c> — three-state like the fact itself: unstated means the
    /// channel states no rule of its own (SPEC-042 R8).
    /// </summary>
    public static ConfigKey<bool?> RendersEmoji { get; } =
        Capability<bool?>(nameof(ChannelCapabilities.RendersEmoji)).Declare();

    /// <summary>
    /// <c>Http:Capabilities:PhoneNumber:Acquisition</c> — the ways the channel obtains the phone number
    /// of the user: names of the members of <see cref="PhoneNumberAcquisition"/> other than
    /// <see cref="PhoneNumberAcquisition.None"/>, a configuration array by index whose entries are
    /// united. None by default: the channel gives no number (SPEC-042 R8, C30).
    /// </summary>
    public static ConfigKey<IReadOnlyList<string>> PhoneAcquisition { get; } =
        Capability<IReadOnlyList<string>>(PhoneNumberFact(nameof(PhoneNumberCapability.Acquisition)))
            .Default(Array.Empty<string>())
            .Declare();

    /// <summary>
    /// <c>Http:Capabilities:PhoneNumber:Proof</c> — whether a number obtained through the channel is
    /// proven to belong to the user: the ceiling of what the registration may state about a number
    /// (SPEC-042 R8, C30, C39). Not proven by default.
    /// </summary>
    public static ConfigKey<PhoneNumberProof> PhoneProof { get; } =
        Capability<PhoneNumberProof>(PhoneNumberFact(nameof(PhoneNumberCapability.Proof)))
            .Default(UnstatedCapabilities.PhoneNumber.Proof)
            .Declare();

    /// <summary>
    /// <c>Http:Capabilities:CodeStart:LinkCarriesTransaction</c> — stated, it declares the start by a
    /// short code (SPEC-003 §9.4) and is the fact of the link of the declaration: <c>false</c> — the
    /// deep link only opens the chat and the code is the only way to start, <c>true</c> — the link
    /// carries the transaction and the code is a fallback start beside it. Unstated — the registration
    /// declares no start by a code (SPEC-042 R8, C30).
    /// </summary>
    public static ConfigKey<bool?> CodeStartLinkCarriesTransaction { get; } =
        Capability<bool?>(CodeStartFact(nameof(ChannelCodeStart.LinkCarriesTransaction))).Declare();

    /// <summary>
    /// <c>Http:Capabilities:CodeStart:MessageTemplate</c> — the message the user sends to the bot, with
    /// the placeholder of the code in it; unstated — the user sends the code alone. Read only with the
    /// declaration of the start by a code (SPEC-042 R8, C30).
    /// </summary>
    public static ConfigKey<string?> CodeStartMessageTemplate { get; } =
        Capability<string?>(CodeStartFact(nameof(ChannelCodeStart.MessageTemplate))).Declare();

    /// <summary>
    /// <c>Http:Capabilities:SupportedCompletionBehaviorActions</c> — the actions after a sign-in the
    /// channel renders: names of the members of <see cref="CompletionBehaviorAction"/>, a configuration
    /// array by index whose entries are united, with <see cref="CompletionBehaviorAction.None"/> always
    /// among them. <see cref="CompletionBehaviorAction.None"/> alone by default (SPEC-042 R8, C30).
    /// </summary>
    public static ConfigKey<IReadOnlyList<string>> SupportedCompletionBehaviorActions { get; } =
        Capability<IReadOnlyList<string>>(nameof(ChannelCapabilities.SupportedCompletionBehaviorActions))
            .Default(new[] { nameof(CompletionBehaviorAction.None) })
            .Declare();

    /// <summary>
    /// <c>Http:Capabilities:DefaultCompletionBehaviorAction</c> — the action applied in place of one the
    /// channel does not render; one of <see cref="SupportedCompletionBehaviorActions"/>.
    /// <see cref="CompletionBehaviorAction.None"/> by default (SPEC-042 R8, C30).
    /// </summary>
    public static ConfigKey<CompletionBehaviorAction> DefaultCompletionBehaviorAction { get; } =
        Capability<CompletionBehaviorAction>(nameof(ChannelCapabilities.DefaultCompletionBehaviorAction))
            .Default(UnstatedCapabilities.DefaultCompletionBehaviorAction)
            .Declare();

    /// <summary>
    /// <c>Http:DisplayName</c> — name of the channel on the sign-in button (SPEC-042 R8).
    /// </summary>
    public static ConfigKey<string?> DisplayName { get; } = Core<string?>(DisplayNameMember).Declare();

    /// <summary>
    /// <c>Http:IconSvgPath</c> — inner markup of the channel glyph; it passes the allowlist of the
    /// sign-in window like the glyph of any channel (SPEC-042 R8).
    /// </summary>
    public static ConfigKey<string?> IconSvgPath { get; } = Core<string?>(IconSvgPathMember).Declare();

    /// <summary>
    /// <c>Http:ErrorMessageText</c> — Natural Key of the status text of a processing error
    /// (SPEC-042 R6, C30).
    /// </summary>
    public static ConfigKey<string?> ErrorMessageText { get; } = Core<string?>(ErrorMessageTextMember)
        .Default(UnstatedRegistrationOptions.ErrorMessageText)
        .Declare();

    /// <summary>
    /// <c>Http:StaleLinkReplyText</c> — Natural Key of the reply to a link that can no longer be served;
    /// unstated — no reply (SPEC-042 R6, C30).
    /// </summary>
    public static ConfigKey<string?> StaleLinkReplyText { get; } = Core<string?>(StaleLinkReplyTextMember).Declare();

    /// <summary>
    /// <c>Http:UnmatchedCodeReplyText</c> — Natural Key of the reply to a start by a code no live
    /// transaction answers to; unstated — the reply to a link that can no longer be served stands in for
    /// it (SPEC-003 CA-227, SPEC-042 C30). Not used by a registration that declares no start by a code.
    /// </summary>
    public static ConfigKey<string?> UnmatchedCodeReplyText { get; } = Core<string?>(UnmatchedCodeReplyTextMember).Declare();

    /// <summary>
    /// <c>Http:UnaddressedReplyText</c> — Natural Key of the reply to a message that names no
    /// transaction; unstated — no reply (SPEC-042 R6, C30).
    /// </summary>
    public static ConfigKey<string?> UnaddressedReplyText { get; } = Core<string?>(UnaddressedReplyTextMember).Declare();

    /// <summary>
    /// <c>Http:OperationTimeout</c> — timeout of one attempt of an outbound call (SPEC-042 L44).
    /// </summary>
    public static ConfigKey<TimeSpan> OperationTimeout { get; } = Core<TimeSpan>(OperationTimeoutMember)
        .Default(TimeSpan.FromSeconds(HttpChannelConstants.DefaultOperationTimeoutSeconds))
        .Declare();

    /// <summary>
    /// <c>Http:DeepLinkTimeout</c> — timeout of the deep-link call (SPEC-042 L44).
    /// </summary>
    public static ConfigKey<TimeSpan> DeepLinkTimeout { get; } = Core<TimeSpan>(DeepLinkTimeoutMember)
        .Default(TimeSpan.FromSeconds(HttpChannelConstants.DefaultDeepLinkTimeoutSeconds))
        .Declare();

    /// <summary>
    /// <c>Http:RetryBudget</c> — budget of all attempts of one outbound call with the pauses between
    /// them (SPEC-042 L44).
    /// </summary>
    public static ConfigKey<TimeSpan> RetryBudget { get; } = Core<TimeSpan>(RetryBudgetMember)
        .Default(TimeSpan.FromSeconds(HttpChannelConstants.DefaultRetryBudgetSeconds))
        .Declare();

    /// <summary>
    /// Declarations of the HTTP channels — what <c>AddHttpChannels()</c> registers into the container,
    /// and what the schema of a deployment carries because of it.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Opens the declaration of a key of the <c>Http</c> subsection addressed at the core level only,
    /// with the channel as its identity dimension. The canonical name follows the address: the
    /// separators of the relative path become the separators of the name.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="relativePath">Address inside the <c>Http</c> subsection.</param>
    /// <returns>Builder of the declaration.</returns>
    private static ConfigKeyBuilder<T> Core<T>(string relativePath) => Declared
        .Of<T>(KeyNamePrefix + relativePath.Replace(ConfigNode.PathSeparator, KeyNameSeparator))
        .Identity(ConfigDimensionNames.Channel)
        .At(ConfigLevel.Core, CoreHttpSectionTemplate + relativePath);

    /// <summary>
    /// Opens the declaration of one channel fact of the <c>Capabilities</c> subsection.
    /// </summary>
    /// <typeparam name="T">Type of the fact.</typeparam>
    /// <param name="property">Name of the property of <see cref="ChannelCapabilities"/>.</param>
    /// <returns>Builder of the declaration.</returns>
    private static ConfigKeyBuilder<T> Capability<T>(string property) =>
        Core<T>(CapabilitiesSectionName + ConfigNode.PathSeparator + property);

    /// <summary>
    /// Address of one member of the phone-number fact inside the <c>Capabilities</c> subsection.
    /// </summary>
    /// <param name="member">Name of the property of <see cref="PhoneNumberCapability"/>.</param>
    /// <returns>The address relative to the <c>Capabilities</c> subsection.</returns>
    private static string PhoneNumberFact(string member) =>
        nameof(ChannelCapabilities.PhoneNumber) + ConfigNode.PathSeparator + member;

    /// <summary>
    /// Address of one member of the declaration of the start by a code inside the <c>Capabilities</c>
    /// subsection.
    /// </summary>
    /// <param name="member">Name of the property of <see cref="ChannelCodeStart"/>.</param>
    /// <returns>The address relative to the <c>Capabilities</c> subsection.</returns>
    private static string CodeStartFact(string member) =>
        nameof(ChannelCapabilities.CodeStart) + ConfigNode.PathSeparator + member;
}
