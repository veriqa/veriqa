// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance;

/// <summary>
/// The suite could not carry out the run — a configuration of the suite or of the composition it
/// rejects, or a seam of the suite missing. It ends the run with <see cref="ConformanceExitCodes.NotRun"/>;
/// the message is printed as it is and names what to fix. The message never carries a secret, the
/// settings string or a body of the wire.
/// </summary>
internal sealed class ConformanceSetupException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What prevented the run.</param>
    public ConformanceSetupException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What prevented the run.</param>
    /// <param name="innerException">The cause.</param>
    public ConformanceSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
