// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// Registry of resolver keys for AuthServer settings (CFG-220/223/224).
/// Declared declaratively with semantics; protective ceilings (lifetimes, rate-limit) — "stricter downward".
/// <para>
/// Each key states its levels WITH THE ADDRESS OF EACH (SPEC-012 §10.6), and for most of them that is
/// the core level alone: a level outside a key's declared set is never queried for it. The ceiling and
/// gate combinators below are therefore the CONTRACT of a key, not a description of a live multi-level
/// resolution — a combinator starts to matter the moment the key's boundaries are widened, which is
/// now one act (a level with its address, in the declaration itself).
/// </para>
/// <para>
/// The addresses are written EXPLICITLY rather than derived from the names of the keys: the sections
/// of a deployed installation took their shape before these keys were named, and an address is what
/// keeps such an installation reading exactly what it read before.
/// </para>
/// </summary>
public static class AuthServerConfigKeys
{
    /// <summary>
    /// Catalog of the keys this assembly declares AS A CHAIN — name, dimensions, levels with the
    /// address of each — for the one registrar of the deployment to declare and bind
    /// (SPEC-012 §10.6). Every key below goes through it: the handwritten registrars that used to bind
    /// their levels one by one are gone, and with them the second place a level was stated in.
    /// <para>
    /// It is initialized before the declarations that fill it, which is the order the initializers of
    /// this class run in.
    /// </para>
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// The OIDC server settings AS THE PRODUCT SHIPS THEM — a default-constructed options object, read
    /// for one thing only: the value a core level yields when the section states nothing.
    /// <para>
    /// It is what keeps the migration of these keys to an address behaviour-neutral. A core level bound
    /// over <c>IOptions</c> always states a value, because the binder leaves the class default in place
    /// where the configuration is silent; a level read by path alone would state nothing there, and the
    /// consumer would receive the default of the TYPE instead of the default of the SETTING. Taking the
    /// shipped values off the options class itself — rather than restating them here — is what keeps
    /// the two from drifting.
    /// </para>
    /// </summary>
    private static readonly OidcServerOptions ShippedServer = new();

    /// <summary>
    /// Access token lifetime (sec), CFG-220. Protective ceiling: a level below the owning one can only
    /// make the value stricter. "Stricter" = a smaller value (the token lives shorter) = more protection.
    /// </summary>
    public static ConfigKey<int> AccessTokenLifetimeSeconds { get; } = Declared
        .Of<int>("OidcServer.AccessTokenLifetimeSeconds")
        .At(
            ConfigLevel.Core,
            OidcServerOptions.SectionName + ":" + nameof(OidcServerOptions.AccessTokenLifetimeSeconds))
        .ProtectiveCeiling(static (u, l) => Math.Min(u, l))
        .Default(ShippedServer.AccessTokenLifetimeSeconds)
        .Declare();

    /// <summary>
    /// Refresh token lifetime (sec), CFG-220. Protective ceiling: "stricter" = a smaller value.
    /// </summary>
    public static ConfigKey<int> RefreshTokenLifetimeSeconds { get; } = Declared
        .Of<int>("OidcServer.RefreshTokenLifetimeSeconds")
        .At(
            ConfigLevel.Core,
            OidcServerOptions.SectionName + ":" + nameof(OidcServerOptions.RefreshTokenLifetimeSeconds))
        .ProtectiveCeiling(static (u, l) => Math.Min(u, l))
        .Default(ShippedServer.RefreshTokenLifetimeSeconds)
        .Declare();

    /// <summary>
    /// Authorization code lifetime (sec), CFG-220. Protective ceiling: "stricter" = a smaller value.
    /// </summary>
    public static ConfigKey<int> AuthorizationCodeLifetimeSeconds { get; } = Declared
        .Of<int>("OidcServer.AuthorizationCodeLifetimeSeconds")
        .At(
            ConfigLevel.Core,
            OidcServerOptions.SectionName + ":" + nameof(OidcServerOptions.AuthorizationCodeLifetimeSeconds))
        .ProtectiveCeiling(static (u, l) => Math.Min(u, l))
        .Default(ShippedServer.AuthorizationCodeLifetimeSeconds)
        .Declare();

    /// <summary>
    /// Refresh token rotation (CFG-220): protective — "stricter" = enabled (true).
    /// A level below the one that enabled it could not disable rotation without a gate.
    /// </summary>
    public static ConfigKey<bool> EnableRefreshTokenRotation { get; } = Declared
        .Of<bool>("OidcServer.EnableRefreshTokenRotation")
        .At(
            ConfigLevel.Core,
            OidcServerOptions.SectionName + ":" + nameof(OidcServerOptions.EnableRefreshTokenRotation))
        .ProtectiveCeiling(static (u, l) => u || l)
        .Default(ShippedServer.EnableRefreshTokenRotation)
        .Declare();

    /// <summary>
    /// Revocation endpoint (CFG-220): protective — "stricter" = enabled (true).
    /// </summary>
    public static ConfigKey<bool> EnableRevocation { get; } = Declared
        .Of<bool>("OidcServer.EnableRevocation")
        .At(ConfigLevel.Core, OidcServerOptions.SectionName + ":" + nameof(OidcServerOptions.EnableRevocation))
        .ProtectiveCeiling(static (u, l) => u || l)
        .Default(ShippedServer.EnableRevocation)
        .Declare();

    /// <summary>
    /// Issuer (CFG-224): a simple value — the first declared level that defines it wins; it is not
    /// split per application. A null Issuer is a legitimate stated value — "derive it from the server
    /// URL" — which is why the core level states it rather than staying unset.
    /// </summary>
    public static ConfigKey<string?> Issuer { get; } = Declared
        .Text("OidcServer.Issuer")
        .At(ConfigLevel.Core, OidcServerOptions.SectionName + ":" + nameof(OidcServerOptions.Issuer))
        .Default(ShippedServer.Issuer)
        .Declare();

    /// <summary>
    /// Name of the dimension that cuts a setting addressed BY CHANNEL (SPEC-012 CFG-237). Its values
    /// are channel types (<c>telegram</c>, <c>whatsapp</c>, a third-party SPI channel type) — the core
    /// has no closed list of them, which is why a channel is asked about by its type rather than
    /// enumerated.
    /// <para>
    /// The name itself is spelled ONCE, in <see cref="ConfigDimensionNames.Channel"/>: an owner of a
    /// per-channel key in a contour that does not depend on this assembly reads it from there, and a
    /// second literal would not fail — it would simply never match the point a consumer asks with.
    /// This member stays as the name the sign-in page keys are declared with.
    /// </para>
    /// </summary>
    public const string ChannelDimensionName = ConfigDimensionNames.Channel;

    /// <summary>
    /// Core-level address of the custom sign-in page script — the member the key is read at, stated
    /// once. It is not only the declaration below that names it: the startup report on a refused script
    /// asks this level for the GATE it carries (<see cref="ReadCoreCustomJs"/>), and a report reading
    /// the gate at a second address of its own is exactly what would let the two disagree.
    /// </summary>
    internal const string CoreCustomJsAddress =
        AuthPageDesignOptions.SectionName + ":" + nameof(AuthPageDesignOptions.CustomJsPath);

    /// <summary>
    /// Member the QR settings live in inside the record of a level above the core — the address the
    /// <see cref="ConfigLevel.Tenant"/> and <see cref="ConfigLevel.Application"/> levels of the QR keys
    /// are read at, relative to the record of a tenant and to the OIDC client entry of an application.
    /// It is stated once, here: the entry carries no property of that name any more, and the snapshot
    /// report of these values walks the very same address.
    /// </summary>
    internal const string ClientQrCodeSection = "QrCode";

    /// <summary>
    /// Member of the QR section carrying the VISIBILITY, as a deployment spells it. It is written out
    /// rather than taken from a member of <see cref="QrCodeOptions"/>: the section is no longer bound
    /// into an object for this setting, and the address of a level is a contract of the deployment
    /// (SPEC-012 §10.6) — it keeps its spelling whatever the shape of the options class becomes.
    /// </summary>
    private const string ShowQrCodeMember = "ShowQrCode";

