// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Email Pull mode endpoint paths (SPEC-016 §4).
/// These duplicate the values from EmailAdapterConstants of the ChannelAdapter project — solely for use
/// in AuthServer (the UI renderer) without depending on the adapter's internal constants.
/// </summary>
internal static class EmailEndpointPaths
{
    /// <summary>
    /// Endpoint path for starting the Pull flow (email entry, generation and sending of the magic link).
    /// </summary>
    public const string Start = "/auth/email/start";

    /// <summary>
    /// Endpoint path for the entry of the code of a sign-in mail that carries one: the address form of a
    /// page posts the code here, on the same place the address was typed into.
    /// </summary>
    public const string Code = "/auth/email/code";
}
