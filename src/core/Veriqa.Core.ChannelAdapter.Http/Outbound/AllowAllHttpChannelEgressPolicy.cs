// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// The egress policy of a self-hosted installation (SPEC-042 R20): the operator configured the address
/// of the external adapter, so every call to it is allowed.
/// </summary>
internal sealed class AllowAllHttpChannelEgressPolicy : IHttpChannelEgressPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> IsAllowedAsync(HttpChannelEgressRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