    /// <summary>
    /// The QR visibility the product SHIPS — the value the core level yields where the section states
    /// none. It lives at the key for the same reason the member name above does: the section is no
    /// longer bound into an object for this setting, so the declaration of the key is the only place
    /// left that can state it (the arrangement <see cref="CorePageBrandingConfigKeys.DefaultPreset"/>
    /// already has). It is <see cref="QrCodeVisibility.DesktopOnly"/> (SPEC-025 HOP-147): a phone gets
    /// the channel buttons, and every other device keeps the QR.
    /// </summary>
    private const QrCodeVisibility ShippedVisibility = QrCodeVisibility.DesktopOnly;

    /// <summary>
    /// Member of the <c>ui_config</c> record the QR settings live in — the snake_case name of its
    /// public JSON schema, which the record spells with <c>[JsonPropertyName]</c> and already deployed
    /// consumers read.
    /// </summary>
    private const string RecordQrCodeSection = "qr_code";

    /// <summary>
    /// Member of the <c>ui_config</c> record holding the PER-CHANNEL scales — the snake_case name of
    /// its public JSON schema, stated once for the two readings of it: the step of the chain that
    /// addresses a channel, and the walk of the level's snapshot.
    /// </summary>
    private const string RecordPixelsPerModuleByChannel = "pixels_per_module_by_channel";

    /// <summary>
    /// QR settings of the authentication page (SPEC-003 §17.3 — presentational group,
    /// SPEC-012 §10.3/§10.6) — TWO keys over the same four levels: the global section, the tenant
    /// record, the OIDC client entry and the <c>ui_config</c> record. Value semantics (CFG-210) applies
    /// per field: the first level that states a field wins that field alone, so an application that
    /// states only the visibility keeps the scale of the level below instead of silently replacing the whole section with its own
    /// defaults. Self-hosted (N=1) resolves every one of them to the core level, i.e. to the global
    /// section named by <see cref="QrCodeOptions.SectionName"/>.
    /// <para>
    /// The core level DECLARES the shipped visibility, <see cref="QrCodeVisibility.DesktopOnly"/>: a
    /// deployment that writes no section is served that value by this declaration, not by the default
    /// of the TYPE — the two differ (the type's default is <see cref="QrCodeVisibility.Always"/>).
    /// Declaring it is also what lets the schema of the deployment print the answer instead of "none
    /// declared".
    /// </para>
    /// </summary>
    public static ConfigKey<QrCodeVisibility> QrCodeShowQrCode { get; } = Declared
        .Of<QrCodeVisibility>("AuthPageDesign.QrCode.ShowQrCode")
        .At(ConfigLevel.Core, QrCodeOptions.SectionName + ":" + ShowQrCodeMember)
        .At(ConfigLevel.Tenant, ClientQrCodeSection + ":" + ShowQrCodeMember)
        .At(ConfigLevel.Application, ClientQrCodeSection + ":" + ShowQrCodeMember)
        .At(ConfigLevel.UiConfig, RecordQrCodeSection + ":show_qr_code")
        .Cached(ConfigCachePolicy.ExternalStore)
        .Default(ShippedVisibility)
        .Declare();

    /// <summary>
    /// Domain of the QR pixel scale — the range the setting admits on ANY level (SPEC-015 §4.2). It is
    /// declared once, on the key, and applied by the resolution mechanism wherever the key is bound, so
    /// no level, reader or report repeats the bounds: the global section, a tenant record, an OIDC
    /// client entry and a <c>ui_config</c> record are all bound by this one declaration. The boundary
    /// text next to the predicate is what the snapshot report names a rejected value against, and the address next to it
    /// is where the report tells the operator to write a correct one — the section and the field, not
    /// the record that broke it: that record the report names itself.
    /// <para>
    /// The policy stays the default one (SPEC-012 CFG-240): a scale outside the range is a degradation
    /// and not a reason to stop the host — a broken entry of one client must not take the deployment
    /// down, and the page renders with the scale of the level below.
    /// </para>
    /// </summary>
    private static readonly ConfigValueDomain<int> PixelsPerModuleDomain = new(
        QrCodeOverrideOptions.IsPixelsPerModuleInRange,
        $"[{QrCodeOptions.MinPixelsPerModule}; {QrCodeOptions.MaxPixelsPerModule}] px per module",
        $"{QrCodeOptions.SectionName}:{nameof(QrCodeOverrideOptions.PixelsPerModule)} (or "
        + $"{nameof(QrCodeOverrideOptions.PixelsPerModuleByChannel)}) of the level stating it");

    /// <summary>
    /// QR pixel scale of the sign-in page FOR A CHANNEL (see <see cref="QrCodeShowQrCode"/> for the
    /// shape of the group). The channel is a dimension of the key (<see cref="ChannelDimensionName"/>,
    /// narrowing): a level
    /// states the scale of a single channel in its <c>PixelsPerModuleByChannel</c> map and the scale
    /// of every other channel in its <c>PixelsPerModule</c> field, and both steps are exhausted inside
    /// that level before the resolution drops to the level below (CFG-236). The allowed range is the
    /// DOMAIN of the key (<see cref="PixelsPerModuleDomain"/>) and therefore holds on EVERY level, not
    /// on the core alone: a value outside it leaves its step unset, and the scale comes from the next
    /// step or the next level.
    /// </summary>
    public static ConfigKey<int> QrCodePixelsPerModule { get; } = Declared
        .Of<int>("AuthPageDesign.QrCode.PixelsPerModule")
        .Narrowing(ChannelDimensionName)
        .At(ConfigLevel.Core, QrCodeOptions.SectionName + ":" + nameof(QrCodeOptions.PixelsPerModule))
        .At(ConfigLevel.Tenant, ClientQrCodeSection + ":" + nameof(QrCodeOptions.PixelsPerModule))
        .At(ConfigLevel.Application, ClientQrCodeSection + ":" + nameof(QrCodeOptions.PixelsPerModule))
        .At(ConfigLevel.UiConfig, RecordQrCodeSection + ":pixels_per_module")
        .Cached(ConfigCachePolicy.ExternalStore)
        .Domain(PixelsPerModuleDomain)
        .Parse(static (node, path, point) => ReadScale(node, path, point))
        .Stated(static (node, path) => StatedScales(node, path))
        .Declare();

    /// <summary>
    /// Layer value of one step of the scale's chain at a level: the step that addresses the channel
    /// answers from the entry of the level's per-channel map that names the channel, the step that
    /// addresses none from the plain member the address names. The two are different MEMBERS of the
    /// record, which an address cannot say, so the key states it here. Whether the setting admits the
    /// value is decided by <see cref="PixelsPerModuleDomain"/>, applied by the mechanism to every
    /// binding alike.
    /// <para>
    /// A value the record cannot be read as is not caught here either: the mechanism reports it and
    /// leaves the step that read it unset (SPEC-012 CFG-246, CFG-240). The map is read entry by entry,
    /// so an entry that cannot be read costs that entry alone — the scale of its channel comes from the
    /// plain member of the same level or from the level below, and the other entries stand — while a
    /// plain member that cannot be read leaves both steps of its level unset.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to — the plain scale.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    private static LayerValue<int> ReadScale(ConfigNode node, string path, ConfigDimensionValues point) =>
        DimensionedConfigValue.Layer<int>(node, point, ChannelDimensionName, ByChannelAddress(node, path), path);

    /// <summary>
    /// Addresses one record of a level STATES the scale at: the plain member the key's address names,
    /// and one address per entry of the level's per-channel map. Both are steps of the same key's
    /// fallback chain (SPEC-012 CFG-237), so both are stated values of one setting — and the map is a
    /// member of its own, which an address cannot name, so the walk of the snapshot is told about it
    /// here for the same reason the reading of a step is (<see cref="ReadScale"/>).
    /// <para>
    /// The entries are asked for by NAME rather than read as a map: a name is what an address is built
    /// of, while a value the walk cannot read is not a value the domain of the setting can reject — and
    /// one such entry must not take the report of the entries beside it down with it.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address of the level — the plain scale.</param>
    /// <returns>Addresses the record states the scale at.</returns>
    private static IEnumerable<string> StatedScales(ConfigNode node, string path)
    {
        yield return path;

        var byChannel = ByChannelAddress(node, path);

        foreach (var channelType in node.Children(byChannel))
        {
            yield return $"{byChannel}{ConfigNode.PathSeparator}{channelType}";
        }
    }

