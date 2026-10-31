// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Messenger.Configuration;

/// <summary>
/// Canonical resolver keys owned by the Messenger channel (SPEC-012 §10.6). The channel owns the
/// CATALOG its keys are declared into and binds their core level itself
/// (<see cref="MessengerConfigKeyRegistrar"/>), so no shared registry has to list the channels the
/// product happens to ship.
/// <para>
/// The catalog is registered where the channel is COMPOSED — <c>AddMessenger()</c> — and that is what
/// carries the key into the schema of a deployment that added this channel and leaves it out of one
/// that did not. Declaring a key and binding a level of it are different acts (CFG-203): the
/// declaration lives here, the core-level getter in the registrar named above.
/// </para>
/// </summary>
public static class MessengerConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this channel — what its registration puts into the schema of
    /// the deployment. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Messenger tenant-credential group of the layer (<c>Channels.messenger.Credentials</c>).
    /// </summary>
    public static ConfigKey<MessengerTenantCredentials> Credentials { get; } =
        ChannelConfigKeys.CredentialKey<MessengerTenantCredentials>(Declared, ChannelTypes.Messenger);

    /// <summary>
    /// Canonical name of the key of the reply to a pressed button that names no transaction.
    /// </summary>
    public const string UnaddressedReplyEnabledKeyName =
        "Channels." + ChannelTypes.Messenger + "." + UnaddressedReplyEnabledMember;

    /// <summary>
    /// Member of the channel section the core level of that key is stated at.
    /// </summary>
    public const string UnaddressedReplyEnabledMember = "UnaddressedReplyEnabled";

    /// <summary>
    /// Whether a pressed button that names no transaction — Get Started pressed with no sign-in link
    /// behind it — is answered with the text telling the user how to go on. Switched on unless a
    /// level says otherwise; switched off, such a press gets no answer at all. The start by the code
    /// of the sign-in page is not governed by it.
    /// <para>
    /// A plain value over the core and the tenant levels, the tenant overriding the core: the Page
    /// the button belongs to is the tenant's, and so is the say on what it answers. In this shipping
    /// the core level is read from the channel section, and the tenant level is addressed by the
    /// identity of the key alone — a deployment that binds no source of that level leaves it unset.
    /// </para>
    /// </summary>
    public static ConfigKey<bool> UnaddressedReplyEnabled { get; } = Declared
        .Of<bool>(UnaddressedReplyEnabledKeyName)
        .At(ConfigLevel.Core, MessengerOptions.SectionName + ":" + UnaddressedReplyEnabledMember)
        .AtIdentity(ConfigLevel.Tenant)
        .Default(true)
        .Declare();

    /// <summary>
    /// Canonical name of the key choosing what the sign-in link names the Page by.
    /// </summary>
    public const string DeepLinkByPageIdKeyName =
        "Channels." + ChannelTypes.Messenger + "." + DeepLinkByPageIdMember;

    /// <summary>
    /// Member of the channel section the core level of that key is stated at.
    /// </summary>
    public const string DeepLinkByPageIdMember = "DeepLinkByPageId";

    /// <summary>
    /// Whether the sign-in link names the Page by its identifier (<c>m.me/&lt;PageId&gt;</c>) instead
    /// of its username (<c>m.me/&lt;PageName&gt;</c>). Switched off unless a level says otherwise:
    /// the link by the username is the form Meta documents. Switched on, the link is built from
    /// <c>PageId</c> and the username of the Page is not needed at all — for a Page whose link by
    /// the username does not open the thread in the Messenger app.
    /// <para>
    /// A plain value over the core and the tenant levels, the tenant overriding the core: the Page
    /// the link leads to is the tenant's, and so is the say on how it is named. In this shipping
    /// the core level is read from the channel section, and the tenant level is addressed by the
    /// identity of the key alone — a deployment that binds no source of that level leaves it unset.
    /// </para>
    /// </summary>
    public static ConfigKey<bool> DeepLinkByPageId { get; } = Declared
        .Of<bool>(DeepLinkByPageIdKeyName)
        .At(ConfigLevel.Core, MessengerOptions.SectionName + ":" + DeepLinkByPageIdMember)
        .AtIdentity(ConfigLevel.Tenant)
        .Default(false)
        .Declare();

    /// <summary>
    /// Declarations of this channel — what <c>AddMessenger()</c> registers into the container, and what
    /// the schema of a deployment carries because of it.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;
}
