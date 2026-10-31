// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Veriqa.Core.Configuration;

/// <summary>
/// The ONE registrar of the keys a deployment declares by catalog (SPEC-012 §10.6, CFG-240/CFG-244).
/// It walks the catalogs handed to it and, through the very calls every handwritten registrar makes,
/// declares each key and binds each of its levels — a level with a record over the node reader of that
/// level, the core level over the application configuration of the host.
/// <para>
/// It is one type and not one per owner because it names no key and no record: what a key is called,
/// where it lives and how it is read comes from the catalog. A key that is NOT in a catalog is not
/// touched by it at all — the handwritten registrars keep working next to it, key by key, until they
/// are migrated.
/// </para>
/// <para>
/// The fate of a value the setting does not admit is not decided here (SPEC-012 §4.1 CFG-240/CFG-246):
/// a value that cannot be read is reported, and a value stated in a SHAPE the setting is not read from
/// — the one failure no parser even attempted — leaves the level with what the KEY states over a record
/// saying nothing: unset for a key that lets the level cede, the shipped default for a level its owner
/// declared to always state a value. Any failed read of the CORE level of a key that governs the levels
/// above it — a protective ceiling or a gated value (CFG-212), or a set whose core level is the ceiling
/// of the intersection (CFG-211) — is answered the same way, whatever the failure: an unset core level
/// would hand the resolution to the levels above it, with no ceiling and no gate over them. Every other
/// value the deployment stated and got wrong leaves its step unset, as CFG-240 has it. What this type guarantees is only that a broken value in the record of ONE owner
/// does not leave the resolution path as an exception — there would be nowhere left to decide
/// anything — and that it never turns a level that always speaks into a silent one.
/// </para>
/// </summary>
public sealed class PathConfigKeyRegistrar : IRegisterConfigKeys
{
    /// <summary>
    /// What the warning tells an operator the level does after a value stated in a shape the setting is
    /// not read from, or after any failed read of the core level of a key that governs the levels above
    /// it (<see cref="GovernsTheLevelsAbove{T}"/>): the key is asked again over a record stating
    /// nothing, and what the level answers then is the key's own
    /// (<see cref="ReadWhereTheRecordStatesNothing{T}"/>).
    /// </summary>
    private const string AnswersAsOverASilentRecord =
        "The level answers as a whole as it does where its record states nothing.";

    /// <summary>
    /// What the warning tells an operator the level does after a value the deployment stated and got
    /// wrong, anywhere but the core level of a key that governs the levels above it: the step is left
    /// unset and the setting comes from the step or the level below (CFG-240).
    /// </summary>
    private const string LeavesTheStepUnset = "The step of the level is left unset.";

    /// <summary>
    /// Catalogs of the owners this registrar serves.
    /// </summary>
    private readonly IReadOnlyList<ConfigKeyCatalog> _catalogs;

    /// <summary>
    /// Node reader of each level that has a record. A level absent from the map is not bound: which
    /// levels a contour has is a property of the deployment (SPEC-012 CFG-202), and the startup report
    /// already names a declared level nobody bound.
    /// </summary>
    private readonly IReadOnlyDictionary<ConfigLevel, IConfigRecordReader<ConfigNode>> _readers;

    /// <summary>
    /// Application configuration of the host — the record of the core level (self-hosted ≡ core,
    /// SPEC-012 §10.1). It is the instance the composition root hands in rather than whatever the
    /// container resolves, because a host that supplies a subsection is served from a different root.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Logger of the values that could not be read.
    /// </summary>
    private readonly ILogger<PathConfigKeyRegistrar> _logger;

