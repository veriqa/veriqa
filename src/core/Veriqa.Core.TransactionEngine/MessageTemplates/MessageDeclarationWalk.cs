// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.TransactionEngine.MessageTemplates;

/// <summary>
/// One node of a message declaration as the startup checks see it: the kind it belongs to, the point
/// of the axis ladder it stands at, and the address an operator has to go to in order to repair it.
/// </summary>
/// <param name="Kind">Message kind identifier.</param>
/// <param name="Point">Values of the axes the node is narrowed by (the kind included).</param>
/// <param name="Address">Address of the node as an operator reads it.</param>
/// <param name="Declaration">What the node states.</param>
/// <param name="Addressable">
/// Whether a resolution can reach the node at all. False when a group is nested out of the canonical
/// order of the groups, or twice: the address of a key enters the groups in one order and once each,
/// so nothing ever resolves to such a node whatever it states.
/// </param>
internal readonly record struct MessageDeclarationNode(
    string Kind,
    IReadOnlyDictionary<string, string> Point,
    string Address,
    MessageTemplateKindOptions Declaration,
    bool Addressable);

/// <summary>
/// The walk of a message declaration — the one place that knows how the nested axis groups of the
/// section correspond to the steps of the ladders the two keys declare
/// (<see cref="MessageTemplateConfigKeys"/>).
/// <para>
/// Both startup checks read the section through it: the one that checks the FORM of a declaration and
/// the one that checks a template ladder against its contract. Written twice, the two would part
/// company the moment an axis is added, and a group nobody walks is a declaration silently ignored.
/// </para>
/// </summary>
internal static class MessageDeclarationWalk
{
    /// <summary>
    /// Groups of a declaration node in the order the address nests them, with the dimension each one
    /// narrows by. The order is the address's own: a group deeper in this list is deeper in the
    /// document.
    /// </summary>
    private static readonly (string Group, string Dimension)[] Groups =
    [
        (MessageTemplateConfigKeys.ByTypeGroup, MessageTemplateConfigKeys.TransactionTypeDimensionName),
        (MessageTemplateConfigKeys.ByActionGroup, MessageTemplateConfigKeys.ActionTypeDimensionName),
        (MessageTemplateConfigKeys.BySurfaceGroup, MessageTemplateConfigKeys.SurfaceDimensionName),
        (MessageTemplateConfigKeys.ByChannelGroup, MessageTemplateConfigKeys.ChannelDimensionName),
        (MessageTemplateConfigKeys.ByLinkPurposeGroup, MessageTemplateConfigKeys.LinkPurposeDimensionName),
        (MessageTemplateConfigKeys.ByDeviceGroup, MessageTemplateConfigKeys.DeviceTypeDimensionName)
    ];

    /// <summary>
    /// The canonical nesting order, as a report to an operator names it — read off the one list above,
    /// so a group added there needs no second edit here.
    /// </summary>
    public static string GroupOrder { get; } = string.Join(", ", Groups.Select(static entry => entry.Group));