    /// <summary>
    /// Address of the per-channel scale map inside the record of a level, next to the plain member the
    /// key's address names.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address of the level — the plain scale.</param>
    /// <returns>Address of the per-channel map.</returns>
    private static string ByChannelAddress(ConfigNode node, string path) =>
        CorePageDesignNode.SiblingAddress(
            node,
            path,
            nameof(QrCodeOptions.PixelsPerModuleByChannel),
            RecordPixelsPerModuleByChannel);

    /// <summary>
    /// Member the settings of the cancel button live in inside the global section of the page
    /// design and inside the record of a level above the core — the tenant record and the OIDC client
    /// entry. Written out for the reason <see cref="ShowQrCodeMember"/> is: no level binds the member
    /// into an object for this setting, and the address of a level is a contract of the deployment
    /// (SPEC-012 §10.6).
    /// </summary>
    private const string CancelSection = "Cancel";

    /// <summary>
    /// Member of <see cref="CancelSection"/> carrying the switch of the button, as a deployment spells it.
    /// </summary>
    private const string CancelEnabledMember = "Enabled";

    /// <summary>
    /// Address of the same switch inside the <c>ui_config</c> record — the snake_case names of its public
    /// JSON schema, which the record spells with <c>[JsonPropertyName]</c>.
    /// </summary>
    private const string RecordCancelEnabledAddress = "cancel:enabled";

    /// <summary>
    /// Whether the product SHIPS the cancel button on — the value the core level yields where
    /// the section states none. It lives at the key for the reason <see cref="ShippedVisibility"/> does:
    /// the declaration of the key is the only place that states it.
    /// </summary>
    private const bool ShippedCancelEnabled = true;

    /// <summary>
    /// Whether the cancel button is offered — "Cancel sign-in" of the sign-in window and "Cancel
    /// confirmation" of the entry page of a confirmation (SPEC-012 CFG-267, SPEC-007 UI-122): pressing it ends
    /// the transaction as declined by the user; the client of a sign-in receives the standard
    /// <c>access_denied</c> answer, the browser of a confirmation the receipt of the refusal. A plain value over the same four levels as
    /// <see cref="QrCodeShowQrCode"/>, each overriding the one below it freely: the first level that
    /// states the switch wins. The core level declares the shipped answer — the button is on.
    /// <para>
    /// The switch decides the MARKUP of the page only. The routes the button posts to are mapped
    /// whatever the value: they are guarded by their own checks, and a page rendered a moment before the
    /// setting was switched off still has to be answered.
    /// </para>
    /// </summary>
    public static ConfigKey<bool> CancelEnabled { get; } = Declared
        .Of<bool>("AuthPageDesign.Cancel.Enabled")
        .At(
            ConfigLevel.Core,
            AuthPageDesignOptions.SectionName + ":" + CancelSection + ":" + CancelEnabledMember)
        .At(ConfigLevel.Tenant, CancelSection + ":" + CancelEnabledMember)
        .At(ConfigLevel.Application, CancelSection + ":" + CancelEnabledMember)
        .At(ConfigLevel.UiConfig, RecordCancelEnabledAddress)
        .Cached(ConfigCachePolicy.ExternalStore)
        .Default(ShippedCancelEnabled)
        .Declare();

    /// <summary>
    /// Member the settings of the channel buttons live in inside the global section of the page design
    /// and inside the record of a level above the core — the tenant record and the OIDC client entry.
    /// Written out for the reason <see cref="ShowQrCodeMember"/> is: no level binds the member into an
    /// object for this setting, and the address of a level is a contract of the deployment
    /// (SPEC-012 §10.6).
    /// </summary>
    private const string ChannelButtonSection = "ChannelButton";

    /// <summary>
    /// Member of <see cref="ChannelButtonSection"/> carrying the visibility of the button, as a
    /// deployment spells it.
    /// </summary>
    private const string ShowChannelButtonMember = "ShowChannelButton";

    /// <summary>
    /// Address of the same visibility inside the <c>ui_config</c> record — the snake_case names of its
    /// public JSON schema, which the record spells with <c>[JsonPropertyName]</c>.
    /// </summary>
    private const string RecordShowChannelButtonAddress = "channel_button:show_channel_button";

    /// <summary>
    /// The channel button visibility the product SHIPS — the value the core level yields where the
    /// section states none: the button is on every device. It lives at the key for the reason
    /// <see cref="ShippedVisibility"/> does.
    /// </summary>
    private const ChannelButtonVisibility ShippedChannelButtonVisibility = ChannelButtonVisibility.Always;

    /// <summary>
    /// On which devices the sign-in page shows the channel button — the button that takes the user into
    /// a channel by its deep link. The counterpart of <see cref="QrCodeShowQrCode"/>, over the same four
    /// levels, each overriding the one below it freely: the first level that states the visibility wins.
    /// The core level declares the shipped answer, <see cref="ChannelButtonVisibility.Always"/>.
    /// <para>
    /// The two visibilities are resolved independently, and some combinations of them leave a device
    /// with no way in at all — neither the QR code nor the button. That is a relation BETWEEN the keys
    /// and is therefore checked by the group the page is made of (<see cref="AuthPageSettingsGroup"/>),
    /// not here: the page then shows the button on every device, and the operator is told.
    /// </para>
    /// </summary>
    public static ConfigKey<ChannelButtonVisibility> ChannelButtonShowChannelButton { get; } = Declared
        .Of<ChannelButtonVisibility>("AuthPageDesign.ChannelButton.ShowChannelButton")
        .At(
            ConfigLevel.Core,
            AuthPageDesignOptions.SectionName + ":" + ChannelButtonSection + ":" + ShowChannelButtonMember)
        .At(ConfigLevel.Tenant, ChannelButtonSection + ":" + ShowChannelButtonMember)
        .At(ConfigLevel.Application, ChannelButtonSection + ":" + ShowChannelButtonMember)
        .At(ConfigLevel.UiConfig, RecordShowChannelButtonAddress)
        .Cached(ConfigCachePolicy.ExternalStore)
        .Default(ShippedChannelButtonVisibility)
        .Declare();

    /// <summary>
    /// Member carrying the layout of the channels inside the global section of the page design and
    /// inside the record of a level above the core — the tenant record and the OIDC client entry.
    /// Written out for the reason <see cref="ShowQrCodeMember"/> is: no level binds the member into an
    /// object for this setting, and the address of a level is a contract of the deployment
    /// (SPEC-012 §10.6).
    /// </summary>
    private const string ChannelLayoutMember = "ChannelLayout";

    /// <summary>
    /// Address of the same layout inside the <c>ui_config</c> record — the snake_case name of its public
    /// JSON schema, which the record spells with <c>[JsonPropertyName]</c>.
    /// </summary>
    private const string RecordChannelLayoutAddress = "channel_layout";

    /// <summary>
    /// The layout of the channels the product SHIPS — the value the core level yields where the section
    /// states none: tabs. It lives at the key for the reason <see cref="ShippedVisibility"/> does.
    /// </summary>
    private const ChannelLayout ShippedChannelLayout = ChannelLayout.Tabs;

    /// <summary>
    /// How several channels share a sign-in page that shows the QR code of each of them: behind tabs,
    /// or all at once. A plain value over the same four levels as <see cref="QrCodeShowQrCode"/>, each
    /// overriding the one below it freely: the first level that states the layout wins. The core level
    /// declares the shipped answer, <see cref="ChannelLayout.Tabs"/>.
    /// <para>
    /// The key arranges and nothing more: the set of channels stays with
    /// <see cref="PageChannelDisplayMode"/> and <see cref="PageChannels"/>, and a page that has nothing
    /// to arrange — one channel, the buttons layout, the unified QR code — is rendered the same
    /// whatever the value.
    /// </para>
    /// </summary>
    public static ConfigKey<ChannelLayout> PageChannelLayout { get; } = Declared
        .Of<ChannelLayout>("AuthPageDesign.ChannelLayout")
        .At(ConfigLevel.Core, AuthPageDesignOptions.SectionName + ":" + ChannelLayoutMember)
        .At(ConfigLevel.Tenant, ChannelLayoutMember)
        .At(ConfigLevel.Application, ChannelLayoutMember)
        .At(ConfigLevel.UiConfig, RecordChannelLayoutAddress)
        .Cached(ConfigCachePolicy.ExternalStore)
        .Default(ShippedChannelLayout)
        .Declare();

