// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Address of a link button of a post-completion message (SPEC-012 §4.9): the one rule that tells
/// a URL from any other configured value, and the one place a configured URL becomes the address a
/// button carries — for the redirect button and for a URL item of the menu alike.
/// </summary>
internal static class CompletionBehaviorButtonAddress
{
    /// <summary>
    /// Placeholder of the link handle in a configured URL (SPEC-012 CFG-094, CFG-095), matched in
    /// this exact case. An operator writes it where the address is to carry the one-time value the
    /// server of the integrator exchanges for the identity of the signed-in user.
    /// </summary>
    public const string HandlePlaceholder = "{handle}";

    /// <summary>
    /// Whether the configured value is a URL: an absolute URI of any scheme, written as
    /// <c>scheme://…</c>. The delimiter is part of the rule and not a detail of it: <c>order:42</c>
    /// parses as an absolute URI too, and a rooted path is read as a <c>file</c> URI on some
    /// platforms, while neither is written with the scheme in front. Whether a platform accepts the
    /// scheme for a button is the platform's to decide.
    /// <para>
    /// The value is judged exactly as written: one with white space at either edge is not a URL. The
    /// parser of URIs would drop that white space, while the button gets the configured string — so
    /// the rule refuses what the two would read differently
    /// (<see cref="HasEdgeWhiteSpace"/>).
    /// </para>
    /// </summary>
    /// <param name="value">Configured value.</param>
    /// <returns><c>true</c> when the value is a URL.</returns>
    public static bool IsUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return !HasEdgeWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && value.StartsWith(uri.Scheme + Uri.SchemeDelimiter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the configured value starts or ends with white space. Such a value is admitted neither
    /// as an address nor as the action of a menu item: it would be read as a URL by one reader and as
    /// callback data by another, and it reaches the platform as written.
    /// </summary>
    /// <param name="value">Configured value.</param>
    /// <returns><c>true</c> when white space stands at either edge of the value.</returns>
    public static bool HasEdgeWhiteSpace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.AsSpan().Trim().Length != value.Length;
    }

    /// <summary>
    /// Whether the configured URL asks for a link handle — carries <see cref="HandlePlaceholder"/>.
    /// </summary>
    /// <param name="configuredUrl">URL stated by the setting.</param>
    /// <returns><c>true</c> when the placeholder stands in the URL at least once.</returns>
    public static bool HasHandlePlaceholder(string configuredUrl)
    {
        ArgumentNullException.ThrowIfNull(configuredUrl);

        return configuredUrl.Contains(HandlePlaceholder, StringComparison.Ordinal);
    }

    /// <summary>
    /// Address a link button carries for the configured URL: the configured value with every
    /// occurrence of <see cref="HandlePlaceholder"/> replaced by the link handle. Without a handle
    /// the configured value is handed over as it is.
    /// </summary>
    /// <param name="configuredUrl">URL stated by the setting.</param>
    /// <param name="handle">Link handle issued for the message; null — none was issued.</param>
    /// <returns>Address of the button.</returns>
    public static string Of(string configuredUrl, string? handle)
    {
        ArgumentNullException.ThrowIfNull(configuredUrl);

        return handle is null
            ? configuredUrl
            : configuredUrl.Replace(HandlePlaceholder, handle, StringComparison.Ordinal);
    }
}
