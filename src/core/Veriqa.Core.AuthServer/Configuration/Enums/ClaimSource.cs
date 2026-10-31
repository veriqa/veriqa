// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration.Enums;

/// <summary>
/// Claim source for scope configuration (SPEC-012 §6.6).
/// </summary>
public enum ClaimSource
{
    /// <summary>
    /// The claim is provided by a channel adapter (Telegram, WhatsApp, MAX).
    /// </summary>
    Channel,

    /// <summary>
    /// The claim is provided by the client's external system (Identity Resolution).
    /// </summary>
    External,

    /// <summary>
    /// The claim is collected by Veriqa itself (SPEC-041 C31): no channel issues it, so its value comes
    /// only from the store or from the completion step, which is why such a claim must describe that
    /// step (<see cref="ClaimDefinition.Completion"/>).
    /// </summary>
    Collected
}