    /// <summary>
    /// Member the settings of remembering the sign-in channel live in inside the global section of the
    /// page design and inside the record of a level above the core — the tenant record and the OIDC
    /// client entry. Written out for the reason <see cref="ShowQrCodeMember"/> is: no level binds the
    /// member into an object for this setting, and the address of a level is a contract of the
    /// deployment (SPEC-012 §10.6).
    /// </summary>
    private const string RememberChannelSection = "RememberChannel";

    /// <summary>
    /// Member of <see cref="RememberChannelSection"/> carrying the switch of the hint of the last
    /// channel, as a deployment spells it.
    /// </summary>
    private const string RememberChannelHintMember = "Hint";

    /// <summary>
    /// Member of <see cref="RememberChannelSection"/> carrying the switch of the "remember" checkbox, as
    /// a deployment spells it.
    /// </summary>
    private const string RememberChannelCheckboxMember = "Checkbox";

    /// <summary>
    /// Whether the product SHIPS the hint of the last channel on — the value the core level yields where
    /// the section states none: it is off. It lives at the key for the reason
    /// <see cref="ShippedVisibility"/> does.
    /// </summary>
    private const bool ShippedRememberChannelHint = false;

    /// <summary>
    /// Whether the product SHIPS the "remember" checkbox on — the value the core level yields where the
    /// section states none: it is off. It lives at the key for the reason <see cref="ShippedVisibility"/>
    /// does.
    /// </summary>
    private const bool ShippedRememberChannelCheckbox = false;

    /// <summary>
    /// Whether the sign-in window, the entry page of a confirmation and the scanner page of the unified
    /// QR code remember, silently, the TYPE of the channel the user went through, and put that channel
    /// forward on the next visit: its tab is selected, its button comes first. A plain value over the
    /// core, tenant and application levels, each overriding the one below it freely: the first level
    /// that states the switch wins. The core level declares the shipped answer — the hint is off.
    /// <para>
    /// The channel is kept in a cookie of the Veriqa host that the script of the page writes and reads;
    /// the server never reads it, and the switch decides only what the page hands its script. The key
    /// has no <c>ui_config</c> level: the record does not carry the member.
    /// </para>
    /// </summary>
    public static ConfigKey<bool> RememberChannelHint { get; } = Declared
        .Of<bool>("AuthPageDesign.RememberChannel.Hint")
        .At(
            ConfigLevel.Core,
            AuthPageDesignOptions.SectionName + ":" + RememberChannelSection + ":" + RememberChannelHintMember)
        .At(ConfigLevel.Tenant, RememberChannelSection + ":" + RememberChannelHintMember)
        .At(ConfigLevel.Application, RememberChannelSection + ":" + RememberChannelHintMember)
        .Cached(ConfigCachePolicy.ExternalStore)
        .Default(ShippedRememberChannelHint)
        .Declare();

    /// <summary>
    /// Whether a page that shows the unified QR code — the sign-in window and the entry page of a
    /// confirmation, on a desktop — offers the checkbox that remembers the channel the user went
    /// through, so that the next visit opens on the panel of that channel with its own QR code. Where
    /// the checkbox is shown, the channel is remembered only once it is checked. A plain value over the
    /// core, tenant and application levels, each overriding the one below it freely: the first level
    /// that states the switch wins. The core level declares the shipped answer — the checkbox is off.
    /// <para>
    /// The channel is kept in a cookie of the Veriqa host that the script of the page writes and reads;
    /// the server never reads it. The switch decides the markup of the page and what it hands its
    /// script: under the unified QR code alone, the page then carries the panels of its channels hidden,
    /// for the script to show the remembered one. The key has no <c>ui_config</c> level: the record does
    /// not carry the member.
    /// </para>
    /// </summary>
    public static ConfigKey<bool> RememberChannelCheckbox { get; } = Declared
        .Of<bool>("AuthPageDesign.RememberChannel.Checkbox")
        .At(
            ConfigLevel.Core,
            AuthPageDesignOptions.SectionName + ":" + RememberChannelSection + ":" + RememberChannelCheckboxMember)
        .At(ConfigLevel.Tenant, RememberChannelSection + ":" + RememberChannelCheckboxMember)
        .At(ConfigLevel.Application, RememberChannelSection + ":" + RememberChannelCheckboxMember)
        .Cached(ConfigCachePolicy.ExternalStore)
        .Default(ShippedRememberChannelCheckbox)
        .Declare();

    /// <summary>
    /// Hint of the sign-in page FOR A CHANNEL: the owner is the tenant (SPEC-012 §9), resolved by the
    /// common precedence of SPEC-012 §10.3. The channel is the IDENTITY dimension of the key — "the
    /// hint for channel X" is a question to the resolver rather than a lookup inside a resolved map, so
    /// the level a value comes from (CFG-232) names the hint of that channel. The chain is one step
    /// long: a hint for every channel alike does not exist (CFG-239), which is exactly what an identity
    /// dimension means — it stands in every step and the address substitutes it. Self-hosted (N=1)
    /// resolves to the core level, i.e. to the map under <c>Veriqa:ChannelDisplay:Hints</c>.
    /// <para>
    /// A hint the map states BLANK is no hint (see <see cref="ChannelDisplayOptions.Hints"/>): the
    /// level then states nothing and the panel is rendered without one, which the key states as a
    /// member of its declaration rather than as a hook — so the walk of a snapshot reads it the same
    /// way the resolution does.
    /// </para>
    /// </summary>
    public static ConfigKey<string?> ChannelHints { get; } = Declared
        .Text("ChannelDisplay.Hints")
        .Identity(ChannelDimensionName)
        .At(
            ConfigLevel.Core,
            ChannelDisplayOptions.SectionName + ":" + nameof(ChannelDisplayOptions.Hints)
            + ":{" + ChannelDimensionName + "}")
        .BlankIsUnstated()
        .Declare();

    /// <summary>
    /// Member of the <c>ui_config</c> record the presence of the record is read at — its schema
    /// version, the one field every record of this level states — the record's only required member. It is
    /// the address of <see cref="UiConfigSelectorAssigned"/>, whose value is the FACT that the record
    /// exists rather than anything written in it.
    /// </summary>
    private const string RecordSchemaVersion = "schema_version";

    /// <summary>
    /// Validity of the <c>ui_config</c> selector carried by the request (CFG-203): the level is set
    /// when the selector addresses a record the application owns, and unset when it does not. The key
    /// carries the FACT of ownership rather than a setting value, because that fact is exactly what the
    /// level's presence means — and reading it through the resolver is what keeps the record store
    /// behind a single reading path (CFG-231).
    /// <para>
    /// The value is therefore what the PRESENCE of the record means: the record of this level exists
    /// only where the reader found one the application owns, and a record that was not found leaves the
    /// level unset before anything is read at all. The address names the record's required member so
    /// that the declaration points at something the record actually holds — nothing is read out of it.
    /// </para>
    /// </summary>
    public static ConfigKey<bool> UiConfigSelectorAssigned { get; } = Declared
        .Of<bool>("UiConfig.SelectorAssigned")
        .At(ConfigLevel.UiConfig, RecordSchemaVersion)
        .Cached(ConfigCachePolicy.ExternalStore)
        .PresenceMeans(true)
        .Declare();

    /// <summary>
    /// Logo URL of the brand row of the sign-in page, read in every preset whose row is rendered
    /// (SPEC-007 UI-100) — branding of the page, owned by the tenant and overridable by the application
    /// and by the <c>ui_config</c> record (SPEC-012 §9). The key has no shipped value: when neither this
    /// key nor <see cref="CorePageBrandingConfigKeys.BrandName"/> resolves to a value, the page shows the product's own mark,
    /// and that default is markup of the page rather than an answer of the key.
    /// <para>
    /// The logo is the one configurable ASSET of the page that does not follow the theme by itself
    /// (an external file rather than a glyph painted with <c>currentColor</c>), so the key is cut by
    /// the theme — and the two steps of that cut read DIFFERENT members of a level (the themed map
    /// against the plain field), which is why the key states a parse hook instead of a second address.
    /// </para>
    /// </summary>
    public static ConfigKey<string?> PageLogoUrl { get; } = Declared
        .Text("AuthPageDesign.LogoUrl")
        .Narrowing(CorePageBrandingConfigKeys.ThemeDimensionName)
        .At(ConfigLevel.Core, AuthPageDesignOptions.SectionName + ":" + nameof(AuthPageDesignOptions.LogoUrl))
        .At(ConfigLevel.Tenant, nameof(AuthPageDesignOptions.LogoUrl))
        .At(ConfigLevel.Application, nameof(AuthPageDesignOptions.LogoUrl))
        .At(ConfigLevel.UiConfig, "logo_url")
        .Cached(ConfigCachePolicy.ExternalStore)
        .Parse(static (node, path, point) => CorePageDesignNode.ThemedText(
            node,
            path,
            point,
            nameof(AuthPageDesignOptions.LogoUrlByTheme),
            "logo_url_by_theme"))

