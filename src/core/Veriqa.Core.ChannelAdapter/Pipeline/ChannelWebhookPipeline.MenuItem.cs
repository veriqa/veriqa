// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Contracts;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The press of a callback item of the post-completion menu (SPEC-012 CFG-094, SPEC-003 CA-194): the
/// core decides nothing about it and hands it to the handler the host registered.
/// </summary>
/// <remarks>
/// The press names no transaction — the sign-in the menu followed is over — so nothing of the
/// transaction engine is read here. A built-in adapter answers the platform for the press itself,
/// where the platform waits for an answer, before the press reaches this code; on the HTTP channel the
/// external adapter answers the platform, and the core answers the request of that adapter.
/// </remarks>
public static partial class ChannelWebhookPipeline
{
    /// <summary>
    /// Hands a menu item press to the handler of the host. A host without a handler leaves the press
    /// without processing: the button was sent while one was registered. A handler that throws — when
    /// it is created or when it is called — is logged and does not fail the processing of the event:
    /// neither the user nor a sign-in depends on what the host does with the press.
    /// </summary>
    /// <remarks>
    /// The handler is created here and nowhere earlier. Creating it runs the host's code, so it stays
    /// inside the same guard as the call, and no other event of the channel ever reaches it.
    /// The call is awaited: the processing of the event ends when the handler returns, which is why
    /// the contract of the handler asks it to return promptly.
    /// </remarks>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="result">The press.</param>
    /// <param name="adapter">Channel adapter the press arrived through.</param>
    /// <param name="cancellationToken">Cancellation token of the event.</param>
    private static async Task HandleMenuItemPressedAsync(
        ChannelInboundProcessor processor,
        ChannelMenuItemPressedResult result,
        IChannelAdapter adapter,
        CancellationToken cancellationToken)
    {
        var logger = processor.Logger;
        var identity = result.Identity;

        try
        {
            if (processor.MenuItemPressHandlerSource?.Invoke() is not { } handler)
            {
                logger.LogDebug(
                    "Menu item pressed with no handler of the host registered — not processed. ChannelType: {ChannelType}",
                    adapter.ChannelType);
                return;
            }

            await handler.HandleAsync(
                new MenuItemPress
                {
                    ChannelType = adapter.ChannelType,
                    ChannelUserId = identity.ChannelUserId,
                    TenantId = identity.TenantId,
                    Value = result.Value,
                    PressId = result.PressId
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The handler of menu item presses of the host failed to be created or to handle the press; the event is processed as usual. ChannelType: {ChannelType}, UserFingerprint: {UserFingerprint}",
                adapter.ChannelType,
                LogMasking.Fingerprint(identity.ChannelUserId));
        }
    }
}
