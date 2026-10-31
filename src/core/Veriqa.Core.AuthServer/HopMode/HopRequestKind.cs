// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Who presented a hop URL, as the headers of the request tell it (SPEC-025 HOP-072).
/// </summary>
internal enum HopRequestKind
{
    /// <summary>
    /// A person following the link — the default whenever the request is not recognized as a preview.
    /// </summary>
    Human,

    /// <summary>
    /// A link-preview agent or a prefetch of the browser: it is never taken into the channel.
    /// </summary>
    Preview
}
