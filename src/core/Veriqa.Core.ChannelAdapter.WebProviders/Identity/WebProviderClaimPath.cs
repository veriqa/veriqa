// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;

using Veriqa.Core.ChannelAdapter.WebProviders.Constants;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Identity;

/// <summary>
/// Path of a value in the answer of a provider, as the configuration of a channel states it: the claim
/// type of the principal of the OpenIddict client, then the members of the JSON object in its value,
/// separated by dots — <c>default_phone.number</c>. OpenIddict turns every member of the userinfo
/// answer into a claim of the same name and keeps an object as one claim with the raw JSON as its value,
/// so a path reaches into such an object without a parser of the provider's own.
/// </summary>
/// <param name="ClaimType">The claim type of the principal — the first segment.</param>
/// <param name="Members">Members of the JSON object in the value of the claim, outermost first; empty —
/// the value of the claim itself.</param>
internal sealed record WebProviderClaimPath(string ClaimType, IReadOnlyList<string> Members)
{
    /// <summary>
    /// Separator of the segments of a path.
    /// </summary>
    public const char SegmentSeparator = '.';

    /// <summary>
    /// Parses a stated path: every segment is non-empty and carries no whitespace around it.
    /// </summary>
    /// <param name="value">The stated path.</param>
    /// <param name="path">The parsed path.</param>
    /// <returns>Whether the value is a path.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out WebProviderClaimPath? path)
    {
        path = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var segments = value.Split(SegmentSeparator);

        if (segments.Any(static segment => segment.Length == 0 || !string.Equals(segment, segment.Trim(), StringComparison.Ordinal)))
        {
            return false;
        }

        path = new WebProviderClaimPath(segments[0], segments[1..]);
        return true;
    }

    /// <summary>
    /// Reads the value the path leads to. The value is a leaf — a string, a number or a boolean, in its
    /// string form; an object, an array, a claim stated with several values, or a path that leads
    /// through something other than an object is unusable. No part of the value is ever returned for an
    /// unusable one: the caller logs the path, not the value. OpenIddict states a top-level array of the
    /// answer as one claim per element, so such an array of one element, or of elements all equal, reads
    /// as that element.
    /// </summary>
    /// <param name="principal">The principal of the OpenIddict client.</param>
    /// <param name="unusable">Whether the provider stated something at the path that is not a leaf.</param>
    /// <returns>The value; <c>null</c> — none (absent, blank, a JSON <c>null</c>, or unusable).</returns>
    public string? Read(ClaimsPrincipal principal, out bool unusable)
    {
        ArgumentNullException.ThrowIfNull(principal);

        unusable = false;

        var values = principal.FindAll(ClaimType)
            .DistinctBy(static claim => claim.Value, StringComparer.Ordinal)
            .ToList();

        if (values.Count == 0)
        {
            return null;
        }

        // OpenIddict states an array of the answer as one claim per element.
        if (values.Count > 1)
        {
            unusable = true;
            return null;
        }

        var claim = values[0];

        if (string.Equals(claim.ValueType, WebProviderAdapterConstants.JsonNullClaimValueType, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(claim.ValueType, WebProviderAdapterConstants.JsonObjectClaimValueType, StringComparison.Ordinal))
        {
            return ReadObject(claim.Value, out unusable);
        }

        // A scalar claim has no members to follow, and a nested array is no leaf.
        if (Members.Count > 0
            || string.Equals(claim.ValueType, WebProviderAdapterConstants.JsonArrayClaimValueType, StringComparison.Ordinal))
        {
            unusable = true;
            return null;
        }

        if (string.Equals(claim.ValueType, ClaimValueTypes.Boolean, StringComparison.Ordinal)
            && bool.TryParse(claim.Value, out var flag))
        {
            return FormatBoolean(flag);
        }

        return Trimmed(claim.Value);
    }

    /// <summary>
    /// The path as the configuration states it.
    /// </summary>
    /// <returns>The segments joined by the separator.</returns>
    public override string ToString() => string.Join(SegmentSeparator, Members.Prepend(ClaimType));

    /// <summary>
    /// Follows the members of the path into the JSON object a claim carries.
    /// </summary>
    /// <param name="json">The raw JSON of the claim.</param>
    /// <param name="unusable">Whether the object holds something at the path that is not a leaf, or
    /// is no JSON.</param>
    /// <returns>The leaf; <c>null</c> — none.</returns>
    private string? ReadObject(string json, out bool unusable)
    {
        unusable = false;

        // The object itself is no leaf.
        if (Members.Count == 0)
        {
            unusable = true;
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            unusable = true;
            return null;
        }

        using (document)
        {
            var element = document.RootElement;

            foreach (var member in Members)
            {
                if (element.ValueKind is not JsonValueKind.Object)
                {
                    unusable = true;
                    return null;
                }

                if (!element.TryGetProperty(member, out element))
                {
                    return null;
                }
            }

            if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                unusable = true;
                return null;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => Trimmed(element.GetString()),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True or JsonValueKind.False => FormatBoolean(element.GetBoolean()),
                _ => null
            };
        }
    }

    /// <summary>
    /// A text value trimmed; blank states nothing.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The trimmed value; <c>null</c> — blank.</returns>
    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The string form of a boolean leaf — the spelling of the JSON literal.
    /// </summary>
    /// <param name="flag">The boolean.</param>
    /// <returns><c>true</c> or <c>false</c>.</returns>
    private static string FormatBoolean(bool flag) =>
        (flag ? bool.TrueString : bool.FalseString).ToLowerInvariant();
}
