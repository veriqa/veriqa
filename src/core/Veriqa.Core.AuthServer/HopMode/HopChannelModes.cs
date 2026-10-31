// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The one answer to the hop mode of a channel for an act, for every step that decides by the switch of
/// one channel: the content of its QR code, the replacement of that QR code and the check of a presented
/// token. A channel that starts by a link answers by its settings (<see cref="HopModeSettings"/>). A
/// channel whose link carries no transaction answers by its declaration instead: hop is on for it and its
/// answer is a page, whatever the switch and the redirect style state — the three steps ask here, so they
/// cannot part on which channel that is.
/// </summary>
/// <remarks>
/// Scoped: the declaration is read off the channel adapters, whose lifetime a host may narrow to the
/// request. The unified hop QR code is not judged here: whether it applies is decided by the switches of
/// the channels alone (<see cref="HopUnifiedMode"/>), and a channel that starts by a code does not turn it
/// on by itself.
/// </remarks>
internal sealed class HopChannelModes
{
    /// <summary>
    /// The reading of the effective hop settings.
    /// </summary>
    private readonly HopModeSettings _settings;

    /// <summary>
    /// Adapters of the channels registered in this deployment.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _channelAdapters;

    /// <summary>
    /// Creates the answer.
    /// </summary>
    /// <param name="settings">The reading of the effective hop settings.</param>
    /// <param name="channelAdapters">Adapters of the registered channels.</param>
    public HopChannelModes(HopModeSettings settings, IEnumerable<IChannelAdapter> channelAdapters)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(channelAdapters);