    /// <summary>
    /// Walks every node a deployment stated for a kind, the root of the kind included.
    /// </summary>
    /// <param name="kind">Message kind identifier.</param>
    /// <param name="declaration">Root of the kind's declaration.</param>
    /// <returns>The nodes of the declaration.</returns>
    public static IEnumerable<MessageDeclarationNode> Walk(string kind, MessageTemplateKindOptions declaration)
    {
        var root = new MessageDeclarationNode(
            kind,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MessageTemplateConfigKeys.KindDimensionName] = kind
            },
            MessageTemplatesOptions.SectionName + ConfigNode.PathSeparator + kind,
            declaration,
            Addressable: true);

        return Descend(root, entered: -1);
    }

    /// <summary>
    /// Yields a node and everything below it.
    /// </summary>
    /// <param name="node">Node being walked.</param>
    /// <param name="entered">Position in <see cref="Groups"/> of the group the node itself stands in.</param>
    /// <returns>The node and its descendants.</returns>
    private static IEnumerable<MessageDeclarationNode> Descend(MessageDeclarationNode node, int entered)
    {
        yield return node;

        for (var index = 0; index < Groups.Length; index++)
        {
            var (group, dimension) = Groups[index];

            foreach (var (value, nested) in GroupOf(node.Declaration, group))
            {
                var point = new Dictionary<string, string>(node.Point, StringComparer.Ordinal)
                {
                    [dimension] = value
                };

                var address = string.Join(
                    ConfigNode.PathSeparator,
                    node.Address,
                    group,
                    value);

                // Nesting is CANONICAL or the node is out of reach: both the address of a key and
                // Navigate below enter the groups in the order of this list and at most once each, so
                // a group written above one that precedes it — or a second time — stands where no
                // resolution ever looks. The node is walked on all the same, unaddressable: the
                // startup check reports it, which is the only way a deployment learns of it at all.
                var addressable = node.Addressable && index > entered;

                var child = new MessageDeclarationNode(node.Kind, point, address, nested, addressable);

                foreach (var descendant in Descend(child, index))
                {
                    yield return descendant;
                }
            }
        }
    }

    /// <summary>
    /// Finds the contract that serves a point of the template ladder, exactly the way the resolution
    /// finds it: step by step of the CONTRACT ladder from the most specific down, and at every step the
    /// deployment's declarations first, the shipped ones only where the deployment states nothing at
    /// THAT address (<see cref="MessageTemplateConfigKeys"/>, the fallback of the core level).
    /// <para>
    /// The two sources are interleaved per step rather than tried one after the other: trying the whole
    /// of one and then the whole of the other would answer with a coarse contract of the deployment
    /// where the resolution answers with a finer shipped one, and the check would then be checking a
    /// pairing that never happens.
    /// </para>
    /// </summary>
    /// <param name="stated">Declarations of the deployment.</param>
    /// <param name="shipped">Declarations the product ships.</param>
    /// <param name="kind">Message kind identifier.</param>
    /// <param name="point">Values of the axes the template node is narrowed by.</param>
    /// <returns>The contract as it is stated, or null when neither source states one.</returns>
    public static MessageContractOptions? FindContract(
        MessageTemplatesOptions stated,
        MessageTemplatesOptions shipped,
        string kind,
        IReadOnlyDictionary<string, string> point)
    {
        var statedRoot = Lookup(stated.Kinds, kind);
        var shippedRoot = Lookup(shipped.Kinds, kind);

        foreach (var step in MessageTemplateConfigKeys.Contract.Dimensions!.Fallback)
        {
            if (ContractAt(statedRoot, step, point) is { } declared)
            {
                return declared;
            }

            if (ContractAt(shippedRoot, step, point) is { } supplied)
            {
                return supplied;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a source states a template ladder at the EXACT address of a point. It is the question
    /// the fallback of the core level asks: the shipped file is read only where the host configuration
    /// holds nothing at that very address, so a shipped ladder an address of the deployment covers is
    /// rendered nowhere.
    /// </summary>
    /// <param name="declarations">Declarations of the deployment.</param>
    /// <param name="kind">Message kind identifier.</param>
    /// <param name="point">Values of the axes the node is narrowed by.</param>
    /// <returns><c>true</c> when this source states a ladder there.</returns>
    public static bool StatesLadder(
        MessageTemplatesOptions declarations,
        string kind,
        IReadOnlyDictionary<string, string> point) => LadderSizeAt(declarations, kind, point) > 0;

    /// <summary>
    /// How many steps a source states at the EXACT address of a point — zero when it states no ladder
    /// there. It answers the question <see cref="StatesLadder"/> asks, with a number instead of a yes:
    /// the two sources of the core level are compared step for step at one address, and the navigation
    /// that finds the node must stay in one place for the comparison to mean anything.
    /// </summary>
    /// <param name="declarations">Declarations of one source.</param>
    /// <param name="kind">Message kind identifier.</param>
    /// <param name="point">Values of the axes the node is narrowed by.</param>
    /// <returns>The number of steps stated there.</returns>
    public static int LadderSizeAt(
        MessageTemplatesOptions declarations,
        string kind,
        IReadOnlyDictionary<string, string> point)
    {
        var root = Lookup(declarations.Kinds, kind);

        if (root is null)
        {
            return 0;
        }

        var address = new HashSet<string>(point.Keys, StringComparer.Ordinal);

        return Navigate(root, address, point)?.Templates.Count ?? 0;
    }

    /// <summary>
    /// The contract one source states at the node one step addresses.
    /// </summary>
    /// <param name="root">Root of the kind's declaration in that source, or null when it has none.</param>
    /// <param name="step">Dimensions the step addresses.</param>
    /// <param name="point">Values of the axes.</param>
    /// <returns>The contract, or null.</returns>
    private static MessageContractOptions? ContractAt(
        MessageTemplateKindOptions? root,
        IReadOnlySet<string> step,
        IReadOnlyDictionary<string, string> point) =>
        root is null ? null : Navigate(root, step, point)?.Contract;

    /// <summary>
    /// Walks from the root of a kind to the node one step of a ladder addresses. A step naming an axis
    /// the point does not carry addresses nothing of its own — the group is dropped from the address,
    /// and the coarser step of the same ladder reads exactly what this one would have read.
    /// </summary>
    /// <param name="root">Root of the kind's declaration.</param>
    /// <param name="step">Dimensions the step addresses.</param>
    /// <param name="point">Values of the axes.</param>
    /// <returns>The node, or null when it is not stated.</returns>
    private static MessageTemplateKindOptions? Navigate(
        MessageTemplateKindOptions root,
        IReadOnlySet<string> step,
        IReadOnlyDictionary<string, string> point)
    {
        var node = root;

        foreach (var (group, dimension) in Groups)
        {
            if (!step.Contains(dimension))
            {
                continue;
            }

            if (!point.TryGetValue(dimension, out var value))
            {
                return null;
            }

            node = Lookup(GroupOf(node, group), value);

            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>
    /// The map of one group of a node — the single place a group NAME meets the member holding it, so
    /// the walk and the address of the keys name the same groups.
    /// </summary>
    /// <param name="declaration">Node of the declaration.</param>
    /// <param name="group">Group name.</param>
    /// <returns>The map of the group.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The node has no such group.</exception>
    private static IDictionary<string, MessageTemplateKindOptions> GroupOf(
        MessageTemplateKindOptions declaration,
        string group) => group switch
        {
            MessageTemplateConfigKeys.ByTypeGroup => declaration.ByType,
            MessageTemplateConfigKeys.ByActionGroup => declaration.ByAction,
            MessageTemplateConfigKeys.BySurfaceGroup => declaration.BySurface,
            MessageTemplateConfigKeys.ByChannelGroup => declaration.ByChannel,
            MessageTemplateConfigKeys.ByLinkPurposeGroup => declaration.ByLinkPurpose,
            MessageTemplateConfigKeys.ByDeviceGroup => declaration.ByDevice,
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown declaration group.")
        };

    /// <summary>
    /// Looks an entry up by an address segment — case-insensitively, the way a configuration path is
    /// matched. It never relies on the comparer of the map: the same shape is filled by the
    /// configuration binder on the deployment's side and by the JSON reader on the shipped one, and
    /// only one of the two keeps a comparer.
    /// </summary>
    /// <typeparam name="T">Type of the entries.</typeparam>
    /// <param name="entries">Entries of a group.</param>
    /// <param name="key">Address segment.</param>
    /// <returns>The entry, or null when there is none.</returns>
    private static T? Lookup<T>(IEnumerable<KeyValuePair<string, T>> entries, string key)
        where T : class
    {
        foreach (var (stated, value) in entries)
        {
            if (string.Equals(stated, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
