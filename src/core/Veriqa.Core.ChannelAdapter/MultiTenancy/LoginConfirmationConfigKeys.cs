// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// Registry of the canonical resolver key of login confirmation (SPEC-012 §4.4, §10.6): the
/// confirmation mode (<see cref="LoginConfirmation"/>). It is resolved as a plain value over the core,
/// tenant and application levels, each level overriding the one below it freely, and it is cut by the
/// channel: a level states the mode of a single channel in a per-channel map next to its plain member.
/// There is no second precedence mechanism (anti-fork CFG-202).
/// <para>
/// The WORDING of a confirmation is not a key of this registry: it is a message, so a level states it
/// as the template ladder of that message (SPEC-036 TPL-116) and it reaches the user through the same
/// resolution as the product's own wording — never as a text handed past the message mechanism.
/// </para>
/// <para>
/// The key is declared in ChannelAdapter (not AuthServer), since its consumers live here: the
/// confirmation orchestrator in the pipeline
/// (<see cref="Pipeline.IEffectiveConfirmationSurfaceResolver"/>) and the channel adapters that send
/// the confirmation prompt — and ChannelAdapter cannot reference AuthServer (a reverse dependency).
/// Each key states its levels WITH THE ADDRESS OF EACH, and the ONE registrar of the deployment
/// (<c>PathConfigKeyRegistrar</c>) declares the key and binds every one of them: the core level inside
/// the global section, the levels above it inside the record of their own level, which the auth server
/// fetches — declaring a key and reading the record of a level are different owners, which is exactly
/// what the contract allows (CFG-203).
/// </para>
/// </summary>
public static class LoginConfirmationConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this registry — what the one registrar of the deployment declares
    /// and binds. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Canonical name of the login confirmation key.
    /// </summary>
    public const string LoginConfirmationKeyName = "LoginConfirmation.Mode";

    /// <summary>
    /// Member the confirmation surface is stated at in the record of a level above the core — the
    /// record of a tenant and the OIDC client entry of an application alike. It is written as text
    /// rather than taken from the entry's type: the entry belongs to the auth server, and this contour
    /// does not reference it (a reverse dependency).
    /// </summary>
    private const string ClientLoginConfirmationMode = "LoginConfirmationMode";

    /// <summary>
    /// Suffix that turns the address of the plain member of a level into the address of the level's
    /// per-channel map: <c>Veriqa:LoginConfirmation:Mode</c> → <c>Veriqa:LoginConfirmation:ModeByChannel</c>
    /// in the global section, <c>LoginConfirmationMode</c> → <c>LoginConfirmationModeByChannel</c> in the
    /// record of a tenant and in the client entry. The plain member is spelled differently on the core
    /// level and above it, so the map is named by the address of the level rather than by one member
    /// name replacing the last segment of it.
    /// </summary>
    private const string ByChannelSuffix = "ByChannel";

    /// <summary>
    /// Domain of the mode — the members of the enumeration, on ANY level and in any entry of a
    /// per-channel map. A number that names no member leaves its step unset, and the mode comes from the
    /// next step or the next level. It stands above the key, since the initializers of this class run
    /// in the order they are written. The policy stays the default one (SPEC-012 CFG-240): a value
    /// outside the domain is a degradation and not a reason to stop the host.
    /// </summary>
    private static readonly ConfigValueDomain<LoginConfirmationMode> ModeDomain = new(
        static value => Enum.IsDefined(value),
        "one of: " + string.Join(", ", Enum.GetNames<LoginConfirmationMode>()),
        $"{LoginConfirmationOptions.SectionName}:{nameof(LoginConfirmationOptions.Mode)} (or "
        + $"{nameof(LoginConfirmationOptions.ModeByChannel)}) of the global section, or "
        + $"{ClientLoginConfirmationMode} (or {ClientLoginConfirmationMode}{ByChannelSuffix}) of a tenant "
        + "record or a client entry");

    /// <summary>
    /// Login confirmation mode FOR A CHANNEL (CFG-040…042). The channel is a dimension of the key
    /// (narrowing): a level states the mode of a single channel in its per-channel map — for example
    /// <c>Veriqa:LoginConfirmation:ModeByChannel:email</c> in the global section, or
    /// <c>LoginConfirmationModeByChannel:email</c> in the client entry of an application — and the mode
    /// of every other channel in its plain member. Both steps are exhausted inside that level before the
    /// resolution drops to the level below (CFG-236), so a level outranks the dimension (CFG-238): an
    /// application that states the plain member alone decides the mode of every channel, whatever map
    /// the core level carries.
    /// <para>
    /// The key declares no answer of its own for a deployment in which no level states the mode: the
    /// consumer reads such a result as the mode the product ships with
    /// (<see cref="LoginConfirmationOptions.Mode"/> of a default-constructed options object).
    /// </para>
    /// </summary>
    public static ConfigKey<LoginConfirmationMode> LoginConfirmation { get; } = Declared
        .Of<LoginConfirmationMode>(LoginConfirmationKeyName)
        .Narrowing(ConfigDimensionNames.Channel)
        .At(
            ConfigLevel.Core,
            LoginConfirmationOptions.SectionName + ":" + nameof(LoginConfirmationOptions.Mode))
        .At(ConfigLevel.Tenant, ClientLoginConfirmationMode)
        .At(ConfigLevel.Application, ClientLoginConfirmationMode)
        .Domain(ModeDomain)
        .Parse(static (node, path, point) => ReadMode(node, path, point))
        .Stated(static (node, path) => StatedAt(node, path))
        .Declare();

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Layer value of one step of the mode's chain at a level: the step that addresses the channel
    /// answers from the entry of the level's per-channel map that names the channel, the step that
    /// addresses none from the plain member the address names. The two are different MEMBERS of the
    /// record, which an address cannot say, so the key states it here. Whether the setting admits the
    /// value is decided by <see cref="ModeDomain"/>, applied by the mechanism to every binding alike.
    /// <para>
    /// A value the record cannot be read as is not caught here either: the mechanism reports it and
    /// leaves the step that read it unset (SPEC-012 CFG-246, CFG-240). The map is read entry by entry,
    /// so an entry that cannot be read costs that entry alone — the mode of its channel comes from the
    /// plain member of the same level or from the level below (CFG-270), and the other entries stand —
    /// while a plain member that cannot be read leaves both steps of its level unset.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to — the plain mode.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    private static LayerValue<LoginConfirmationMode> ReadMode(
        ConfigNode node,
        string path,
        ConfigDimensionValues point) =>
        DimensionedConfigValue.Layer<LoginConfirmationMode>(
            node,
            point,
            ConfigDimensionNames.Channel,
            ByChannelAddress(path),
            path);

    /// <summary>
    /// Addresses one record of a level STATES the mode at: the plain member the key's address names, and
    /// one address per entry of the level's per-channel map. Both are steps of the same key's fallback
    /// chain (SPEC-012 CFG-237), so both are stated values of one setting. The entries are asked for by
    /// NAME rather than read as a map: one entry the walk cannot read must not take the report of the
    /// entries beside it down with it.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address of the level — the plain member.</param>
    /// <returns>Addresses the record states the setting at.</returns>
    private static IEnumerable<string> StatedAt(ConfigNode node, string path)
    {
        yield return path;

        var byChannel = ByChannelAddress(path);

        foreach (var channelType in node.Children(byChannel))
        {
            yield return $"{byChannel}{ConfigNode.PathSeparator}{channelType}";
        }
    }

    /// <summary>
    /// Address of the per-channel map of a level — the address of the level's plain member followed by
    /// <see cref="ByChannelSuffix"/>, on every level alike.
    /// </summary>
    /// <param name="path">Address of the level — the plain member.</param>
    /// <returns>Address of the per-channel map.</returns>
    private static string ByChannelAddress(string path) => path + ByChannelSuffix;
}
