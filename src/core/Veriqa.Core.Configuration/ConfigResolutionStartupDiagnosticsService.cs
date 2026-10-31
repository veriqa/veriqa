// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Veriqa.Core.Configuration;

/// <summary>
/// Startup report of the configuration mechanism (SPEC-012 §10.6). It prints the two things an
/// operator needs at deployment time and nothing else:
/// <list type="bullet">
/// <item><description>
/// the MAP of the contour — which source serves which level, and which levels this contour does not
/// have at all. A missing level is a line of the map, not a warning: which levels exist is a property
/// of the deployment (CFG-202 — in self-hosted the tenant IS the core), and warning about it would
/// mean shouting on every normal start.
/// </description></item>
/// <item><description>
/// the DISCREPANCY "a level is declared but has no binding" — the key declares a level, the contour
/// has a source for that level, and no extraction for the pair "key + level" was registered. This is
/// what happens when the boundaries of a setting are widened and the binding is forgotten.
/// </description></item>
/// </list>
/// <para>
/// Registration errors that would make a level silently stop working (two sources on one level, a
/// duplicate binding, a binding to an unserved level) are not reported here — they stop the start,
/// and they do so in the map and the registry themselves, which is why this service is the place
/// where they surface.
/// </para>
/// <para>
/// One defect of a DECLARATION is refused here rather than in the registry, because it is the only
/// one that can be seen over the catalogs as a whole: an address of a level ABOVE the core one that
/// names a section of the application configuration of the host
/// (<see cref="RefuseAddressesNamingTheHostStore"/>). It stops the start whatever level it is found
/// at — a defect of a declaration is a defect of code, and the level-by-level answer of CFG-246
/// applies to VALUES.
/// </para>
/// </summary>
internal sealed class ConfigResolutionStartupDiagnosticsService : IHostedService
{
    /// <summary>
    /// Map "level → source" of the contour.
    /// </summary>
    private readonly ConfigSourceMap _sources;

    /// <summary>
    /// Registry of the extraction bindings.
    /// </summary>
    private readonly ConfigBindingRegistry _bindings;

    /// <summary>
    /// Slice of the schema of declared keys — the same slice a consumer of the module reads, so the
    /// report and a contour's own check speak about one thing.
    /// </summary>
    private readonly ConfigKeySchemaView _schema;

    /// <summary>
    /// Catalogs of the declarations this deployment registered — the same ones
    /// <see cref="PathConfigKeyRegistrar"/> walks. They are what carries the ADDRESS of each level,
    /// which the schema slice deliberately does not: the slice answers "what does a key declare", and
    /// an address is where the key is read, not what it declares. A deployment that registers no
    /// catalog declares no address, and the check below finds nothing to look at.
    /// </summary>
    private readonly IReadOnlyList<ConfigKeyCatalog> _declarations;

    /// <summary>
    /// Levels the contour reads through a reader of DOCUMENTS — a record walked by path
    /// (<see cref="ConfigurationResolverServiceCollectionExtensions.AddVeriqaConfigLevelNodeReader{TReader}"/>).
    /// A level a key addresses by IDENTITY is never bound over such a reader
    /// (<see cref="PathConfigKeyRegistrar"/>), so on these levels the absence of its binding is how the
    /// contour is built rather than a forgotten one.
    /// </summary>
    private readonly IReadOnlySet<ConfigLevel> _documentReadLevels;

    /// <summary>
    /// Logger of the startup report.
    /// </summary>
    private readonly ILogger<ConfigResolutionStartupDiagnosticsService> _logger;

    /// <summary>
    /// Creates the startup report service.
    /// </summary>
    /// <param name="sources">Map of the contour.</param>
    /// <param name="bindings">Registry of the extraction bindings.</param>
    /// <param name="schema">Slice of the schema of declared keys.</param>
    /// <param name="declarations">Catalogs of the declarations of this deployment.</param>
    /// <param name="documentReadLevels">Levels the contour reads through a reader of documents.</param>
    /// <param name="logger">Logger.</param>
    public ConfigResolutionStartupDiagnosticsService(
        ConfigSourceMap sources,
        ConfigBindingRegistry bindings,
        ConfigKeySchemaView schema,
        IEnumerable<ConfigKeyCatalog> declarations,
        IReadOnlySet<ConfigLevel> documentReadLevels,
        ILogger<ConfigResolutionStartupDiagnosticsService> logger)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(documentReadLevels);

