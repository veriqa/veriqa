// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.MessageTemplates;

/// <summary>
/// Shape of the <c>Veriqa:MessageTemplates</c> section — the message declarations a deployment writes
/// (SPEC-036 §4.6). A message is TWO settings, so an entry of the section states them side by side:
/// the <see cref="MessageTemplateKindOptions.Contract"/> of the message and the ladder of its
/// <see cref="MessageTemplateKindOptions.Templates"/>, each narrowed further by the nested axis groups
/// of the same entry.
/// <para>
/// The root of the section does NOT derive from a collection: the entries live in
/// <see cref="Kinds"/>. A root that IS a dictionary is bound by putting every JSON member into it, and
/// it can therefore never grow a scalar member of its own — an irreversible property of the shape once
/// the surface is published. The section itself keeps its form: its members are read into
/// <see cref="Kinds"/> by the configurator of these options, not by <c>services.Configure(section)</c>.
/// </para>
/// <para>
/// These options are the surface a deployment is VALIDATED against at startup. They are not what a
/// render point reads: the effective value of each of the two settings is resolved by the canonical
/// resolver at the address the key declares (<see cref="MessageTemplateConfigKeys"/>), level by level.
/// </para>
/// </summary>
public sealed class MessageTemplatesOptions
{
    /// <summary>
    /// Root section of the product configuration — the first segment of the core address of every
    /// message setting.
    /// </summary>
    public const string RootSectionName = "Veriqa";

    /// <summary>
    /// Name of the section inside <see cref="RootSectionName"/>. It is also the first segment of the
    /// address of these settings inside the record of a level above the core, where the address is
    /// relative to the record rather than to the host configuration (SPEC-012 §10.1).
    /// </summary>
    public const string GroupName = "MessageTemplates";

    /// <summary>
    /// Configuration section literal — the single place it is spelled out.
    /// </summary>
    public const string SectionName = RootSectionName + ":" + GroupName;

    /// <summary>
    /// Declarations by message kind identifier. The identifier is an address segment of both keys, so
    /// it is matched the way a configuration path is matched — case-insensitively.
    /// </summary>
    public IDictionary<string, MessageTemplateKindOptions> Kinds { get; init; } =
        new Dictionary<string, MessageTemplateKindOptions>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One node of a message declaration: what a deployment states for a kind at one point of the axis
/// ladder, and the nested groups that narrow it further (SPEC-036 §4.6, TPL-116).
/// <para>
/// The type is its own child: a group entry states exactly what the kind root states — a contract, a
/// template ladder, and groups below it — because the axes are declared as DIMENSIONS of the two keys
/// and the address of a step is built by dropping the groups the step does not address
/// (<see cref="MessageTemplateConfigKeys"/>). The nesting the ladder of steps reaches is the one the
/// startup validation admits; a group written where no step reads it is refused at startup rather
/// than ignored.
/// </para>
/// </summary>
public sealed class MessageTemplateKindOptions
{
    /// <summary>
    /// Contract of the message at this point of the ladder: the slots it declares. Null — the point
    /// states no contract, and the contract is answered by a coarser step or by a level below.
    /// </summary>
    public MessageContractOptions? Contract { get; init; }

    /// <summary>
    /// Ordered template variants at this point of the ladder, fullest → minimal. Empty — the point
    /// states no ladder. A step that states one REPLACES the ladder whole: variants of different
    /// steps are never merged (SPEC-036 §9, one text is one unit of resolution).
    /// <para>
    /// A variant is a text or a structure the renderer of a channel understands
    /// (<see cref="MessageTemplateVariant"/>); both spellings live in one array.
    /// </para>
    /// </summary>
    public IList<MessageTemplateVariant> Templates { get; init; } = new List<MessageTemplateVariant>();

    /// <summary>Declarations narrowed by the transaction type.</summary>
    public IDictionary<string, MessageTemplateKindOptions> ByType { get; init; } =
        new Dictionary<string, MessageTemplateKindOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declarations narrowed by the action type.</summary>
    public IDictionary<string, MessageTemplateKindOptions> ByAction { get; init; } =
        new Dictionary<string, MessageTemplateKindOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declarations narrowed by the surface the message is shown on.</summary>
    public IDictionary<string, MessageTemplateKindOptions> BySurface { get; init; } =
        new Dictionary<string, MessageTemplateKindOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declarations narrowed by the channel.</summary>
    public IDictionary<string, MessageTemplateKindOptions> ByChannel { get; init; } =
        new Dictionary<string, MessageTemplateKindOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Declarations narrowed by the purpose of the link the message is placed in. The group refines
    /// the channel and stands under a channel entry only.
    /// </summary>
    public IDictionary<string, MessageTemplateKindOptions> ByLinkPurpose { get; init; } =
        new Dictionary<string, MessageTemplateKindOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Declarations narrowed by the type of the device the transaction was started on. The group
    /// stands under any node and is the innermost one: nothing nests below a device entry.
    /// </summary>
    public IDictionary<string, MessageTemplateKindOptions> ByDevice { get; init; } =
        new Dictionary<string, MessageTemplateKindOptions>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shape of a message CONTRACT as a deployment writes it: the slots the message declares
/// (SPEC-036 §4.1). The contract is what says which slots exist; a template variant may reference no
/// other one.
/// </summary>
public sealed class MessageContractOptions
{
    /// <summary>Declared slots. Names are unique within the contract.</summary>
    public IList<MessageSlotOptions> Slots { get; init; } = new List<MessageSlotOptions>();
}

/// <summary>
/// Shape of one slot declaration in a message contract. Types are validated at startup (TPL-111);
/// <c>money</c> is rejected in v1 (D3).
/// </summary>
public sealed class MessageSlotOptions
{
    /// <summary>Slot name (snake_case, TPL-005).</summary>
    public string? Name { get; init; }

    /// <summary>Slot type: <c>string|enum|number|datetime|entity_ref</c> (<c>money</c> rejected in v1).</summary>
    public string? Type { get; init; }

    /// <summary>
    /// Where the value comes from. Null — <see cref="SlotSource.Caller"/>: a deployment declaring a
    /// slot of its own is declaring what the calling party supplies, and the server sources are the
    /// ones the product ships.
    /// </summary>
    public SlotSource? Source { get; init; }

    /// <summary>Whether the caller must supply this value (caller slots only, default false).</summary>
    public bool Required { get; init; }

    /// <summary>Whether a server slot always has a value (server slots only, default false).</summary>
    public bool Guaranteed { get; init; }

    /// <summary>
    /// Max length (string/entity_ref). Mandatory for a caller slot (TPL-006d); a server slot the
    /// product ships takes the global ceiling
    /// (<see cref="MessageTemplateLimits.MaxSlotValueLength"/>) when it states none.
    /// </summary>
    public int? MaxLength { get; init; }

    /// <summary>Allowed set (mandatory for enum, non-empty).</summary>
    public IList<string>? Values { get; init; }

    /// <summary>Optional lower bound (number).</summary>
    public decimal? Min { get; init; }

    /// <summary>Optional upper bound (number).</summary>
    public decimal? Max { get; init; }

    /// <summary>
    /// Natural Key the value is the translation of — stated by a
    /// <see cref="SlotSource.LocalizedText"/> slot and by no other one. It is the contract that names
    /// the phrase, so a template variant may use the slot but never choose what it says.
    /// </summary>
    public string? NaturalKey { get; init; }
}
