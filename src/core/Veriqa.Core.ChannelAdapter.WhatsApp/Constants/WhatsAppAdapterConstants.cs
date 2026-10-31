// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

/// <summary>
/// Common WhatsApp adapter constants (SPEC-003 §10, §21).
/// Provider-specific constants are extracted into <see cref="MetaCloudApiConstants"/>.
/// </summary>
internal static class WhatsAppAdapterConstants
{
    /// <summary>
    /// Adapter version in semver format.
    /// </summary>
    public const string AdapterVersion = "2.0.0";

    /// <summary>
    /// Webhook path relative to the base URL.
    /// </summary>
    public const string WebhookPath = "/api/channels/whatsapp/webhook";

    /// <summary>
    /// Confirm button text.
    /// </summary>
    public const string ConfirmButtonText = "Confirm ✅";

    /// <summary>
    /// Decline button text.
    /// </summary>
    public const string DeclineButtonText = "Decline ❌";

    /// <summary>
    /// WhatsApp deep link base URL.
    /// </summary>
    public const string DeepLinkBaseUrl = "https://wa.me/";

    /// <summary>
    /// Maximum raw metadata size in bytes (CA-005). Alias of the contract-wide
    /// <see cref="ChannelAdapterLimits.MaxRawMetadataSize"/> — the limit is identical for every channel,
    /// so this class does not carry its own literal.
    /// </summary>
    public const int MaxRawMetadataSize = ChannelAdapterLimits.MaxRawMetadataSize;

    /// <summary>
    /// Deep link prefix for authentication.
    /// </summary>
    public const string DeepLinkPrefix = CallbackDataPrefixes.Auth;

    /// <summary>
    /// Format of a business-scoped user ID (BSUID) of a WhatsApp user, as Meta documents it: the two-letter
    /// ISO 3166 country code, a period and up to 128 Latin letters and digits (<c>US.13491208655302741918</c>).
    /// A parent BSUID (<c>US.ENT.…</c>) does not match — the adapter works with the plain one. Anchored with
    /// <c>\A</c>/<c>\z</c>, so a trailing line break does not pass.
    /// </summary>
    public const string BusinessScopedUserIdPattern = @"\A[A-Za-z]{2}\.[A-Za-z0-9]{1,128}\z";

    /// <summary>
    /// Base text of the button that opens a list of post-sign-in buttons, taken when the message
    /// brings no localized label of its own (<c>ActionButtonsContent.ChooserLabel</c> is null). The
    /// same English text is the Natural Key the core localizes the label by.
    /// </summary>
    public const string ChooserLabelBaseText = "Choose";

    /// <summary>
    /// Largest length of the body of a <c>cta_url</c> message, in characters (Meta, WhatsApp Cloud API,
    /// "Interactive CTA URL messages", checked 2026-10-24).
    /// </summary>
    public const int CtaUrlBodyMaxLength = 1024;

    /// <summary>
    /// Number of buttons of a <c>cta_url</c> message: the form carries exactly one (Meta,
    /// "Interactive CTA URL messages", checked 2026-10-24).
    /// </summary>
    public const int CtaUrlMaxButtons = 1;

    /// <summary>
    /// Largest length of the caption of the button of a <c>cta_url</c> message, in characters (Meta,
    /// "Interactive CTA URL messages", checked 2026-10-24).
    /// </summary>
    public const int CtaUrlButtonLabelMaxLength = 20;

    /// <summary>
    /// Largest length of the body of a reply buttons message, in characters (Meta, WhatsApp Cloud API,
    /// "Interactive reply buttons messages", checked 2026-10-24).
    /// </summary>
    public const int ReplyButtonsBodyMaxLength = 1024;

    /// <summary>
    /// Largest number of buttons of a reply buttons message (Meta, "Interactive reply buttons
    /// messages", checked 2026-10-24). A menu with more callback buttons is drawn as a list.
    /// </summary>
    public const int ReplyButtonsMaxButtons = 3;

    /// <summary>
    /// Largest length of the caption of a reply button, in characters (Meta, "Interactive reply buttons
    /// messages", checked 2026-10-24).
    /// </summary>
    public const int ReplyButtonTitleMaxLength = 20;

    /// <summary>
    /// Largest length of the identifier of a reply button, in characters, the ownership marker included
    /// (Meta, "Interactive reply buttons messages", checked 2026-10-24).
    /// </summary>
    public const int ReplyButtonIdMaxLength = 256;

    /// <summary>
    /// Largest length of the body of a list message, in characters (Meta, WhatsApp Cloud API,
    /// "Interactive list messages", checked 2026-10-24).
    /// </summary>
    public const int ListBodyMaxLength = 4096;

    /// <summary>
    /// Largest number of rows of a list message, all sections together (Meta, "Interactive list
    /// messages", checked 2026-10-24).
    /// </summary>
    public const int ListMaxRows = 10;

    /// <summary>
    /// Largest length of the caption of the button that opens a list, in characters (Meta, "Interactive
    /// list messages", checked 2026-10-24). The same caption titles the one section of the list, whose
    /// title the platform requires and allows 24 characters, so a caption within this limit fits both.
    /// </summary>
    public const int ListButtonTextMaxLength = 20;

    /// <summary>
    /// Largest length of the title of a row of a list, in characters (Meta, "Interactive list messages",
    /// checked 2026-10-24).
    /// </summary>
    public const int ListRowTitleMaxLength = 24;

    /// <summary>
    /// Largest length of the identifier of a row of a list, in characters, the ownership marker included
    /// (Meta, "Interactive list messages", checked 2026-10-24).
    /// </summary>
    public const int ListRowIdMaxLength = 200;
}
