// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Platform;

/// <summary>
/// A call of the platform stub did not succeed: no answer, a status outside 2xx, or a body outside the
/// contract of the stub. It fails the scenario that made the call; the message names the call.
/// </summary>
internal sealed class PlatformStubException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What went wrong, naming the call of the stub.</param>
    public PlatformStubException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What went wrong, naming the call of the stub.</param>
    /// <param name="innerException">The cause.</param>
    public PlatformStubException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
