// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Settings of the short window in which a repeated delivery of an inbound channel event is dropped
/// before it is processed. Configuration section: Veriqa:Channels:InboundDeduplication.
/// </summary>
/// <remarks>
/// The window is a setting of the whole installation: a tenant and an application do not override it.
/// The marks it is judged against live in the memory of one process, so a repeat a load balancer sends
/// to another instance is not dropped.
/// </remarks>
public sealed class ChannelInboundDeduplicationOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:Channels:InboundDeduplication";

    /// <summary>
    /// Window of a deployment that states none.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long after an accepted event another one with the same key is taken for its repeat and
    /// dropped. <see cref="TimeSpan.Zero"/> turns the dropping off; a negative value stops the host at
    /// start.
    /// </summary>
    public TimeSpan Window { get; set; } = DefaultWindow;
}
