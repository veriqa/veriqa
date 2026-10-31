// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

namespace Veriqa.Core.ChannelAdapter.Http.Constants;

/// <summary>
/// String and numeric literals of the HTTP channel wrapper (SPEC-042 §4–§6). The defaults below are
/// only the answers of a registration that states nothing: every value a registration may state is
/// read from its own configuration section.
/// </summary>
internal static class HttpChannelConstants
{
    /// <summary>
    /// Default timeout of one attempt of an outbound call, in seconds (SPEC-042 L44, key
    /// <c>Http:OperationTimeout</c>).
    /// </summary>
    public const int DefaultOperationTimeoutSeconds = 10;

    /// <summary>
    /// Default timeout of the deep-link call, in seconds (SPEC-042 L44, key <c>Http:DeepLinkTimeout</c>):
    /// short, because the call stands on the path that renders the sign-in page.
    /// </summary>
    public const int DefaultDeepLinkTimeoutSeconds = 2;

    /// <summary>
    /// Default budget of all attempts of one outbound call together with the pauses between them, in
    /// seconds (SPEC-042 L44, key <c>Http:RetryBudget</c>).
    /// </summary>
    public const int DefaultRetryBudgetSeconds = 20;

    /// <summary>
    /// Shortest timeout a registration may state (SPEC-042 L44): the lower bound the timeout strategy of
    /// the resilience pipeline accepts.
    /// </summary>
    public static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Longest timeout a registration may state (SPEC-042 L44): the upper bound the timeout strategy of
    /// the resilience pipeline accepts.
    /// </summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(1);

    /// <summary>
    /// Separator of the hours and the minutes of a timeout a registration states (<c>hh:mm:ss</c>): a
    /// value without it is a bare integer, which a time span reads as a number of days.
    /// </summary>
    public const char TimeSpanComponentSeparator = ':';

    /// <summary>
    /// Most retries of one outbound call after its first attempt (SPEC-042 L44, SPEC-003 §12.2).
    /// </summary>
    public const int MaxRetryAttempts = 3;

    /// <summary>
    /// Pause before the first retry, in seconds; every next pause doubles it — 1 s, 2 s, 4 s (SPEC-042
    /// L44, SPEC-003 §12.2).
    /// </summary>
    public const int FirstRetryDelaySeconds = 1;

    /// <summary>
    /// Name of the property of a resilience context that carries the moment an outbound call started,
    /// as a timestamp of the clock of the host: the retry budget left is measured from it.
    /// </summary>
    public const string CallStartedAtProperty = "Veriqa.HttpChannel.CallStartedAt";

    /// <summary>
    /// Name of the <c>HttpClient</c> of the outbound calls to the external adapters.
    /// </summary>
    public const string HttpClientName = "Veriqa.Core.ChannelAdapter.Http";

    /// <summary>
    /// Longest deep link accepted from an external adapter, in characters (SPEC-042 L45).
    /// </summary>
    public const int MaxDeepLinkLength = 2048;

    /// <summary>
    /// Natural Key of the caption of the confirm button — the key the built-in channels use, so its
    /// translations are shared (SPEC-042 L46).
    /// </summary>
    public const string ConfirmButtonText = "Confirm ✅";

    /// <summary>
    /// Natural Key of the caption of the decline button — the key the built-in channels use, so its
    /// translations are shared (SPEC-042 L46).
    /// </summary>
    public const string DeclineButtonText = "Decline ❌";

    /// <summary>
    /// Longest opaque settings string a registration may carry, in characters (SPEC-042 R12, C30).
    /// </summary>
    public const int MaxAdapterSettingsLength = 8192;

    /// <summary>
    /// Prefix of the Standard Webhooks serialization of a transport secret: the rest of the value is
    /// the key in base64 (SPEC-042 C30, C31).
    /// </summary>
    public const string TransportSecretPrefix = "whsec_";

    /// <summary>
    /// Shortest decoded transport key, in bytes (SPEC-042 C30).
    /// </summary>
    public const int MinTransportSecretBytes = 24;

    /// <summary>
    /// Longest decoded transport key, in bytes (SPEC-042 C30).
    /// </summary>
    public const int MaxTransportSecretBytes = 64;

    /// <summary>
    /// Allowed distance between the signing moment of an inbound event and the moment the core
    /// receives it, in seconds, in either direction (SPEC-042 C31): five minutes. A moment exactly on
    /// the boundary is accepted.
    /// </summary>
    public const long SignatureToleranceSeconds = 300;

    /// <summary>
    /// Prefix of one signature of the symmetric Standard Webhooks scheme — the only scheme of the wire
    /// version 1 (SPEC-042 C31); the asymmetric <c>v1a</c> never matches.
    /// </summary>
    public const string SignatureSchemePrefix = "v1,";

    /// <summary>
    /// Separator of the signatures in the <c>webhook-signature</c> header — one per secret during a
    /// rotation (SPEC-042 C31).
    /// </summary>
    public const char SignatureSeparator = ' ';

    /// <summary>
    /// Separator of the parts of the signed content <c>id.timestamp.body</c> (SPEC-042 C31).
    /// </summary>
    public const string SignedContentSeparator = ".";

    /// <summary>
    /// Separator of the major and the minor part of a wire version <c>major.minor</c> (SPEC-042 C40).
    /// </summary>
    public const char WireVersionSeparator = '.';

    /// <summary>
    /// Grammar of a URI scheme (RFC 3986 §3.1): <c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )</c>.
    /// </summary>
    public const string DeepLinkSchemePattern = @"\A[A-Za-z][A-Za-z0-9+.\-]*\z";

    /// <summary>
    /// Prefix of the Windows protocol-handler schemes, every one of which is refused as a deep-link
    /// scheme (SPEC-042 E55).
    /// </summary>
    public const string ForbiddenDeepLinkSchemePrefix = "ms-";

    /// <summary>
    /// Schemes a registration may not admit for a deep link, compared without regard to case
    /// (SPEC-042 E55): a deep link is opened by the browser of the person signing in, and these schemes
    /// either run code, read local content or leave the transport unprotected. <c>https</c> is admitted
    /// always and is not listed among the extra schemes at all.
    /// </summary>
    public static readonly FrozenSet<string> ForbiddenDeepLinkSchemes = new[]
    {
        Uri.UriSchemeHttp,
        "javascript",
        "data",
        "vbscript",
        Uri.UriSchemeFile,
        "blob",
        "about",
        "filesystem",
        "intent",
        "search-ms"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
