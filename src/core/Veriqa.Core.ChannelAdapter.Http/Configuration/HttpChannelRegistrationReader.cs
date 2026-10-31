// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Constants;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Http.Configuration;

/// <summary>
/// The single place the HTTP channel sections of the configuration are read and checked
/// (SPEC-042 C30, E53, E55). It runs once, at the composition of the host: every violation throws, so
/// a host whose HTTP channels are misconfigured stops before it accepts the first request, and the
/// message names the full path of the offending key — or, for a wrong name, the channel.
/// <para>
/// A disabled registration is checked by its name only — format and reserved names; the rest of its
/// section is not read, the way the section of a disabled built-in channel is not validated either.
/// A name repeated among the HTTP sections themselves cannot occur: configuration keys are
/// case-insensitive, so two sections spelled the same are one section, and the name format admits
/// lowercase only. A name colliding with a channel registered by code is caught where the channels
/// are registered (<c>CustomChannelRegistry</c>) and, for a disabled registration or a channel that
/// declares the name without an adapter, by <see cref="HttpChannelStartupCheck"/>.
/// </para>
/// </summary>
internal static partial class HttpChannelRegistrationReader
{
    /// <summary>
    /// Names of the channels shipped with the product, reserved for them (SPEC-042 R7). The set is
    /// derived from the public constants of <see cref="ChannelTypes"/> rather than listed here, so a
    /// channel added to that registry is reserved from the version it ships in without a change here.
    /// </summary>
    private static readonly FrozenSet<string> ReservedChannelTypes = typeof(ChannelTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(static field => (string)field.GetRawConstantValue()!)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Actions after a sign-in a registration may declare (SPEC-042 R8), each with the content kind its
    /// message is sent as — the welcome as plain text, the menu and the redirect as a text with buttons;
    /// <c>null</c> — the action sends no message. Listed rather than taken from
    /// <see cref="CompletionBehaviorAction"/> whole: each needs a message the wire carries, so an action
    /// added to the enumeration is not declarable over HTTP until the wire carries its message. One table
    /// holds both facts, so an action cannot be declarable without its message kind being checked.
    /// </summary>
    private static readonly FrozenDictionary<CompletionBehaviorAction, ChannelMessageKind?> DeclarableCompletionBehaviorActions =
        new Dictionary<CompletionBehaviorAction, ChannelMessageKind?>
        {
            [CompletionBehaviorAction.None] = null,
            [CompletionBehaviorAction.WelcomeMessage] = ChannelMessageKind.PlainText,
            [CompletionBehaviorAction.Menu] = ChannelMessageKind.ActionButtons,
            [CompletionBehaviorAction.Redirect] = ChannelMessageKind.ActionButtons
        }.ToFrozenDictionary();

    /// <summary>
    /// Reads every HTTP channel section of the configuration and checks it. A channel section is an
    /// HTTP registration when it carries the <c>Http</c> subsection; every other section is left alone.
    /// </summary>
    /// <param name="configuration">Application configuration of the host.</param>
    /// <returns>The names of all HTTP registrations and the model of the enabled ones.</returns>
    /// <exception cref="InvalidOperationException">A section violates SPEC-042 E53/E55.</exception>
    public static HttpChannelRegistrations Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var channelTypes = new List<string>();
        var enabled = new List<HttpChannelRegistration>();
        var secrets = new List<DecodedSecret>();

        foreach (var channel in ChannelsSection(configuration).GetChildren())
        {
            var http = channel.GetSection(HttpChannelConfigKeys.HttpSectionName);

            if (!http.Exists())
            {
                continue;
            }

            var channelType = channel.Key;

            CheckName(channelType, channel.Path);
            channelTypes.Add(channelType);

            if (!ReadEnabled(channel))
            {
                continue;
            }

            enabled.Add(ReadRegistration(channelType, http, secrets));
        }

        return new HttpChannelRegistrations(channelTypes.AsReadOnly(), enabled.AsReadOnly());
    }

    /// <summary>
    /// Reads the core-level <c>Enabled</c> flag of a channel as it stands NOW — the live half of the
    /// declaration of the channel, followed on every read the way the flag of a built-in channel is.
    /// A flag that stopped being a boolean after the start answers "disabled": a channel whose state
    /// cannot be read is not offered.
    /// </summary>
    /// <param name="configuration">Application configuration of the host.</param>
    /// <param name="channelType">Channel type of the registration.</param>
    /// <returns>Whether the channel is enabled at the core level.</returns>
    public static bool IsEnabled(IConfiguration configuration, string channelType)
    {
        var value = ChannelsSection(configuration)
            .GetSection(channelType)[HttpChannelConfigKeys.EnabledMember];

        return TryParseEnabled(value, out var isEnabled) && isEnabled;
    }

    /// <summary>
    /// Section <c>Veriqa:Channels</c> the channel sections live under.
    /// </summary>
    /// <param name="configuration">Application configuration of the host.</param>
    /// <returns>The section.</returns>
    private static IConfigurationSection ChannelsSection(IConfiguration configuration) =>
        configuration.GetSection(ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName);

