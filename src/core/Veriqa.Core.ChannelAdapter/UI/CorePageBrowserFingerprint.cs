// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The browser fingerprint a page of the "Core" contour computes (SPEC-025 HOP-090b): the attributes it
/// is taken from and the client functions that compute it. One home for every page that computes it — the
/// launch page and the page of the channel buttons of the hop mode, which deliver it to the scanner context
/// of the transaction, and the page a link of a sign-in letter opens, which compares it with that stored
/// value. Two copies of the algorithm would drift apart, and a fingerprint computed by one would never equal
/// one computed by the other. It lives in this assembly because it is the lowest project both page families
/// reference.
/// <para>
/// <see cref="Hashed"/> is the one place that states which attributes the fingerprint takes and in what
/// order. An attribute is a name together with its reader, so the set cannot name an attribute a page has
/// no way to read. The set is part of the scanner context collector of the hop mode: a change of it changes
/// the fingerprint of every scanner, so it goes together with a new version of that collector.
/// </para>
/// </summary>
internal static class CorePageBrowserFingerprint
{
    /// <summary>
    /// The attributes of the fingerprint, in the order they are hashed. <c>Name</c> is what the page puts
    /// before the value in the hashed line; <c>Reader</c> is the JavaScript expression the page reads the
    /// value by. The expression may throw or give <c>undefined</c> in a browser that lacks the property; the
    /// page then takes the value as empty.
    /// </summary>
    public static IReadOnlyList<(string Name, string Reader)> Hashed { get; } =
    [
        // The preferred language of the browser.
        ("language", "navigator.language"),

        // The preferred languages of the browser, in their order.
        ("languages", "(navigator.languages || []).join(',')"),

        // The IANA time zone of the browser.
        ("timeZone", "Intl.DateTimeFormat().resolvedOptions().timeZone"),

        // The screen: the shorter side, the longer side and the color depth, so a turn of the device does
        // not change it.
        ("screen", "Math.min(screen.width, screen.height) + 'x' + Math.max(screen.width, screen.height) + 'x' + screen.colorDepth"),

        // The number of logical processors the browser reports.
        ("hardwareConcurrency", "navigator.hardwareConcurrency"),

        // The number of simultaneous touch points the device supports.
        ("maxTouchPoints", "navigator.maxTouchPoints"),

        // The platform the browser reports.
        ("platform", "navigator.platform")
    ];

    /// <summary>
    /// The client declarations that compute the fingerprint, to be put into the body of an enclosing
    /// function of a script; they take the names <c>attributes</c>, <c>read</c>, <c>fingerprint</c> and
    /// <c>boundedFingerprint</c> in that scope. Built once per process.
    /// <para>
    /// <c>boundedFingerprint(limit)</c> returns a promise of the fingerprint: the attributes of
    /// <see cref="Hashed"/>, in that order, as lines <c>name=value</c> joined by a line feed (an absent value
    /// is empty), hashed as UTF-8 by SHA-256 and given in base64url without padding. The attributes
    /// themselves never leave the page. A failure, a browser without <c>crypto.subtle</c> or the time limit
    /// (<c>limit</c>, in milliseconds) gives null — each caller decides what it does without one. The
    /// promise never rejects.
    /// </para>
    /// </summary>
    public static string Functions { get; } = BuildFunctions();

    /// <summary>
    /// Builds <see cref="Functions"/>.
    /// </summary>
    /// <returns>The declarations.</returns>
    private static string BuildFunctions()
    {
        var readers = string.Join(
            ",\n",
            Hashed.Select(attribute =>
                $"[{JsonSerializer.Serialize(attribute.Name)}, function () {{ return {attribute.Reader}; }}]"));

        return $$"""
            var attributes = [
            {{readers}}
            ];
            function read(reader) {
              try {
                var value = reader();
                return value === undefined || value === null ? '' : String(value);
              } catch (e) {
                return '';
              }
            }
            function fingerprint() {
              var subtle = window.crypto && window.crypto.subtle;
              if (!subtle || typeof TextEncoder !== 'function') { return Promise.resolve(null); }
              var text = attributes.map(function (attribute) {
                return attribute[0] + '=' + read(attribute[1]);
              }).join('\n');
              return subtle.digest('SHA-256', new TextEncoder().encode(text)).then(function (digest) {
                var bytes = new Uint8Array(digest);
                var binary = '';
                for (var i = 0; i < bytes.length; i++) { binary += String.fromCharCode(bytes[i]); }
                return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
              });
            }
            function boundedFingerprint(limit) {
              return new Promise(function (resolve) {
                setTimeout(function () { resolve(null); }, limit);
                try { fingerprint().then(resolve, function () { resolve(null); }); } catch (e) { resolve(null); }
              });
            }
            """;
    }
}
