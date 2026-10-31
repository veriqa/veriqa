// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// What the mail of a sign-in by an address typed into the page (the Pull mode of Email) carries (SPEC-016
/// §4). The integrator chooses a set of them; the user is given one button to send the mail and no choice of
/// what it carries. Declared here, beside <see cref="IEmailPullAvailability"/>, because the auth server, which
/// renders the address form, and the Email adapter, which answers what the mail carries, do not reference each
/// other.
/// </summary>
public enum EmailPullMethod
{
    /// <summary>
    /// The mail carries the magic link: following it confirms the address.
    /// </summary>
    MagicLink,

    /// <summary>
    /// The mail carries a code: entering it on the page the address was typed into confirms the address.
    /// </summary>
    Code
}