        _sources = sources;
        _bindings = bindings;
        _schema = schema;
        _declarations = [.. declarations];
        _documentReadLevels = documentReadLevels;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A level above the core one is addressed at a section of the host's own configuration — see
    /// <see cref="RefuseAddressesNamingTheHostStore"/>.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ReportContourMap();
        ReportSchema();
        ReportUnboundKeys();
        ReportUnboundLevels();

        // Last, after the whole report: it throws, and anything left behind it would never reach the
        // operator — the same order the walk of the snapshot refuses a start in.
        RefuseAddressesNamingTheHostStore();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Prints the map of the contour: one line per served level and one line listing the levels this
    /// deployment does not have.
    /// </summary>
    private void ReportContourMap()
    {
        foreach (var level in _sources.ServedLevels)
        {
            var source = _sources.SourceOf(level)!;

            _logger.LogInformation(
                "Configuration level {ConfigLevel} is served by source {ConfigSource} ({ConfigSourceKind}).",
                level,
                source.Name,
                source.IsLive ? "live" : "external");
        }

        var absent = _sources.AbsentLevels.ToArray();
        if (absent.Length > 0)
        {
            _logger.LogInformation(
                "Configuration levels absent in this contour: {ConfigLevels}.",
                string.Join(", ", absent));
        }
    }

    /// <summary>
    /// Prints the SCHEMA of declared keys: the names as one line, and the boundaries of each key —
    /// levels, gates, dimensions, cache policy, domain — one line apiece at the debug level, because
    /// a deployment of forty keys does not need forty lines on every normal start to know that
    /// nothing is wrong.
    /// </summary>
    private void ReportSchema()
    {
        _logger.LogInformation(
            "Configuration schema declares {ConfigKeyCount} keys: {ConfigKeys}.",
            _schema.Keys.Length,
            string.Join(", ", _schema.Keys.Select(key => key.Name)));

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        foreach (var key in _schema.Keys)
        {
            _logger.LogDebug(
                "Configuration key {ConfigKey} ({ConfigKeyKind}): levels {ConfigLevels}, gated {ConfigGatedLevels}, "
                + "dimensions {ConfigDimensions}, cache {ConfigCacheTtl}, domain {ConfigDomain}.",
                key.Name,
                key.Kind,
                Describe(key.Levels.Select(level => level.ToString())),
                Describe(key.GatedLevels.Select(level => level.ToString())),
                Describe(key.Dimensions),
                key.CachePolicy.Ttl is { } ttl ? ttl.ToString() : "none",
                key.Domain ?? "unbounded");
        }
    }

    /// <summary>
    /// Renders a set of a declaration for the report; an empty set is named rather than printed as an
    /// empty string, so a line never reads as if a fact were missing.
    /// </summary>
    /// <param name="values">Values of the set.</param>
    /// <returns>Text of the set.</returns>
    private static string Describe(IEnumerable<string> values)
    {
        var text = string.Join(", ", values);

        return text.Length == 0 ? "none" : text;
    }

