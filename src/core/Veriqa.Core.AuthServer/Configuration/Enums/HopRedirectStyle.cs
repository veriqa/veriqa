// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration.Enums;

/// <summary>
/// How the hop resolve endpoint hands the scanning device over to the channel (SPEC-025 HOP-065).
/// Written in the configuration by the name of the member.
/// </summary>
public enum HopRedirectStyle
{
    /// <summary>
    /// An intermediate page with the channel button is shown before the channel opens. The default.
    /// </summary>
    Interstitial,

    /// <summary>
    /// The resolve endpoint redirects straight to the channel deep link, with no page in between.
    /// </summary>
    Direct
}
