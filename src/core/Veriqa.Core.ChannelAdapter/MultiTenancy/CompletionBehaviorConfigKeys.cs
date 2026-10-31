// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// Registry of the canonical resolver keys of the post-completion behavior (SPEC-012 §4.9, §10.6): the
/// action a channel performs after a sign-in is completed (<see cref="Action"/>) and what the actions
/// with buttons show — the items of the menu (<see cref="MenuItems"/>), the address and the label of
/// the redirect button (<see cref="RedirectUrl"/>, <see cref="RedirectButtonText"/>). Each is resolved
/// as a plain value over the core, tenant and application levels — each level overrides the one below
/// it freely; the action, the items and the address are cut by the channel. There is no second
/// precedence mechanism (anti-fork CFG-202).
/// <para>
/// The keys are declared in ChannelAdapter for the same reason the login confirmation key is: their
/// consumer lives in the pipeline of this contour, next to the channel facts the resolved action is
/// checked against. The <c>ui_config</c> level is not declared: the settings are behavior and not
/// branding, so a record chosen by a request parameter has no say in them.
/// </para>
/// </summary>
public static class CompletionBehaviorConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this registry — what the one registrar of the deployment declares
    /// and binds. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Section the settings are stated in inside the record of a level above the core — the record of a
    /// tenant and the OIDC client entry of an application alike. It is written as text rather than
    /// taken from the entry's type: the entry belongs to the auth server, and this contour does not
    /// reference it (a reverse dependency).
    /// </summary>
    private const string ClientSection = "CompletionBehavior";

    /// <summary>
    /// What the product ships the section with. The shipped values are taken off the options class
    /// rather than restated here, so the two cannot drift.
    /// </summary>
    private static readonly CompletionBehaviorOptions Shipped = new();

    /// <summary>
    /// Domain of the action — the members of the enumeration, on ANY level. It is declared once, on
    /// the key, and applied by the resolution mechanism wherever the key is bound: a number that names
    /// no member leaves its step unset and the action comes from the next step or the next level. It
    /// stands above the key, since the initializers of this class run in the order they are written.
    /// <para>
    /// The policy stays the default one (SPEC-012 CFG-240): a value outside the domain is a
    /// degradation and not a reason to stop the host.
    /// </para>
    /// </summary>
    private static readonly ConfigValueDomain<CompletionBehaviorAction> ActionDomain = new(
        static value => Enum.IsDefined(value),
        "one of: " + string.Join(", ", Enum.GetNames<CompletionBehaviorAction>()),
        $"{CompletionBehaviorOptions.SectionName}:{nameof(CompletionBehaviorOptions.Action)} (or "
        + $"{nameof(CompletionBehaviorOptions.ActionByChannel)}) of the level stating it");

    /// <summary>
    /// Domain of the menu items: every item of the list carries a label and an action, and the
    /// action is written without white space at its edges — the one reading of the value the button
    /// address works by (<see cref="CompletionBehaviorButtonAddress.HasEdgeWhiteSpace"/>): an action
    /// padded with a space would otherwise turn a link into callback data without a word. One item
    /// outside the rule takes the WHOLE list of its step out of the domain — a menu shown with a hole
    /// in it is not what the level wrote. The policy stays the default one (SPEC-012 CFG-240).
    /// </summary>
    private static readonly ConfigValueDomain<IReadOnlyList<CompletionBehaviorMenuItem>> MenuItemsDomain = new(
        static items => items.All(static item =>
            item is not null
            && !string.IsNullOrWhiteSpace(item.Text)
            && !string.IsNullOrWhiteSpace(item.Action)
            && !CompletionBehaviorButtonAddress.HasEdgeWhiteSpace(item.Action)),
        $"a list of items, each with a non-blank {nameof(CompletionBehaviorMenuItem.Text)} and a non-blank "
        + $"{nameof(CompletionBehaviorMenuItem.Action)} written without white space at its edges",
        $"{CompletionBehaviorOptions.SectionName}:{nameof(CompletionBehaviorOptions.MenuItems)} (or "
        + $"{nameof(CompletionBehaviorOptions.MenuItemsByChannel)}) of the level stating it");

    /// <summary>
    /// Domain of the redirect address — a URL by the one rule of the button address
    /// (<see cref="CompletionBehaviorButtonAddress.IsUrl"/>). The policy stays the default one
    /// (SPEC-012 CFG-240): a value that is not a URL — one with white space at its edges included —
    /// leaves its step unset.
    /// </summary>
    private static readonly ConfigValueDomain<string?> RedirectUrlDomain = new(
        static value => value is not null && CompletionBehaviorButtonAddress.IsUrl(value),
        "an absolute URL written as scheme://..., without white space at its edges",
        $"{CompletionBehaviorOptions.SectionName}:{nameof(CompletionBehaviorOptions.RedirectUrl)} (or "
        + $"{nameof(CompletionBehaviorOptions.RedirectUrlByChannel)}) of the level stating it");

    /// <summary>
    /// Post-completion action FOR A CHANNEL (CFG-090, CFG-091). The channel is a dimension of the key
    /// (narrowing): a level states the action of a single channel in its <c>ActionByChannel</c> map
    /// and the action of every other channel in its <c>Action</c> member, and both steps are exhausted
    /// inside that level before the resolution drops to the level below (CFG-236). A level therefore
    /// outranks the dimension (CFG-238): an application that states the plain member alone decides the
    /// action of every channel, whatever map the core level carries.
    /// <para>
    /// The key declares no answer of its own for a deployment in which no level states the action:
    /// the consumer reads such a result as the action the product ships with
    /// (<see cref="CompletionBehaviorOptions.Action"/> of a default-constructed options object).
    /// </para>
    /// </summary>
    public static ConfigKey<CompletionBehaviorAction> Action { get; } = Declared
        .Of<CompletionBehaviorAction>("CompletionBehavior.Action")
        .Narrowing(ConfigDimensionNames.Channel)
        .At(
            ConfigLevel.Core,
            CompletionBehaviorOptions.SectionName + ":" + nameof(CompletionBehaviorOptions.Action))
        .At(ConfigLevel.Tenant, ClientSection + ":" + nameof(CompletionBehaviorOptions.Action))
        .At(ConfigLevel.Application, ClientSection + ":" + nameof(CompletionBehaviorOptions.Action))
        .Domain(ActionDomain)
        .Parse(static (node, path, point) => ReadAction(node, path, point))
        .Stated(static (node, path) =>
            StatedAt(node, path, nameof(CompletionBehaviorOptions.ActionByChannel)))
        .Declare();

    /// <summary>
    /// Items of the post-completion menu FOR A CHANNEL (CFG-094). The channel is a dimension of the
    /// key exactly as it is of <see cref="Action"/>: a level states the items of a single channel in
    /// its <c>MenuItemsByChannel</c> map and the items of every other channel in its
    /// <c>MenuItems</c> member, and a level outranks the dimension (CFG-238).
    /// <para>
    /// The key declares no answer of its own for a deployment in which no level states the items: the
    /// consumer reads such a result as no menu being configured.
    /// </para>
    /// </summary>
    public static ConfigKey<IReadOnlyList<CompletionBehaviorMenuItem>> MenuItems { get; } = Declared
        .Of<IReadOnlyList<CompletionBehaviorMenuItem>>("CompletionBehavior.MenuItems")
        .Narrowing(ConfigDimensionNames.Channel)
        .At(
            ConfigLevel.Core,
            CompletionBehaviorOptions.SectionName + ":" + nameof(CompletionBehaviorOptions.MenuItems))
        .At(ConfigLevel.Tenant, ClientSection + ":" + nameof(CompletionBehaviorOptions.MenuItems))
        .At(ConfigLevel.Application, ClientSection + ":" + nameof(CompletionBehaviorOptions.MenuItems))
        .Domain(MenuItemsDomain)
        .Parse(static (node, path, point) => ReadMenuItems(node, path, point))
        .Stated(static (node, path) =>
            StatedAt(node, path, nameof(CompletionBehaviorOptions.MenuItemsByChannel)))
        .Declare();

    /// <summary>
    /// Address of the post-completion redirect button FOR A CHANNEL (CFG-095). The channel is a
    /// dimension of the key exactly as it is of <see cref="Action"/>: a level states the address of a
    /// single channel in its <c>RedirectUrlByChannel</c> map and the address of every other channel
    /// in its <c>RedirectUrl</c> member, and a level outranks the dimension (CFG-238).
    /// <para>
    /// The key declares no answer of its own for a deployment in which no level states the address:
    /// the consumer reads such a result as no address being configured.
    /// </para>
    /// </summary>
    public static ConfigKey<string?> RedirectUrl { get; } = Declared
        .Text("CompletionBehavior.RedirectUrl")
        .Narrowing(ConfigDimensionNames.Channel)
        .At(
            ConfigLevel.Core,
            CompletionBehaviorOptions.SectionName + ":" + nameof(CompletionBehaviorOptions.RedirectUrl))
        .At(ConfigLevel.Tenant, ClientSection + ":" + nameof(CompletionBehaviorOptions.RedirectUrl))
        .At(ConfigLevel.Application, ClientSection + ":" + nameof(CompletionBehaviorOptions.RedirectUrl))
        .Domain(RedirectUrlDomain)
        .Parse(static (node, path, point) => ReadRedirectUrl(node, path, point))
        .Stated(static (node, path) =>
            StatedAt(node, path, nameof(CompletionBehaviorOptions.RedirectUrlByChannel)))
        .Declare();

    /// <summary>
    /// Label of the post-completion redirect button (CFG-095) — a Natural Key the consumer localizes
    /// by the locale of the recipient. A plain value over the core, tenant and application levels,
    /// with no dimension; a blank text states nothing. The core level always answers — with the
    /// label of its section, or with the one the product ships with.
    /// </summary>
    public static ConfigKey<string> RedirectButtonText { get; } = Declared
        .Of<string>("CompletionBehavior.RedirectButtonText")
        .At(
            ConfigLevel.Core,
            CompletionBehaviorOptions.SectionName + ":" + nameof(CompletionBehaviorOptions.RedirectButtonText))
        .At(ConfigLevel.Tenant, ClientSection + ":" + nameof(CompletionBehaviorOptions.RedirectButtonText))
        .At(
            ConfigLevel.Application,
            ClientSection + ":" + nameof(CompletionBehaviorOptions.RedirectButtonText))
        .BlankIsUnstated()
        .Default(Shipped.RedirectButtonText)
        .Declare();

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Layer value of one step of the action's chain at a level: the step that addresses the channel
    /// answers from the entry of the level's per-channel map that names the channel, the step that
    /// addresses none from the plain member the address names. The two are different MEMBERS of the
    /// record, which an address cannot say, so the key states it here. Whether the setting admits the
    /// value is decided by <see cref="ActionDomain"/>, applied by the mechanism to every binding alike.
    /// <para>
    /// A value the record cannot be read as is not caught here either: the mechanism reports it and
    /// leaves the step that read it unset (SPEC-012 CFG-246, CFG-240). The map is read entry by entry,
    /// so an entry that cannot be read costs that entry alone — the action of its channel comes from
    /// the plain member of the same level or from the level below, and the other entries stand — while
    /// a plain member that cannot be read leaves both steps of its level unset.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to — the plain action.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    private static LayerValue<CompletionBehaviorAction> ReadAction(
        ConfigNode node,
        string path,
        ConfigDimensionValues point) =>
        DimensionedConfigValue.Layer<CompletionBehaviorAction>(
            node,
            point,
            ConfigDimensionNames.Channel,
            ByChannelAddress(node, path, nameof(CompletionBehaviorOptions.ActionByChannel)),
            path);

    /// <summary>
    /// Layer value of one step of the menu items' chain at a level: the step that addresses the
    /// channel answers from the entry of the level's per-channel map that names the channel, the step
    /// that addresses none from the plain member the address names. An EMPTY list states nothing, the
    /// way a blank text does: the step yields no value and the items come from the next step or the
    /// next level. Whether the items are admitted is decided by <see cref="MenuItemsDomain"/>.
    /// <para>
    /// The map is read entry by entry (<see cref="DimensionedConfigValue.TryReadFor{TValue}"/>): an
    /// entry that cannot be read — not a list, or an item with a member the strict binding refuses —
    /// costs the step of its own channel alone, and that channel takes the plain member of the same
    /// level or the level below (SPEC-012 CFG-246, CFG-240), while the other entries stand. The two
    /// steps read different members and nothing else: the step of the channel does not read the plain
    /// list, so a plain list that cannot be read costs the step that addresses no channel alone.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to — the plain list of items.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    private static LayerValue<IReadOnlyList<CompletionBehaviorMenuItem>> ReadMenuItems(
        ConfigNode node,
        string path,
        ConfigDimensionValues point)
    {
        List<CompletionBehaviorMenuItem>? items;

        if (point.Values is { } addressed && addressed.ContainsKey(ConfigDimensionNames.Channel))
        {
            items = DimensionedConfigValue.TryReadFor<List<CompletionBehaviorMenuItem>>(
                node,
                point,
                ConfigDimensionNames.Channel,
                ByChannelAddress(node, path, nameof(CompletionBehaviorOptions.MenuItemsByChannel)),
                out var found)
                ? found
                : null;
        }
        else
        {
            items = node.TryRead<List<CompletionBehaviorMenuItem>>(path, out var plain) ? plain : null;
        }

        return items is { Count: > 0 }
            ? LayerValue<IReadOnlyList<CompletionBehaviorMenuItem>>.Set(items)
            : LayerValue<IReadOnlyList<CompletionBehaviorMenuItem>>.None;
    }

    /// <summary>
    /// Layer value of one step of the redirect address' chain at a level: the step that addresses the
    /// channel answers from the entry of the level's per-channel map that names the channel, the step
    /// that addresses none from the plain member the address names. A blank text states nothing.
    /// Whether the text is an address is decided by <see cref="RedirectUrlDomain"/>.
    /// <para>
    /// The map is read entry by entry: an entry that cannot be read as text — a subsection where the
    /// address belongs — costs the step of its own channel alone, and that channel takes the plain
    /// member of the same level or the level below (SPEC-012 CFG-246, CFG-240), while the other entries
    /// stand; a plain member that cannot be read leaves both steps of its level unset.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to — the plain address.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    private static LayerValue<string?> ReadRedirectUrl(ConfigNode node, string path, ConfigDimensionValues point) =>
        DimensionedConfigValue.Layer(
            node,
            point,
            ConfigDimensionNames.Channel,
            ByChannelAddress(node, path, nameof(CompletionBehaviorOptions.RedirectUrlByChannel)),
            path);

    /// <summary>
    /// Addresses one record of a level STATES a channel-cut setting at: the plain member the key's
    /// address names, and one address per entry of the level's per-channel map. Both are steps of the
    /// same key's fallback chain (SPEC-012 CFG-237), so both are stated values of one setting.
    /// <para>
    /// The entries are asked for by NAME rather than read as a map: a name is what an address is built
    /// of, and one entry the walk cannot read must not take the report of the entries beside it down
    /// with it.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address of the level — the plain member.</param>
    /// <param name="mapMember">Name of the per-channel map standing next to the plain member.</param>
    /// <returns>Addresses the record states the setting at.</returns>
    private static IEnumerable<string> StatedAt(ConfigNode node, string path, string mapMember)
    {
        yield return path;

        var byChannel = ByChannelAddress(node, path, mapMember);

        foreach (var channelType in node.Children(byChannel))
        {
            yield return $"{byChannel}{ConfigNode.PathSeparator}{channelType}";
        }
    }

    /// <summary>
    /// Address of a per-channel map inside the record of a level, next to the plain member the key's
    /// address names. The map is spelled the same way on every level the keys declare, so both
    /// spellings handed to the helper are that one name.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address of the level — the plain member.</param>
    /// <param name="mapMember">Name of the per-channel map standing next to the plain member.</param>
    /// <returns>Address of the per-channel map.</returns>
    private static string ByChannelAddress(ConfigNode node, string path, string mapMember) =>
        CorePageDesignNode.SiblingAddress(node, path, mapMember, mapMember);
}
