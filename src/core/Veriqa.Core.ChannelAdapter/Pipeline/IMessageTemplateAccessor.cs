// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The single point at which a render point of the channel contour obtains a message
/// (SPEC-036 TPL-001, TPL-056): the contract and the template ladder, each resolved through the
/// canonical resolver — a message is two ordinary levelled settings, so a wording a deployment
/// declares actually reaches the rendered text.
/// </summary>
internal interface IMessageTemplateAccessor
{
    /// <summary>
    /// Resolves a message for the ownership context the caller states.
    /// </summary>
    /// <param name="kind">Message kind identifier (<see cref="MessageKinds"/>).</param>
    /// <param name="surface">
    /// Surface the message is shown on (<see cref="MessageSurfaces"/>), when the render point has one to
    /// name. An unnamed axis is not asked about at all, and the steps that would have addressed it
    /// behave as the coarser ones — which is what a kind whose wording does not depend on the axis wants.
    /// </param>
    /// <param name="channel">
    /// Channel the message goes through (<c>ChannelTypes</c>). The channel REFINES the surface and never
    /// appears without one (SPEC-036 §4.6), so naming it without a surface is a programming error.
    /// </param>
    /// <param name="context">
    /// Ownership context the resolution runs in — mandatory, and stated by the caller alone: a render
    /// point knows the transaction whose text this is, and the accessor knows no transaction at all
    /// (SPEC-036 TPL-116). A render point without any owner passes
    /// <see cref="ResolutionContext.Core"/> and says why. The ambient tenant is not read here: where a
    /// channel path has one, it enters through <c>TransactionResolutionContext</c>, which is the single
    /// place ownership is assembled (CFG-202, anti-fork).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message, or null when no level declares one.</returns>
    /// <exception cref="ArgumentException">A channel is named without a surface.</exception>
    ValueTask<ResolvedMessage?> FindAsync(
        string kind,
        string? surface,
        string? channel,
        ResolutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a message for a point of the chain the caller assembled itself — the same question as
    /// the overload above, asked by a render point whose address narrows by more axes than the kind,
    /// the surface and the channel (the outcome receipt narrows by the transaction type and the action
    /// type as well, SPEC-036 TPL-123).
    /// <para>
    /// The point is the TEMPLATE's: it may carry a value of any dimension either key declares, and each
    /// of the two is then asked with its own — a key is never asked about an axis it does not declare
    /// (SPEC-012 §10.6). A dimension the point leaves out is not asked about at all, and the step that
    /// would have addressed it behaves as the coarser one.
    /// </para>
    /// </summary>
    /// <param name="point">Dimension values of the address, the <c>kind</c> included.</param>
    /// <param name="context">Ownership context of the resolution; see <see cref="FindAsync(string, string?, string?, ResolutionContext, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message, or null when no level declares one.</returns>
    /// <exception cref="ArgumentException">The point names no kind, names a channel without a surface,
    /// or names a link purpose without a channel.</exception>
    ValueTask<ResolvedMessage?> FindAsync(
        ConfigDimensionValues point,
        ResolutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a message a render point shows only where some level words it — a kind whose base states
    /// no ladder, so that an address no level states a ladder for is an ordinary answer rather than a
    /// defect of the deployment (the hint of a channel, <see cref="MessageKinds.HopChannelHint"/>). That
    /// one answer is null without a report; every other refusal — no contract, no ladder its contract
    /// admits, a failure — is the one of
    /// <see cref="FindAsync(ConfigDimensionValues, ResolutionContext, CancellationToken)"/>.
    /// <para>
    /// The default asks <see cref="FindAsync(ConfigDimensionValues, ResolutionContext, CancellationToken)"/>,
    /// which reports the missing ladder: an implementation that knows the levels tells the two apart.
    /// </para>
    /// </summary>
    /// <param name="point">Dimension values of the address, the <c>kind</c> included.</param>
    /// <param name="context">Ownership context of the resolution; see <see cref="FindAsync(string, string?, string?, ResolutionContext, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message, or null when no level declares one.</returns>
    /// <exception cref="ArgumentException">The point is one
    /// <see cref="FindAsync(ConfigDimensionValues, ResolutionContext, CancellationToken)"/> refuses.</exception>
    ValueTask<ResolvedMessage?> FindOptionalAsync(
        ConfigDimensionValues point,
        ResolutionContext context,
        CancellationToken cancellationToken = default) =>
        FindAsync(point, context, cancellationToken);

    /// <summary>
    /// Resolves a message a render point cannot go on without. Nothing resolved means one of two
    /// things, and the message names both: the product carries no such kind at all (a programming
    /// error), or a deployment left the kind without one of its two halves — which is a configuration
    /// error, and the far likelier one on an installation that has been edited.
    /// </summary>
    /// <param name="kind">Message kind identifier (<see cref="MessageKinds"/>).</param>
    /// <param name="surface">Surface the message is shown on; see <see cref="FindAsync(string, string?, string?, ResolutionContext, CancellationToken)"/>.</param>
    /// <param name="channel">Channel the message goes through; see <see cref="FindAsync(string, string?, string?, ResolutionContext, CancellationToken)"/>.</param>
    /// <param name="context">Ownership context of the resolution; see <see cref="FindAsync(string, string?, string?, ResolutionContext, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message.</returns>
    /// <exception cref="InvalidOperationException">No level yields a renderable message.</exception>
    async ValueTask<ResolvedMessage> RequireAsync(
        string kind,
        string? surface,
        string? channel,
        ResolutionContext context,
        CancellationToken cancellationToken = default) =>
        await FindAsync(kind, surface, channel, context, cancellationToken)
            ?? throw NotResolved(kind);

    /// <summary>
    /// Resolves, at a point of dimension values, a message a render point cannot go on without; see
    /// <see cref="RequireAsync(string, string?, string?, ResolutionContext, CancellationToken)"/> for what
    /// nothing resolved means.
    /// </summary>
    /// <param name="point">Dimension values of the address, the <c>kind</c> included.</param>
    /// <param name="context">Ownership context of the resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message.</returns>
    /// <exception cref="InvalidOperationException">No level yields a renderable message.</exception>
    async ValueTask<ResolvedMessage> RequireAsync(
        ConfigDimensionValues point,
        ResolutionContext context,
        CancellationToken cancellationToken = default) =>
        await FindAsync(point, context, cancellationToken)
            ?? throw NotResolved(
                point.Values is { } values && values.TryGetValue(MessageTemplateConfigKeys.KindDimensionName, out var kind)
                    ? kind
                    : string.Empty);

    /// <summary>
    /// The failure of a message nothing resolved: the two causes it can have, named in one place.
    /// </summary>
    /// <param name="kind">Message kind identifier.</param>
    /// <returns>The exception to throw.</returns>
    private static InvalidOperationException NotResolved(string kind) => new(
        $"No message could be resolved for kind '{kind}': either the product declares no such "
        + $"kind, or the '{MessageTemplatesOptions.SectionName}' configuration of this "
        + "deployment leaves it without a contract, or without a template variant its contract "
        + "admits. Which of the three it is has just been logged.");
}

/// <summary>
/// Implementation over the canonical resolver. It states no ownership of its own: the context comes
/// from the caller, because the render point is the one that knows the transaction whose text this is
/// (SPEC-036 TPL-116). Reading an ambient tenant here instead would answer over the tenant level
/// alone, and the application and <c>ui_config</c> levels a deployment declares would never answer.
/// <para>
/// <b>It also holds the runtime half of the rule across the two settings (TPL-111).</b>
/// The startup check reaches every level whose records exist at startup; the <c>ui_config</c> level is
/// beyond it by construction, since a record there is chosen by a request parameter. So BOTH halves of
/// the rule are asked here as well: the EDITION of a variant referencing a slot the contract does
/// not declare (a) is REJECTED — the other edition of the same variant stays usable by the sink asking
/// for it — and a level whose minimal variant of some edition rests on a slot the contract does not
/// guarantee (e) is left behind whole. The resolution goes on with the next variant of the same level
/// and, when a level is left with none, with the level below it. The host does not fall over a record
/// it has never seen, and a token that would have stayed in the text as a literal never reaches a user.
/// </para>
/// </summary>
internal sealed class MessageTemplateAccessor : IMessageTemplateAccessor
{
    /// <summary>
    /// Canonical multi-level configuration resolver.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger of a rejected variant and of a kind no level declares.
    /// </summary>
    private readonly ILogger<MessageTemplateAccessor> _logger;

    /// <summary>
    /// Creates the accessor.
    /// </summary>
    /// <param name="resolver">Canonical multi-level configuration resolver.</param>
    /// <param name="logger">Logger.</param>
    public MessageTemplateAccessor(IConfigurationResolver resolver, ILogger<MessageTemplateAccessor> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValueTask<ResolvedMessage?> FindAsync(
        string kind,
        string? surface,
        string? channel,
        ResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(context);

        // An axis is either named with a value or not named at all: passing it empty would ask about
        // the surface called "" instead of asking about no surface. What the caller DID name travels
        // on whole, the channel included even when the surface is missing: the one place that judges
        // a channel named without a surface is the overload below, and dropping the channel here
        // would hide that call from it — the caller would silently be answered by a coarser wording
        // instead of being told that the point it assembled addresses no step of the ladder.
        var point = (surface, channel) switch
        {
            (null or "", null or "") => ConfigDimensionValues.Of(
                (MessageTemplateConfigKeys.KindDimensionName, kind)),
            (null or "", _) => ConfigDimensionValues.Of(
                (MessageTemplateConfigKeys.KindDimensionName, kind),
                (MessageTemplateConfigKeys.ChannelDimensionName, channel)),
            (_, null or "") => ConfigDimensionValues.Of(
                (MessageTemplateConfigKeys.KindDimensionName, kind),
                (MessageTemplateConfigKeys.SurfaceDimensionName, surface)),
            _ => ConfigDimensionValues.Of(
                (MessageTemplateConfigKeys.KindDimensionName, kind),
                (MessageTemplateConfigKeys.SurfaceDimensionName, surface),
                (MessageTemplateConfigKeys.ChannelDimensionName, channel))
        };

        return FindAsync(point, context, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<ResolvedMessage?> FindAsync(
        ConfigDimensionValues point,
        ResolutionContext context,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(point, context, ladderOptional: false, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ResolvedMessage?> FindOptionalAsync(
        ConfigDimensionValues point,
        ResolutionContext context,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(point, context, ladderOptional: true, cancellationToken);

    /// <summary>
    /// Resolves a message at a point of dimension values: the contract, then the ladder its contract admits.
    /// </summary>
    /// <param name="point">Dimension values of the address, the <c>kind</c> included.</param>
    /// <param name="context">Ownership context of the resolution.</param>
    /// <param name="ladderOptional">
    /// Whether no level stating a ladder for the address is an ordinary answer, given without a report.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The message, or null when no level declares one.</returns>
    private async ValueTask<ResolvedMessage?> ResolveAsync(
        ConfigDimensionValues point,
        ResolutionContext context,
        bool ladderOptional,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stated = point.Values ?? ConfigDimensionValues.None.Values;

        if (!stated.TryGetValue(MessageTemplateConfigKeys.KindDimensionName, out var kind)
            || string.IsNullOrEmpty(kind))
        {
            throw new ArgumentException(
                "The kind is the identity dimension of both keys of a message — \"the text of WHICH "
                + "message\" is the question itself, so a point that names none asks nothing.",
                nameof(point));
        }

        // No step of the ladder names the channel without the surface it refines, so a point that named
        // one alone would silently be answered by a coarser step — the wording of another channel, or of
        // none. Said out loud rather than resolved into something plausible.
        if (Names(stated, MessageTemplateConfigKeys.ChannelDimensionName)
            && !Names(stated, MessageTemplateConfigKeys.SurfaceDimensionName))
        {
            throw new ArgumentException(
                "A channel refines a surface and no step of the template ladder addresses it without one: "
                + "name the surface the message is shown on beside the channel.",
                nameof(point));
        }

        // The same holds one axis further: the link purpose refines the channel, and a point naming it
        // without one would be answered by a step that ignores the purpose without saying so.
        if (Names(stated, MessageTemplateConfigKeys.LinkPurposeDimensionName)
            && !Names(stated, MessageTemplateConfigKeys.ChannelDimensionName))
        {
            throw new ArgumentException(
                "A link purpose refines a channel and no step of the template ladder addresses it without "
                + "one: name the channel the message goes through beside the link purpose.",
                nameof(point));
        }

        // The two settings are asked with DIFFERENT points, because they declare different axes: the
        // presentation axes belong to the template alone, and asking a key about an axis it does not
        // declare is an error, not a narrower question (SPEC-012 §10.6). That difference is the model
        // itself — one contract of a kind, several wordings of it.
        var contractPoint = Narrowed(stated, MessageTemplateConfigKeys.Contract.Dimensions);
        var templatePoint = Narrowed(stated, MessageTemplateConfigKeys.Template.Dimensions);

        var resolvedContract = await _resolver.ResolveAsync(
            MessageTemplateConfigKeys.Contract,
            context,
            contractPoint,
            cancellationToken);

        var contract = resolvedContract.Value;

        if (contract is null)
        {
            // The product ships a contract for every kind it declares, so this is a deployment whose
            // engine configuration is not in place — worth an operator's attention, never a throw on
            // the render path.
            _logger.LogError(
                "No level declares a contract for message kind {MessageKind}; the message cannot be rendered.",
                kind);

            return null;
        }

        var templates = await ResolveConformingLadderAsync(
            kind, contract, context, templatePoint, ladderOptional, cancellationToken);

        return templates is null
            ? null
            : new ResolvedMessage(kind, contract, templates, resolvedContract.SourceLevel);
    }

    /// <summary>
    /// Whether the point states a value of a dimension. A dimension present with a blank value states
    /// nothing: the group of the address is dropped for it exactly as it is for one left out.
    /// </summary>
    /// <param name="stated">Dimension values of the point.</param>
    /// <param name="dimension">Dimension name.</param>
    /// <returns><c>true</c> when the point asks about that dimension.</returns>
    private static bool Names(IReadOnlyDictionary<string, string> stated, string dimension) =>
        stated.TryGetValue(dimension, out var value) && !string.IsNullOrEmpty(value);

    /// <summary>
    /// The caller's point narrowed to the dimensions ONE key declares. Asking a key about an axis it
    /// does not declare is an error rather than a narrower question (SPEC-012 §10.6), and the two keys
    /// of a message declare different sets — so the point a caller assembles is the union of both and
    /// each key is asked with its own share of it.
    /// </summary>
    /// <param name="stated">Dimension values of the caller's point.</param>
    /// <param name="dimensions">Dimensions the key declares.</param>
    /// <returns>The point of that key.</returns>
    private static ConfigDimensionValues Narrowed(
        IReadOnlyDictionary<string, string> stated,
        ConfigKeyDimensions? dimensions)
    {
        if (dimensions is null)
        {
            return ConfigDimensionValues.None;
        }

        var values = new List<(string Dimension, string? Value)>(dimensions.Names.Count);

        foreach (var dimension in dimensions.Names)
        {
            if (Names(stated, dimension))
            {
                values.Add((dimension, stated[dimension]));
            }
        }

        return ConfigDimensionValues.Of(values.ToArray().AsSpan());
    }

    /// <summary>
    /// Resolves the template ladder and keeps only the variants the contract admits, descending to the
    /// level below whenever a step is left with none (SPEC-036 TPL-111, runtime half).
    /// </summary>
    /// <param name="kind">Message kind identifier.</param>
    /// <param name="contract">Contract of the message — the allowlist of slots.</param>
    /// <param name="context">Ownership context of the resolution.</param>
    /// <param name="point">Dimension values the ladder is asked with.</param>
    /// <param name="ladderOptional">
    /// Whether no level stating a ladder for the address at all is an ordinary answer, given without a
    /// report. A level that stated one its contract refused is still reported.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The variants that may be rendered, or null when no level states any.</returns>
    private async ValueTask<IReadOnlyList<MessageTemplateVariant>?> ResolveConformingLadderAsync(
        string kind,
        MessageContract contract,
        ResolutionContext context,
        ConfigDimensionValues point,
        bool ladderOptional,
        CancellationToken cancellationToken)
    {
        var current = context;
        ConfigLevel? answered = null;

        while (true)
        {
            var resolved = await _resolver.ResolveAsync(
                MessageTemplateConfigKeys.Template,
                current,
                point,
                cancellationToken);

            if (resolved.Value is null && ladderOptional && answered is null)
            {
                // No level words the message for this address — the kind is shown only where one does.
                _logger.LogDebug(
                    "No level declares a template ladder for message kind {MessageKind} at this address; the "
                    + "message is not shown.",
                    kind);

                return null;
            }

            if (resolved.Value is not { } ladder)
            {
                _logger.LogError(
                    "No level declares a template ladder for message kind {MessageKind}; the message cannot be rendered.",
                    kind);

                return null;
            }

            // The descent has to make progress, and whether it did is read off the ANSWER rather than
            // assumed from the narrowed context: a reader is free to serve a record the context no
            // longer addresses, and a level answering twice would spin here forever. A level is lower
            // when its place in the precedence order is later (SPEC-012 §10.3).
            if (answered is { } above && (resolved.SourceLevel is not { } level || level <= above))
            {
                _logger.LogError(
                    "No level states a template ladder of message kind {MessageKind} its contract "
                    + "admits; the message cannot be rendered.",
                    kind);

                return null;
            }

            var admitted = Admitted(kind, contract, ladder, resolved.SourceLevel);

            if (admitted.Count > 0)
            {
                return admitted;
            }

            if (Below(current, resolved.SourceLevel) is not { } lower)
            {
                _logger.LogError(
                    "No level states a template ladder of message kind {MessageKind} its contract "
                    + "admits; the message cannot be rendered.",
                    kind);

                return null;
            }

            answered = resolved.SourceLevel;
            current = lower;
        }
    }

    /// <summary>
    /// Keeps the editions of the ladder whose every token is a slot the contract declares, reporting
    /// the rest.
    /// <para>
    /// <b>A LEVEL is admitted whole or not at all.</b> The MINIMAL variant of the ladder it states is
    /// the floor the render degrades to (TPL-032) — the one text that is chosen when no other is fully
    /// covered by values. Drop the floor and keep a fuller variant, and that fuller one BECOMES the
    /// floor: a text resting on slots that are not always filled, whose token reaches a user as a
    /// literal. So a LEVEL whose minimal variant the contract refuses is left behind entirely and the
    /// resolution goes on with the level below — the same fail-closed the rejection of a single edition
    /// of a variant serves. There is such a floor for EVERY EDITION of the ladder, since a sink degrades
    /// among the steps stating the edition it asked for (<see cref="MessageTemplateVariant.Floors"/>).
    /// </para>
    /// <para>
    /// <b>The floor is judged on two counts, not one.</b> A token outside the contract is the obvious
    /// one; a token INSIDE it whose value is not guaranteed ends exactly the same way, since nothing
    /// shorter is left to degrade onto. Both are asked of the floor and of the floor only: a fuller
    /// variant may rest on optional values freely — that is what the ladder is for.
    /// </para>
    /// </summary>
    /// <param name="kind">Message kind identifier.</param>
    /// <param name="contract">Contract of the message.</param>
    /// <param name="ladder">Variants as the answering step stated them.</param>
    /// <param name="level">Level the ladder came from — what an operator has to go and repair.</param>
    /// <returns>The admitted variants, in the order the ladder states them, each with the editions resting
    /// on a text the contract refuses taken off it — and a variant left with no edition at all dropped;
    /// empty when the level itself is not admitted.</returns>
    private IReadOnlyList<MessageTemplateVariant> Admitted(
        string kind,
        MessageContract contract,
        IReadOnlyList<MessageTemplateVariant> ladder,
        ConfigLevel? level)
    {
        // A ladder has a floor PER EDITION: a sink degrades among the steps stating the edition it
        // asked for, so the last step of the ladder answers for one edition only and a ladder ending in
        // a plain-only step still has an HTML floor of its own. Every one of them is judged, and one
        // failing leaves the whole ladder of this level behind; the report names the edition, since the
        // two degrade apart and an operator repairing the other one would change nothing.
        foreach (var (edition, floor) in MessageTemplateVariant.Floors(ladder))
        {
            if (OutsideContract(contract, floor.TextsOf(edition)) is { } undeclared)
            {
                _logger.LogWarning(
                    "The MINIMAL template variant of the {Edition} edition of message kind {MessageKind} "
                    + "declared at level {ConfigLevel} references slot {SlotName}, which its contract does "
                    + "not declare; the whole ladder of that level is left behind, since no fuller variant "
                    + "may take the floor of the degradation.",
                    edition,
                    kind,
                    level,
                    undeclared);

                return [];
            }

            // The floor may rest only on values that are ALWAYS there — the second half of the rule the
            // startup check holds over the declarations it can enumerate. A slot the contract declares but
            // does not guarantee is admitted by the check above and still leaves its token in the rendered
            // text as a literal: the selection has no shorter variant to degrade onto and falls back on
            // this very one. So the ladder of this level is left behind whole for this reason too, and the
            // resolution goes on with the level below.
            if (NotGuaranteed(kind, contract, floor.TextsOf(edition)) is { } optional)
            {
                _logger.LogWarning(
                    "The MINIMAL template variant of the {Edition} edition of message kind {MessageKind} "
                    + "declared at level {ConfigLevel} rests on slot {SlotName}, whose value is not "
                    + "guaranteed; the whole ladder of that level is left behind, since the floor of the "
                    + "degradation would render that token as a literal.",
                    edition,
                    kind,
                    level,
                    optional);

                return [];
            }
        }

        var admitted = new List<MessageTemplateVariant>(ladder.Count);

        foreach (var variant in ladder)
        {
            // A variant is judged TEXT BY TEXT, and only the editions RESTING on the offending text are
            // taken off it — the same way a sink asks a step for the edition it is about to deliver and
            // never sees the other one. Rejecting the variant whole over a token in the edition nobody
            // asked for would hand the OTHER edition a new floor: an earlier, fuller variant, which the
            // check above never judged and which may rest on a slot that is not always filled. The floor
            // of every edition survives this loop by construction — it is the LAST variant stating its
            // edition, and it passed this very predicate a few lines above, so nothing here can remove it
            // or promote another variant into its place.
            //
            // The unit is the TEXT and not the edition because the report has to be true about the
            // deployment's file: the string spelling of a step states one text as both editions, and the
            // subject is one text of the step whatever edition delivers it. Which editions a named text
            // costs the step is the step's answer (TextParts), so a text is reported once and the
            // editions come off it together.
            MessageTemplateVariant? kept = variant;

            foreach (var part in variant.TextParts())
            {
                if (OutsideContract(contract, [part.Text]) is not { } outside)
                {
                    continue;
                }

                if (part.Role is MessageTemplateTextRole.Subject)
                {
                    _logger.LogWarning(
                        "The SUBJECT of a template variant of message kind {MessageKind} declared at level "
                        + "{ConfigLevel} references slot {SlotName}, which its contract does not declare; "
                        + "every edition of the variant is rejected, since a mail shows its subject whatever "
                        + "edition its body is delivered in.",
                        kind,
                        level,
                        outside);

                    // The step goes whole, and not edition by edition: the subject is refused, and a step
                    // is not deliverable without it. Taking the STATED editions off it would be the same
                    // thing for every step that states one — but not for a step that states none, which
                    // would keep the refused subject and stay in the ladder while the record above says
                    // it left. What the record says and what the ladder holds are one answer here.
                    kept = null;

                    continue;
                }

                _logger.LogWarning(
                    "The body stated by the {Editions} edition(s) of a template variant of message kind "
                    + "{MessageKind} declared at level {ConfigLevel} references slot {SlotName}, which its "
                    + "contract does not declare; those editions of the variant are rejected.",
                    string.Join(", ", part.Editions),
                    kind,
                    level,
                    outside);

                foreach (var edition in part.Editions)
                {
                    kept = kept?.Without(edition);
                }
            }

            if (kept is null)
            {
                // Nothing is left of the step, so the LADDER an operator reads in the file and the ladder
                // the render actually degrades along are no longer the same length. Said out loud as a
                // record of its own: the rejections above name texts, and a reader counting them has no
                // way to tell the step that lost one edition from the step that lost all of them.
                _logger.LogWarning(
                    "No edition of a template variant of message kind {MessageKind} declared at level "
                    + "{ConfigLevel} survives its contract; that step leaves the ladder entirely and the "
                    + "render degrades along the steps left.",
                    kind,
                    level);

                continue;
            }

            admitted.Add(kept);
        }

        return admitted;
    }

    /// <summary>
    /// The first token of the given texts whose slot the contract declares without guaranteeing a value
    /// for it; null when every one of them always carries a value. Tokens outside the contract are none
    /// of this method's business — the caller refuses those first.
    /// </summary>
    /// <param name="kind">Message kind identifier — whether a caller slot is guaranteed depends on it.</param>
    /// <param name="contract">Contract of the message.</param>
    /// <param name="texts">Texts to walk — a whole variant, or the texts of one edition of it.</param>
    /// <returns>Name of the offending slot, or null.</returns>
    private static string? NotGuaranteed(string kind, MessageContract contract, IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            foreach (var referenced in SlotTokenScanner.ExtractSlotNames(text))
            {
                if (contract.FindSlot(referenced) is { } slot && !slot.IsAlwaysPresentIn(kind))
                {
                    return referenced;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The first token of the given texts that is not a declared slot; null when every token is one.
    /// </summary>
    /// <param name="contract">Contract of the message.</param>
    /// <param name="texts">Texts to walk — a whole variant, or the texts of one edition of it.</param>
    /// <returns>Name of the offending slot, or null.</returns>
    private static string? OutsideContract(MessageContract contract, IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            foreach (var referenced in SlotTokenScanner.ExtractSlotNames(text))
            {
                if (contract.FindSlot(referenced) is null)
                {
                    return referenced;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The same ownership context with the level that answered — and everything above it — taken out,
    /// so the next resolution is answered strictly lower. Null at the core level: there is nothing
    /// below it to descend to.
    /// </summary>
    /// <param name="context">Ownership context of the resolution.</param>
    /// <param name="level">Level that answered.</param>
    /// <returns>The context of the levels below, or null.</returns>
    private static ResolutionContext? Below(ResolutionContext context, ConfigLevel? level) => level switch
    {
        ConfigLevel.UiConfig => ResolutionContext.Of(
            context.TenantId,
            context.ApplicationId,
            uiConfigSelector: null),
        ConfigLevel.Application => ResolutionContext.Of(
            context.TenantId,
            applicationId: null,
            uiConfigSelector: null),
        ConfigLevel.Tenant => ResolutionContext.Core,
        _ => null
    };
}
