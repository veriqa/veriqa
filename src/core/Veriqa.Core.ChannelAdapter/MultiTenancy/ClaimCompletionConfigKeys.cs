// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// Registry of the canonical resolver keys of claim completion in the channel (SPEC-012 §10.6): the
/// window of the <c>Optional</c> phone step (<see cref="PhoneOptionalWindow"/>). Resolved as a plain
/// value through the shared resolver; no precedence of its own is built here (anti-fork CFG-202).
/// <para>
/// The key is declared in ChannelAdapter because its consumer lives here — the starter of the phone
/// step, which asks the channel for the number and ends the wait of an <c>Optional</c> one.
/// </para>
/// </summary>
internal static class ClaimCompletionConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this registry — what the one registrar of the deployment declares
    /// and binds. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// Canonical name of the key of the window.
    /// </summary>
    public const string PhoneOptionalWindowKeyName = "ClaimCompletion.PhoneOptionalWindow";

    /// <summary>
    /// Address of the core level of the window inside the global section.
    /// </summary>
    private const string CorePhoneOptionalWindowAddress = "Veriqa:ClaimCompletion:PhoneOptionalWindow";

    /// <summary>
    /// Member the window is stated at in the OIDC client entry of an application. It is written as text
    /// rather than taken from the entry's type: the entry belongs to the auth server, and this contour
    /// does not reference it (a reverse dependency). The record is flat and shared by every owner
    /// writing into it, so the member carries the name of its subject.
    /// </summary>
    private const string ApplicationPhoneOptionalWindowMember = "ClaimCompletionPhoneOptionalWindow";

    /// <summary>
    /// A window that ends before it starts waits for nothing: the value is a positive duration.
    /// </summary>
    private static readonly ConfigValueDomain<TimeSpan> PhoneOptionalWindowDomain = new(
        static window => window > TimeSpan.Zero,
        "a positive duration");

    /// <summary>
    /// Window of the <c>Optional</c> phone step in the channel (SPEC-041 C74, L73): how long the core
    /// waits for an answer to the phone number request before it counts the step skipped. A plain value
    /// over the core and application levels, each overriding the one below it freely; the core level
    /// always states a value — the one of the global section, or the one the product ships with. A value
    /// outside the domain leaves its level unset, and the level below answers. Whatever the value, the
    /// wait never reaches the deadline of the transaction: its consumer bounds it by what remains of the
    /// TTL.
    /// </summary>
    public static ConfigKey<TimeSpan> PhoneOptionalWindow { get; } = Declared
        .Of<TimeSpan>(PhoneOptionalWindowKeyName)
        .At(ConfigLevel.Core, CorePhoneOptionalWindowAddress)
        .At(ConfigLevel.Application, ApplicationPhoneOptionalWindowMember)
        .Domain(PhoneOptionalWindowDomain)
        .Default(ClaimCompletionPhoneStepConstants.DefaultOptionalWindow)
        .Declare();

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;
}
