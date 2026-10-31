// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// Keys of what the core says about the chat of a channel: whether the pages offer the spare start code
/// of the channel (<see cref="SpareStartCodeEnabled"/>) and the name the chat is shown under on the pages
/// and in the text of a confirmation question (<see cref="BotName"/>). Both are resolved through the
/// shared resolver; no precedence of their own is built here.
/// <para>
/// The keys belong to the core and not to an adapter: the pages draw the code block and name the chat
/// for any channel — a built-in one, a third-party one, an HTTP channel — so an adapter declares the
/// capability and the integrator switches it. The channel is the identity dimension of both, and the
/// levels are those of the credentials of a channel — the core and the tenant: the chat belongs to the
/// owner of the bot. The application level is deliberately absent.
/// </para>
/// </summary>
internal static class ChannelChatConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this registry — what the one registrar of the deployment declares
    /// and binds. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Canonical name of the key of the spare start code.
    /// </summary>
    public const string SpareStartCodeEnabledKeyName = "Channels.SpareStartCodeEnabled";

    /// <summary>
    /// Canonical name of the key of the name of the chat.
    /// </summary>
    public const string BotNameKeyName = "Channels.BotName";

    /// <summary>
    /// Member of a channel section switching the spare start code of that channel.
    /// </summary>
    private const string SpareStartCodeEnabledMember = "SpareStartCodeEnabled";

    /// <summary>
    /// Member of a channel section carrying the name of the chat of that channel.
    /// </summary>
    private const string BotNameMember = "BotName";

    /// <summary>
    /// Prefix of the core-level address of a member of a channel section, with the channel dimension in it.
    /// </summary>
    private const string CoreChannelMemberPrefix =
        ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName
        + ":{" + ConfigDimensionNames.Channel + "}:";

    /// <summary>
    /// Whether the spare start code of a channel is prepared and shown: the message a person sends into
    /// the chat when the link of the channel opened it and nothing started there. It is read only for a
    /// channel whose link carries the transaction; a channel whose only start is the code is not
    /// switched by it. Switched off, no code is issued on behalf of the channel and no page shows its
    /// block. On unless a level says otherwise, so a deployment that states nothing keeps the block.
    /// </summary>
    public static ConfigKey<bool> SpareStartCodeEnabled { get; } = Declared
        .Of<bool>(SpareStartCodeEnabledKeyName)
        .Identity(ConfigDimensionNames.Channel)
        .At(ConfigLevel.Core, CoreChannelMemberPrefix + SpareStartCodeEnabledMember)
        .AtIdentity(ConfigLevel.Tenant)
        .Default(true)
        .Declare();

    /// <summary>
    /// Name the chat of a channel is shown under in the texts of the pages and of a confirmation question
    /// (<see cref="UI.ChannelBotNames"/>), written the way the person sees it — it is shown as stated,
    /// with nothing put in front, and is not localized. A level that states one overrides the name the
    /// adapter of the channel reports: the integrator sees what the chat is called on the side of the user.
    /// No level stating one leaves the name to the adapter.
    /// </summary>
    public static ConfigKey<string?> BotName { get; } = Declared
        .Text(BotNameKeyName)
        .Identity(ConfigDimensionNames.Channel)
        .At(ConfigLevel.Core, CoreChannelMemberPrefix + BotNameMember)
        .AtIdentity(ConfigLevel.Tenant)
        .BlankIsUnstated()
        .Declare();

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;
}
