// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The observation of a superseded hop token (SPEC-025 HOP-155): a person presented a token the page has
/// replaced, or was due to replace, by the time of the presentation. It writes one log entry and decides
/// nothing — the answer, the audit, the statuses and the transaction are the same as without it.
/// </summary>
/// <remarks>
/// Neither the token nor the hop URL reaches the entry: it names the transaction the check loaded and the
/// channel of the token, which the codec authenticated.
/// </remarks>
internal sealed class HopSupersededObservation
{
    /// <summary>
    /// Template of the log entry of a superseded token.
    /// </summary>
    private const string SupersededLogTemplate =
        "A superseded hop token was presented. ResultCode: {ResultCode}, TransactionId: {TransactionId}, "
        + "ChannelType: {ChannelType}";

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HopSupersededObservation> _logger;

    /// <summary>
    /// Clock the moment of the presentation is read from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the observation.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock.</param>
    public HopSupersededObservation(ILogger<HopSupersededObservation> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Writes the entry of a superseded token when the presentation is one: made by a person, the token and
    /// its transaction passed the check of the expiries (a refusal of the rows after it does not matter —
    /// the token of a page is replaced the same way whatever the channel answers now), the token expires
    /// before its transaction, and the share of its lifetime after which the page asks for a replacement has
    /// passed. Any other presentation writes nothing.
    /// </summary>
    /// <param name="requestKind">Who presented the URL.</param>
    /// <param name="outcome">Outcome of the check of the token and its transaction.</param>
    public void Observe(HopRequestKind requestKind, HopPresentationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (requestKind is not HopRequestKind.Human
            || !PassedExpiryRows(outcome.Refusal)
            || outcome is not { Token: { } token, Transaction: { } transaction }
            || !HopReissueSchedule.ExpiresBeforeTransaction(token.ExpiresAt, transaction.ExpiresAt)
            || _timeProvider.GetUtcNow() <= HopReissueSchedule.DueAt(token.IssuedAt, token.ExpiresAt))
        {
            return;
        }

        // transaction.Id.ToString() is Base62 — no log injection; the channel type is the one the codec
        // authenticated inside the token, empty for the unified token.
        _logger.LogInformation(
            SupersededLogTemplate,
            HopModeResultCodes.TokenSuperseded,
            transaction.Id.ToString(),
            token.ChannelType);
    }

    /// <summary>
    /// Whether the check passed the rows of the token and of its transaction's expiry: the outcome is
    /// admitted, or refused by a row after them — the state of entering a channel, the adapter and the switch,
    /// or their substitutes for the unified token (SPEC-025 HOP-074, HOP-143). The list is positive: a refusal
    /// added later is not observed until it is placed here.
    /// </summary>
    /// <param name="refusal">The refusal of the outcome; null — admitted.</param>
    /// <returns>True — the token and its transaction were found in time.</returns>
    private static bool PassedExpiryRows(HopRefusal? refusal) =>
        refusal is null
            or HopRefusal.NotInTransitionState
            or HopRefusal.AdapterMissing
            or HopRefusal.HopDisabled
            or HopRefusal.UnifiedSetEmpty
            or HopRefusal.UnifiedModeInactive;
}
