// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Http.Conformance.Platform;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Sending;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

/// <summary>
/// Who talks to the adapter in a run besides the wrapper: the sender of the calls the wrapper does not
/// make, the receiver of its events and the stub of its platform.
/// </summary>
/// <param name="Sender">The sender; present in every mode.</param>
/// <param name="Receiver">The receiver; <c>null</c> in the calls mode.</param>
/// <param name="PlatformStub">The platform stub; <c>null</c> outside the automatic mode.</param>
internal sealed record ConformanceParties(
    OutOfWrapperSender Sender,
    EventReceiver? Receiver,
    PlatformStubClient? PlatformStub);
