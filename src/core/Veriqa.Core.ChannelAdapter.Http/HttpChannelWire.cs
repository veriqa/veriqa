// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http;

/// <summary>
/// Names of the wire contract between the core and an external channel adapter (SPEC-042 §4): the
/// version the core speaks, the transport-signature headers and the vocabulary of the envelopes. The
/// values are the names of the specification verbatim, so a party that talks to the wrapper — a
/// conformance run, a reference adapter written in .NET — builds its envelopes from here instead of
/// retyping them.
/// </summary>
public static class HttpChannelWire
{
    /// <summary>
    /// Version of the wire contract the core writes into every envelope, <c>major.minor</c>
    /// (SPEC-042 C40).
    /// </summary>
    public const string Version = "1.0";

    /// <summary>
    /// Major version the core understands: an envelope of any other major version is outside the
    /// contract (SPEC-042 L42).
    /// </summary>
    public const int MajorVersion = 1;

    /// <summary>
    /// Header of the message identifier of the Standard Webhooks signature (SPEC-042 C31).
    /// </summary>
    public const string WebhookIdHeader = "webhook-id";

    /// <summary>
    /// Header of the signing moment of the Standard Webhooks signature, in unix seconds (SPEC-042 C31).
    /// </summary>
    public const string WebhookTimestampHeader = "webhook-timestamp";

    /// <summary>
    /// Header of the Standard Webhooks signatures: <c>v1,&lt;base64&gt;</c> per secret, space-separated
    /// (SPEC-042 C31).
    /// </summary>
    public const string WebhookSignatureHeader = "webhook-signature";

    /// <summary>
    /// Longest value of a pressed item of the menu a <see cref="Events.MenuItemPressed"/> event may
    /// carry, in characters (<see cref="string.Length"/>); an empty value or a longer one is read as an
    /// unrelated event (SPEC-042 C39).
    /// </summary>
    public const int MaxMenuItemValueLength = 1024;

    /// <summary>
    /// Values of <c>operation</c> of a call envelope — the calls the core makes to the external adapter
    /// (SPEC-042 C32–C38).
    /// </summary>
    public static class Operations
    {
        /// <summary>
        /// Send the confirmation prompt (SPEC-042 C34).
        /// </summary>
        public const string SendPrompt = "send-prompt";

        /// <summary>
        /// Send a message (SPEC-042 C35).
        /// </summary>
        public const string SendMessage = "send-message";

        /// <summary>
        /// Show the outcome of a transaction (SPEC-042 C36).
        /// </summary>
        public const string ReportOutcome = "report-outcome";

        /// <summary>
        /// Produce the deep link of a transaction (SPEC-042 C37).
        /// </summary>
        public const string DeepLink = "deep-link";

        /// <summary>
        /// Report the health of the adapter (SPEC-042 C38).
        /// </summary>
        public const string Health = "health";
    }

    /// <summary>
    /// Values of <c>event</c> of an inbound event envelope — what the external adapter posts to the
    /// webhook route of its channel (SPEC-042 C39).
    /// </summary>
    public static class Events
    {
        /// <summary>
        /// The user started the authentication of a transaction.
        /// </summary>
        public const string AuthStart = "auth_start";

        /// <summary>
        /// The user started the authentication by sending the short code shown on the sign-in page; the
        /// event names no transaction, the core finds it by the code. Posted only by a registration that
        /// declares the start by a code.
        /// </summary>
        public const string AuthStartByCode = "auth_start_by_code";

        /// <summary>
        /// The user confirmed a transaction.
        /// </summary>
        public const string AuthConfirm = "auth_confirm";

        /// <summary>
        /// The user declined a transaction.
        /// </summary>
        public const string AuthDecline = "auth_decline";

        /// <summary>
        /// The user shared a phone number.
        /// </summary>
        public const string PhoneShared = "phone_shared";

        /// <summary>
        /// The user chose the secondary action of the phone step.
        /// </summary>
        public const string PhoneStepAction = "phone_step_action";

        /// <summary>
        /// The user pressed a callback item of the post-sign-in menu. The event names no transaction:
        /// the press is handed to the host, not to a sign-in. The identifier of the delivery
        /// (<see cref="WebhookIdHeader"/>) is the identifier of the press the host deduplicates by.
        /// </summary>
        public const string MenuItemPressed = "menu_item_pressed";

        /// <summary>
        /// A vetted user wrote without naming a transaction.
        /// </summary>
        public const string Unaddressed = "unaddressed";

        /// <summary>
        /// The event has nothing to do with authentication.
        /// </summary>
        public const string Unrelated = "unrelated";
    }

    /// <summary>
    /// Error codes of a response envelope that belong to the wire contract itself (SPEC-042 C32);
    /// besides them an adapter answers with the codes of the channel contract that apply to the
    /// operation.
    /// </summary>
    public static class ErrorCodes
    {
        /// <summary>
        /// The adapter cannot carry the confirmation out; terminal only in the answer to
        /// <see cref="Operations.SendPrompt"/> (SPEC-042 R16).
        /// </summary>
        public const string ChannelCannotContinue = "channel_cannot_continue";

        /// <summary>
        /// The adapter does not speak the major version of the envelope (SPEC-042 L42).
        /// </summary>
        public const string UnsupportedWireVersion = "unsupported_wire_version";

        /// <summary>
        /// The adapter does not know the operation of the envelope (SPEC-042 L42).
        /// </summary>
        public const string UnsupportedOperation = "unsupported_operation";
    }
}
