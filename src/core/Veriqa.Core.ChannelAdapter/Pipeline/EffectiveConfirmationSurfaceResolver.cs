// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Implementation of effective confirmation surface resolution (SPEC-012 §4.4.2). Consumes
/// the canonical level resolver (anti-fork CFG-202 — builds no precedence of its own):
/// key <see cref="LoginConfirmationConfigKeys.LoginConfirmation"/> asked about the adapter's channel →
/// the shipped mode when no level states one → expansion of the
/// <c>ChannelDefault</c> sentinel from the channel's single confirmation fact → routing of an
/// in-channel confirmation the channel cannot perform to the core's own surface, with a WARNING
/// (CFG-049a).
/// </summary>
internal sealed class EffectiveConfirmationSurfaceResolver : IEffectiveConfirmationSurfaceResolver
{
    /// <summary>
    /// Login confirmation AS THE PRODUCT SHIPS IT — a default-constructed options object, read for one
    /// thing only: the mode applied when no level states one. The shipped value is taken off the
    /// options class rather than restated here, so the two cannot drift.
    /// </summary>
    private static readonly LoginConfirmationOptions Shipped = new();

    /// <summary>
    /// Canonical level resolver from TASK-040.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<EffectiveConfirmationSurfaceResolver> _logger;

    /// <summary>
    /// Creates the effective confirmation surface resolver.
    /// </summary>
    /// <param name="resolver">Canonical level resolver.</param>
    /// <param name="logger">Logger.</param>
    public EffectiveConfirmationSurfaceResolver(
        IConfigurationResolver resolver,
        ILogger<EffectiveConfirmationSurfaceResolver> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<ConfirmationSurface> ResolveEffectiveSurfaceAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(adapter);

        // 1. Resolve the effective value along the ownership ladder (§10.3), asked about the adapter's
        //    channel: inside each level the per-channel entry is tried before the plain member (CFG-236).
        //    The application level is the transaction's application attribution (self-hosted = global 1:1).
        var context = TransactionResolutionContext.For(transaction);

        var resolved = await _resolver.ResolveAsync(
            LoginConfirmationConfigKeys.LoginConfirmation,
            context,
            ConfigDimensionValues.Of((ConfigDimensionNames.Channel, adapter.ChannelType)),
            cancellationToken);

        // The key declares no answer for a deployment in which no level states the mode, so an
        // unanswered chain is read as the mode the product ships with (ChannelDefault).
        var requested = resolved.SourceLevel is null ? Shipped.Mode : resolved.Value;

        // 1a. "Confirm nowhere" is a decision about SIGNING IN, and a transaction whose subject IS the
        //     confirmation has no such reading: honouring it would complete the action the relying
        //     party asked about without ever asking the user — in a messenger and on the fast link of
        //     a mail alike (SPEC-039 R36/L35). The value is read as the channel default instead, and
        //     the sentinel expansion below decides the rest, so the rule lands in ONE place and holds
        //     at every point that resolves a surface.
        if (requested is LoginConfirmationMode.None
            && string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal))
        {
            requested = LoginConfirmationMode.ChannelDefault;
        }

        var supportsInChannel = adapter.Capabilities.SupportsInChannelConfirmation;

        switch (requested)
        {
            // 2. ChannelDefault is a sentinel: expand it from the channel's single fact. Can confirm
            //    inside itself — let it; cannot — the core asks the question on its own surface.
            case LoginConfirmationMode.ChannelDefault:
                return supportsInChannel ? ConfirmationSurface.InChannel : ConfirmationSurfacePolicy.CoreDefault;

            // 3a. No confirmation at all — a decision of the ownership ladder, which no channel takes
            //     part in. It applies verbatim and is the ONLY way to reach this surface: routing
            //     never produces it, so a channel declaration can no longer cancel a configured
            //     confirmation.
            case LoginConfirmationMode.None:
                return ConfirmationSurface.None;

            // 3b. The core's own page is available for every channel, including one that can confirm
            //     inside itself: the core accepts the channel input and asks the question in the
            //     browser. Not an impossible combination — no warning.
            case LoginConfirmationMode.OnWebPage:
                return ConfirmationSurface.OnWebPage;

            // 3c. In-channel confirmation, and the channel can do it.
            case LoginConfirmationMode.InChannel when supportsInChannel:
                return ConfirmationSurface.InChannel;
        }

        // 4. What is left cannot be honoured as asked, for one of two different reasons, and the log
        //    names the one that applies — it is the only way the operator learns about the downgrade.
        //    (a) The reserved InChannelAndOnWebPage: its double-confirmation logic is not implemented
        //    (CFG-047), so it behaves like a plain InChannel request — in-channel where the channel
        //    can confirm, the core's own surface where it cannot. It warns in both cases, including
        //    the one where the channel does support in-channel confirmation: nothing about the
        //    channel is at fault there, the second confirmation is simply never performed.
        //    (b) An explicit InChannel for a channel that cannot confirm inside itself: routed to the
        //    core's own surface.
        var isReservedSurface = requested is LoginConfirmationMode.InChannelAndOnWebPage;

        var applied = isReservedSurface && supportsInChannel
            ? ConfirmationSurface.InChannel
            : ConfirmationSurfacePolicy.CoreDefault;

        if (isReservedSurface)
        {
            _logger.LogWarning(
                "reserved login confirmation surface {Requested} is not implemented, applied {Applied} on channel {Channel}",
                requested,
                applied,
                adapter.ChannelType);
        }
        else
        {
            _logger.LogWarning(
                "login confirmation surface {Requested} is not supported by channel {Channel}, applied {Applied}",
                requested,
                adapter.ChannelType,
                applied);
        }

        return applied;
    }
}