    /// <summary>
    /// Checks the name of an HTTP registration: the channel-type format and the reserved names
    /// (SPEC-042 R7, E55).
    /// </summary>
    /// <param name="channelType">Name of the channel section.</param>
    /// <param name="sectionPath">Full path of the channel section.</param>
    /// <exception cref="InvalidOperationException">The name is malformed or reserved.</exception>
    private static void CheckName(string channelType, string sectionPath)
    {
        if (!ChannelTypeRegex().IsMatch(channelType))
        {
            throw new InvalidOperationException(
                $"Invalid HTTP channel name '{channelType}' (section '{sectionPath}'): a channel type must match "
                + $"{CustomChannelConstants.ChannelTypePattern} — a lowercase URL-safe token starting with a latin "
                + "letter, up to 64 characters (letters, digits and '-'). Rename the section.");
        }

        if (ReservedChannelTypes.Contains(channelType))
        {
            throw new InvalidOperationException(
                $"HTTP channel name '{channelType}' (section '{sectionPath}') is reserved: the names of the channels "
                + $"shipped with the product ({string.Join(", ", ReservedChannelTypes.Order(StringComparer.Ordinal))}) "
                + "cannot be registered as HTTP channels, whether or not the shipped channel is added to this host. "
                + "Rename the section.");
        }
    }

    /// <summary>
    /// Reads the core-level <c>Enabled</c> flag of a channel at start; unstated means enabled
    /// (SPEC-042 C30).
    /// </summary>
    /// <param name="channel">Channel section.</param>
    /// <returns>Whether the registration is enabled.</returns>
    /// <exception cref="InvalidOperationException">The flag is not a boolean.</exception>
    private static bool ReadEnabled(IConfigurationSection channel)
    {
        var (value, path) = ReadValue(channel, HttpChannelConfigKeys.EnabledMember);

        if (!TryParseEnabled(value, out var isEnabled))
        {
            throw Invalid(path, "is not a boolean (true or false)");
        }

        return isEnabled;
    }

    /// <summary>
    /// Parses a stated <c>Enabled</c> flag; a blank or absent value states nothing and means enabled.
    /// </summary>
    /// <param name="value">Stated value.</param>
    /// <param name="isEnabled">Parsed flag.</param>
    /// <returns>Whether the value could be read.</returns>
    private static bool TryParseEnabled(string? value, out bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            isEnabled = true;
            return true;
        }

