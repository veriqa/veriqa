// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Http;

using Veriqa.Core.AuthServer.InitiatorContext;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The action under the outcome of a transaction on the pages of the core that report it away from the
/// sign-in window — the scanner page of the unified hop QR code, the launch page of a hop and the receipt
/// of the question page and of the page of the missing claims. The action itself is the one of
/// <see cref="CorePageCloseAction"/>: it closes the tab and changes nothing of the transaction. Only its
/// caption is chosen here, and by the device the page is opened on, not by the type of the transaction:
/// <list type="bullet">
/// <item><description>a page opened on the same kind of device as the one the sign-in was started on —
/// both a phone, both a desktop — is most likely that device, and the person goes on in the application
/// once the tab is gone: "Return to {app}";</description></item>
/// <item><description>a page on another kind of device, a device of a kind the User-Agent does not tell,
/// and a transaction without an initiator context or without the name of its application:
/// "Close".</description></item>
/// </list>
/// The kind of the device is a hint read off the User-Agent, never a fact (SPEC-017 ICC-002): a wrong
/// guess changes the words of a button whose action is the same either way. A confirmation created
/// server-to-server carries no initiator context, which is a technical reason and not a choice by type:
/// no browser of its initiator was ever seen, so its pages always say "Close".
/// </summary>
internal static class OutcomeCloseAction
{
    /// <summary>
    /// Natural Key of the caption on a page most likely opened on the device the sign-in was started on;
    /// <c>{0}</c> is the name of the application.
    /// </summary>
    public const string ReturnToApplicationText = "Return to {0}";

    /// <summary>
    /// Id of the element that holds the row of the action. A page that follows the outcome by script
    /// carries the row hidden, and the status block of the page shows it with the outcome
    /// (<see cref="PageStatusBlock"/>); the script finds it by a bare literal of the same value
    /// (<c>getElementById("veriqa-outcome-close")</c>) — the two are renamed together.
    /// </summary>
    public const string ElementId = "veriqa-outcome-close";

    /// <summary>
    /// The slot of the name of the application in <see cref="ReturnToApplicationText"/>. Replaced as text
    /// rather than formatted: an override of the text is the integrator's and may carry braces of its own.
    /// </summary>
    private const string ApplicationSlot = "{0}";

    /// <summary>
    /// The texts of the action an owning level may override (SPEC-012 §4.3, key <c>AuthPage.Texts</c>).
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } =
        [.. CorePageCloseAction.OverridableTexts, ReturnToApplicationText];

    /// <summary>
    /// The kind of the device a request comes from, as its User-Agent tells it.
    /// </summary>
    /// <param name="httpContext">The request of the page.</param>
    /// <param name="userAgentNormalizer">The normalizer that tells the kind of a device by its User-Agent.</param>
    /// <returns>The kind of the device; null — the User-Agent tells none.</returns>
    public static string? OpenedOn(HttpContext httpContext, IUserAgentNormalizer userAgentNormalizer)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(userAgentNormalizer);

        return userAgentNormalizer.Normalize(httpContext.Request.Headers.UserAgent.ToString())?.DeviceType;
    }

    /// <summary>
    /// The name of the application the caption names, or null when the caption is "Close".
    /// </summary>
    /// <param name="transaction">The transaction of the page.</param>
    /// <param name="openedOn">The kind of the device the page is opened on (<see cref="OpenedOn"/>); null — unknown.</param>
    /// <returns>The name of the application; null — the page offers "Close".</returns>
    public static string? ApplicationName(Transaction transaction, string? openedOn)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction.InitiatorContextSnapshot is not { DeviceType: { } startedOn } initiator
            || startedOn is InitiatorContextFields.DeviceTypes.Unknown
            || !string.Equals(startedOn, openedOn, StringComparison.Ordinal))
        {
            return null;
        }

        // The name is the attribution of the transaction and no personal datum, so no display decision
        // narrows it (SPEC-017 ICC-081); it is cleaned as every value a page of the core puts into a text.
        return MessageValueSanitizer.Sanitize(initiator.ClientApplicationName);
    }

    /// <summary>
    /// The caption of the button.
    /// </summary>
    /// <param name="applicationName">The name of the application; null — "Close".</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides stated by the levels of the transaction.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <returns>The caption, not encoded.</returns>
    public static string Caption(
        string? applicationName,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return string.IsNullOrEmpty(applicationName)
            ? texts.Localize(localizer, CorePageCloseAction.CloseText, contentLanguage)
            : texts.Localize(localizer, ReturnToApplicationText, contentLanguage)
                .Replace(ApplicationSlot, applicationName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the row of the action: the button and the line shown when the browser left the tab open.
    /// </summary>
    /// <param name="applicationName">The name of the application; null — "Close".</param>
    /// <param name="hidden">Whether the row waits for the script of the page to show it with the outcome.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides stated by the levels of the transaction.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <returns>Ready markup of the row.</returns>
    public static string Row(
        string? applicationName,
        bool hidden,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return CorePageCloseAction.OutcomeRow(
            ElementId,
            hidden,
            Caption(applicationName, localizer, texts, contentLanguage),
            texts.Localize(localizer, CorePageCloseAction.CloseHint, contentLanguage));
    }
}
