// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// What tells a preview of a hop URL from a person following it (SPEC-025 HOP-072): the allow-list of
/// link-preview agents, matched in the <c>User-Agent</c> without regard to case, and the headers and
/// values by which a browser announces a prefetch or a preview.
/// <para>
/// Only markers that name a preview agent unambiguously belong here. A marker that also occurs in the
/// user agent of an ordinary browser or of the webview of a messenger app would turn a person into a
/// preview, and the person would never be taken into the channel; an agent the list misses is taken
/// for a person, which is the default.
/// </para>
/// </summary>
internal static class HopPreviewMarkers
{
    /// <summary>Facebook link preview.</summary>
    public const string FacebookExternalHit = "facebookexternalhit";

    /// <summary>Facebook crawler.</summary>
    public const string Facebot = "Facebot";

    /// <summary>X (Twitter) card preview.</summary>
    public const string Twitterbot = "Twitterbot";

    /// <summary>Slack link unfurling.</summary>
    public const string Slackbot = "Slackbot";

    /// <summary>Telegram link preview.</summary>
    public const string TelegramBot = "TelegramBot";

    /// <summary>
    /// WhatsApp link preview. The trailing slash is part of the marker: the preview agent sends
    /// <c>WhatsApp/&lt;version&gt;</c>, and the bare name of the app is not matched, so a user agent that
    /// merely mentions the app is not taken for the preview agent.
    /// </summary>
    public const string WhatsApp = "WhatsApp/";

    /// <summary>Discord link embed.</summary>
    public const string Discordbot = "Discordbot";

    /// <summary>LinkedIn link preview.</summary>
    public const string LinkedInBot = "LinkedInBot";

    /// <summary>Skype link preview.</summary>
    public const string SkypeUriPreview = "SkypeUriPreview";

    /// <summary>Iframely embed service.</summary>
    public const string Iframely = "Iframely";

    /// <summary>Reddit link preview.</summary>
    public const string Redditbot = "redditbot";

    /// <summary>Apple link preview.</summary>
    public const string Applebot = "Applebot";

    /// <summary>Pinterest link preview.</summary>
    public const string Pinterestbot = "Pinterestbot";

    /// <summary>VK link preview.</summary>
    public const string VkShare = "vkShare";

    /// <summary>Google page renderer used for link previews.</summary>
    public const string GooglePageRenderer = "Google-PageRenderer";

    /// <summary>
    /// Google Read Aloud: a browser hands it the page it shows, and it loads the URL again from its own
    /// addresses, without the cookies of the browser.
    /// </summary>
    public const string GoogleReadAloud = "Google-Read-Aloud";

    /// <summary>Former token of Google Read Aloud.</summary>
    public const string GoogleSpeakr = "google-speakr";

    /// <summary>Google Messages link preview in a chat.</summary>
    public const string GoogleMessages = "GoogleMessages";

    /// <summary>Gemini Notebook fetching a URL a person added as a source.</summary>
    public const string GoogleGeminiNotebook = "Google-GeminiNotebook";

    /// <summary>Former token of Gemini Notebook (NotebookLM).</summary>
    public const string GoogleNotebookLm = "Google-NotebookLM";

    /// <summary>Google Pinpoint fetching a URL a person added to a collection.</summary>
    public const string GooglePinpoint = "Google-Pinpoint";

    /// <summary>Standard header of a speculative load (<c>Sec-Purpose: prefetch</c>).</summary>
    public const string SecPurposeHeader = "Sec-Purpose";

    /// <summary>Legacy purpose header of a prefetch.</summary>
    public const string PurposeHeader = "Purpose";

    /// <summary>Purpose header some browsers send instead of <c>Purpose</c>.</summary>
    public const string XPurposeHeader = "X-Purpose";

    /// <summary>Prefetch header of Firefox.</summary>
    public const string XMozHeader = "X-Moz";

    /// <summary>Value of a purpose header that announces a prefetch.</summary>
    public const string PrefetchPurpose = "prefetch";

    /// <summary>Value of a purpose header that announces a preview.</summary>
    public const string PreviewPurpose = "preview";

    /// <summary>
    /// The allow-list of link-preview agents.
    /// </summary>
    public static IReadOnlyList<string> UserAgents { get; } =
    [
        FacebookExternalHit, Facebot, Twitterbot, Slackbot, TelegramBot, WhatsApp, Discordbot, LinkedInBot,
        SkypeUriPreview, Iframely, Redditbot, Applebot, Pinterestbot, VkShare, GooglePageRenderer,
        GoogleReadAloud, GoogleSpeakr, GoogleMessages, GoogleGeminiNotebook, GoogleNotebookLm, GooglePinpoint
    ];

    /// <summary>
    /// The headers by which a browser announces a prefetch or a preview.
    /// </summary>
    public static IReadOnlyList<string> PurposeHeaders { get; } =
        [SecPurposeHeader, PurposeHeader, XPurposeHeader, XMozHeader];

    /// <summary>
    /// The values of a purpose header that mark the request as a preview.
    /// </summary>
    public static IReadOnlyList<string> PreviewPurposes { get; } = [PrefetchPurpose, PreviewPurpose];
}
