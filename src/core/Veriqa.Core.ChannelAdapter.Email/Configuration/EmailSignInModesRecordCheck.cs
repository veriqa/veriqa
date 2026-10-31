// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Warns about a record of a tenant or an application whose set of the Email sign-in modes
/// (<see cref="EmailConfigKeys.SignInModesRecordAddress"/>) can never narrow anything: written as an empty
/// list, or naming no mode of the core ceiling. The resolution skips such a level in silence — the set of
/// the level above it applies (<see cref="EmailConfigKeys.SignInModes"/>) — so this is where the operator
/// hears that the record does not do what it says. The set of what the sign-in mail carries
/// (<see cref="EmailConfigKeys.PullMethodsRecordAddress"/>) written as an empty list is skipped the same way
/// (<see cref="EmailConfigKeys.PullMethods"/>), and the same walk warns about it.
/// <para>
/// The records are those of the walkers of the tenant and the application levels that are the source of
/// their level in this deployment — the walk the report of a configuration snapshot makes — and the check
/// is repeated on every reload a walker announces. A level without such a walker is not checked. A value
/// the set cannot be read from is not judged here: the report of the snapshot names it by the domain of
/// the key. Each finding is written once and stays quiet while it is still there, as in that report.
/// </para>
/// </summary>
internal sealed class EmailSignInModesRecordCheck : IHostedService, IDisposable
{
    /// <summary>
    /// How a ceiling with no mode is named in the warning.
    /// </summary>
    private const string NoModeText = "none";

    /// <summary>
    /// Separator of the modes of the ceiling in the warning.
    /// </summary>
    private const string ModeSeparator = ", ";

    /// <summary>
    /// Walkers of the tenant and the application levels that are the source of their level.
    /// </summary>
    private readonly IReadOnlyList<IConfigNodeWalker> _walkers;

    /// <summary>
    /// Global Email channel settings as bound at the first read — the source of the ceiling, the same
    /// snapshot the resolver reads it from.
    /// </summary>
    private readonly IOptions<EmailOptions> _options;

    /// <summary>
    /// Logger of the check.
    /// </summary>
    private readonly ILogger<EmailSignInModesRecordCheck> _logger;

    /// <summary>
    /// Subscriptions to the reloads of the walked levels; disposed together with the check.
    /// </summary>
    private readonly List<IDisposable> _subscriptions = [];

    /// <summary>
    /// Guards the walk and the comparison with the findings already written: a single edit may wake the
    /// subscriptions of several levels at once.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Findings the last walk held; only a finding absent from it is written.
    /// </summary>
    private IReadOnlySet<Finding> _reported = new HashSet<Finding>();

    /// <summary>
    /// Creates the check of the records.
    /// </summary>
    /// <param name="walkers">Walkers of the records of the configuration levels.</param>
    /// <param name="options">Global Email channel settings as bound at the first read.</param>
    /// <param name="logger">Logger.</param>
    public EmailSignInModesRecordCheck(
        IEnumerable<IConfigNodeWalker> walkers,
        IOptions<EmailOptions> options,
        ILogger<EmailSignInModesRecordCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(walkers);

        _walkers =
        [
            .. walkers.Where(static walker =>
                walker.CanWalk && walker.Level is ConfigLevel.Tenant or ConfigLevel.Application)
        ];
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribed before the first walk: a reload landing between the two would otherwise be lost
        // until the next one, and a reload racing the first walk is dropped by the deduplication.
        foreach (var walker in _walkers)
        {
            if (walker.Subscribe(Check) is { } subscription)
            {
                _subscriptions.Add(subscription);
            }
        }

        Check();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    /// <summary>
    /// Whether a record states a set of the sign-in modes that narrows nothing: written as an empty list,
    /// or naming no mode of the ceiling.
    /// </summary>
    /// <param name="node">Node of the record.</param>
    /// <param name="ceiling">Modes the deployment serves.</param>
    /// <returns><c>true</c> when the record states such a set.</returns>
    private static bool StatesSetNarrowingNothing(ConfigNode node, IReadOnlyList<EmailMode> ceiling)
    {
        var stated = node.Children(EmailConfigKeys.RecordSection).Any(static member =>
            string.Equals(member, EmailConfigKeys.SignInModesMember, StringComparison.OrdinalIgnoreCase));

        if (!stated)
        {
            return false;
        }

        // The configuration of the platform keeps an empty list as a member holding nothing: it is among
        // the members of the section and yet states no value.
        if (!node.Has(EmailConfigKeys.SignInModesRecordAddress))
        {
            return true;
        }

        IReadOnlyList<EmailMode> modes;

        try
        {
            if (!node.TryRead<IReadOnlyList<EmailMode>>(EmailConfigKeys.SignInModesRecordAddress, out var read))
            {
                return true;
            }

            modes = read;
        }

        // A set that cannot be read is named by the report of the snapshot, by the domain of the key; the
        // read fails with the exceptions of whichever dialect the record is read in. Cancellation is not a
        // configuration failure and stays unhandled.
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return false;
        }

        return EmailConfigKeys.IsWellFormedModeSet(modes) && !modes.Any(mode => ceiling.Contains(mode));
    }

