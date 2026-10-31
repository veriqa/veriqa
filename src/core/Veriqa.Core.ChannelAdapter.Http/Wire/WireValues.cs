// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Wire;

/// <summary>
/// Enumeration values the payloads of the wire carry (SPEC-042 C34–C36, C39). They are names of the wire
/// contract, not of the core types they are mapped from: a renamed member of the core must not move
/// the wire (SPEC-042 R10), so the mapping names every value explicitly.
/// </summary>
internal static class WireValues
{
    /// <summary>
    /// Values of <c>prompt.variant</c> of <c>send-prompt</c> (SPEC-042 C34).
    /// </summary>
    public static class PromptVariants
    {
        /// <summary>
        /// The prompt carries the details of the initiator.
        /// </summary>
        public const string Detailed = "detailed";

        /// <summary>
        /// The prompt carries the subject text of a confirmation transaction.
        /// </summary>
        public const string Subject = "subject";

        /// <summary>
        /// The prompt carries no details.
        /// </summary>
        public const string Suppressed = "suppressed";
    }

    /// <summary>
    /// Values of <c>prompt.reason</c> of the <c>suppressed</c> variant (SPEC-042 C34).
    /// </summary>
    public static class SuppressionReasons
    {
        /// <summary>
        /// The initiator snapshot was never collected.
        /// </summary>
        public const string InitiatorSnapshotUnavailable = "initiator_snapshot_unavailable";

        /// <summary>
        /// Showing the initiator details is switched off by configuration.
        /// </summary>
        public const string DisplayDisabledByConfiguration = "display_disabled_by_configuration";

        /// <summary>
        /// No message declares the subject of the confirmation.
        /// </summary>
        public const string ConfirmationTemplateUnavailable = "confirmation_template_unavailable";
    }

    /// <summary>
    /// Values of <c>outcome</c> of <c>report-outcome</c> (SPEC-042 C36).
    /// </summary>
    public static class Outcomes
    {
        /// <summary>
        /// The transaction was confirmed.
        /// </summary>
        public const string Confirmed = "confirmed";

        /// <summary>
        /// The transaction was declined.
        /// </summary>
        public const string Declined = "declined";

        /// <summary>
        /// The transaction expired.
        /// </summary>
        public const string Expired = "expired";

        /// <summary>
        /// The transaction failed.
        /// </summary>
        public const string Failed = "failed";

        /// <summary>
        /// The sign-in is confirmed and waits for the claims it still lacks — the one value that is
        /// not terminal.
        /// </summary>
        public const string AwaitingClaims = "awaiting_claims";
    }

    /// <summary>
    /// Values of the secondary action of the phone step, one set for both directions:
    /// <c>phone_number_request.secondary_action</c> of <c>send-message</c> (SPEC-042 C35) and
    /// <c>action</c> of the <c>phone_step_action</c> event (SPEC-042 C39).
    /// </summary>
    public static class PhoneStepActions
    {
        /// <summary>
        /// Continue the sign-in without the phone number.
        /// </summary>
        public const string Skip = "skip";

        /// <summary>
        /// Cancel the sign-in.
        /// </summary>
        public const string CancelSignIn = "cancel_sign_in";
    }

    /// <summary>
    /// Values of <c>display_intent</c> of <c>report-outcome</c> (SPEC-042 C36).
    /// </summary>
    public static class DisplayIntents
    {
        /// <summary>
        /// Show the outcome in place of the prompt.
        /// </summary>
        public const string ReplacePrompt = "replace_prompt";

        /// <summary>
        /// Show the outcome as a message of its own.
        /// </summary>
        public const string NewMessage = "new_message";
    }

    /// <summary>
    /// Values of <c>purpose</c> of <c>deep-link</c> (SPEC-042 C37): where the link is going to be used.
    /// </summary>
    public static class LinkPurposes
    {
        /// <summary>
        /// A button on a page of the core, or the link handed to the relying party.
        /// </summary>
        public const string Button = "button";

        /// <summary>
        /// The target the hop resolve endpoint leads to after a hop QR code is scanned.
        /// </summary>
        public const string HopQr = "hop_qr";

        /// <summary>
        /// The content of a QR picture that carries the link itself.
        /// </summary>
        public const string DirectQr = "direct_qr";
    }
}