        return bool.TryParse(value, out isEnabled);
    }

    /// <summary>
    /// Reads and checks the <c>Http</c> subsection of one enabled registration.
    /// </summary>
    /// <param name="channelType">Channel type of the registration.</param>
    /// <param name="http">The <c>Http</c> subsection.</param>
    /// <param name="secrets">Secrets of the registrations read so far, extended with this one's.</param>
    /// <returns>The model of the registration.</returns>
    /// <exception cref="InvalidOperationException">The subsection violates SPEC-042 E53/E55.</exception>
    private static HttpChannelRegistration ReadRegistration(
        string channelType,
        IConfigurationSection http,
        List<DecodedSecret> secrets)
    {
        var baseUrl = ReadBaseUrl(http);
        var transportSecrets = ReadTransportSecrets(channelType, http, secrets);
        var deepLinkSchemes = ReadDeepLinkSchemes(http);

        CheckCoreAdapterSettings(http);

        var unstatedOptions = HttpChannelConfigKeys.UnstatedRegistrationOptions;
        var registrationOptions = unstatedOptions with
        {
            ErrorMessageText = ReadText(http, HttpChannelConfigKeys.ErrorMessageTextMember)
                ?? unstatedOptions.ErrorMessageText,
            StaleLinkReplyText = ReadText(http, HttpChannelConfigKeys.StaleLinkReplyTextMember)
                ?? unstatedOptions.StaleLinkReplyText,
            UnaddressedReplyText = ReadText(http, HttpChannelConfigKeys.UnaddressedReplyTextMember)
                ?? unstatedOptions.UnaddressedReplyText,

            // Stated without the declaration of the start by a code, the text is simply never asked for.
            UnmatchedCodeReplyText = ReadText(http, HttpChannelConfigKeys.UnmatchedCodeReplyTextMember)
                ?? unstatedOptions.UnmatchedCodeReplyText,

            // The platform's verification handshake reaches the external adapter, never the core, so
            // no GET route is mapped for an HTTP channel (SPEC-042 R4).
            WebhookVerificationQueryKey = null
        };

        var tenant = ReadTenant(http);
        var capabilities = ReadCapabilities(http.GetSection(HttpChannelConfigKeys.CapabilitiesSectionName));

        CheckCodeStartOwner(http, tenant, capabilities);

        return new HttpChannelRegistration
        {
            ChannelType = channelType,
            Tenant = tenant,
            BaseUrl = baseUrl,
            TransportSecrets = transportSecrets,
            DeepLinkSchemes = deepLinkSchemes,
            Capabilities = capabilities,
            DisplayName = ReadText(http, HttpChannelConfigKeys.DisplayNameMember),
            IconSvgPath = ReadText(http, HttpChannelConfigKeys.IconSvgPathMember),
            RegistrationOptions = registrationOptions,
            OperationTimeout = ReadTimeout(
                http,
                HttpChannelConfigKeys.OperationTimeoutMember,
                TimeSpan.FromSeconds(HttpChannelConstants.DefaultOperationTimeoutSeconds)),
            DeepLinkTimeout = ReadTimeout(
                http,
                HttpChannelConfigKeys.DeepLinkTimeoutMember,
                TimeSpan.FromSeconds(HttpChannelConstants.DefaultDeepLinkTimeoutSeconds)),
            RetryBudget = ReadTimeout(
                http,
                HttpChannelConfigKeys.RetryBudgetMember,
                TimeSpan.FromSeconds(HttpChannelConstants.DefaultRetryBudgetSeconds))
        };
    }

    /// <summary>
    /// Reads the tenant that owns the registration (SPEC-042 R60). A key that is not stated leaves the
    /// registration to the operator; a key stated with an empty or blank value is refused (SPEC-042
    /// E55) rather than read as unstated: the operator meant to name an owner, and reading the slip as
    /// "no owner" would open the registration to every tenant. The value is taken as it is — no
    /// trimming, no case folding — because the tenant of a route and of a client is not normalized
    /// either, and the comparisons with it are ordinal.
    /// </summary>
    /// <param name="http">The <c>Http</c> subsection.</param>
    /// <returns>The owner, or <c>null</c> when the key is not stated.</returns>
    /// <exception cref="InvalidOperationException">The key is stated blank, or is a section.</exception>
    private static string? ReadTenant(IConfigurationSection http)
    {
        var (tenant, path) = ReadValue(http, HttpChannelConfigKeys.TenantMember);

        if (tenant is not null || http.GetSection(HttpChannelConfigKeys.TenantMember).Value is null)
        {
            return tenant;
        }

        throw Invalid(
            path,
            "is empty: name the tenant that owns the registration, or remove the key to leave the registration "
            + "to the operator");
    }

    /// <summary>
    /// Refuses the start by a code on a registration that has an owner (SPEC-042 E55). A code is taken
    /// from the store of the whole deployment before the start path compares the tenant of its
    /// transaction with the owner, so the adapter of one tenant could spend the live codes of another;
    /// a registration of the operator compares nothing and is not exposed to that. This is the one place
    /// that ties the owner to the start by a code — the wire, the pipeline and the conformance suite do
    /// not depend on it.
    /// </summary>
    /// <param name="http">The <c>Http</c> subsection.</param>
    /// <param name="tenant">The owner of the registration; <c>null</c> — none.</param>
    /// <param name="capabilities">The declared facts of the registration.</param>
    /// <exception cref="InvalidOperationException">The registration has an owner and declares the start by a code.</exception>
    private static void CheckCodeStartOwner(IConfigurationSection http, string? tenant, ChannelCapabilities capabilities)
    {
        if (tenant is null || capabilities.CodeStart is null)
        {
            return;
        }

        var declarationPath = ConfigurationPath.Combine(
            http.Path,
            HttpChannelConfigKeys.CapabilitiesSectionName,
            nameof(ChannelCapabilities.CodeStart),
            nameof(ChannelCodeStart.LinkCarriesTransaction));

        throw Invalid(
            ConfigurationPath.Combine(http.Path, HttpChannelConfigKeys.TenantMember),
            $"names the tenant that owns the registration while '{declarationPath}' declares the start by a code: "
            + "in this version the start by a code is available only to a registration without an owner. Remove "
            + "one of the two keys");
    }

    /// <summary>
    /// Reads the base URL: stated (SPEC-042 E53), absolute, <c>https</c> — or <c>http</c> when the
    /// operator admitted it explicitly (SPEC-042 E55, N56).
    /// </summary>
    /// <param name="http">The <c>Http</c> subsection.</param>
    /// <returns>The base URL.</returns>
    /// <exception cref="InvalidOperationException">The URL is missing, relative or of another scheme.</exception>
    private static Uri ReadBaseUrl(IConfigurationSection http)
    {
        var (value, path) = ReadValue(http, HttpChannelConfigKeys.BaseUrlMember);

        if (value is null)
        {
            throw Invalid(path, "is not set: every enabled HTTP channel names the URL of its external adapter");
        }

        // The value itself is not quoted: a URL may carry credentials in its user-info part.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var baseUrl))
        {
            throw Invalid(path, "is not an absolute URI");
        }

        if (string.Equals(baseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl;
        }

        var allowInsecurePath = ConfigurationPath.Combine(http.Path, HttpChannelConfigKeys.AllowInsecureHttpMember);

        if (!string.Equals(baseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                path,
                $"uses the '{baseUrl.Scheme}' scheme: only https is admitted, and http only with "
                + $"'{allowInsecurePath}' = true");
        }

        if (!ReadBoolean(http, HttpChannelConfigKeys.AllowInsecureHttpMember, defaultValue: false))
        {
            throw Invalid(
                path,
                "uses the http scheme, which leaves the calls to the external adapter readable and its answers "
                + $"forgeable in the network. Use https, or set '{allowInsecurePath}' = true when the network to "
                + "the adapter is isolated");
        }

        return baseUrl;
    }

    /// <summary>
    /// Reads the transport secrets: the current one stated (SPEC-042 E53), the previous one optional,
    /// each in the Standard Webhooks serialization, and none of them shared with another registration
    /// (SPEC-042 C31, E55). Neither value is ever quoted in a message.
    /// </summary>
    /// <param name="channelType">Channel type of the registration.</param>
    /// <param name="http">The <c>Http</c> subsection.</param>
    /// <param name="secrets">Secrets of the registrations read so far, extended with this one's.</param>
    /// <returns>The secrets, the current one first.</returns>
    /// <exception cref="InvalidOperationException">A secret is missing, malformed or shared.</exception>
    private static IReadOnlyList<string> ReadTransportSecrets(
        string channelType,
        IConfigurationSection http,
        List<DecodedSecret> secrets)
    {
        var (current, currentPath) = ReadValue(http, HttpChannelConfigKeys.TransportSecretMember);

        if (current is null)
        {
            throw Invalid(currentPath, "is not set: every enabled HTTP channel signs its traffic with a secret of its own");
        }

        var stated = new List<string>();

        AddSecret(channelType, current, currentPath, stated, secrets);

        var (previous, previousPath) = ReadValue(http, HttpChannelConfigKeys.PreviousTransportSecretMember);

        if (previous is not null)
        {
            AddSecret(channelType, previous, previousPath, stated, secrets);
        }

        return stated.AsReadOnly();
    }

    /// <summary>
    /// Decodes one stated secret, refuses it when another registration already holds the same key,
    /// and records it. The comparison is over the decoded bytes, so two spellings of one key collide.
    /// </summary>
    /// <param name="channelType">Channel type of the registration.</param>
    /// <param name="serialized">Secret in the <c>whsec_</c> serialization.</param>
    /// <param name="path">Full path of the key that stated it.</param>
    /// <param name="stated">Secrets of this registration.</param>
    /// <param name="secrets">Secrets of every registration read so far.</param>
    /// <exception cref="InvalidOperationException">The secret is malformed or shared.</exception>
    private static void AddSecret(
        string channelType,
        string serialized,
        string path,
        List<string> stated,
        List<DecodedSecret> secrets)
    {
        if (!HttpChannelTransportSecret.TryDecode(serialized, out var key))
        {
            throw Invalid(
                path,
                $"is not a Standard Webhooks secret: expected '{HttpChannelConstants.TransportSecretPrefix}' followed "
                + $"by the base64 of a key of {HttpChannelConstants.MinTransportSecretBytes} to "
                + $"{HttpChannelConstants.MaxTransportSecretBytes} bytes");
        }

        foreach (var other in secrets)
        {
            if (!string.Equals(other.ChannelType, channelType, StringComparison.Ordinal)
                && other.Key.AsSpan().SequenceEqual(key))
            {
                throw Invalid(
                    path,
                    $"holds the same transport secret as '{other.Path}': every HTTP channel signs with a secret of "
                    + "its own, or an event signed for one channel is accepted on the route of another. Generate a "
                    + "new secret for one of them");
            }
        }

        secrets.Add(new DecodedSecret(channelType, path, key));
        stated.Add(serialized);
    }

    /// <summary>
    /// Reads the deep-link schemes admitted beyond <c>https</c>: each one by the scheme grammar of
    /// RFC 3986 and none of the forbidden ones, without regard to case (SPEC-042 E55). <c>https</c>
    /// itself is admitted always and is not an error.
    /// </summary>
    /// <param name="http">The <c>Http</c> subsection.</param>
    /// <returns>The admitted extra schemes, compared without regard to case.</returns>
    /// <exception cref="InvalidOperationException">A scheme is malformed or forbidden.</exception>
    private static IReadOnlySet<string> ReadDeepLinkSchemes(IConfigurationSection http)
    {
        var schemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (scheme, path) in ReadList(http, HttpChannelConfigKeys.DeepLinkSchemesMember))
        {
            if (!DeepLinkSchemeRegex().IsMatch(scheme))
            {
                throw Invalid(
                    path,
                    "is not a URI scheme: RFC 3986 admits a latin letter followed by letters, digits, '+', '-' or '.'");
            }

            if (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HttpChannelConstants.ForbiddenDeepLinkSchemes.Contains(scheme)
                || scheme.StartsWith(HttpChannelConstants.ForbiddenDeepLinkSchemePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    path,
                    $"names the scheme '{scheme}', which a deep link may not use: it runs code, reads local content "
                    + "or leaves the transport unprotected in the browser of the person signing in");
            }

            schemes.Add(scheme);
        }

        return schemes.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks the length of the opaque settings string stated at the core level (SPEC-042 R12, E55).
    /// The value of a tenant is bounded by the domain of the key, where the snapshot of the tenant's
    /// configuration is checked (SPEC-012 CFG-240). The value is never quoted in a message.
    /// </summary>
    /// <param name="http">The <c>Http</c> subsection.</param>
    /// <exception cref="InvalidOperationException">The string is longer than the limit.</exception>
    private static void CheckCoreAdapterSettings(IConfigurationSection http)
    {
        var (settings, path) = ReadValue(http, HttpChannelConfigKeys.AdapterSettingsMember);

        if (settings is { Length: > HttpChannelConstants.MaxAdapterSettingsLength })
        {
            throw Invalid(path, $"is longer than {HttpChannelConstants.MaxAdapterSettingsLength} characters");
        }
    }

    /// <summary>
    /// Reads the channel facts, one key per property of <see cref="ChannelCapabilities"/> — two for the
    /// phone-number fact and two for the declaration of the start by a code, one per member of each; an
    /// unstated fact keeps its safe default, and plain text is the only content kind unless others are
    /// listed (SPEC-042 R8).
    /// </summary>
    /// <param name="capabilities">The <c>Capabilities</c> subsection.</param>
    /// <returns>The declared facts.</returns>
    /// <exception cref="InvalidOperationException">A fact is not a value of its type, or two facts disagree.</exception>
    private static ChannelCapabilities ReadCapabilities(IConfigurationSection capabilities)
    {
        var unstated = HttpChannelConfigKeys.UnstatedCapabilities;
        var kinds = ReadList(capabilities, nameof(ChannelCapabilities.SupportedMessageKinds));
        var supportedKinds = kinds.Count is 0
            ? unstated.SupportedMessageKinds
            : kinds.Select(static kind => new ChannelMessageKind(kind.Value)).ToFrozenSet();
        var (supportedActions, defaultAction) = ReadCompletionBehavior(capabilities, supportedKinds);

        return new ChannelCapabilities
        {
            SupportsInChannelConfirmation = ReadBoolean(
                capabilities,
                nameof(ChannelCapabilities.SupportsInChannelConfirmation),
                unstated.SupportsInChannelConfirmation),
            SupportedMessageKinds = supportedKinds,
            SupportsMessageUpdate = ReadBoolean(
                capabilities,
                nameof(ChannelCapabilities.SupportsMessageUpdate),
                unstated.SupportsMessageUpdate),
            DeliversOutcomeNotice = ReadBoolean(
                capabilities,
                nameof(ChannelCapabilities.DeliversOutcomeNotice),
                unstated.DeliversOutcomeNotice),
            ProvidesRecipientLocale = ReadBoolean(
                capabilities,
                nameof(ChannelCapabilities.ProvidesRecipientLocale),
                unstated.ProvidesRecipientLocale),
            RendersEmoji = ReadNullableBoolean(capabilities, nameof(ChannelCapabilities.RendersEmoji)),
            PhoneNumber = ReadPhoneNumber(
                capabilities.GetSection(nameof(ChannelCapabilities.PhoneNumber)),
                capabilities,
                supportedKinds),
            CodeStart = ReadCodeStart(capabilities.GetSection(nameof(ChannelCapabilities.CodeStart))),
            SupportedCompletionBehaviorActions = supportedActions,
            DefaultCompletionBehaviorAction = defaultAction
        };
    }

    /// <summary>
    /// Reads what the channel can do after a sign-in is completed: the actions it renders, united from
    /// the listed entries and always with <see cref="CompletionBehaviorAction.None"/> among them, and the
    /// action applied in place of one outside them (SPEC-042 R8, C30). Unstated, the channel does
    /// nothing after a sign-in. Besides a value that names no action, two disagreements are refused at
    /// start (SPEC-042 E55): a default outside the declared actions, and a declared action whose message
    /// the registration does not carry — the core drops a message of an undeclared kind before the
    /// adapter, so the action would silently do nothing.
    /// </summary>
    /// <param name="capabilities">The <c>Capabilities</c> subsection.</param>
    /// <param name="supportedKinds">Content kinds the registration declares.</param>
    /// <returns>The declared actions and the default one.</returns>
    /// <exception cref="InvalidOperationException">
    /// An entry or the default is outside the names of the actions, or the facts disagree.
    /// </exception>
    private static (FrozenSet<CompletionBehaviorAction> Supported, CompletionBehaviorAction Default) ReadCompletionBehavior(
        IConfigurationSection capabilities,
        IReadOnlySet<ChannelMessageKind> supportedKinds)
    {
        var actionsPath = ConfigurationPath.Combine(
            capabilities.Path,
            nameof(ChannelCapabilities.SupportedCompletionBehaviorActions));
        var kindsPath = ConfigurationPath.Combine(capabilities.Path, nameof(ChannelCapabilities.SupportedMessageKinds));
        var supported = ReadList(capabilities, nameof(ChannelCapabilities.SupportedCompletionBehaviorActions))
            .Select(static entry => ReadCompletionBehaviorAction(entry.Value, entry.Path))
            .Append(CompletionBehaviorAction.None)
            .ToFrozenSet();
        var (value, defaultPath) = ReadValue(capabilities, nameof(ChannelCapabilities.DefaultCompletionBehaviorAction));
        var defaultAction = value is null ? CompletionBehaviorAction.None : ReadCompletionBehaviorAction(value, defaultPath);

        if (!supported.Contains(defaultAction))
        {
            throw Invalid(
                defaultPath,
                $"names {defaultAction} while '{actionsPath}' does not list it: the default replaces an action the "
                + "channel cannot render, so it must be one the channel renders. List the action, or change the default");
        }

        foreach (var action in supported)
        {
            if (DeclarableCompletionBehaviorActions[action] is { } kind && !supportedKinds.Contains(kind))
            {
                throw Invalid(
                    actionsPath,
                    $"names {action} while '{kindsPath}' does not list '{kind.Value}': the message sent after "
                    + "the sign-in would never reach the external adapter. List the content kind, or remove the action");
            }
        }

        return (supported, defaultAction);
    }

    /// <summary>
    /// Reads one name of an action after a sign-in, without regard to case.
    /// </summary>
    /// <param name="value">Stated value.</param>
    /// <param name="path">Full path of the key.</param>
    /// <returns>The action.</returns>
    /// <exception cref="InvalidOperationException">The value is outside the names of the actions.</exception>
    private static CompletionBehaviorAction ReadCompletionBehaviorAction(string value, string path)
    {
        // The names are matched one by one rather than parsed as the enumeration: the parser would also
        // admit a number and a comma-separated list.
        foreach (var action in DeclarableCompletionBehaviorActions.Keys)
        {
            if (IsName(value, action.ToString()))
            {
                return action;
            }
        }

        throw Invalid(
            path,
            $"is not an action after a sign-in: expected one of {string.Join(", ", DeclarableCompletionBehaviorActions.Keys.Order())}");
    }

    /// <summary>
    /// Reads the declaration of the start by a code in the fullness of <see cref="ChannelCodeStart"/>
    /// (SPEC-042 R8, C30; SPEC-003 §9.4). <see cref="ChannelCodeStart.LinkCarriesTransaction"/> is the key
    /// that declares it: stated, the start by a code is declared, unstated, it is not — whatever else the
    /// section holds. A template stated without it is refused rather than dropped: the operator meant to
    /// declare the start, and reading the slip as "no start by a code" would leave the channel off every
    /// page that shows a code. So is a single value in place of the section, as for the phone-number fact.
    /// The template itself is not judged here: the core judges it where the channel is shown
    /// (SPEC-003 CA-217), as it judges the template of a built-in channel.
    /// </summary>
    /// <param name="codeStart">The <c>Capabilities:CodeStart</c> subsection.</param>
    /// <returns>The declaration; <c>null</c> — the start by a code is not declared.</returns>
    /// <exception cref="InvalidOperationException">
    /// The declaration is a single value, the fact of the link is not a boolean, or a template is stated
    /// without the fact of the link.
    /// </exception>
    private static ChannelCodeStart? ReadCodeStart(IConfigurationSection codeStart)
    {
        if (!string.IsNullOrWhiteSpace(codeStart.Value))
        {
            throw Invalid(
                codeStart.Path,
                "is a single value, not a section: declare the start by a code by its members, "
                + $"'{nameof(ChannelCodeStart.LinkCarriesTransaction)}' and '{nameof(ChannelCodeStart.MessageTemplate)}'");
        }

        var linkCarriesTransaction = ReadNullableBoolean(codeStart, nameof(ChannelCodeStart.LinkCarriesTransaction));
        var (messageTemplate, messageTemplatePath) = ReadValue(codeStart, nameof(ChannelCodeStart.MessageTemplate));

        if (linkCarriesTransaction is { } carriesTransaction)
        {
            return new ChannelCodeStart
            {
                MessageTemplate = messageTemplate,
                LinkCarriesTransaction = carriesTransaction
            };
        }

        if (messageTemplate is null)
        {
            return null;
        }

        throw Invalid(
            messageTemplatePath,
            $"is stated while '{ConfigurationPath.Combine(codeStart.Path, nameof(ChannelCodeStart.LinkCarriesTransaction))}' "
            + "is not: the start by a code is declared by that key. State it (false — the link only opens the chat, "
            + "true — the link carries the transaction and the code is a fallback), or remove the template");
    }

    /// <summary>
    /// Reads the phone-number fact in the fullness of <see cref="PhoneNumberCapability"/>: the ways of
    /// obtaining the number, united from the listed entries, and the proof (SPEC-042 R8, C30). Unstated,
    /// the fact is <see cref="PhoneNumberCapability.None"/>. A single value in place of the section is
    /// refused rather than read as unstated: the operator meant to state the fact, and reading the slip
    /// as "no number" would silently drop the channel from every sign-in that requires one. Two
    /// combinations a built-in channel keeps consistent in code are checked here, because here the
    /// operator states them by hand: a proof without a way of obtaining the number, and a number asked
    /// for on request by a registration that does not carry the request (SPEC-042 E55).
    /// </summary>
    /// <param name="phoneNumber">The <c>Capabilities:PhoneNumber</c> subsection.</param>
    /// <param name="capabilities">The <c>Capabilities</c> subsection, for the paths of the messages.</param>
    /// <param name="supportedKinds">Content kinds the registration declares.</param>
    /// <returns>The declared fact.</returns>
    /// <exception cref="InvalidOperationException">
    /// The fact is a single value, a member is outside its names, or the two disagree.
    /// </exception>
    private static PhoneNumberCapability ReadPhoneNumber(
        IConfigurationSection phoneNumber,
        IConfigurationSection capabilities,
        IReadOnlySet<ChannelMessageKind> supportedKinds)
    {
        if (!string.IsNullOrWhiteSpace(phoneNumber.Value))
        {
            throw Invalid(
                phoneNumber.Path,
                "is a single value, not a section: state the fact by its members, "
                + $"'{nameof(PhoneNumberCapability.Acquisition)}' and '{nameof(PhoneNumberCapability.Proof)}'");
        }

        var acquisition = PhoneNumberAcquisition.None;

        foreach (var (way, path) in ReadList(phoneNumber, nameof(PhoneNumberCapability.Acquisition)))
        {
            // The names are matched one by one rather than parsed as the enumeration: the parser would
            // also admit a number, a comma-separated combination and the name of "no way at all".
            if (IsName(way, nameof(PhoneNumberAcquisition.Automatic)))
            {
                acquisition |= PhoneNumberAcquisition.Automatic;
            }
            else if (IsName(way, nameof(PhoneNumberAcquisition.OnRequest)))
            {
                acquisition |= PhoneNumberAcquisition.OnRequest;
            }
            else
            {
                throw Invalid(
                    path,
                    $"is not a way of obtaining the phone number: expected {nameof(PhoneNumberAcquisition.Automatic)} "
                    + $"or {nameof(PhoneNumberAcquisition.OnRequest)}");
            }
        }

        var proof = ReadPhoneNumberProof(phoneNumber, out var proofPath);

        if (proof is PhoneNumberProof.Proven && acquisition is PhoneNumberAcquisition.None)
        {
            throw Invalid(
                proofPath,
                $"states a proven phone number while '{ConfigurationPath.Combine(phoneNumber.Path, nameof(PhoneNumberCapability.Acquisition))}' "
                + "names no way of obtaining one: a channel that gives no number proves none. State the way, or "
                + "remove the key");
        }

        if (acquisition.HasFlag(PhoneNumberAcquisition.OnRequest)
            && !supportedKinds.Contains(ChannelMessageKind.PhoneNumberRequest))
        {
            throw Invalid(
                ConfigurationPath.Combine(phoneNumber.Path, nameof(PhoneNumberCapability.Acquisition)),
                $"names {nameof(PhoneNumberAcquisition.OnRequest)} while "
                + $"'{ConfigurationPath.Combine(capabilities.Path, nameof(ChannelCapabilities.SupportedMessageKinds))}' "
                + $"does not list '{ChannelMessageKinds.PhoneNumberRequest}': the request for the number would never "
                + "reach the external adapter. List the content kind, or remove the way");
        }

        return new PhoneNumberCapability { Acquisition = acquisition, Proof = proof };
    }

    /// <summary>
    /// Reads the proof of the phone-number fact: one of the two names of <see cref="PhoneNumberProof"/>,
    /// not proven when unstated.
    /// </summary>
    /// <param name="phoneNumber">The <c>Capabilities:PhoneNumber</c> subsection.</param>
    /// <param name="path">Full path of the key.</param>
    /// <returns>The proof.</returns>
    /// <exception cref="InvalidOperationException">The value is outside the two names.</exception>
    private static PhoneNumberProof ReadPhoneNumberProof(IConfigurationSection phoneNumber, out string path)
    {
        (var value, path) = ReadValue(phoneNumber, nameof(PhoneNumberCapability.Proof));

        if (value is null || IsName(value, nameof(PhoneNumberProof.NotProven)))
        {
            return PhoneNumberProof.NotProven;
        }

        if (IsName(value, nameof(PhoneNumberProof.Proven)))
        {
            return PhoneNumberProof.Proven;
        }

        throw Invalid(
            path,
            $"is not a proof of the phone number: expected {nameof(PhoneNumberProof.NotProven)} or "
            + $"{nameof(PhoneNumberProof.Proven)}");
    }

    /// <summary>
    /// Whether a stated value is the given name of an enumeration member, without regard to case.
    /// </summary>
    /// <param name="value">Stated value.</param>
    /// <param name="name">Name of the member.</param>
    /// <returns>Whether the value is the name.</returns>
    private static bool IsName(string value, string name) =>
        string.Equals(value, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a text value; blank states nothing.
    /// </summary>
    /// <param name="parent">Section the value lives in.</param>
    /// <param name="member">Member carrying the value.</param>
    /// <returns>The text, or <c>null</c> when unstated.</returns>
    /// <exception cref="InvalidOperationException">The member is a section rather than a value.</exception>
    private static string? ReadText(IConfigurationSection parent, string member) => ReadValue(parent, member).Value;

    /// <summary>
    /// Reads a boolean value.
    /// </summary>
    /// <param name="parent">Section the value lives in.</param>
    /// <param name="member">Member carrying the value.</param>
    /// <param name="defaultValue">Value of an unstated member.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The value is not a boolean.</exception>
    private static bool ReadBoolean(IConfigurationSection parent, string member, bool defaultValue) =>
        ReadNullableBoolean(parent, member) ?? defaultValue;

    /// <summary>
    /// Reads a boolean value that may stay unstated.
    /// </summary>
    /// <param name="parent">Section the value lives in.</param>
    /// <param name="member">Member carrying the value.</param>
    /// <returns>The value, or <c>null</c> when unstated.</returns>
    /// <exception cref="InvalidOperationException">The value is not a boolean.</exception>
    private static bool? ReadNullableBoolean(IConfigurationSection parent, string member)
    {
        var (value, path) = ReadValue(parent, member);

        if (value is null)
        {
            return null;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw Invalid(path, "is not a boolean (true or false)");
        }

        return parsed;
    }

    /// <summary>
    /// Reads a timeout: a time span stated with its hours and minutes, greater than zero (SPEC-042 L44),
    /// within the range the timeout strategy of the outbound calls accepts — a value outside it would
    /// fail the first call instead of the start.
    /// </summary>
    /// <param name="parent">Section the value lives in.</param>
    /// <param name="member">Member carrying the value.</param>
    /// <param name="defaultValue">Value of an unstated member.</param>
    /// <returns>The timeout.</returns>
    /// <exception cref="InvalidOperationException">The value is not a positive time span within the range.</exception>
    private static TimeSpan ReadTimeout(IConfigurationSection parent, string member, TimeSpan defaultValue)
    {
        var (value, path) = ReadValue(parent, member);

        if (value is null)
        {
            return defaultValue;
        }

        // A bare integer parses as a number of days; no timeout of the contract is stated that way, so
        // a value without the hours-minutes separator is refused rather than read as days.
        if (!value.Contains(HttpChannelConstants.TimeSpanComponentSeparator, StringComparison.Ordinal)
            || !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timeout))
        {
            throw Invalid(
                path,
                "is not a time span in the form hh:mm:ss (for example 00:00:10 for ten seconds; a bare "
                + "number would be read as days)");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw Invalid(path, "must be greater than zero");
        }

        if (timeout < HttpChannelConstants.MinTimeout || timeout > HttpChannelConstants.MaxTimeout)
        {
            throw Invalid(
                path,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"must be between {HttpChannelConstants.MinTimeout:c} and {HttpChannelConstants.MaxTimeout:c}"));
        }

        return timeout;
    }

    /// <summary>
    /// Reads a list stated as a configuration array by index (<c>…:0</c>, <c>…:1</c>; in the
    /// environment <c>…__0</c>). Every entry is a non-blank single value.
    /// </summary>
    /// <param name="parent">Section the list lives in.</param>
    /// <param name="member">Member carrying the list.</param>
    /// <returns>The entries with the full path of each, in index order; empty when unstated.</returns>
    /// <exception cref="InvalidOperationException">The member is a single value, or an entry is blank.</exception>
    private static IReadOnlyList<(string Value, string Path)> ReadList(IConfigurationSection parent, string member)
    {
        var list = parent.GetSection(member);

        if (!string.IsNullOrWhiteSpace(list.Value))
        {
            throw Invalid(list.Path, "is a single value, not a list: state the entries by index (…:0, …:1)");
        }

        var entries = new List<(string Value, string Path)>();

        foreach (var entry in list.GetChildren())
        {
            var (value, path) = ReadValue(list, entry.Key);

            if (value is null)
            {
                throw Invalid(path, "is empty");
            }

            entries.Add((value, path));
        }

        return entries.AsReadOnly();
    }

    /// <summary>
    /// Reads one single value of a section. A blank value states nothing.
    /// </summary>
    /// <param name="parent">Section the value lives in.</param>
    /// <param name="member">Member carrying the value.</param>
    /// <returns>The value (or <c>null</c> when unstated) and the full path of the member.</returns>
    /// <exception cref="InvalidOperationException">The member is a section rather than a value.</exception>
    private static (string? Value, string Path) ReadValue(IConfigurationSection parent, string member)
    {
        var node = parent.GetSection(member);

        if (node.Value is null && node.GetChildren().Any())
        {
            throw Invalid(node.Path, "is a section, not a single value");
        }

        return (string.IsNullOrWhiteSpace(node.Value) ? null : node.Value, node.Path);
    }

    /// <summary>
    /// The refusal of a key: the full path of the key and what is wrong with it.
    /// </summary>
    /// <param name="path">Full path of the key.</param>
    /// <param name="problem">What is wrong with the value.</param>
    /// <returns>The exception to throw.</returns>
    private static InvalidOperationException Invalid(string path, string problem) =>
        new($"Invalid HTTP channel configuration: '{path}' {problem}.");

    /// <summary>
    /// Compiled matcher of the channel-type format.
    /// </summary>
    [GeneratedRegex(CustomChannelConstants.ChannelTypePattern)]
    private static partial Regex ChannelTypeRegex();

    /// <summary>
    /// Compiled matcher of the URI scheme grammar.
    /// </summary>
    [GeneratedRegex(HttpChannelConstants.DeepLinkSchemePattern)]
    private static partial Regex DeepLinkSchemeRegex();

    /// <summary>
    /// A transport key of one registration, decoded, with the key that stated it.
    /// </summary>
    /// <param name="ChannelType">Channel type of the registration.</param>
    /// <param name="Path">Full path of the key that stated the secret.</param>
    /// <param name="Key">Decoded key.</param>
    private sealed record DecodedSecret(string ChannelType, string Path, byte[] Key);
}