        _settings = settings;
        _channelAdapters = channelAdapters;
    }

    /// <summary>
    /// Tells whether a channel starts by a message with a code alone: it declared the start by a code and
    /// states that its link carries no transaction. A channel whose link does carry the transaction keeps
    /// the code as a spare way in and is an ordinary channel to the hop mode.
    /// </summary>
    /// <param name="adapter">Adapter of the channel.</param>
    /// <returns><c>true</c> when the message with the code is the only start of the channel.</returns>
    public static bool StartsByCodeOnly(IChannelAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        return adapter.Capabilities.CodeStart is { LinkCarriesTransaction: false };
    }

    /// <summary>
    /// Tells whether a presentation of the hop URL of a channel is answered with the page of the channel
    /// buttons, laid out for that one channel, in place of the launch page: the Email channel while it
    /// accepts a typed address and its redirect style is <c>Interstitial</c>. That page keeps the field
    /// for the address beside the button of the mail and opens the mail by itself, as the launch page
    /// would. Under <c>Direct</c> the redirect to the mail is the answer, as it is for every other channel,
    /// and an Email channel that accepts no typed address has nothing but its mail to offer and is
    /// answered by the redirect style as well. The question about the typed address is the one the
    /// sign-in window asks before it offers the field, asked in the context of the transaction of the act,
    /// and an adapter that does not answer it is taken to accept one, as it is there. The one answer both
    /// the presentation and the receiver of client signals ask, so they cannot part on which page the
    /// signals of the channel come from.
    /// </summary>
    /// <param name="adapter">Adapter of the channel.</param>
    /// <param name="channelMode">Hop mode of the channel for the act.</param>
    /// <param name="context">Resolution context of the transaction of the act.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the presentation is answered with the page of the channel buttons.</returns>
    public static async ValueTask<bool> AnswersOnButtonsPageAsync(
        IChannelAdapter adapter,
        HopChannelMode channelMode,
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(channelMode);
        ArgumentNullException.ThrowIfNull(context);

        return adapter.ChannelType is ChannelTypes.Email
            && AnswersByPage(channelMode)
            && (adapter is not IEmailPullAvailability pull || await pull.IsPullAvailableAsync(context, cancellationToken));
    }

    /// <summary>
    /// Tells whether a presentation of the hop URL of a channel is answered with a page a person acts on —
    /// the one answer both the presentation and the receiver of client signals ask, so they cannot part on
    /// whether there is a page to send a signal from. The redirect style <c>Interstitial</c> gives one —
    /// the launch page, or the page of the channel buttons for a channel answered with it
    /// (<see cref="AnswersOnButtonsPageAsync"/>) — and <c>Direct</c> gives none.
    /// </summary>
    /// <param name="channelMode">Hop mode of the channel for the act.</param>
    /// <returns><c>true</c> when the presentation is answered with a page.</returns>
    public static bool AnswersByPage(HopChannelMode channelMode)
    {
        ArgumentNullException.ThrowIfNull(channelMode);

        return channelMode.RedirectStyle is HopRedirectStyle.Interstitial;
    }

    /// <summary>
    /// Tells whether this deployment serves a channel that starts by a code alone — the fact the mapping
    /// of the resolve route asks once, at startup: for such a channel a hop URL is issued with the hop
    /// switch off, so the route has to exist where the switch alone would not map it. A channel serves
    /// when its adapter is registered and the channel can be available to a layer
    /// (<see cref="HopModeSettings.CanBeAvailable"/>): one that is switched off gets no hop URL, and
    /// maps no route for it either.
    /// </summary>
    /// <param name="services">Root service provider of the host.</param>
    /// <param name="settings">The reading of the effective hop settings.</param>
    /// <returns><c>true</c> when at least one such channel is served.</returns>
    public static bool IsCodeChannelServed(IServiceProvider services, HopModeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        // The adapters may be registered with a lifetime narrower than the process, so they are read
        // inside a scope of their own.
        using var scope = services.CreateScope();

        var codeChannels = scope.ServiceProvider.GetServices<IChannelAdapter>()
            .Where(StartsByCodeOnly)
            .Select(static adapter => adapter.ChannelType)
            .ToHashSet(StringComparer.Ordinal);

        return settings.CanBeAvailable(codeChannels);
    }

    /// <summary>
    /// The hop mode of a channel named by its type. A type no registered adapter answers for is judged by
    /// the settings alone.
    /// </summary>
    /// <param name="context">Resolution context of the act.</param>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hop mode of the channel for the act.</returns>
    public ValueTask<HopChannelMode> ResolveAsync(
        ResolutionContext context,
        string channelType,
        CancellationToken cancellationToken) =>
        ResolveAsync(context, channelType, FindAdapter(channelType), cancellationToken);

    /// <summary>
    /// Tells whether hop gives a channel a page for an act — the question the report of the start asks:
    /// hop is on for the channel, a hop URL of the channel is issued at all
    /// (<see cref="HopUrlChannelsAsync"/>) and its presentation is answered with a page
    /// (<see cref="AnswersByPage"/>). A type no registered adapter answers for is judged by its settings
    /// alone, as its mode is, and no hop URL is asked about for it.
    /// </summary>
    /// <param name="context">Resolution context of the act.</param>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="hopUrlChannels">
    /// Types of the channels a hop URL is issued for (<see cref="HopUrlChannelsAsync"/>), asked in the same
    /// resolution context: whether the Email channel starts by its Push mail depends on the sign-in modes
    /// its tenant and its application choose.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the channel has a page.</returns>
    public async ValueTask<bool> GivesPageAsync(
        ResolutionContext context,
        string channelType,
        IReadOnlySet<string> hopUrlChannels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hopUrlChannels);

        var adapter = FindAdapter(channelType);
        var mode = await ResolveAsync(context, channelType, adapter, cancellationToken);

        if (!mode.IsEnabled)
        {
            return false;
        }

        return AnswersByPage(mode) && (adapter is null || hopUrlChannels.Contains(channelType));
    }

    /// <summary>
    /// The channels, out of the given types, a hop URL is issued for at all (<see cref="IssuesHopUrlAsync"/>)
    /// in a resolution context. Each adapter is asked once for the context. A type no registered adapter
    /// answers for is not listed — it has no adapter to ask, and its page is judged by its settings alone.
    /// </summary>
    /// <param name="context">Resolution context of the act.</param>
    /// <param name="channelTypes">Types of the channels to ask.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Types of the channels a hop URL is issued for.</returns>
    public async ValueTask<IReadOnlySet<string>> HopUrlChannelsAsync(
        ResolutionContext context,
        IEnumerable<string> channelTypes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(channelTypes);

        var issued = new HashSet<string>(StringComparer.Ordinal);

        foreach (var channelType in channelTypes)
        {
            if (FindAdapter(channelType) is { } adapter && await IssuesHopUrlAsync(adapter, context, cancellationToken))
            {
                issued.Add(channelType);
            }
        }

        return issued;
    }

    /// <summary>
    /// Tells whether a hop URL of a channel is issued at all. A hop URL replaces an absolute target only,
    /// and the Email channel has one only while it starts by its Push mail: its Pull start is a relative
    /// path of the sign-in window, so an Email channel on Pull is handed no hop URL and its page of the
    /// channel buttons is never opened by one. An adapter that does not answer the question is taken to
    /// have an absolute target, as every channel that starts by a link does.
    /// </summary>
    /// <remarks>
    /// The sign-in modes are those of the given context — the core ceiling narrowed by its tenant and its
    /// application. The tenant credentials of the channel are those of the tenant scope of the call: the
    /// report of the start asks under the default implicit tenant, so in this delivery a tenant that turns
    /// Push on or off by its own credentials is not seen there.
    /// </remarks>
    /// <param name="adapter">Adapter of the channel.</param>
    /// <param name="context">Resolution context of the act.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a hop URL of the channel is issued.</returns>
    private static async ValueTask<bool> IssuesHopUrlAsync(
        IChannelAdapter adapter,
        ResolutionContext context,
        CancellationToken cancellationToken) =>
        adapter is not IEmailPushAvailability push || await push.IsPushEffectiveAsync(context, cancellationToken);

    /// <summary>
    /// The hop mode of the channel of an adapter the caller already holds.
    /// </summary>
    /// <param name="context">Resolution context of the act.</param>
    /// <param name="adapter">Adapter of the channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hop mode of the channel for the act.</returns>
    public ValueTask<HopChannelMode> ResolveAsync(
        ResolutionContext context,
        IChannelAdapter adapter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        return StartsByCodeOnly(adapter)
            ? _settings.ResolveCodeChannelAsync(context, adapter.ChannelType, cancellationToken)
            : _settings.ResolveChannelAsync(context, adapter.ChannelType, cancellationToken);
    }

    /// <summary>
    /// The registered adapter of a channel type.
    /// </summary>
    /// <param name="channelType">Type of the channel.</param>
    /// <returns>The adapter, or null when no registered adapter answers for the type.</returns>
    private IChannelAdapter? FindAdapter(string channelType) =>
        _channelAdapters.FirstOrDefault(
            candidate => string.Equals(candidate.ChannelType, channelType, StringComparison.Ordinal));

    /// <summary>
    /// The hop mode of a channel whose adapter was already looked up: by the adapter when there is one,
    /// by the settings alone when there is none.
    /// </summary>
    /// <param name="context">Resolution context of the act.</param>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="adapter">The registered adapter of the type, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hop mode of the channel for the act.</returns>
    private ValueTask<HopChannelMode> ResolveAsync(
        ResolutionContext context,
        string channelType,
        IChannelAdapter? adapter,
        CancellationToken cancellationToken) =>
        adapter is null
            ? _settings.ResolveChannelAsync(context, channelType, cancellationToken)
            : ResolveAsync(context, adapter, cancellationToken);
}
