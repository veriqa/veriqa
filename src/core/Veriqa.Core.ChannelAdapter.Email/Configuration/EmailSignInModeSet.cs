// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.Enums;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// The effective set of the sign-in modes of Email in one ownership context: the ceiling of the core
/// level narrowed by the tenant and the application, with the preferred mode of the same context.
/// </summary>
/// <param name="Pull">Pull (the magic link) is offered.</param>
/// <param name="Push">Push (the one-tap email) is offered.</param>
/// <param name="PreferredMode">Mode shown first when both are offered.</param>
/// <param name="PullMethods">
/// What the mail of Pull carries, in the order of <see cref="EmailPullMethod"/> and without repeats; it
/// applies only while <paramref name="Pull"/> is offered.
/// </param>
internal sealed record EmailSignInModeSet(
    bool Pull,
    bool Push,
    EmailMode PreferredMode,
    IReadOnlyList<EmailPullMethod> PullMethods)
{
    /// <summary>
    /// Whether the mail of Pull carries a code: the address form then asks for the code once the mail is
    /// sent.
    /// </summary>
    public bool PullCarriesCode => PullMethods.Contains(EmailPullMethod.Code);

    /// <summary>
    /// Whether the mail of Pull carries the magic link.
    /// </summary>
    public bool PullCarriesLink => PullMethods.Contains(EmailPullMethod.MagicLink);

    /// <summary>
    /// Push is taken when it is in the set and either preferred or the only one: a preferred mode outside
    /// the set does not apply, the mode left in it does.
    /// </summary>
    public bool PushIsEffective => Push && (PreferredMode is EmailMode.Push || !Pull);
}