    /// <summary>
    /// Creates the registrar of the declared catalogs.
    /// </summary>
    /// <param name="catalogs">Catalogs of the owners this registrar serves.</param>
    /// <param name="readers">Node reader of each level that has a record.</param>
    /// <param name="configuration">Application configuration of the host (the core level).</param>
    /// <param name="logger">Logger.</param>
    public PathConfigKeyRegistrar(
        IEnumerable<ConfigKeyCatalog> catalogs,
        IReadOnlyDictionary<ConfigLevel, IConfigRecordReader<ConfigNode>> readers,
        IConfiguration configuration,
        ILogger<PathConfigKeyRegistrar> logger)
    {
        ArgumentNullException.ThrowIfNull(catalogs);

        _catalogs = [.. catalogs];
        _readers = readers ?? throw new ArgumentNullException(nameof(readers));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void DeclareKeys(IConfigKeyDeclarations keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        Walk(new DeclarationPass(keys));
    }

    /// <inheritdoc />
    public void Register(IConfigBindings bindings, IConfigCoreBindings coreBindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(coreBindings);

        Walk(new BindingPass(this, bindings, coreBindings));
    }

    /// <summary>
    /// Hands every declaration of every catalog to one pass.
    /// </summary>
    /// <param name="pass">What is done with each declaration.</param>
    private void Walk(IConfigKeyDeclarationVisitor pass)
    {
        foreach (var catalog in _catalogs)
        {
            foreach (var declared in catalog.Keys)
            {
                declared.Accept(pass);
            }
        }
    }

    /// <summary>
    /// Binds every level of one key: the core level over the application configuration, a level with a
    /// record over the node reader of that level. A level the declaration addresses by IDENTITY is not
    /// bound here — it has no path, so it belongs to the store that keeps values as rows rather than to
    /// a reader of documents.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="bindings">Registry of level bindings over source records.</param>
    /// <param name="coreBindings">Registry of core-level bindings.</param>
    private void Bind<T>(DeclaredConfigKey<T> declared, IConfigBindings bindings, IConfigCoreBindings coreBindings)
    {
        foreach (var address in declared.Levels)
        {
            if (address.Template is not { } template)
            {
                continue;
            }

            if (address.Level is ConfigLevel.Core)
            {
                coreBindings.RegisterCore(
                    declared.Key,
                    point => ReadCore(declared, template, point),
                    point => ReadCoreWhereNothingIsStated(declared, template, point));

                continue;
            }

            if (!_readers.TryGetValue(address.Level, out var reader))
            {
                continue;
            }

            bindings.Register<T, ConfigNode>(
                declared.Key,
                address.Level,
                reader,
                (node, point) => Read(declared, template, node, point));
        }
    }

    /// <summary>
    /// Reads one step of the chain out of the record of a level.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="template">Address of the level.</param>
    /// <param name="node">Subtree of the record.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    private LayerValue<T> Read<T>(
        DeclaredConfigKey<T> declared,
        ConfigAddressTemplate template,
        ConfigNode node,
        ConfigDimensionValues point) =>
        template.Resolve(point) is { } path
            ? ReadValue(declared, node, path, point)
            : LayerValue<T>.None;

    /// <summary>
    /// Reads one step of the chain out of the application configuration of the host. The core level
    /// has no record, so the node is built over the SECTION the address names and the value is read at
    /// the rest of the path — which is why a core address always names both (checked when the
    /// declaration is built).
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="template">Address of the core level.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    /// <remarks>
    /// The node is rebuilt on every resolution rather than captured once: a reload of the
    /// configuration reaches the next resolution, exactly as it does for a core getter over
    /// <c>IOptionsMonitor.CurrentValue</c>.
    /// </remarks>
    private LayerValue<T> ReadCore<T>(
        DeclaredConfigKey<T> declared,
        ConfigAddressTemplate template,
        ConfigDimensionValues point) =>
        CoreStep(template, point) is var (node, path)
            ? ReadValue(declared, node, path, point)
            : LayerValue<T>.None;