        // A walk reads the plain member the address names and knows nothing of the themed map beside
        // it, so it cannot reproduce what this key resolves to. The report names the pair instead of
        // reporting a value that is only half of what the level states.
        .NotWalked()
        .Declare();

    /// <summary>
    /// Wording of the markup shape <see cref="AuthPageFooterMarkup"/> admits — the one shape of every
    /// setting whose value the sign-in page emits as markup, told to an operator by each such domain.
    /// </summary>
    private const string AdmissiblePageMarkupShape =
        "inline markup of the tags a, span, div, p, strong, em and br with the attributes href, target, "
        + "rel and class in double quotes, no class named veriqa or veriqa-*, the tags balanced, and every "
        + "link carrying target=\"_blank\" "
        + "and an http(s) or root-relative href";

    /// <summary>
    /// Core-level address of the footer block of the sign-in page — the member the key is read at, and
    /// the section its domain tells an operator to write a correct value at.
    /// </summary>
    private const string CoreFooterHtmlAddress =
        AuthPageDesignOptions.SectionName + ":" + nameof(AuthPageDesignOptions.FooterHtml);

    /// <summary>
    /// Domain of the footer block — the shape of markup the setting admits on ANY level
    /// (<see cref="AuthPageFooterMarkup"/>). A blank value is admitted: it is no block at all, and the
    /// key states it as a value no level has stated.
    /// <para>
    /// The policy is the strict one: the value is emitted into the page as markup, and a block that
    /// would break the window is not a degradation to render around. At the core level an inadmissible
    /// value stops the start; above it the record stating the value — the entry of one client — is left
    /// out of the effective configuration whole, and every other setting of that record is resolved from
    /// the level below as well.
    /// </para>
    /// </summary>
    private static readonly ConfigValueDomain<string?> FooterHtmlDomain = new(
        static markup => string.IsNullOrWhiteSpace(markup) || AuthPageFooterMarkup.IsValid(markup),
        AdmissiblePageMarkupShape,
        CoreFooterHtmlAddress + " of the core level, or '" + nameof(AuthPageDesignOptions.FooterHtml)
        + "' of the record of the level stating it",
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Footer block of the sign-in page — the integrator's own markup (legal links, a note, a support
    /// contact) emitted as the last element of the card, in every preset. The owner is the tenant, and
    /// the application may state its own block; the <c>ui_config</c> record does not state it at all —
    /// a tenant artifact selectable per request must not put markup on the sign-in page. There is no
    /// shipped value: no level stating one means no block.
    /// <para>
    /// The value is MARKUP and is emitted into the page unsanitized: its correctness and its safety are
    /// the integrator's responsibility. Its shape is checked (<see cref="AuthPageFooterMarkup"/>) on every
    /// level by the domain of the key and once more where it is emitted. Every link needs
    /// <c>target="_blank"</c>: leaving the page in the same tab ends the live sign-in, and coming back
    /// requests <c>GET /connect/authorize</c> again, which creates a new transaction.
    /// </para>
    /// <para>
    /// The value is a Natural Key: the page resolves it through the host locale files, and a value no
    /// locale file carries is emitted as it is. A blank value states nothing, so a level below cannot
    /// switch off the block a level above it states.
    /// </para>
    /// </summary>
    public static ConfigKey<string?> PageFooterHtml { get; } = Declared
        .Text("AuthPageDesign.FooterHtml")
        .At(ConfigLevel.Core, CoreFooterHtmlAddress)
        .At(ConfigLevel.Tenant, nameof(AuthPageDesignOptions.FooterHtml))
        .At(ConfigLevel.Application, nameof(AuthPageDesignOptions.FooterHtml))
        .Cached(ConfigCachePolicy.ExternalStore)
        .BlankIsUnstated()
        .Domain(FooterHtmlDomain)
        .Declare();

    /// <summary>
    /// Core-level address of the block the sign-in page shows once the hop has started — the member
    /// the key is read at, and the section its domain tells an operator to write a correct value at.
    /// </summary>
    private const string CoreHopInstructionHtmlAddress =
        AuthPageDesignOptions.SectionName + ":" + nameof(AuthPageDesignOptions.HopInstructionHtml);

    /// <summary>
    /// Domain of the hop instruction — the same admissible shape and the same strict policy as the
    /// footer block (<see cref="FooterHtmlDomain"/>), under its own address: a rejection names the
    /// setting the operator has to correct, and that is not the footer.
    /// </summary>
    private static readonly ConfigValueDomain<string?> HopInstructionHtmlDomain = new(
        static markup => string.IsNullOrWhiteSpace(markup) || AuthPageFooterMarkup.IsValid(markup),
        AdmissiblePageMarkupShape,
        CoreHopInstructionHtmlAddress + " of the core level, or '"
        + nameof(AuthPageDesignOptions.HopInstructionHtml) + "' of the record of the level stating it",
        ConfigValueRejectionPolicy.FailStart);

    /// <summary>
    /// Content of the block the sign-in page shows in place of the QR code and the channel choice once
    /// the first code of the hop family has arrived (SPEC-025 HOP-101) — text or the integrator's own
    /// markup. The levels are those of <see cref="PageFooterHtml"/>: the tenant owns it, the application
    /// may state its own, and the <c>ui_config</c> record does not state it at all — markup is not
    /// selectable per request. There is no shipped value: no level stating one means the page's own
    /// localized text. The block is styled by the page's rules and, beyond them, by the integrator's
    /// stylesheet (<see cref="CorePageBrandingConfigKeys.CustomCss"/>); the key carries no style of its own.
    /// <para>
    /// The value is MARKUP, emitted unsanitized, and checked for the shape of
    /// <see cref="AuthPageFooterMarkup"/> on every level by the domain of the key and once more where it
    /// is emitted; a translation outside that shape gives the page its default text.
    /// </para>
    /// <para>
    /// The value is a Natural Key: the page resolves it through the host locale files, and a value no
    /// locale file carries is emitted as it is. A blank value states nothing, so a level below cannot
    /// switch off the content a level above it states.
    /// </para>
    /// </summary>
    public static ConfigKey<string?> PageHopInstructionHtml { get; } = Declared
        .Text("AuthPageDesign.HopInstructionHtml")
        .At(ConfigLevel.Core, CoreHopInstructionHtmlAddress)
        .At(ConfigLevel.Tenant, nameof(AuthPageDesignOptions.HopInstructionHtml))
        .At(ConfigLevel.Application, nameof(AuthPageDesignOptions.HopInstructionHtml))
        .Cached(ConfigCachePolicy.ExternalStore)
        .BlankIsUnstated()
        .Domain(HopInstructionHtmlDomain)
        .Declare();

    /// <summary>
    /// Custom script of the sign-in page — path AND its SRI hash as ONE value
    /// (<see cref="CorePageCustomResource"/>). This is the risky customization mode of SPEC-012 §4.3
    /// (CFG-031/CFG-037), and its level set is narrower in both directions than that of the stylesheet
    /// (<see cref="CorePageBrandingConfigKeys.CustomCss"/>):
    /// <list type="bullet">
    /// <item><description>
    /// the <c>ui_config</c> record does NOT state it at all — a tenant artifact selectable per request
    /// must not put an executable script on the sign-in page;
    /// </description></item>
    /// <item><description>
    /// the application states it only through a gate of the owning level (CFG-212 names the permission
    /// of risky custom JS as a protective setting), which is why the key declares its application
    /// level as a GATED value rather than a plain one.
    /// </description></item>
    /// </list>
    /// <para>
    /// The WALK of a configuration snapshot does not read this pair, and the key SAYS so
    /// (<c>ConfigKeyBuilder.NotWalked</c>): the value stands over TWO members of the record — the path
    /// and its SRI hash — which only a parse hook can assemble, and a hook is applied by the path of
    /// reading that calls it. The report of the snapshot therefore NAMES the pairs of this key as
    /// standing outside it, rather than leaving an operator to tell "nothing is stated here" from
    /// "nothing was ever read here".
    /// </para>
    /// </summary>
    public static ConfigKey<CorePageCustomResource?> PageCustomJs { get; } = Declared
        .Of<CorePageCustomResource?>("AuthPageDesign.CustomJs")
        .At(ConfigLevel.Core, CoreCustomJsAddress)
        .At(ConfigLevel.Application, nameof(AuthPageDesignOptions.CustomJsPath))
        .GatedValue(ConfigLevel.Application)
        .Parse(static (node, path, _) => node.Level is ConfigLevel.Core
            ? ReadCoreCustomJs(node, path)
            : CorePageDesignNode.CustomResource(
                node,
                path,
                nameof(AuthPageDesignOptions.JsSriHash),
                nameof(AuthPageDesignOptions.JsSriHash)))
        .NotWalked()
        .Declare();

    /// <summary>
    /// Value of the CORE level of the custom script. It is stated ALWAYS — with a null resource where
    /// the global section names no script — because the value of this level is what CARRIES the gate
    /// of SPEC-012 §10.3: the resolver reads the gate off the level that declared it, and a core level
    /// that stated nothing would leave the gate closed and refuse a client's script that the owner of
    /// the section had explicitly permitted (CFG-031/CFG-212).
    /// <para>
    /// It is internal rather than private because the gate it returns is the one thing about this level
    /// a READER outside the resolution has to know: the startup report on a refused script asks it
    /// through <see cref="EffectiveCustomJsGate"/> instead of reading the same flag off the bound
    /// options class. The two answers part exactly where the read refuses a member of this level — the
    /// resolution then answers the level as a WHOLE as over a record stating nothing, with the shipped
    /// closed gate, while the binder of an options class passes a member of a refused shape over in
    /// silence and keeps the flag it did bind.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the host configuration the address is relative to.</param>
    /// <param name="path">Address the step resolved to — the member carrying the script path.</param>
    /// <returns>Layer value of the core level, with the gate it declares.</returns>
    internal static LayerValue<CorePageCustomResource?> ReadCoreCustomJs(ConfigNode node, string path)
    {
        var script = CorePageDesignNode.CustomResource(
            node,
            path,
            nameof(AuthPageDesignOptions.JsSriHash),
            nameof(AuthPageDesignOptions.JsSriHash));

        var gate = node.TryRead<bool>(
            CorePageDesignNode.SiblingAddress(
                node,
                path,
                nameof(AuthPageDesignOptions.CustomJsAllowApplicationOverride),
                nameof(AuthPageDesignOptions.CustomJsAllowApplicationOverride)),
            out var open)
            && open;

        return LayerValue<CorePageCustomResource?>.Set(script.HasValue ? script.Value : null, gate);
    }

    /// <summary>
    /// Path to the SignalR client loaded by the sign-in page.
    /// <para>
    /// The WALK of a configuration snapshot does not read this pair, and the key SAYS so
    /// (<c>ConfigKeyBuilder.NotWalked</c>): the allowlist of schemes lives in the hook, so a walk
    /// reading the address would report a value the resolution refuses. The allowlist is deliberately
    /// NOT turned into a domain, which would be the one way to put the check in front of a walk as
    /// well: a domain holds on EVERY level, and the global section — the deploying party's own — is not
    /// filtered, so moving the check would change what a deployment resolves. The report of the
    /// snapshot NAMES the pairs of this key as standing outside it instead of saying nothing about
    /// them.
    /// </para>
    /// </summary>
    public static ConfigKey<string?> PageSignalRClientPath { get; } = Declared
        .Text("AuthPageDesign.SignalRClientPath")
        .At(
            ConfigLevel.Core,
            AuthPageDesignOptions.SectionName + ":" + nameof(AuthPageDesignOptions.SignalRClientPath))
        .At(ConfigLevel.UiConfig, "signalr_client_path")
        .Cached(ConfigCachePolicy.ExternalStore)
        .Parse(static (node, path, _) => ReadSignalRClientPath(node, path))
        .NotWalked()
        .Declare();

    /// <summary>
    /// Value of one level of the SignalR client path. The path is emitted into a <c>&lt;script src&gt;</c>,
    /// so a value of the <c>ui_config</c> record whose scheme is outside the allowlist
    /// (<c>javascript:</c>/<c>data:</c>/protocol-relative <c>//</c>) leaves the level unset and the page
    /// falls back to the global — or bundled — client: that record is a tenant artifact a request
    /// selects, and it must not choose what the page executes. The global section is the deploying
    /// party's own and is not filtered here.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to.</param>
    /// <returns>Layer value of the level.</returns>
    private static LayerValue<string?> ReadSignalRClientPath(ConfigNode node, string path)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.TryRead<string>(path, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return LayerValue<string?>.None;
        }

        return node.Level is ConfigLevel.UiConfig && !CorePageHead.IsAllowedUrlScheme(value)
            ? LayerValue<string?>.None
            : LayerValue<string?>.Set(value);
    }

