// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The form of the two parts of a hop URL — the effective base and the <c>HopPath</c> — and the check
/// of what normalization cannot repair (SPEC-025 HOP-033a, HOP-123). Each rule is stated here once: the
/// reading of the hop settings hands every consumer values already in this form, and the startup
/// validation judges a configured value by the same functions, so the URL a QR code carries and the
/// route that accepts it cannot part.
/// </summary>
internal static class HopUrlForm
{
    /// <summary>
    /// Separator of the segments of a URL path — also the one between the <c>HopPath</c> and the token of
    /// a hop URL, as in <see cref="RouteTemplate"/>.
    /// </summary>
    internal const char PathSeparator = '/';

    /// <summary>
    /// The characters a hop path may carry: the separator and the characters a segment of a URL path
    /// carries as they are (RFC 3986 <c>pchar</c> without percent-encoding — unreserved, sub-delims,
    /// <c>:</c> and <c>@</c>). None of them is rewritten on the way from the issued URL to the routing:
    /// a browser sends them unencoded and the server has nothing to decode. Every other character is
    /// either rewritten on that way — <c>%</c> is decoded by the server, <c>\</c> becomes <c>/</c> in a
    /// browser, whitespace is stripped or encoded — or ends the path (<c>?</c>, <c>#</c>), or is a route
    /// parameter (<c>{</c>, <c>}</c>), or makes the issued URL no URI (non-ASCII, <c>"</c>, <c>&lt;</c>);
    /// a route registered over such a literal is never reached by the URL issued over it.
    /// </summary>
    private static readonly SearchValues<char> PathCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~!$&'()*+,;=:@/");

    /// <summary>
    /// Dot segments of a URL path: a request path is normalized before routing, so a route whose base
    /// path carries one is registered as written and never reached.
    /// </summary>
    private static readonly string[] DotSegments = [".", ".."];

    /// <summary>
    /// Brings a configured <c>HopPath</c> to its one form: one leading slash, no trailing one, inner
    /// segments kept (<c>r</c>, <c>/r/</c>, <c>//r//</c> → <c>/r</c>; <c>/a/b/</c> → <c>/a/b</c>).
    /// </summary>
    /// <param name="hopPath">Configured path.</param>
    /// <returns>Path in its normalized form.</returns>
    internal static string NormalizeHopPath(string hopPath)
    {
        ArgumentNullException.ThrowIfNull(hopPath);

        return PathSeparator + hopPath.Trim(PathSeparator);
    }

    /// <summary>
    /// Brings an effective issuer to the form of the base of a hop URL — no surrounding whitespace and no
    /// trailing slash (<c>https://id.example.com/</c> → <c>https://id.example.com</c>) — or tells that it
    /// cannot be one. Surrounding whitespace (the line break a secret file ends with) and a trailing slash
    /// are no defects and are removed here — the parse of a URL ignores that whitespace too, so the value
    /// judged and the value returned are the same text; what normalization cannot repair — nothing
    /// stated, a relative value, a scheme other than <c>https</c> — yields null.
    /// </summary>
    /// <param name="issuer">Effective issuer; null — not stated.</param>
    /// <returns>Base in its normalized form, or null when the issuer is no absolute HTTPS URL.</returns>
    internal static string? NormalizeBase(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        var stated = issuer.Trim();

        if (!Uri.TryCreate(stated, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return stated.TrimEnd(PathSeparator);
    }

    /// <summary>
    /// Route template of the resolve endpoint over a normalized <c>HopPath</c>:
    /// <c>{HopPath}/{hop_token}</c>.
    /// </summary>
    /// <param name="normalizedHopPath">Path in the form of <see cref="NormalizeHopPath"/>.</param>
    /// <returns>Route template.</returns>
    internal static string RouteTemplate(string normalizedHopPath) =>
        $"{normalizedHopPath}{PathSeparator}{{{HopModeConstants.HopTokenRouteParameter}}}";

    /// <summary>
    /// The ONE check of whether a configured <c>HopPath</c> can serve as the base path of the resolve
    /// endpoint (SPEC-025 HOP-123): what normalization cannot repair. The path is suitable when, with
    /// its slashes removed, something is left; it is no absolute URL; it carries only the characters a
    /// URL path carries unchanged up to the routing (<see cref="PathCharacters"/> — which leaves out a
    /// query, a fragment, whitespace and a route parameter) and no dot segment; and the route template
    /// built over it is one the routing accepts.
    /// </summary>
    /// <param name="hopPath">Configured path.</param>
    /// <returns>Null when the path is suitable; otherwise what is wrong with it.</returns>
    internal static string? FindHopPathDefect(string? hopPath)
    {
        // The checks go from what the value IS to what it contains; the parse of the route template
        // comes last and catches what the explicit rules above it do not name.
        if (hopPath is null || NormalizeHopPath(hopPath) is [PathSeparator])
        {
            return "the path is empty once its slashes are removed";
        }

        // A path starting with a slash is never taken for an absolute URL: on Unix a rooted path
        // parses as an absolute file URI, which is not what an operator who wrote "/r" meant.
        if (!hopPath.StartsWith(PathSeparator) && Uri.TryCreate(hopPath, UriKind.Absolute, out _))
        {
            return "the path is an absolute URL, while it has to be a path relative to the base of the node";
        }

        if (hopPath.AsSpan().IndexOfAnyExcept(PathCharacters) is var index and >= 0)
        {
            var character = hopPath[index];

            return $"the path contains '{character}' (U+{(int)character:X4}), while it may carry only ASCII "
                + "letters, digits, '/' and the characters -._~!$&'()*+,;=:@; any other character is changed "
                + "by the browser or the server before routing, or ends the path, so the route would not be reached";
        }

        if (hopPath.Split(PathSeparator).Any(static segment => DotSegments.Contains(segment)))
        {
            return "the path contains a '.' or '..' segment, which a request path never carries once normalized";
        }

        try
        {
            RoutePatternFactory.Parse(RouteTemplate(NormalizeHopPath(hopPath)));
        }
        catch (RoutePatternException exception)
        {
            return $"the route template built over the path is rejected by the routing: {exception.Message}";
        }

        return null;
    }
}