    /// <summary>
    /// Names the keys the deployment declared and left WITHOUT A SINGLE binding. Such a key exists,
    /// is part of the schema and never receives a value from anywhere — which is exactly why it is
    /// named: this is what a key declared in one assembly and bound in another looks like when the
    /// contour that binds it is not part of the deployment. It is a line of the report, not an error:
    /// which parts a deployment is composed of is its own decision (CFG-202).
    /// </summary>
    private void ReportUnboundKeys()
    {
        var unbound = _bindings.Keys
            .Where(key => !_bindings.BoundLevelsOf(key.Name).Any())
            .Select(key => key.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (unbound.Length == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Configuration keys declared without a single extraction binding in this deployment: {ConfigKeys}.",
            string.Join(", ", unbound));
    }

    /// <summary>
    /// Warns about every pair "key + level" where the level is declared by the key, served by the
    /// contour, and yet has no extraction binding: such a level is silently empty. Keys with no
    /// binding at all are out of the walk — they are named once by <see cref="ReportUnboundKeys"/>.
    /// <para>
    /// A pair whose level the key addresses by IDENTITY and the contour reads through a reader of
    /// documents is not such a discrepancy: a level without a path is bound by the store that keeps
    /// values as rows, never over a document, so in this contour it has no value for the key by
    /// construction — the degenerate case of a level that is opt-in for the key. Warning about it would
    /// mean warning on every normal start.
    /// </para>
    /// </summary>
    private void ReportUnboundLevels()
    {
        var identityAddressed = IdentityAddressedLevels();

        foreach (var key in _bindings.Keys)
        {
            var bound = _bindings.BoundLevelsOf(key.Name).ToHashSet();

            // A key with NO binding at all is a different fact and is reported as one line above:
            // repeating it here once per declared level would say the same thing five times, and it is
            // not the discrepancy this warning exists for — that one is "the boundaries of a setting
            // were widened and one binding was forgotten", which presupposes the others are there.
            if (bound.Count == 0)
            {
                continue;
            }

            foreach (var level in key.Levels)
            {
                if (bound.Contains(level)
                    || _sources.SourceOf(level) is null
                    || (_documentReadLevels.Contains(level) && identityAddressed.Contains((key.Name, level))))
                {
                    continue;
                }

                _logger.LogWarning(
                    "Configuration key {ConfigKey} declares level {ConfigLevel}, which this contour serves, " +
                    "but no extraction binding is registered for the pair: the level stays empty.",
                    key.Name,
                    level);
            }
        }
    }

    /// <summary>
    /// Pairs "key + level" whose level a declaration of the deployment addresses by IDENTITY — with no
    /// path at all.
    /// </summary>
    /// <returns>Pairs addressed by identity.</returns>
    private HashSet<(string Name, ConfigLevel Level)> IdentityAddressedLevels() =>
        _declarations
            .SelectMany(catalog => catalog.Keys)
            .SelectMany(declared => declared.Levels
                .Where(address => address.Kind is ConfigLevelAddressKind.Identity)
                .Select(address => (declared.Name, address.Level)))
            .ToHashSet();

    /// <summary>
    /// Stops the start on a declaration that addresses a level ABOVE THE CORE ONE at a section of the
    /// application configuration of the host. Such an address names the store the values are kept in,
    /// and the invariant of the model is that a declaring assembly names none: above the core level the
    /// address is relative to the RECORD of that level, and what fetches the record is a reader
    /// (SPEC-012 §10.1, CFG-231). A declaration that reaches past its record reads the host's own
    /// configuration under every owner at once, and no reader stands between it and the store.
    /// <para>
    /// It is a defect of a DECLARATION rather than of a value, so it stops the start whatever level it
    /// was found at: the level-by-level answer of CFG-246 — a record thrown out above the core, a start
    /// refused at it — is about what a snapshot STATES, and there is nothing here to throw out. The
    /// message names the key, the level and the address; a value never enters it, so a secret cannot.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A level above the core one names a host section.</exception>
    private void RefuseAddressesNamingTheHostStore()
    {
        var hostSections = HostConfigurationSections();

        if (hostSections.Count == 0)
        {
            return;
        }

        foreach (var address in DeclaredAddresses())
        {
            if (address.Level is ConfigLevel.Core
                || address.Template?.RootSegment is not { } root
                || !hostSections.Contains(root))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Configuration key '{address.Name}' addresses level {address.Level} at '{address.Text}', which "
                + $"opens at section '{root}' of the application configuration of the host: only the core level is "
                + "addressed from there. Above it an address is relative to the record of its own level, and the "
                + "record is fetched by a reader, so a declaration names no store.");
        }
    }

    /// <summary>
    /// Sections of the host's own configuration this deployment names, taken from the addresses of the
    /// CORE level: the core level is the one whose record IS the application configuration of the host,
    /// so the first segment of such an address is a section of it and nothing else is. The mechanism
    /// knows no section name of its own and could not hold a list of them — it knows no key name and no
    /// record type (CFG-231) — which is why the answer is derived from what the deployment declared.
    /// <para>
    /// Matched case-insensitively, the way a configuration path is matched everywhere else.
    /// </para>
    /// </summary>
    /// <returns>Sections of the host configuration named by the declarations.</returns>
    private HashSet<string> HostConfigurationSections() =>
        DeclaredAddresses()
            .Where(address => address.Level is ConfigLevel.Core)
            .Select(address => address.Template?.RootSegment)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every level address of every declaration of the deployment, with the key it belongs to.
    /// </summary>
    /// <returns>Addresses of the declarations.</returns>
    private IEnumerable<(string Name, ConfigLevel Level, string? Text, ConfigAddressTemplate? Template)>
        DeclaredAddresses() =>
        _declarations
            .SelectMany(catalog => catalog.Keys)
            .SelectMany(declared => declared.Levels.Select(
                address => (declared.Name, address.Level, Text: address.Address, address.Template)));
}