    /// <summary>
    /// What the core level states at one step of the chain over a record stating nothing — the answer
    /// the level gives where the value it does state is one the domain of the key does not admit
    /// (CFG-240). It is the same question a value of a refused shape is answered with
    /// (<see cref="ReadWhereTheRecordStatesNothing{T}"/>), so a setting the product ships a default for
    /// answers with that default whichever of the two ways the stated value proved unusable.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="template">Address of the core level.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step over a silent record.</returns>
    private LayerValue<T> ReadCoreWhereNothingIsStated<T>(
        DeclaredConfigKey<T> declared,
        ConfigAddressTemplate template,
        ConfigDimensionValues point) =>
        CoreStep(template, point) is var (node, path)
            ? ReadWhereTheRecordStatesNothing(declared, node, path, point)
            : LayerValue<T>.None;

    /// <summary>
    /// Splits a core address resolved at a chain point into the node over the SECTION it names and the
    /// path of the value within that section.
    /// </summary>
    /// <param name="template">Address of the core level.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>The node and the path within it; null when the address does not resolve at the point.</returns>
    private (ConfigNode Node, string Path)? CoreStep(ConfigAddressTemplate template, ConfigDimensionValues point)
    {
        if (template.Resolve(point) is not { } path)
        {
            return null;
        }

        var separator = path.IndexOf(ConfigNode.PathSeparator);
        var node = ConfigNode.Over(
            _configuration.GetSection(path[..separator]),
            ConfigLevel.Core,
            recordLabel: null);

        return (node, path[(separator + 1)..]);
    }