    /// <summary>
    /// Whether a record states the set of what the sign-in mail carries as an empty list. The configuration of
    /// the platform keeps such a list as a member holding nothing — among the members of the section and yet
    /// stating no value — so the level states nothing and the set of the level above it applies.
    /// </summary>
    /// <param name="node">Node of the record.</param>
    /// <returns><c>true</c> when the record states the set as an empty list.</returns>
    private static bool StatesEmptyPullMethods(ConfigNode node) =>
        node.Children(EmailConfigKeys.RecordSection).Any(static member =>
            string.Equals(member, EmailConfigKeys.PullMethodsMember, StringComparison.OrdinalIgnoreCase))
        && !node.Has(EmailConfigKeys.PullMethodsRecordAddress);

    /// <summary>
    /// The ceiling as the warning names it.
    /// </summary>
    /// <param name="ceiling">Modes the deployment serves.</param>
    /// <returns>Text of the ceiling.</returns>
    private static string CeilingText(IReadOnlyList<EmailMode> ceiling) =>
        ceiling.Count > 0 ? string.Join(ModeSeparator, ceiling) : NoModeText;

    /// <summary>
    /// Walks the records of every walked level and writes the findings the previous walk did not hold.
    /// </summary>
    private void Check()
    {
        lock (_gate)
        {
            IReadOnlyList<EmailMode> ceiling;

            try
            {
                ceiling = EmailConfigKeyRegistrar.SignInModeCeiling(_options.Value);
            }

            // An Email section that cannot be bound is named by the report of the snapshot; the records
            // are judged again on the next reload a walker announces. Cancellation stays unhandled.
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return;
            }

            var findings = new HashSet<Finding>();

            foreach (var walker in _walkers)
            {
                CollectFindings(walker, ceiling, findings);
            }

            foreach (var finding in findings.Where(finding => !_reported.Contains(finding)))
            {
                if (finding.Setting is FindingSetting.PullMethods)
                {
                    _logger.LogWarning(
                        "The Email pull methods at {Address} of the {Level} record {Record} are an empty list: the level "
                        + "is skipped and the set of the level above it applies. Name MagicLink, Code or both there, "
                        + "or remove the member.",
                        finding.Address,
                        finding.Level,
                        finding.Record);

                    continue;
                }

                _logger.LogWarning(
                    "The Email sign-in modes at {Address} of the {Level} record {Record} name no mode the deployment "
                    + "serves ({Ceiling}): the level is skipped and the set of the level above it applies. Name at "
                    + "least one of the served modes there. The list does not switch Email off: the deployment "
                    + "switches the channel off with Enabled of its Email section, and a client narrows the "
                    + "channels of its sign-ins with acr_values.",
                    finding.Address,
                    finding.Level,
                    finding.Record,
                    finding.Ceiling);
            }

            _reported = findings;
        }
    }

    /// <summary>
    /// Adds the findings of the records of one level.
    /// </summary>
    /// <param name="walker">Walker of the level.</param>
    /// <param name="ceiling">Modes the deployment serves.</param>
    /// <param name="findings">Findings of this walk.</param>
    private static void CollectFindings(IConfigNodeWalker walker, IReadOnlyList<EmailMode> ceiling, HashSet<Finding> findings)
    {
        IReadOnlyList<ConfigNode> nodes;

        try
        {
            nodes = [.. walker.Walk()];
        }

        // A level that cannot be walked is named by the report of the snapshot, which walks the same
        // records; the other levels are still checked. Cancellation stays unhandled.
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            nodes = [];
        }

        foreach (var node in nodes.Where(node => StatesSetNarrowingNothing(node, ceiling)))
        {
            findings.Add(new Finding(
                FindingSetting.SignInModes,
                node.Level,
                node.RecordLabel,
                node.ConfigurationKeyOf(EmailConfigKeys.SignInModesRecordAddress) ?? EmailConfigKeys.SignInModesRecordAddress,
                CeilingText(ceiling)));
        }

        foreach (var node in nodes.Where(StatesEmptyPullMethods))
        {
            findings.Add(new Finding(
                FindingSetting.PullMethods,
                node.Level,
                node.RecordLabel,
                node.ConfigurationKeyOf(EmailConfigKeys.PullMethodsRecordAddress) ?? EmailConfigKeys.PullMethodsRecordAddress,
                Ceiling: null));
        }
    }

    /// <summary>
    /// The setting a finding is about.
    /// </summary>
    private enum FindingSetting
    {
        /// <summary>
        /// The set of the sign-in modes, which names no mode the deployment serves.
        /// </summary>
        SignInModes,

        /// <summary>
        /// The set of what the sign-in mail carries, written as an empty list.
        /// </summary>
        PullMethods
    }

    /// <summary>
    /// One record stating a set that narrows nothing, or one stating the set of what the sign-in mail carries
    /// as an empty list.
    /// </summary>
    /// <param name="Setting">The setting the finding is about.</param>
    /// <param name="Level">Level of the record.</param>
    /// <param name="Record">Label of the record; null when it has none.</param>
    /// <param name="Address">Configuration key of the set, or its address inside the record.</param>
    /// <param name="Ceiling">Ceiling the set of the sign-in modes was judged against, as the warning names it;
    /// null for the set of what the sign-in mail carries, which no ceiling judges.</param>
    private readonly record struct Finding(
        FindingSetting Setting,
        ConfigLevel Level,
        string? Record,
        string Address,
        string? Ceiling);
}