    /// <summary>
    /// Member of the OIDC client entry the application states its login-initiation address at. It is
    /// written out rather than taken from a property of the entry's type: the entry carries no member
    /// of that name, and the address of a level is a contract of the deployment (SPEC-012 §10.6).
    /// </summary>
    private const string ClientInitiateLoginUriMember = "InitiateLoginUri";

    /// <summary>
    /// The client's own address for STARTING a sign-in — <c>initiate_login_uri</c> of OIDC Dynamic
    /// Client Registration 1.0 §2, the HTTPS URL a third party sends the user to when it wants this
    /// client to sign them in. The sign-in window offers it as the way out of an expired page: the
    /// relying party's own entry point is a better place to land than a repeat of a request whose
    /// <c>state</c> and <c>nonce</c> the relying party may already have written off. The callback's
    /// "sign-in session expired" page offers the same address, and it is the only one that page has:
    /// there is no request behind a callback to repeat.
    /// <para>
    /// The level is the APPLICATION and only it, because the value belongs to one client by
    /// definition — it is a field of that client's metadata. A global section stating it would state
    /// one client's entry point for all of them. There is nothing for a <c>ui_config</c> record to
    /// say either: the record is selectable per request, and a per-request navigation target on a
    /// terminal page is a redirect surface with no validation behind it.
    /// </para>
    /// <para>
    /// The declaration is a plain text key: whether the stated value is an ABSOLUTE URL of an allowed
    /// scheme is decided where it is consumed, together with the deployment's environment (a plain
    /// <c>http</c> entry point is a development arrangement) and with the client it belongs to, which
    /// is what the log entry about a refused value has to name.
    /// </para>
    /// </summary>
    public static ConfigKey<string?> ClientInitiateLoginUri { get; } = Declared
        .Text("Client.InitiateLoginUri")
        .At(ConfigLevel.Application, ClientInitiateLoginUriMember)
        .BlankIsUnstated()
        .Declare();

    /// <summary>
    /// Address of the core level of the claims completion form switch inside the global section.
    /// </summary>
    private const string CoreClaimCompletionFormEnabledAddress = "Veriqa:ClaimCompletion:FormEnabled";

    /// <summary>
    /// Whether the claims completion form of the product is on when neither level states otherwise.
    /// </summary>
    private const bool ShippedClaimCompletionFormEnabled = true;

    /// <summary>
    /// Whether the claims completion form of Veriqa is shown to the users of an application
    /// (SPEC-041 C34, R16). A relying party with a profile completion form of its own (Keycloak, Logto)
    /// switches it off, so that the user does not fill two forms in a row: with the form off, the
    /// requirement set of a transaction records every claim but <c>phone_number</c> and <c>email</c> as
    /// <c>IfAvailable</c> — such a claim is issued when the channel provides it and never blocks the
    /// sign-in. A plain value over the core and application levels, each overriding the one below it
    /// freely: which form the user sees is the integrator's choice. The core level always states a value
    /// — the one of the global section, or the one the product ships with (the form is on).
    /// </summary>
    public static ConfigKey<bool> ClaimCompletionFormEnabled { get; } = Declared
        .Of<bool>("ClaimCompletion.FormEnabled")
        .At(ConfigLevel.Core, CoreClaimCompletionFormEnabledAddress)
        .At(ConfigLevel.Application, nameof(OidcClientOptions.ClaimCompletionFormEnabled))
        .Default(ShippedClaimCompletionFormEnabled)
        .Declare();

