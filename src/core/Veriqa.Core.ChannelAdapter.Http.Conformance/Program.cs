// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Runtime.InteropServices;

using Veriqa.Core.ChannelAdapter.Http.Conformance;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

// The conformance suite of the HTTP channel wire (SPEC-042 R23). Its configuration comes from the
// standard providers — environment variables and the command line — and the address of the receiver
// of events is the standard configuration of Kestrel. Exit code: 0 — no scenario failed, 1 — at least
// one failed, 2 — the run could not be carried out; the message of a code 2 names what to fix.
//
// SIGINT and SIGTERM are taken here alone (the hosts of the suite do not listen to them): the first
// cancels the run, which stops with code 2 and no report — a report of the scenarios that ran would
// read as the verdict of a whole run; a second signal ends the process at once.
using var interruption = new CancellationTokenSource();
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Interrupt);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Interrupt);

var builder = WebApplication.CreateSlimBuilder(args);

try
{
    var options = ConformanceOptionsReader.Read(builder.Configuration);
    return await ConformanceRunner.RunAsync(builder, options, interruption.Token);
}
catch (OperationCanceledException) when (interruption.IsCancellationRequested)
{
    await Console.Error.WriteLineAsync("The run was interrupted by a signal; no report is written.");
    return ConformanceExitCodes.NotRun;
}
catch (ConformanceSetupException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return ConformanceExitCodes.NotRun;
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync("The conformance suite failed: " + exception.GetType().Name + ": " + exception.Message);
    return ConformanceExitCodes.NotRun;
}

// The first signal cancels the run and keeps the process; a signal after it takes its default action.
void Interrupt(PosixSignalContext signal)
{
    signal.Cancel = !interruption.IsCancellationRequested;
    interruption.Cancel();
}
