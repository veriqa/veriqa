// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;

/// <summary>
/// What sets one composition of the wrapper apart from the working one. Nothing else differs: the same
/// configuration, the same channel type, the same seams of the suite.
/// </summary>
/// <param name="Name">Name of the variant in the messages of the suite.</param>
/// <param name="ConfigurationOverlay">Keys laid over the configuration of the suite; <c>null</c> — none.</param>
/// <param name="Clock">Clock of the composition; <c>null</c> — the system clock.</param>
internal sealed record WrapperVariant(
    string Name,
    IReadOnlyDictionary<string, string?>? ConfigurationOverlay,
    TimeProvider? Clock);