    /// <summary>
    /// Address of the core level of the claims completion form surface inside the global section.
    /// </summary>
    private const string CoreClaimCompletionFormSurfaceAddress = "Veriqa:ClaimCompletion:FormSurface";

    /// <summary>
    /// Where the claims completion form is shown when neither level states otherwise.
    /// </summary>
    private const ClaimFormSurface ShippedClaimCompletionFormSurface = ClaimFormSurface.Auto;

    /// <summary>
    /// Where the claims completion form of an application is shown (SPEC-041 C34, L53): chosen by the path
    /// of the sign-in, always the sign-in page, the device that confirmed the sign-in with the sign-in page
    /// as the fallback, or that device only. The value is read once, when the web step of a transaction
    /// begins. A plain value over the core and application levels, each overriding the one below it
    /// freely, like <see cref="ClaimCompletionFormEnabled"/>; the core level always states a value — the
    /// one of the global section, or the one the product ships with (<see cref="ClaimFormSurface.Auto"/>).
    /// </summary>
    public static ConfigKey<ClaimFormSurface> ClaimCompletionFormSurface { get; } = Declared
        .Of<ClaimFormSurface>("ClaimCompletion.FormSurface")
        .At(ConfigLevel.Core, CoreClaimCompletionFormSurfaceAddress)
        .At(ConfigLevel.Application, nameof(OidcClientOptions.ClaimCompletionFormSurface))
        .Default(ShippedClaimCompletionFormSurface)
        .Declare();

    /// <summary>
    /// Sign-in page title supplied by the <c>ui_config</c> record. There is no global section behind
    /// it: an unset level leaves the renderer with its own text. Either way the text reaching the page
    /// is a Natural Key resolved through the host locale files — a stated value does not bypass
    /// localization, it replaces the key that goes into it.
    /// </summary>
    public static ConfigKey<string?> PageTitle { get; } = Declared
        .Text("AuthPage.Title")
        .At(ConfigLevel.UiConfig, "title")
        .Cached(ConfigCachePolicy.ExternalStore)
        .BlankIsUnstated()
        .Declare();

    /// <summary>
    /// Sign-in page instruction supplied by the <c>ui_config</c> record; see <see cref="PageTitle"/>
    /// for why the key declares no core level.
    /// </summary>
    public static ConfigKey<string?> PageInstruction { get; } = Declared
        .Text("AuthPage.Instruction")
        .At(ConfigLevel.UiConfig, "instruction")
        .Cached(ConfigCachePolicy.ExternalStore)
        .BlankIsUnstated()
        .Declare();

    /// <summary>
    /// Wording of the stated instruction for a page that hides the QR code — a phone under
    /// <c>QrVisibility: DesktopOnly</c>, any device under <c>Never</c> — supplied by the <c>ui_config</c>
    /// record. Unstated — that wording takes <see cref="PageInstruction"/>, and without it the page's own
    /// wording. See <see cref="PageTitle"/> for why the key declares no core level.
    /// </summary>
    public static ConfigKey<string?> PageInstructionWithoutQr { get; } = Declared
        .Text("AuthPage.InstructionWithoutQr")
        .At(ConfigLevel.UiConfig, "instruction_without_qr")
        .Cached(ConfigCachePolicy.ExternalStore)
        .BlankIsUnstated()
        .Declare();

    /// <summary>
    /// Wording of the stated instruction for a page that shows the QR code and hides the channel
    /// buttons, supplied by the <c>ui_config</c> record. Unstated — that wording takes
    /// <see cref="PageInstruction"/>, and without it the page's own wording. See <see cref="PageTitle"/>
    /// for why the key declares no core level.
    /// </summary>
    public static ConfigKey<string?> PageInstructionWithoutButton { get; } = Declared
        .Text("AuthPage.InstructionWithoutButton")
        .At(ConfigLevel.UiConfig, "instruction_without_button")
        .Cached(ConfigCachePolicy.ExternalStore)
        .BlankIsUnstated()
        .Declare();

    /// <summary>
    /// Channel codes shown on the sign-in page, from the <c>ui_config</c> record (SPEC-002 §4.6).
    /// The value can only NARROW the global display set/order — the narrowing itself is applied by the
    /// channel display service, which owns the global set (CFG-211).
    /// </summary>
    public static ConfigKey<IReadOnlyList<string>> PageChannels { get; } = Declared
        .Of<IReadOnlyList<string>>("ChannelDisplay.Channels")
        .At(ConfigLevel.UiConfig, "channels")
        .Cached(ConfigCachePolicy.ExternalStore)
        .Declare();

    /// <summary>
    /// Member of the global channel-display section carrying the MODE, as a deployment spells it —
    /// written out for the reason <see cref="ShowQrCodeMember"/> is: the section is no longer bound
    /// into an object for this setting, and the address of a level is a contract of the deployment.
    /// </summary>
    private const string ChannelDisplayModeMember = "Mode";

    /// <summary>
    /// The channel display mode the product SHIPS — the value the core level yields where the global
    /// section states none. It is the answer a deployment that configured nothing was already served,
    /// and declaring it is what lets the schema of the deployment print it.
    /// <para>
    /// Internal rather than private because the ONE fallback of the reader — the case where no level
    /// of the chain spoke at all — must name this constant instead of restating the mode: a second
    /// spelling of the shipped answer would drift away from the key the day this one changes.
    /// </para>
    /// </summary>
    internal const ChannelDisplayMode ShippedChannelDisplayMode = ChannelDisplayMode.Multichannel;

    /// <summary>
    /// Channel display mode: the GLOBAL mode of the deployment, which a <c>ui_config</c> record may
    /// replace for the requests it serves (SPEC-012 §9 — owned by the tenant, overridden by
    /// <c>ui_config</c>; in a self-hosted installation the tenant IS the core, CFG-202). The shape is
    /// the one <see cref="CorePageBrandingConfigKeys.Theme"/> has: a core level at the address of the global section, a
    /// <c>ui_config</c> level above it, and the shipped answer declared at the key.
    /// <para>
    /// The core level is what makes the mode a setting of the mechanism on EVERY level it lives at,
    /// rather than on the record alone. Before it, the global mode was an input member of the bound
    /// section, so an unknown spelling there stopped the host inside the binder — the very outcome the
    /// declared policy of this key (SkipStep, by the default domain of an enum) says it must not have.
    /// </para>
    /// </summary>
    public static ConfigKey<ChannelDisplayMode> PageChannelDisplayMode { get; } = Declared
        .Of<ChannelDisplayMode>("ChannelDisplay.Mode")
        .At(ConfigLevel.Core, ChannelDisplayOptions.SectionName + ":" + ChannelDisplayModeMember)
        .At(ConfigLevel.UiConfig, "channel_display_mode")
        .Cached(ConfigCachePolicy.ExternalStore)
        .Default(ShippedChannelDisplayMode)
        .Declare();

    /// <summary>
    /// Transaction lifetime override (sec) from the <c>ui_config</c> record; an unset level keeps the
    /// lifetime configured for the transaction engine.
    /// </summary>
    public static ConfigKey<int> TransactionTtlSeconds { get; } = Declared
        .Of<int>("Transaction.TtlSeconds")
        .At(ConfigLevel.UiConfig, "ttl_seconds")
        .Cached(ConfigCachePolicy.ExternalStore)
        .Declare();

    /// <summary>
    /// Time zone the moments of a transaction are shown in, supplied by the <c>ui_config</c> record —
    /// the deployment's own default for the relying parties that state none themselves. An unset level
    /// means the zone stays unknown, and a moment is then shown in UTC with the marker that says so.
    /// <para>
    /// A value the deployment states but this installation cannot resolve into a zone leaves the level
    /// unset (see <see cref="ReadDefaultTimeZone"/>): the wrong zone is not a lesser evil than an
    /// unknown one — a moment shown in it reads as another moment, and silently.
    /// </para>
    /// </summary>
    public static ConfigKey<string?> DefaultTimeZone { get; } = Declared
        .Text("Transaction.DefaultTimeZone")
        .At(ConfigLevel.UiConfig, "default_time_zone")
        .Cached(ConfigCachePolicy.ExternalStore)
        .Parse(static (node, path, _) => ReadDefaultTimeZone(node, path))

