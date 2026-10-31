// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

using Microsoft.AspNetCore.Builder;

using Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Platform;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Sending;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

/// <summary>
/// A run of the suite: starts the compositions of the wrapper and, outside the calls mode, the receiver
/// of events, runs the scenarios of the mode one after another, prints a line per scenario —
/// <c>PASS|FAIL|N/A &lt;id&gt;: &lt;reason&gt;</c> — and writes the JSON report when a path is set. The
/// exit code is <see cref="ConformanceExitCodes.Violations"/> when a scenario failed,
/// <see cref="ConformanceExitCodes.Conforms"/> otherwise; what prevents the run is a
/// <see cref="ConformanceSetupException"/> for the caller.
/// </summary>
internal static class ConformanceRunner
{
    /// <summary>
    /// Line prefix of a passed scenario.
    /// </summary>
    private const string PassedLinePrefix = "PASS";

    /// <summary>
    /// Line prefix of a failed scenario.
    /// </summary>
    private const string FailedLinePrefix = "FAIL";

    /// <summary>
    /// Line prefix of a scenario that does not apply.
    /// </summary>
    private const string NotApplicableLinePrefix = "N/A";

    /// <summary>
    /// Report value of a passed scenario and of a run with no failed scenario.
    /// </summary>
    private const string PassedValue = "passed";

    /// <summary>
    /// Report value of a failed scenario and of a run with one.
    /// </summary>
    private const string FailedValue = "failed";

    /// <summary>
    /// Report value of a scenario that does not apply.
    /// </summary>
    private const string NotApplicableValue = "not_applicable";

    /// <summary>
    /// Carries out a run.
    /// </summary>
    /// <param name="builder">The builder of the suite: its configuration, and the application of the receiver.</param>
    /// <param name="options">Options of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ConformanceSetupException">The run cannot be carried out.</exception>
    public static async Task<int> RunAsync(WebApplicationBuilder builder, ConformanceOptions options, CancellationToken cancellationToken)
    {
        var configuration = builder.Configuration;
        var schema = WireSchema.Load();
        var seams = new WrapperSeams(new WireCallRecorder(), new ConformanceInboundEventIds());

        // Every composition is a started host before the first scenario.
        await using var compositions = await WrapperCompositions.StartAsync(configuration, options.ChannelType, seams, cancellationToken);

        var working = compositions.Working;
        var receiver = options.Mode is ConformanceModes.Calls
            ? null
            : new EventReceiver(working.Adapter, schema, seams.EventIds);
        var parties = new ConformanceParties(
            OutOfWrapperSender.Create(configuration, options.ChannelType, working.HttpClients, seams.Recorder),
            receiver,
            options.PlatformStubUrl is { } platformStubUrl ? new PlatformStubClient(working.HttpClients, platformStubUrl) : null);

        // The receiver listens before the first scenario and until the last one: the adapter may post
        // an event at any moment of the run. It stops before the compositions it calls through.
        await using var receiverHost = receiver is null
            ? null
            : await EventReceiverHost.StartAsync(builder, options.ChannelType, receiver, cancellationToken);

        var context = new ConformanceContext(options, compositions, seams, schema, parties);
        var reported = new List<ConformanceReportScenario>();

        // The facts of the registration are those of the started working composition: the scenarios of
        // the phone number and of the start by a code join the run by what it declares.
        foreach (var scenario in ConformanceScenarioCatalog.For(options.Mode, working.Adapter.Capabilities))
        {
            var outcome = await RunScenarioAsync(scenario, context, cancellationToken);

            // A scenario the cancellation cut short came to no verdict, whatever it returned.
            cancellationToken.ThrowIfCancellationRequested();

            await Console.Out.WriteLineAsync(LineOf(scenario.Id, outcome));
            reported.Add(new ConformanceReportScenario
            {
                Id = scenario.Id,
                Status = StatusValueOf(outcome.Status),
                Reason = outcome.Reason
            });
        }

        var anyFailed = reported.Exists(static scenario => scenario.Status is FailedValue);

        if (options.ReportPath is { } reportPath)
        {
            await WriteReportAsync(
                reportPath,
                new ConformanceReport
                {
                    WireVersion = HttpChannelWire.Version,
                    Mode = ConformanceOptionsReader.NameOf(options.Mode),
                    ChannelType = options.ChannelType,
                    Outcome = anyFailed ? FailedValue : PassedValue,
                    Scenarios = reported
                });
        }

        return anyFailed ? ConformanceExitCodes.Violations : ConformanceExitCodes.Conforms;
    }

    /// <summary>
    /// Runs one scenario. An exception of the scenario is its failure — the adapter drove the wrapper
    /// somewhere the scenario did not expect; an exception that prevents the run propagates.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> RunScenarioAsync(
        ConformanceScenario scenario,
        ConformanceContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await scenario.RunAsync(context, cancellationToken);
        }
        catch (PlatformStubException exception)
        {
            return ScenarioOutcome.Failed(exception.Message);
        }
        catch (Exception exception)
            when (exception is not ConformanceSetupException
                  && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return ScenarioOutcome.Failed("the scenario ended with " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// The output line of a scenario.
    /// </summary>
    /// <param name="id">Identifier of the scenario.</param>
    /// <param name="outcome">Its outcome.</param>
    /// <returns>The line.</returns>
    private static string LineOf(string id, ScenarioOutcome outcome)
    {
        var prefix = outcome.Status switch
        {
            ScenarioStatus.Passed => PassedLinePrefix,
            ScenarioStatus.Failed => FailedLinePrefix,
            _ => NotApplicableLinePrefix
        };

        return outcome.Reason is null
            ? prefix + " " + id
            : prefix + " " + id + ": " + outcome.Reason;
    }

    /// <summary>
    /// The report value of a status.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>The value.</returns>
    private static string StatusValueOf(ScenarioStatus status) => status switch
    {
        ScenarioStatus.Passed => PassedValue,
        ScenarioStatus.Failed => FailedValue,
        _ => NotApplicableValue
    };

    /// <summary>
    /// Writes the report, creating its directory when needed. It is written whole once every scenario has
    /// run: a signal that comes while it is written does not cut the verdict of the run short.
    /// </summary>
    /// <param name="path">Path of the report.</param>
    /// <param name="report">The report.</param>
    /// <returns>A task that completes when the report is written.</returns>
    /// <exception cref="ConformanceSetupException">The report cannot be written.</exception>
    private static async Task WriteReportAsync(string path, ConformanceReport report)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(
                path,
                JsonSerializer.SerializeToUtf8Bytes(report, ConformanceReportJsonContext.Default.ConformanceReport),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ConformanceSetupException(
                "The report cannot be written to '" + ConformanceConfigKeys.PathOf(ConformanceConfigKeys.ReportPath) + "' = '" + path + "': " + exception.Message,
                exception);
        }
    }
}