    /// <summary>
    /// Reads the value at the resolved address — through the parse hook of the key where it states
    /// one, and through the configuration binder otherwise.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="node">Subtree the address is relative to.</param>
    /// <param name="path">Address the step resolved to.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    /// <remarks>
    /// A value the parser cannot take is caught HERE, on the boundary of the resolution path. Letting
    /// it out would take a broken entry of one owner out through every resolution that touches the
    /// level, and the deployment's own answer to a rejected value — declared by the key — would never
    /// be reached. What the step is left with afterwards depends on WHICH failure it was, and for the
    /// one that says nothing about the value it is asked of the KEY rather than decided here (see
    /// <see cref="ReadWhereTheRecordStatesNothing{T}"/>).
    /// </remarks>
    private LayerValue<T> ReadValue<T>(
        DeclaredConfigKey<T> declared,
        ConfigNode node,
        string path,
        ConfigDimensionValues point)
    {
        try
        {
            return declared.StatedAtStep(node, path, point);
        }
        // A value stated in a shape the setting is not read from is the ONE failure no parser even got
        // to attempt: the deployment has not been told that what it wrote is outside what the setting
        // admits, and the binder of an options class passes such a value over in silence and keeps
        // the class default. So the level is left with what the key states over a record saying nothing.
        catch (ConfigValueShapeException failure)
        {
            // The address the failure names, not the one the read was asked at: a parse hook reads
            // several members of its level, and the member whose SHAPE the read refused is the one an
            // operator has to go and repair. Naming the declared address instead would call a correctly
            // written member broken.
            Report(declared, node, failure.Path, failure, AnswersAsOverASilentRecord);

            return ReadWhereTheRecordStatesNothing(declared, node, path, point);
        }
        // EVERY other failure of the read is caught here too, not an enumerated few of them: the
        // reading path spans three dialects — the configuration binder, the type converter of the
        // platform and the JSON reader a store's record is read with — and the parse hook of a key is
        // the owner's own code on top of them, so the set of types a bad value can arrive as is not one
        // this boundary can hold a list of. Cancellation is not a failure of a value and travels on.
        // At the core level of a key that governs the levels above it the failure is answered the way a
        // refused shape is, whatever its type: leaving that step unset would lift the ceiling or open
        // the gate for the levels above (CFG-211/CFG-212).
        catch (Exception failure) when (failure is not OperationCanceledException
            && GovernsTheLevelsAbove(declared, node))
        {
            Report(declared, node, path, failure, AnswersAsOverASilentRecord);

            return ReadWhereTheRecordStatesNothing(declared, node, path, point);
        }
        // Anywhere else such a value IS one the deployment stated and got wrong, and refusing it leaves
        // the step unset (CFG-240).
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Report(declared, node, path, failure, LeavesTheStepUnset);

            return LayerValue<T>.None;
        }
    }

    /// <summary>
    /// Whether a failed read of this level must not leave the step unset, whatever the failure: the
    /// level is the CORE one, and the key governs the levels above it — a protective ceiling or a
    /// value whose override needs a gate (CFG-212), or a set whose core level takes part in the
    /// intersection as its ceiling (<see cref="ConfigKey{T}.CoreParticipatesInSetIntersection"/>,
    /// CFG-211). The resolver takes the first level that states a value as the base of a protective or
    /// gated key and applies neither the ceiling nor the gate to it, and it intersects a set over the
    /// levels that state one, so an unset core level would leave the levels above it uncapped and
    /// ungated.
    /// <para>
    /// Such a level is answered as over a record stating nothing
    /// (<see cref="ReadWhereTheRecordStatesNothing{T}"/>) — the path a value of a shape the setting is
    /// not read from already takes — so what it states is the key's own answer. In today's delivery
    /// every protective and gated key answers there with its shipped value and its shipped gate, which
    /// is closed, and every set whose core level is a ceiling answers with its shipped set, which is not
    /// empty. Not covered: a set whose core level stays out of the intersection — that level caps
    /// nothing above the tenant, and below the tenant it is the base of the narrowing only while it is
    /// stated and allows something, so a failed read leaves the step unset (CFG-240) and the levels
    /// below the tenant intersect among themselves, as for an unstated core set — and every level above
    /// the core, where a failed read leaves the step unset and the value already accumulated below it
    /// stands.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="node">Subtree the read failed over.</param>
    /// <returns><c>true</c> when the level is answered as over a record stating nothing.</returns>
    private static bool GovernsTheLevelsAbove<T>(DeclaredConfigKey<T> declared, ConfigNode node) =>
        node.Level is ConfigLevel.Core
        && (declared.Key.Kind is ConfigKeyKind.ProtectiveCeiling or ConfigKeyKind.GatedValue
            || declared.Key is { Kind: ConfigKeyKind.Set, CoreParticipatesInSetIntersection: true });

    /// <summary>
    /// What the step is left with after a value stated in a shape the setting is not read from — or any
    /// failed read of the core level of a key that governs the levels above it
    /// (<see cref="GovernsTheLevelsAbove{T}"/>) — has been reported: the key is asked AGAIN, over the
    /// same record STATING NOTHING
    /// (<see cref="ConfigNode.WhereNothingIsStated"/>).
    /// <para>
    /// The question this answers belongs to the key and not to this boundary. A key whose owner
    /// declared that a level ALWAYS states a value — the core level of a setting the product ships a
    /// default for — states that default over a silent record, and a section holding <c>{ }</c> where a
    /// number belongs must leave the level with the same value as a section holding nothing at all:
    /// before the levels were read by address the binder of the options class did exactly that,
    /// silently. Deciding it here instead would mean a second copy of every shipped default, in the one
    /// place that has no way of knowing them.
    /// </para>
    /// <para>
    /// The record is silenced WHOLE and not at the rejected address alone, because a hook is free to
    /// read several members of its level: the path of a custom script together with its SRI hash, a
    /// themed map beside the plain field, a value beside the gate that governs it. Silencing one member
    /// of such a level would hand the resolution a value half stated and half invented — a script path
    /// that quietly lost its integrity pin — while a level asked over a silent record answers as a
    /// WHOLE: the shipped default where the owner declared the level always speaks, and nothing at all
    /// where the level may cede, so the pair below it arrives intact (CFG-210).
    /// </para>
    /// <para>
    /// The report above is made once: this second read is silent, so an operator sees one warning per
    /// value, not two.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="node">Subtree the address is relative to.</param>
    /// <param name="path">Address the step resolved to.</param>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>Layer value of the step.</returns>
    private static LayerValue<T> ReadWhereTheRecordStatesNothing<T>(
        DeclaredConfigKey<T> declared,
        ConfigNode node,
        string path,
        ConfigDimensionValues point)
    {
        try
        {
            return declared.StatedAtStep(node.WhereNothingIsStated(), path, point);
        }
        // The guard of this type holds for the second read as well: a hook is the owner's own code, and
        // a hook that fails over a record stating nothing must still not take the resolution down with
        // it. Cancellation is not a failure of a value and travels on.
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return LayerValue<T>.None;
        }
    }

    /// <summary>
    /// Names the value that could not be read: the key, the level, the record it came from, the
    /// address of the value and the failure that ended the read — never the value itself. It says what
    /// the level does next in the words of the mechanism (the two clauses below) rather than as a fact
    /// about this key, because the value a level asked again lands on is the key's own answer and this
    /// boundary does not know it.
    /// <para>
    /// The address is the one the FAILURE names where it knows one, and the address the read was asked
    /// at otherwise — the caller decides which, because only the caller has the failure in hand. The two
    /// part on a key whose parse hook reads several members of its level: the read is asked at the
    /// address of the declaration and refuses a value at a member beside it, and an operator sent to the
    /// declared address would find a correctly written value there. The address is spelled as precisely
    /// as the source of the record can spell it — a section of the application configuration names the
    /// whole configuration key, a record of a store the path inside itself.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="declared">Declaration of the key.</param>
    /// <param name="node">Subtree the address is relative to.</param>
    /// <param name="path">Address of the value the read refused.</param>
    /// <param name="failure">Failure of the parser.</param>
    /// <param name="outcome">What the level does next — one of the two clauses of this type.</param>
    private void Report<T>(
        DeclaredConfigKey<T> declared,
        ConfigNode node,
        string path,
        Exception failure,
        string outcome)
    {
        // The failure of a parser quotes the text it choked on, so it travels to the log only for a
        // setting whose value is not a secret: a secret never enters diagnostics, whatever shape it
        // arrived in (ConfigKey{T}.IsSecret). For such a key the reason is reduced to the TYPE of the
        // failure — no text of the exception, and therefore no text of the value, reaches the log,
        // while an operator still tells a rejected value apart from a defect in the parse hook itself.
        if (declared.Key.IsSecret)
        {
            _logger.LogWarning(
                "Configuration key {ConfigKey} cannot be read at level {ConfigLevel}: the record {ConfigRecord} "
                + "states a value at '{ConfigPath}' that cannot be read into the type of the setting: {Reason}. "
                + "{ConfigOutcome}",
                declared.Name,
                node.Level,
                node.RecordLabel,
                path,
                failure.GetType().Name,
                outcome);

            return;
        }

        _logger.LogWarning(
            failure,
            "Configuration key {ConfigKey} cannot be read at level {ConfigLevel}: the record {ConfigRecord} "
            + "states a value at '{ConfigPath}' that cannot be read into the type of the setting. "
            + "{ConfigOutcome}",
            declared.Name,
            node.Level,
            node.RecordLabel,
            path,
            outcome);
    }

    /// <summary>
    /// Pass that DECLARES every key of the catalogs — the step that fills the schema of the deployment.
    /// </summary>
    /// <param name="keys">Registry of key declarations.</param>
    private sealed class DeclarationPass(IConfigKeyDeclarations keys) : IConfigKeyDeclarationVisitor
    {
        /// <inheritdoc />
        public void Visit<T>(DeclaredConfigKey<T> declared) =>
            keys.Declare(declared.Key, declared.DeclaredDefault, declared.NotWalked);
    }

    /// <summary>
    /// Pass that BINDS every level of every key of the catalogs.
    /// </summary>
    /// <param name="registrar">Registrar the bindings are read by.</param>
    /// <param name="bindings">Registry of level bindings over source records.</param>
    /// <param name="coreBindings">Registry of core-level bindings.</param>
    private sealed class BindingPass(
        PathConfigKeyRegistrar registrar,
        IConfigBindings bindings,
        IConfigCoreBindings coreBindings) : IConfigKeyDeclarationVisitor
    {
        /// <inheritdoc />
        public void Visit<T>(DeclaredConfigKey<T> declared) => registrar.Bind(declared, bindings, coreBindings);
    }
}