        // Whether a stated identifier is a zone THIS installation can convert to is a judgement over
        // the platform's zone database, not something the pair in the record states. A walk reads the
        // pair and cannot make that judgement, so the report names the pairs of this key as standing
        // outside it instead of stating a zone the resolution would have dropped.
        .NotWalked()
        .Declare();

    /// <summary>
    /// Layer value of the default time zone: a stated identifier the platform resolves, and nothing
    /// else. An unresolvable one is dropped here rather than at the entry, so every reader of the key
    /// gets a zone that can actually be converted to.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to.</param>
    /// <returns>Layer value of the level.</returns>
    private static LayerValue<string?> ReadDefaultTimeZone(ConfigNode node, string path)
    {
        ArgumentNullException.ThrowIfNull(node);

        var stated = StatedText(node, path);

        return stated.HasValue && !RequestTimeZone.IsResolvable(stated.Value)
            ? LayerValue<string?>.None
            : stated;
    }

    /// <summary>
    /// Core-level value of the compatibility quirks: no relaxations. The default is "disabled", and the
    /// only way past it is the gate.
    /// </summary>
    private static readonly IReadOnlyList<string> NoQuirks = new List<string>().AsReadOnly();

    /// <summary>
    /// Per-client set of enabled compatibility quirks (CFG-212, protective ceiling). The core level always
    /// supplies a value — the EMPTY set — and carries the deployment gate; the application level supplies
    /// the client's own set. "Stricter" for a set of relaxations is the INTERSECTION (see
    /// <see cref="IntersectQuirks"/>): with the gate closed the client's set collapses to empty, with the
    /// gate open the resolver takes the client's set as is.
    /// <para>
    /// The core level is addressed at the GATE and not at a set of its own, because a set of its own is
    /// not a thing the deployment states: the core value is unconditionally empty, and the one member of
    /// the global section this key reads is the permission that may open the level above it. Hence the
    /// parse hook — the two levels of this key read different shapes (see <see cref="ReadQuirks"/>).
    /// </para>
    /// <para>
    /// The APPLICATION level stands over TWO members of the entry: the keys the entry lists outright
    /// (the declared address) and the <see cref="OidcClientOptions.CompatibilityProfile"/> that names a
    /// set of them. A profile is a shorthand for the very same keys, so it has to be read where the
    /// value of this level is read — reading it anywhere else would leave the resolver, and therefore
    /// the relaxation itself, blind to it while the entry looked configured.
    /// </para>
    /// </summary>
    public static ConfigKey<IReadOnlyList<string>> ClientCompatibilityQuirks { get; } = Declared
        .Of<IReadOnlyList<string>>("OpenIddict.ClientCompatibilityQuirks")
        .At(
            ConfigLevel.Core,
            OidcClientsOptions.SectionName + ":"
            + nameof(OidcClientsOptions.CompatibilityQuirksAllowApplicationOverride))
        .At(ConfigLevel.Application, nameof(OidcClientOptions.CompatibilityQuirks))
        .ProtectiveCeiling(IntersectQuirks)
        .Parse(static (node, path, _) => ReadQuirks(node, path))

        // The two levels of this key read different SHAPES — the core one a gate, the application one
        // a set — so a walk reading the address of a level gets the wrong thing at one of them and
        // could not reproduce the value at either. The report names the pairs.
        .NotWalked()
        .Declare();

    /// <summary>
    /// Layer value of one level of the compatibility quirks. The CORE level is stated unconditionally,
    /// which is load-bearing rather than stylistic: the resolver takes the first level that has a value
    /// as its base, so a core level returning "no value" would make the client's own set the base and
    /// apply it while bypassing the gate entirely. The gate itself is read off the member the address
    /// names — the permission stated next to the clients it governs.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to.</param>
    /// <returns>Layer value of the level.</returns>
    private static LayerValue<IReadOnlyList<string>> ReadQuirks(ConfigNode node, string path)
    {
        if (node.Level is ConfigLevel.Core)
        {
            return LayerValue<IReadOnlyList<string>>.Set(
                NoQuirks,
                node.TryRead<bool>(path, out var open) && open);
        }

        var listed = node.TryRead<IReadOnlyList<string>>(path, out var quirks) ? quirks : null;
        var fromProfile = ReadProfileQuirks(node);

        // The level cedes only when it states NEITHER member: a set assembled from a profile alone is a
        // value of this level just as much as one written out key by key.
        if (listed is null && fromProfile.Count is 0)
        {
            return LayerValue<IReadOnlyList<string>>.None;
        }

        if (fromProfile.Count is 0)
        {
            return LayerValue<IReadOnlyList<string>>.Set(listed!);
        }

        // Ordinal, like the registries: a differently-cased spelling is not the same key, and the
        // client options validator reports it instead of this union merging it away.
        var effective = new HashSet<string>(listed ?? [], StringComparer.Ordinal);
        effective.UnionWith(fromProfile);

        return LayerValue<IReadOnlyList<string>>.Set(effective.ToList().AsReadOnly());
    }

    /// <summary>
    /// Quirk keys the entry names through its compatibility profile — the second member the application
    /// level of <see cref="ClientCompatibilityQuirks"/> stands over.
    /// </summary>
    /// <param name="node">Subtree of the client entry.</param>
    /// <returns>Keys of the named profile; an empty set when the entry names none or names an unknown one.</returns>
    /// <remarks>
    /// An unknown name yields nothing rather than throwing, exactly as <see cref="ClientCompatibilityProfiles.Expand"/>
    /// does: it is the client options validator that reports the name and fails the start, and throwing
    /// here would replace that message with a less precise one. Nothing is relaxed in the meantime — an
    /// unknown name expands to no keys at all.
    /// </remarks>
    private static IReadOnlySet<string> ReadProfileQuirks(ConfigNode node) =>
        node.TryRead<string>(nameof(OidcClientOptions.CompatibilityProfile), out var profile)
        && !string.IsNullOrWhiteSpace(profile)
            ? ClientCompatibilityProfiles.Expand(profile)
            : FrozenSet<string>.Empty;

    /// <summary>
    /// Picks the stricter of two quirk sets, i.e. their intersection: fewer relaxations = more protection.
    /// The result is a subset of BOTH arguments, which is what makes the closed gate work — the core level
    /// contributes the empty set, and an intersection with it is empty regardless of what the client listed.
    /// Elements are compared Ordinal; duplicates in the lower level collapse to a single entry.
    /// </summary>
    /// <param name="upper">Value of the upper (owning) level.</param>
    /// <param name="lower">Value of the lower (overriding) level.</param>
    /// <returns>Intersection of the two sets.</returns>
    private static IReadOnlyList<string> IntersectQuirks(IReadOnlyList<string> upper, IReadOnlyList<string> lower)
    {
        // The method intersects two relaxation sets: only a key allowed by both levels survives.

        if (upper.Count is 0 || lower.Count is 0)
        {
            return [];
        }

        var allowed = new HashSet<string>(upper, StringComparer.Ordinal);

        return lower
            .Where(allowed.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Layer value of a TEXT setting: a level that states nothing at the address — or states a blank
    /// text there — states no value at all, and the resolution moves on to the level below. It is the
    /// same feature detection the handwritten bindings of these keys performed over the record itself:
    /// a half-finished edit that left an empty string behind is not an override.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to.</param>
    /// <returns>Layer value of the level.</returns>
    private static LayerValue<string?> StatedText(ConfigNode node, string path) =>
        node.TryRead<string>(path, out var value) && !string.IsNullOrWhiteSpace(value)
            ? LayerValue<string?>.Set(value)
            : LayerValue<string?>.None;

    /// <summary>
    /// Declarations of the keys of this registry — every one of them is stated AS A CHAIN. They enter
    /// the schema through the registrar that reads the catalogs, which is also what binds every level
    /// of them: a key is declared once, in the declaration itself.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;
}
