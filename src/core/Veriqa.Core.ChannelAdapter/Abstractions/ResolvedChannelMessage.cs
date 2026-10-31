// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// The template of a channel message resolved by <see cref="IChannelMessageTemplates"/>, with the
/// context of its transaction a channel cannot read itself. The three context values are what the
/// deep-link prefill is rendered with; the confirmation prompt is rendered in the language of its
/// recipient, which the prompt context carries, and not in <see cref="Locale"/>.
/// </summary>
/// <param name="Message">The resolved template; never null.</param>
/// <param name="Locale">Locale tag stated by the request that created the transaction, or null when
/// the transaction states none (or the store does not have it).</param>
/// <param name="TimeZone">Time zone the moments of the message are shown in (an IANA identifier), or
/// null — UTC.</param>
/// <param name="ApplicationName">Human-readable name of the client application the transaction was
/// started by, or null when it is unknown.</param>
public sealed record ResolvedChannelMessage(
    ResolvedMessage Message,
    string? Locale,
    string? TimeZone,
    string? ApplicationName)
{
    /// <summary>
    /// Brand name the levels of the transaction stated at its creation, as configured — a Natural Key
    /// the text translates into its language; null when none is stated or the transaction is unknown.
    /// </summary>
    public string? BrandName { get; init; }
}
