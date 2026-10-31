// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers;

using Microsoft.Extensions.Primitives;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Step 1 of the resolve endpoint (SPEC-025 HOP-072): tells a preview of a hop URL from a person
/// following it by the HEADERS of the request alone — before the token is looked at, so the answer does
/// not depend on what the token leads to.
/// </summary>
internal static class HopRequestClassifier
{
    /// <summary>
    /// The allow-list of link-preview agents, matched as substrings of the <c>User-Agent</c> without
    /// regard to case.
    /// </summary>
    private static readonly SearchValues<string> PreviewAgents =
        SearchValues.Create(HopPreviewMarkers.UserAgents.ToArray(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The values of a purpose header that announce a prefetch or a preview, matched as substrings
    /// without regard to case.
    /// </summary>
    private static readonly SearchValues<string> PreviewPurposes =
        SearchValues.Create(HopPreviewMarkers.PreviewPurposes.ToArray(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies the request: a preview when its <c>User-Agent</c> names an agent of the allow-list or
    /// one of the purpose headers announces a prefetch or a preview; a person otherwise — including a
    /// request without a <c>User-Agent</c>.
    /// </summary>
    /// <param name="headers">Headers of the request.</param>
    /// <returns>Who presented the hop URL.</returns>
    public static HopRequestKind Classify(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (AnyContains(headers.UserAgent, PreviewAgents))
        {
            return HopRequestKind.Preview;
        }

        foreach (var header in HopPreviewMarkers.PurposeHeaders)
        {
            if (AnyContains(headers[header], PreviewPurposes))
            {
                return HopRequestKind.Preview;
            }
        }

        return HopRequestKind.Human;
    }

    /// <summary>
    /// Whether any value of a header contains one of the markers.
    /// </summary>
    /// <param name="values">Values of the header.</param>
    /// <param name="markers">Markers to look for.</param>
    /// <returns><c>true</c> when a marker occurs in a value.</returns>
    private static bool AnyContains(StringValues values, SearchValues<string> markers)
    {
        foreach (var value in values)
        {
            if (value is not null && value.AsSpan().ContainsAny(markers))
            {
                return true;
            }
        }

        return false;
    }
}
