// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Authentication page renderer interface (SPEC-007 §7, UI-050).
/// Allows embedded-mode clients to fully replace the rendering logic.
/// </summary>
public interface IAuthPageRenderer
{
    /// <summary>
    /// Generates the authentication HTML page.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>HTML content of the page.</returns>
    string RenderAuthPage(AuthPageRenderContext context);
}

/// <summary>
/// Authentication page rendering context.
/// Contains all data required to build the HTML.
/// </summary>
public sealed class AuthPageRenderContext
{
    /// <summary>
    /// Session identifier (external session_id).
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional callback URL to redirect to after successful authentication.
    /// May be used by custom renderer implementations; the default renderer
    /// obtains the redirect URL from the SignalR message TransactionStatusMessage.RedirectUrl.
    /// </summary>
    public string? CallbackUrl { get; init; }

    /// <summary>
    /// Transaction expiration moment (UTC).
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Seconds LEFT of the transaction's lifetime at the moment the page was built, measured by the
    /// server's own clock. Never negative: a transaction already past its moment has none left.
    /// <para>
    /// The countdown of the page is driven by this remainder and not by <see cref="ExpiresAt"/>,
    /// because the two answer different questions. A moment has to be compared against a clock, and
    /// the only clock the page has is the browser's: a device running minutes ahead then declared a
    /// live transaction expired and stopped listening for its outcome. A remainder needs no shared
    /// clock at all — the browser measures the interval with its own, and only the DRIFT over that
    /// interval can be wrong.
    /// </para>
    /// </summary>
    public double RemainingSeconds { get; init; }

    /// <summary>
    /// Where the page sends the user to start over once its transaction has expired, or failed on a
    /// page that shows that ending as its last screen; null or empty — the page offers no way out and
    /// shows the ending as text alone.
    /// <para>
    /// It is stated by the caller because only the caller knows whether starting over is a thing this
    /// surface may offer: the sign-in window is reached by an authorize request that can simply be
    /// made again, while a confirmation transaction created server-to-server has no such request to
    /// repeat, and a page inventing one would send the user somewhere the relying party never
    /// created (SPEC-039 C15).
    /// </para>
    /// </summary>
    public string? RestartUrl { get; init; }

    /// <summary>
    /// The form behind the cancel button — "Cancel sign-in" on the sign-in window, "Cancel confirmation"
    /// on the entry page of a confirmation (SPEC-007 UI-122); null (the default) — the page renders no such
    /// button.
    /// <para>
    /// It is stated by the caller for the reason <see cref="RestartUrl"/> is: where the refusal goes and who
    /// may send it are the surface's to know — the sign-in window returns it to the client that started the
    /// sign-in and checks the browser that started it, the entry page of a confirmation shows its receipt
    /// to the holder of its address — and the button is there only where the deployment left it switched
    /// on. The scanner page of either type is another device and carries none.
    /// </para>
    /// </summary>
    public AuthPageCancelForm? CancelForm { get; init; }

    /// <summary>
    /// Channel data for display (deep link, QR code).
    /// </summary>
    public required IReadOnlyList<ChannelDisplayInfo> Channels { get; init; }

    /// <summary>
    /// The lines of the channels of the page, by <see cref="ChannelDisplayInfo.ChannelType"/>, already
    /// resolved by the caller over the levels of the transaction and worded in the content language of the
    /// page — messages of the template mechanism, so the renderer only shows them. A channel of the page
    /// without an entry has no line once the answer is awaited in it, and its scan and the start of the
    /// transition into it take the lines without a channel. Empty (the default) — no channel has lines.
    /// </summary>
    public IReadOnlyDictionary<string, AuthPageChannelLines> ChannelLines { get; init; } =
        ReadOnlyDictionary<string, AuthPageChannelLines>.Empty;

    /// <summary>
    /// The line of a begun transition into a channel the page does not show, already resolved by the
    /// caller; <c>{0}</c> stands for the name of the channel, which the script of the page fills by the
    /// channel of the event. Null — none.
    /// </summary>
    public string? FallbackChannelLaunchedLine { get; init; }

    /// <summary>
    /// The channel the sender entered the transaction by, when the page is rendered while the answer is
    /// awaited in that channel — the fact the hub replays to a page that subscribes as the answer of the
    /// bot (SPEC-025 HOP-180, SPEC-007 UI-119). The page is then rendered in the state that answer puts it
    /// in, so a page reloaded at that moment does not show the channel choice first. Null (the default) —
    /// the answer is not awaited in a channel.
    /// </summary>
    public string? AnsweredChannelType { get; init; }

    /// <summary>
    /// Language code (ru, en, zh).
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// CSP nonce for inline scripts (SPEC-007 §6.3).
    /// If set, it is added to all &lt;script&gt; tags as the nonce="..." attribute.
    /// </summary>
    public string? CspNonce { get; init; }

    /// <summary>
    /// Effective design of the page (SPEC-012 §4.2–4.3), already resolved by the caller: every field
    /// carries the value of the level that owns it (CFG-210). The renderer merges nothing — a level
    /// added to a design field reaches the page without a line of change here, and an integrator's own
    /// renderer cannot silently lose one.
    /// </summary>
    public AuthPageDesignOptions Design { get; init; } = new();

    /// <summary>
    /// Page title stated by an owning level (SPEC-012 CFG-203); null — no level stated one and the
    /// renderer uses its own text. The stated value is a Natural Key, not finished output: the
    /// renderer resolves it through the host locale files exactly as it resolves its own key, and a
    /// value no locale file carries stays the text it already is.
    /// <para>
    /// The value is RAW and may carry markup: the built-in renderer emits it unencoded when it is plain
    /// text or inline markup of the admitted shape — the tags <c>span</c>, <c>strong</c>, <c>em</c>,
    /// <c>a</c> and <c>br</c> — and as encoded text otherwise. A renderer of the integrator's own
    /// receives the same raw value and must either encode it or check it itself.
    /// </para>
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// On-page instruction stated by an owning level — the wording of every device that
    /// <see cref="InstructionWithoutQr"/> and <see cref="InstructionWithoutButton"/> do not word; null —
    /// the renderer's own text. A stated value is a Natural Key, resolved the same way as
    /// <see cref="Title"/>, and is raw in the same sense: it may carry markup, which a renderer of the
    /// integrator's own must encode or check itself.
    /// </summary>
    public string? Instruction { get; init; }

    /// <summary>
    /// Wording of the stated instruction for a page that hides the QR code — a phone under the default
    /// QR visibility; null — <see cref="Instruction"/>. Which device hides the QR code is knowable in the
    /// browser only, so a renderer puts every wording into the markup and lets the page pick one. Raw
    /// and resolved like <see cref="Instruction"/>.
    /// </summary>
    public string? InstructionWithoutQr { get; init; }

    /// <summary>
    /// Wording of the stated instruction for a page that shows the QR code and hides the channel
    /// buttons; null — <see cref="Instruction"/>. Picked in the browser and raw like
    /// <see cref="InstructionWithoutQr"/>.
    /// </summary>
    public string? InstructionWithoutButton { get; init; }

    /// <summary>
    /// Colors the levels of the page state for the buttons of its channels, by channel type (key
    /// <c>AuthPageDesign.ChannelButtonColor</c>), already resolved by the caller over the same context as
    /// <see cref="Design"/>; null — none stated, and every button keeps the color of its channel. A value
    /// is <c>#RRGGBB</c> as the levels stated it; a renderer of the integrator's own that writes it into a
    /// stylesheet must check it there, the built-in one leaves out anything else.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ChannelButtonColors { get; init; }

    /// <summary>
    /// Overrides of the renderer's OWN texts stated by the levels of the page (SPEC-012 §4.3, key
    /// <c>AuthPage.Texts</c>), already resolved by the caller over the same context as
    /// <see cref="Design"/>; <see cref="CorePageTexts.None"/> (the default) — no text is overridden.
    /// <para>
    /// They apply only where the renderer takes a built-in Natural Key of its own. A text stated through a
    /// dedicated setting — <see cref="Title"/>, <see cref="Instruction"/>, the brand name and the markup of
    /// the design, a terminal line the caller states — is never replaced by them.
    /// </para>
    /// </summary>
    public CorePageTexts Texts { get; init; } = CorePageTexts.None;

    /// <summary>
    /// Effective QR visibility of the window, resolved by the caller (SPEC-012 CFG-210). It arrives
    /// already resolved because the resolution context of the sign-in page is built ONCE, at the entry
    /// of the request, and travels explicitly: a renderer — including an integrator's own — neither
    /// builds a context nor reaches for one ambiently. Unset, it is the value the product ships,
    /// <see cref="QrCodeVisibility.DesktopOnly"/>.
    /// </summary>
    public QrCodeVisibility QrVisibility { get; init; } = QrCodeVisibility.DesktopOnly;

    /// <summary>
    /// Effective visibility of the channel buttons of the window — the buttons that take the user into
    /// a channel by its deep link, the "open mail client" button of Email among them. It arrives
    /// resolved by the caller, for the reason <see cref="QrVisibility"/> does, and already reconciled
    /// with it: a pair that would leave a device with neither the QR code nor the button reaches the
    /// renderer as <see cref="Configuration.Enums.ChannelButtonVisibility.Always"/>. Unset, it is the
    /// value the product ships — the buttons are on every device.
    /// <para>
    /// The controls that lead into no channel are not its subject: the email-address form, the button
    /// revealing that form and the links switching the two Email sections stay whatever the value.
    /// </para>
    /// </summary>
    public ChannelButtonVisibility ChannelButtonVisibility { get; init; } = ChannelButtonVisibility.Always;

    /// <summary>
    /// Layout of the channels on a window that shows the QR code of each of them, resolved by the
    /// caller for the reason <see cref="QrVisibility"/> is. Unset, it is the value the product ships,
    /// <see cref="Configuration.Enums.ChannelLayout.Tabs"/>.
    /// <para>
    /// It is a request about ARRANGEMENT and holds where the window would otherwise show tabs — two
    /// channels or more, each with a QR code of its own. Under
    /// <see cref="Configuration.Enums.ChannelLayout.Grid"/> such a window shows the panels of all its
    /// channels at once, each a group named by its channel, and no tabs. A window of one channel, a
    /// window laid out as buttons (<see cref="QrCodeVisibility.Never"/>, the scanner page) and a window
    /// with the unified QR code (<see cref="UnifiedQrCodeBase64"/>) have nothing to arrange and are
    /// rendered the same whatever the value.
    /// </para>
    /// </summary>
    public ChannelLayout ChannelLayout { get; init; } = ChannelLayout.Tabs;

    /// <summary>
    /// Whether the page hints at the channel the user went through last time on this device, resolved
    /// by the caller for the reason <see cref="QrVisibility"/> is (key
    /// <c>AuthPageDesign.RememberChannel.Hint</c>). <see langword="false"/> (the default) — the page is
    /// rendered as without the setting.
    /// <para>
    /// The channel is remembered as its TYPE in a cookie of the Veriqa host that the script of the page
    /// writes and reads; the server never reads it. The value therefore decides what the page hands its
    /// script, and the script puts the remembered channel forward only where it is one of the channels
    /// of this page.
    /// </para>
    /// </summary>
    public bool RememberChannelHint { get; init; }

    /// <summary>
    /// Whether a page that shows the unified QR code (<see cref="UnifiedQrCodeBase64"/>) offers, on a
    /// desktop, the checkbox that remembers the channel the user went through, resolved by the caller
    /// for the reason <see cref="QrVisibility"/> is (key <c>AuthPageDesign.RememberChannel.Checkbox</c>).
    /// <see langword="false"/> (the default) — the page is rendered as without the setting.
    /// <para>
    /// The channel is kept in a cookie of the Veriqa host that the script of the page writes, only once
    /// the checkbox is checked, and reads; the server never reads it. A page without the unified QR code
    /// is rendered the same whatever the value. A page of the unified QR code without the per-channel QR
    /// codes (<see cref="UnifiedQrWithChannelQrCodes"/> off) carries them under the value, hidden until the
    /// page shows the remembered channel alone, and replaces them as it replaces the unified one.
    /// </para>
    /// </summary>
    public bool RememberChannelCheckbox { get; init; }

    /// <summary>
    /// Whether the page is allowed to take the browser off itself when a status arrives that has a
    /// navigation target. <see langword="true"/> (the default) — the behaviour of the sign-in window:
    /// a completed transaction continues on the OIDC callback.
    /// <para>
    /// <see langword="false"/> — the page addresses a transaction that has NO browser callback of its
    /// own (a confirmation created server-to-server, SPEC-039 C15): the terminal outcome is shown in
    /// place, the browser stays on this URL and the transaction stays readable through the status
    /// surface. In this mode the callback target is not built and a target arriving in a status
    /// message is ignored — the page has one destination of its own instead, and only that one
    /// (<see cref="ConfirmationQuestionUrl"/>).
    /// </para>
    /// </summary>
    public bool NavigatesAway { get; init; } = true;

    /// <summary>
    /// Root-relative URL of the page where the core ASKS what this transaction is about
    /// (SPEC-039 R37, E29) — the single destination of a page rendered with
    /// <see cref="NavigatesAway"/> = <see langword="false"/>. Null or empty — the page has no
    /// destination at all and stays where it is, which is what a page WITH navigation gets: on that
    /// path the question belongs to the OIDC callback, not here.
    /// <para>
    /// It is stated by the caller rather than composed by the page script, so that both delivery
    /// paths of a status — the SignalR message and the polling fallback — take the browser to the
    /// same place: the target is a fact of the transaction, not a field one of the two carries. The
    /// path base is applied by the page itself, as it is to every other root-relative URL here.
    /// </para>
    /// </summary>
    public string? ConfirmationQuestionUrl { get; init; }

    /// <summary>
    /// Terminal line for a CONFIRMED outcome, ready and localized; null or empty — the renderer uses
    /// its own sign-in wording ("Sign-in successful!").
    /// <para>
    /// A page that does not navigate away shows its status line as the LAST screen the user sees
    /// (SPEC-039 R51): on the sign-in path that line lives for the moment before the callback takes
    /// over, while here it stays, and a sign-in wording would assert a sign-in that never happened.
    /// The wording therefore comes from the caller — from the one port that words terminal outcomes —
    /// and the renderer neither resolves nor invents one, which is what keeps this page free of a
    /// second set of terminal texts.
    /// </para>
    /// </summary>
    public string? TerminalConfirmedText { get; init; }

    /// <summary>
    /// Terminal line for an EXPIRED outcome; null or empty — the renderer's own sign-in wording. See
    /// <see cref="TerminalConfirmedText"/> for why it is stated by the caller.
    /// </summary>
    public string? TerminalExpiredText { get; init; }

    /// <summary>
    /// Terminal line for a transaction the USER declined; null or empty — the renderer's own sign-in
    /// wording ("Sign-in declined"). A refusal is a line of its own on every page, never the line of a
    /// failure.
    /// </summary>
    public string? TerminalDeclinedText { get; init; }

    /// <summary>
    /// Terminal line for a failure that is NOT the user's refusal; null or empty — the renderer's own
    /// sign-in wording ("Authentication error"). A failure with no reason stated lands here as well:
    /// asserting that the user refused, without the reason saying so, would be the page inventing an
    /// outcome.
    /// </summary>
    public string? TerminalErrorText { get; init; }

    /// <summary>
    /// Type of the transaction the page belongs to. The renderer's own texts that name the act speak of a
    /// sign-in for a sign-in and for a caller that states no type (null), and of a confirmation for any
    /// other transaction.
    /// </summary>
    public string? TransactionType { get; init; }

    /// <summary>
    /// Subject of the confirmation, ready and localized as PLAIN text, shown when the deployment put
    /// this surface into the set of subject-display surfaces (SPEC-012 §4.11, CFG-104); null or empty —
    /// the page shows no subject and its markup is unchanged.
    /// <para>
    /// The text arrives worded: the substitution, the sanitizing and the degradation between wording
    /// variants happen in the one engine of the contour, and this page's whole share of them is the
    /// HTML encoding on output (SPEC-039 R8). The question itself is NOT asked here — this surface
    /// only shows what is being confirmed (R36).
    /// </para>
    /// <para>
    /// It is worded in <see cref="Language"/>. When the page's own texts fall back to the English base
    /// text, the subject keeps that language and its line says so with its own <c>lang</c>.
    /// </para>
    /// </summary>
    public string? SubjectText { get; init; }

    /// <summary>
    /// Whether this is the scanner page of the unified hop QR code — the page the phone that scanned the
    /// code gets on the route of the hop URL (SPEC-025 HOP-146). The channels are laid out as buttons by
    /// the server, no QR code is on the page whatever <see cref="QrVisibility"/> states, the buttons are
    /// on it whatever <see cref="ChannelButtonVisibility"/> states — they are the only way further — and
    /// the instruction a page shows once the hop has started is off: the page stays where it is after a
    /// tap — with a choice of channels in the state of the chosen channel, whose way back brings the
    /// buttons back (HOP-193), with one channel on its buttons — so a second tap is the fallback when the
    /// app did not open (HOP-186). <see langword="false"/> (the default) — every other page.
    /// True only on the scanner page, which the built-in renderer always renders: a renderer of the host's own
    /// (<c>UseAuthPageRenderer</c>) never receives it true.
    /// </summary>
    public bool IsScannerPage { get; init; }

    /// <summary>
    /// Whether the page offers the action under the outcome of its transaction — a button that only closes
    /// the tab, rendered hidden and shown with an ending of the transaction (success, refusal, failure,
    /// expiry), with the line "You can close this tab." for a browser that leaves the tab open. The scanner
    /// page of the unified hop QR code offers it: it reports an outcome the browser of the sign-in window
    /// goes on from. <see langword="false"/> (the default) — every other page.
    /// True only on the scanner page, which the built-in renderer always renders: a renderer of the host's own
    /// (<c>UseAuthPageRenderer</c>) never receives it true.
    /// </summary>
    public bool OffersOutcomeClose { get; init; }

    /// <summary>
    /// The name of the application the caption of that action names — "Return to {0}" with the name in the
    /// slot; null — the caption is "Close". The core states a name when the page is opened on the same type
    /// of device (desktop, mobile or tablet) as the one the sign-in started on and the application name is
    /// known. Ignored where <see cref="OffersOutcomeClose"/> is <see langword="false"/>.
    /// Stated only on the scanner page, which the built-in renderer always renders: a renderer of the host's own
    /// (<c>UseAuthPageRenderer</c>) never receives a name in it.
    /// </summary>
    public string? OutcomeCloseApplicationName { get; init; }

    /// <summary>
    /// Whether a tap on a channel button of the scanner page sends the client signal
    /// <c>channel_launched</c> with the type of the channel back to the hop URL (SPEC-025 HOP-156); the
    /// caller states it from the effective acceptance of client signals for the transaction. Ignored
    /// outside the scanner page (<see cref="IsScannerPage"/>): on any other page the phone is the
    /// initiator, and there is no other device to tell which channel was chosen.
    /// True only on the scanner page, which the built-in renderer always renders: a renderer of the host's own
    /// (<c>UseAuthPageRenderer</c>) never receives it true.
    /// </summary>
    public bool SendsChannelLaunchedSignal { get; init; }

    /// <summary>
    /// Whether the scanner page computes the browser fingerprint and sends it back to the hop URL with the
    /// signal of the opened page (SPEC-025 HOP-090); the caller states it from the effective collection of
    /// the fingerprint for the transaction. Ignored outside the scanner page
    /// (<see cref="IsScannerPage"/>): any other page is not opened by a hop URL and has no receiver for it.
    /// True only on the scanner page, which the built-in renderer always renders: a renderer of the host's own
    /// (<c>UseAuthPageRenderer</c>) never receives it true.
    /// </summary>
    public bool CollectsHopFingerprint { get; init; }

    /// <summary>
    /// The target the scanner page of one channel opens by itself, once per tab, as the launch page of the
    /// hop does: the absolute <c>mailto:</c> of the Email channel the page is laid out for (SPEC-025
    /// HOP-195); the caller states it where the hop URL of that channel is answered with this page and the
    /// transaction does not already wait for the person on a page of the core (HOP-179). Null (the
    /// default) — the page does not open the channel by itself; a tap on its button does. Ignored outside
    /// the scanner page (<see cref="IsScannerPage"/>).
    /// Stated only on the scanner page, which the built-in renderer always renders: a renderer of the host's own
    /// (<c>UseAuthPageRenderer</c>) never receives a target in it.
    /// </summary>
    public string? HopLaunchTarget { get; init; }

    /// <summary>
    /// The unified hop QR code of the page as a Base64 PNG, without a data URI prefix (the form of
    /// <see cref="ChannelDisplayInfo.QrCodeBase64"/>): the one QR code of a transaction whose channel is
    /// chosen on the phone that scans it (SPEC-025 HOP-183). The caller states it for a page of two
    /// channels or more whose QR visibility is not <see cref="QrCodeVisibility.Never"/>, when the unified
    /// mode applies. The page then shows this one QR code in the channel container with the channel
    /// buttons under it, or beside it where the window is wide enough for two columns, and — unless
    /// <see cref="UnifiedQrWithChannelQrCodes"/> — neither tabs nor per-channel QR codes (HOP-187); the QR
    /// code obeys
    /// <see cref="QrVisibility"/> as the per-channel ones do. Null or empty — the per-channel layout;
    /// ignored on the scanner page (<see cref="IsScannerPage"/>), which shows no QR code.
    /// </summary>
    public string? UnifiedQrCodeBase64 { get; init; }

    /// <summary>
    /// Own expiry of the unified hop token <see cref="UnifiedQrCodeBase64"/> carries (SPEC-025 HOP-149); null —
    /// the page has no unified QR code. A page replaces the unified QR code while it is shown when its token
    /// expires before the transaction (HOP-152); a renderer that does not read this value shows the QR code
    /// until its token expires.
    /// </summary>
    public DateTimeOffset? UnifiedQrExpiresAt { get; init; }

    /// <summary>
    /// Whether the page of the unified QR code (<see cref="UnifiedQrCodeBase64"/>) keeps the per-channel QR
    /// codes as well: tabs, the first of them — "Any channel" — with the unified QR code and the buttons of
    /// all channels, followed by a tab per channel with its own QR code. The caller states it from the QR
    /// mode of hop (<see cref="HopQrMode.UnifiedWithChannels"/>). Ignored when
    /// <see cref="UnifiedQrCodeBase64"/> is null or empty, on the scanner page (<see cref="IsScannerPage"/>),
    /// and on a page of one channel or of <see cref="QrCodeVisibility.Never"/>, which have no tab to add and
    /// no channel QR code to keep.
    /// </summary>
    public bool UnifiedQrWithChannelQrCodes { get; init; }

    /// <summary>
    /// Whether the "Any channel" tab shows the buttons of the channels beside the unified QR code (SPEC-025
    /// HOP-187); <c>false</c> — the tab shows the unified QR code alone, while the tab of each channel keeps its
    /// button. The caller states it from the hop setting
    /// <see cref="HopModeOptions.ShowChannelButtonsOnAnyChannelTab"/>. Taken into account only on a page that
    /// shows the "Any channel" tab (<see cref="UnifiedQrWithChannelQrCodes"/>); ignored anywhere else.
    /// </summary>
    public bool AnyChannelTabShowsChannelButtons { get; init; } = true;

    /// <summary>
    /// Application path base (<c>HttpContext.Request.PathBase</c>) when hosting the issuer under
    /// a sub-path of the origin (for example, <c>/demo</c> for https://veriqa.app/demo). Empty string —
    /// application at the root. The renderer prepends the prefix to all root-relative page URLs
    /// (SignalR hub, fallback polling, email endpoints, bundled assets) — otherwise, behind a prefixed
    /// route, the browser would request them outside the application.
    /// </summary>
    public string PathBase { get; init; } = string.Empty;
}

/// <summary>
/// Built-in authentication page renderer (SPEC-007 §7).
/// Generates production-ready HTML with QR codes, channel buttons,
/// SignalR connection, countdown, and animations.
/// </summary>
internal sealed class DefaultAuthPageRenderer : IAuthPageRenderer
{
    /// <summary>
    /// Transaction status polling interval in fallback mode (ms). 3000 ms = ~20 requests/min from one
    /// page — far inside the polling rate limit (600 per minute by default,
    /// <see cref="Configuration.RateLimitOptions.PollingPermitLimit"/>), which several pages behind one
    /// address share. SPEC-007 §5.4. The page of the claims completion polls at the same interval, under
    /// the same limit.
    /// </summary>
    internal const int PollingIntervalMs = 3000;

    /// <summary>
    /// Tab-count threshold at which tabs are laid out as a 2×2 grid (modifier
    /// <c>veriqa-tabs--grid</c>) instead of a single row — otherwise, on a narrow screen the last tab
    /// (Email) would be clipped past the right edge of the container. It counts the tabs of the strip,
    /// which are the channels of the page: a strip with the "Any channel" tab is laid out by a rule of its
    /// own (<see cref="AnyChannelTabsClass"/>) whatever the number of its tabs.
    /// </summary>
    private const int FourTabGridThreshold = 4;

    /// <summary>
    /// Channel-tab threshold at which the tab grid holds three tabs to a row instead of two (modifier
    /// <see cref="ThreeColumnTabsClass"/>), so a strip of many channels takes fewer rows. It counts the
    /// channel tabs alone: the "Any channel" tab keeps a row of its own and is not one of them.
    /// </summary>
    private const int ThreeColumnTabsThreshold = 5;

    /// <summary>
    /// Modifier of the tab grid next to <c>veriqa-tabs--grid</c>: three tabs to a row, and the tabs of an
    /// incomplete last row share its whole width — one tab takes the row, two take half of it each — so the
    /// grid never leaves an empty cell.
    /// </summary>
    private const string ThreeColumnTabsClass = "veriqa-tabs--three-columns";

    /// <summary>
    /// Modifier of the tab strip of a page with the "Any channel" tab, next to <c>veriqa-tabs--grid</c>
    /// (SPEC-025 HOP-187): the "Any channel" tab takes a row of its own across the strip, the channel tabs
    /// stand under it two to a row (three from <see cref="ThreeColumnTabsThreshold"/> on), and the last of an
    /// odd number of them takes its row whole — so the grid never leaves an empty cell.
    /// </summary>
    private const string AnyChannelTabsClass = "veriqa-tabs--any-channel";

    /// <summary>
    /// The mark of <see cref="AuthPageStrings.ChannelTabWithQrCode"/> the renderer replaces with the glyph of
    /// a QR code, at every place the label carries it.
    /// </summary>
    private const char QrCodeMark = '▦';

    /// <summary>
    /// Slot of <see cref="AuthPageStrings.ChannelTabWithQrCode"/> — the place the channel is named at.
    /// </summary>
    private const string TabChannelSlot = "{0}";

    /// <summary>
    /// Classes of the glyph of a QR code in a tab label: the size of the brand icon of the tab, and no brand
    /// color — the glyph takes the text color of the tab.
    /// </summary>
    private const string TabQrIconClass = "veriqa-tab-icon veriqa-tab-icon--qr";

    /// <summary>
    /// The glyph of a QR code in a tab label (<see cref="TabQrIconClass"/>): the same markup for every tab.
    /// </summary>
    private static readonly string TabQrGlyph = ChannelIconSvg.RenderQrCode(TabQrIconClass);

    /// <summary>
    /// Name of the root attribute that carries the QR visibility setting into the page. The value is
    /// handed over as it is resolved — the server does not branch the markup on it, the page decides
    /// (SPEC-007 UI-020). The stylesheet of the window keys its hiding rules on this attribute.
    /// </summary>
    private const string QrVisibilityAttributeName = "data-qr-visibility";

    /// <summary>
    /// Name of the root attribute the page script sets once it has established the device class. It is
    /// absent by default, and every rule that hides something requires it to be present — so a device
    /// that could not be established keeps showing everything.
    /// </summary>
    private const string DeviceAttributeName = "data-device";

    /// <summary>
    /// Name of the root attribute that carries the visibility of the channel buttons into the page —
    /// the counterpart of <see cref="QrVisibilityAttributeName"/>, handed over the same way: the server
    /// does not branch the markup on it, the stylesheet of the window hides the buttons by it and by
    /// <see cref="DeviceAttributeName"/>.
    /// </summary>
    private const string ChannelButtonVisibilityAttributeName = "data-channel-button-visibility";

    /// <summary>
    /// Name of the root attribute carrying the state of the page. It is the third attribute of the
    /// same mechanism as <see cref="QrVisibilityAttributeName"/> and <see cref="DeviceAttributeName"/>:
    /// the state is written on the root element and the stylesheet decides what each state shows, so
    /// the page needs no handler of its own on any of the elements it puts out.
    /// </summary>
    private const string PageStateAttributeName = "data-page-state";

    /// <summary>
    /// State of a page whose transaction is still running — the value the markup carries unless the page is
    /// rendered with the line of the answer of a channel's bot (<see cref="AnswerElsewherePageStateValue"/>).
    /// </summary>
    private const string WaitingPageStateValue = "waiting";

    /// <summary>
    /// State of a page whose transaction has run out of time, set by the script. Everything the page
    /// offered as a way IN is dropped in this state: a QR that is no longer scannable, a channel
    /// button whose deep link no longer resolves and an email form whose request the server would
    /// refuse are three ways to spend the user's attention on an action that cannot succeed.
    /// </summary>
    private const string ExpiredPageStateValue = "expired";

    /// <summary>
    /// State of a page whose transaction failed and which shows that ending as its last screen, set by
    /// the script. The screen is the one of the expired state — everything that belonged to the sign-in
    /// that did not happen leaves, the line of the outcome stays (SPEC-007 UI-111). It is a value of its
    /// own rather than the expired one because the attribute names what happened: a stylesheet of the
    /// integrator may tell the two endings apart.
    /// </summary>
    private const string FailedPageStateValue = "failed";

    /// <summary>
    /// State of a page set by the script when its transaction completed, on a page that does not leave
    /// (<see cref="AuthPageRenderContext.NavigatesAway"/> = <see langword="false"/>): the entry page of a
    /// confirmation and the scanner page of both types, which have nowhere to go and nothing left to offer
    /// (SPEC-025 HOP-186). The stylesheet takes the channel choice, the lead instruction, the block of the hop
    /// and the cancel form away in this state, so the title, the subject and the success line are the whole
    /// screen. The sign-in window leaves for its callback and keeps its state on completion.
    /// </summary>
    private const string CompletedPageStateValue = "completed";

    /// <summary>
    /// State of a page once the answer is no longer given on it: the scanner page of a sign-in on
    /// <c>AwaitingWebConfirmation</c>, where the core asks the question on the window that started the sign-in
    /// (SPEC-025 HOP-186, HOP-179), and a sign-in page that put the line of the answer of a channel's bot on its
    /// status line (SPEC-025 HOP-180, SPEC-007 UI-119). The script sets it; a page rendered while the answer
    /// is awaited in a channel is rendered in it, with that line already on its status line
    /// (<see cref="AuthPageRenderContext.AnsweredChannelType"/>). The transaction is bound
    /// to a subject by then, so another channel could only lead into a refusal: the stylesheet takes the
    /// channel choice, the lead instruction, the block of the started hop and the spare code of a phone
    /// away, and the status line, the countdown and the cancel button stay. Still a waiting page — the
    /// terminal states write their own value over it.
    /// </summary>
    private const string AnswerElsewherePageStateValue = "answer-elsewhere";

    /// <summary>
    /// Name of the attribute a channel link of the scanner page carries when a tap on it sends the client
    /// signal of the chosen channel (SPEC-025 HOP-156). Its value is the channel type the signal names; a
    /// control that leads into no channel — the button revealing the Email Pull form — does not carry it.
    /// </summary>
    private const string ChannelLaunchAttributeName = "data-launch-channel";

    /// <summary>
    /// Name of the root attribute the script sets on the first code of the hop family it receives
    /// (SPEC-025 HOP-101). Only its presence counts: the stylesheet then hides the channel choice and
    /// the lead instruction and shows the instruction block rendered for that moment. It is a display
    /// mode INSIDE the waiting state, not a state of its own — the values of
    /// <see cref="PageStateAttributeName"/> stay what they are.
    /// </summary>
    private const string HopStartedAttributeName = "data-hop-started";

    /// <summary>
    /// Name of the root attribute the script sets while the address form of Email is open — opened by the
    /// link of the Push section or by the button of the Pull panel — and takes off when the form is closed.
    /// Only its presence counts: the stylesheet then takes the rest of the channel choice away — the tab
    /// strip, the unified QR code and the panels of the other channels — and puts the instruction of the
    /// form (<see cref="EmailFormInstructionClass"/>) in place of the lead instruction. A display mode
    /// inside the waiting state, as <see cref="HopStartedAttributeName"/> is.
    /// </summary>
    private const string EmailFormOpenAttributeName = "data-email-form-open";

    /// <summary>
    /// Modifier of the instruction of the open address form of Email, rendered hidden next to the lead
    /// instruction it stands in for while <see cref="EmailFormOpenAttributeName"/> is set. It carries the
    /// modifier of the lead instruction as well, so every state that takes the lead instruction away takes
    /// this one with it.
    /// </summary>
    private const string EmailFormInstructionClass = "veriqa-instruction--email-form";

    /// <summary>
    /// Value of <see cref="DeviceAttributeName"/> for a phone — the only device class the window
    /// establishes today.
    /// </summary>
    private const string MobileDeviceValue = "mobile";

    /// <summary>
    /// Name of the attribute the image of a hop QR code carries when the page replaces that QR code before its
    /// token expires (SPEC-025 HOP-152). Its value is the entry the script asks the hub by: the channel type
    /// of a per-channel QR code, empty for the unified one.
    /// </summary>
    private const string HopQrEntryAttributeName = "data-hop-qr-entry";

    /// <summary>
    /// Entry of the unified hop QR code: the token names no channel, so the entry is empty, as the channel
    /// type of the token is (SPEC-025 HOP-183).
    /// </summary>
    private static readonly string UnifiedHopQrEntry = string.Empty;

    /// <summary>
    /// Class of the wording that only holds while the QR code is on the page. The stylesheet drops it
    /// on the very attributes that drop the QR itself, so the text cannot outlive what it describes.
    /// </summary>
    private const string QrShownTextClass = "veriqa-if-qr";

    /// <summary>
    /// Class of the wording for a page without the QR code — the counterpart of
    /// <see cref="QrShownTextClass"/>, hidden by default and shown exactly where the QR is not.
    /// </summary>
    private const string QrHiddenTextClass = "veriqa-if-no-qr";

    /// <summary>
    /// Class of the wording that only holds while the channel button is on the page — the counterpart
    /// of <see cref="QrShownTextClass"/> for the other way into a channel. The stylesheet drops it on
    /// the very attributes that drop the button.
    /// </summary>
    private const string ButtonShownTextClass = "veriqa-if-button";

    /// <summary>
    /// Class of the wording for a page that shows the QR code without the channel button, hidden by
    /// default and shown exactly where the button is not.
    /// </summary>
    private const string ButtonHiddenTextClass = "veriqa-if-no-button";

    /// <summary>
    /// Class of the channel named inside the instruction of a page with one channel: its brand icon and
    /// its name, kept on one line.
    /// </summary>
    private const string InstructionChannelClass = "veriqa-instruction-channel";

    /// <summary>
    /// Slot of the instruction of a page with one channel — the place the channel is named at. The
    /// same form as the slot of the other texts of the window that take a value.
    /// </summary>
    private const string InstructionChannelSlot = "{0}";

    /// <summary>
    /// Name the log gives the title of the page when its text is refused as markup.
    /// </summary>
    private const string TitleElementName = "title";

    /// <summary>
    /// Name the log gives the lead instruction of the page when its text is refused as markup.
    /// </summary>
    private const string InstructionElementName = "instruction";

    /// <summary>
    /// Name the log gives the hint of a channel when its text is refused as markup; the slot takes the
    /// channel type, since a page carries one hint per channel.
    /// </summary>
    private const string HintElementNameFormat = "hint of the {0} channel";

    /// <summary>
    /// Attribute form of the setting values: the enum name in lower case, the same convention the
    /// preset attribute uses. Building the values here rather than spelling them out keeps the markup,
    /// the page script and the stylesheet on one source — the enum itself.
    /// </summary>
    private static readonly string DesktopOnlyVisibilityValue =
        QrCodeVisibility.DesktopOnly.ToString().ToLowerInvariant();

    /// <summary>
    /// Attribute form of <see cref="ChannelButtonVisibility.MobileOnly"/> — the one value of the channel
    /// button visibility that depends on the device, and so the one the head script asks about.
    /// </summary>
    private static readonly string MobileOnlyVisibilityValue =
        ChannelButtonVisibility.MobileOnly.ToString().ToLowerInvariant();

    /// <summary>
    /// Class of the container holding everything that leads into a channel — the tab strip and the
    /// channel panels.
    /// </summary>
    private const string ChannelsClass = "veriqa-channels";

    /// <summary>
    /// Identifier of the channel container. The page script reads the layout of the channels from it
    /// and, once the hop has started, checks whether the focus was inside it.
    /// </summary>
    private const string ChannelsElementId = "veriqa-channels";

    /// <summary>
    /// Identifier of the block shown once the hop has started. It is focusable by the script only
    /// (<c>tabindex="-1"</c>): the focus the hidden channel choice held is moved onto it.
    /// </summary>
    private const string HopInstructionElementId = "veriqa-hop-instruction";

    /// <summary>
    /// Identifier of the button inside the block shown once the hop has started that brings the channel
    /// choice back. The page script binds it; the server takes no part in the return.
    /// </summary>
    private const string HopChooseAnotherElementId = "veriqa-hop-choose-another";

    /// <summary>
    /// Identifier of the block of the checkbox that remembers the channel (SPEC-025 HOP-187): under
    /// the channel choice, outside its container — the start of the hop takes that container away, and the
    /// checkbox stays. Rendered hidden; the page script shows it on a desktop.
    /// </summary>
    private const string RememberElementId = "veriqa-remember";

    /// <summary>
    /// Identifier of the checkbox itself, the native control its label names by <c>for</c>.
    /// </summary>
    private const string RememberCheckboxElementId = "veriqa-remember-checkbox";

    /// <summary>
    /// Identifier of the label of the checkbox, whose text the script words by the state of the page.
    /// </summary>
    private const string RememberLabelElementId = "veriqa-remember-label";

    /// <summary>
    /// Identifier of the way back from the state of the remembered channel to the whole channel choice: the
    /// button of the block under the lead instruction (<see cref="RememberBackClass"/>). The block is rendered
    /// hidden; the script shows it in that state alone and takes it off the page once a channel has answered,
    /// as it does the way back of the started hop.
    /// </summary>
    private const string RememberBackElementId = "veriqa-remember-back";

    /// <summary>
    /// Class of the block that holds the way back from the state of the remembered channel: the "OR" divider
    /// and the button under it.
    /// </summary>
    private const string RememberBackClass = "veriqa-remember-back";

    /// <summary>
    /// Name of the root attribute the script sets on the scanner page once a tap on a channel link sent the
    /// person into that channel (SPEC-025 HOP-193), and the server renders on a page whose channel has already
    /// answered (<see cref="ChosenChannelAtRender"/>). Only its presence counts: the stylesheet then takes the
    /// title of the page, the lead instruction and the channel choice away and shows the way back to the
    /// choice. A display mode inside the waiting state, as <see cref="HopStartedAttributeName"/> is — the
    /// values of <see cref="PageStateAttributeName"/> stay what they are.
    /// </summary>
    private const string ChannelChosenAttributeName = "data-channel-chosen";

    /// <summary>
    /// Name of the attribute naming the channel type on both halves of the chosen channel of the scanner
    /// page: on each link that leads into a channel, and on the heading of that channel the script shows on
    /// a tap of it — or the server renders shown on a page whose channel has already answered
    /// (<see cref="ChosenChannelAtRender"/>).
    /// </summary>
    private const string ContinueChannelAttributeName = "data-continue-channel";

    /// <summary>
    /// Identifier of the block of the scanner page holding one heading per channel the page leads into
    /// (<see cref="HopLaunchPageHeading.ContinueText"/>), each hidden until the script shows the heading of the
    /// channel tapped — but the one of a channel that has already answered, which the server renders shown
    /// (<see cref="ChosenChannelAtRender"/>).
    /// </summary>
    private const string ChosenHeadingsElementId = "veriqa-chosen-headings";

    /// <summary>
    /// Identifier of the button of the scanner page that brings the channel choice back once a channel was
    /// chosen. The page script binds it; the server takes no part in the return.
    /// </summary>
    private const string ChosenBackElementId = "veriqa-chosen-back";

    /// <summary>
    /// Class of the same button — what the stylesheet hides it by until a channel is chosen, and for good once
    /// the answer is given elsewhere or the page has ended.
    /// </summary>
    private const string ChosenBackClass = "veriqa-chosen-back";

    /// <summary>
    /// Class of the status line of a page.
    /// </summary>
    private const string StatusClass = "veriqa-status";

    /// <summary>
    /// Classes of the status line showing a line of progress — the look the script gives the line of the
    /// answer of a channel's bot, and the renderer gives it on a page rendered with that line. The script
    /// writes the same classes as a literal (<c>applyChannelAnswer</c> in <c>veriqa-auth-page.js</c>):
    /// rename both together.
    /// </summary>
    private const string AnsweredStatusClass = StatusClass + " confirmed";

    /// <summary>
    /// Name of the root attribute a head script sets on a page that shows a spare start code. While it
    /// stands, the stylesheet keeps every spare start code out of sight until the page script opens it
    /// (<see cref="SpareCodeOpenClass"/>). A browser that runs no script leaves it unset, and the spare
    /// start codes are on screen from the load.
    /// </summary>
    private const string SpareCodeHeldAttributeName = "data-spare-code-held";

    /// <summary>
    /// Name of the root attribute a head script sets on a desktop page whose cookie of the checkbox names a
    /// channel of the page. While it stands, the stylesheet keeps the lead instruction and the channels out of
    /// sight, so the page is never painted showing every channel before the page script turns it to the state
    /// of the remembered channel; the page script takes the attribute off once that state is applied, and the
    /// load of the page takes it off in any case. A browser that runs no script leaves it unset.
    /// </summary>
    private const string RememberedChannelHeldAttributeName = "data-remembered-channel-held";

    /// <summary>
    /// Class of the element that wraps the spare start code of one channel — in the panel of the channel
    /// and in the block shown once the hop has started.
    /// </summary>
    private const string SpareCodeClass = "veriqa-spare-code";

    /// <summary>
    /// Modifier of <see cref="SpareCodeClass"/> the page script adds when the spare start code is to be
    /// shown.
    /// </summary>
    private const string SpareCodeOpenClass = "veriqa-spare-code--open";

    /// <summary>
    /// Attribute of <see cref="SpareCodeClass"/> naming the channel type the spare start code belongs to.
    /// </summary>
    private const string SpareCodeChannelAttributeName = "data-spare-code-channel";

    /// <summary>
    /// Class of the channel button of a panel — the link into the channel, and the button of the Email Pull
    /// form that reveals it in the buttons layout.
    /// </summary>
    private const string ChannelButtonClass = "veriqa-btn-channel";

    /// <summary>
    /// Selector of the channel button of a panel, as the page script finds it next to the spare start
    /// code of the same panel.
    /// </summary>
    private const string ChannelButtonSelector = "a." + ChannelButtonClass;

    /// <summary>
    /// Attributes of a channel link that opens the channel in a new tab: the page that opened it stays
    /// under it and follows the transaction.
    /// </summary>
    private const string NewTabLinkAttributes = " target=\"_blank\" rel=\"noopener noreferrer\"";

    /// <summary>
    /// Attributes of a channel link that opens the channel in the tab of the page itself. The address of
    /// the scanner page carries the hop token, so it is still kept out of the Referer.
    /// </summary>
    private const string SameTabLinkAttributes = " rel=\"noreferrer\"";

    /// <summary>
    /// Prefix of the identifier of a channel panel, followed by the channel type
    /// (<see cref="AuthPageScriptConfig.PanelElementIdPrefix"/>).
    /// </summary>
    private const string PanelElementIdPrefix = "veriqa-panel-";

    /// <summary>
    /// Modifier of the panel of Email. While <see cref="EmailFormOpenAttributeName"/> is set, the stylesheet
    /// hides every panel without it, so the panel of Email stands alone with its address form.
    /// </summary>
    private const string EmailPanelClass = "veriqa-panel--email";

    /// <summary>
    /// Modifier of the label of the StatusBadge primitive (<see cref="HopReopenedWarning.BadgeClass"/>) that the
    /// page script puts on the channel the user went through last time: neutral colors instead of the warning
    /// ones, a pill inside a tab and on the corner of a button.
    /// </summary>
    private const string LastUsedBadgeClass = "veriqa-badge--last-used";

    /// <summary>
    /// The channel statuses that tell the channel of the event works, the moment the page remembers it outside
    /// the scanner page: the answer of the bot, and for Email, which has no bot, the opened link and the mail
    /// received.
    /// </summary>
    private static readonly string[] WorkingChannelStatusCodes =
    [
        ChannelAnswerStatuses.ChannelAnswered,
        EmailChannelStatusKeys.PullOpened,
        EmailChannelStatusKeys.PushMailReceived
    ];

    /// <summary>
    /// Identifier of the block under the status line that offers the spare start code on a phone: the
    /// link <see cref="AuthPageStrings.ProblemSigningIn"/> (<see cref="AuthPageStrings.ProblemConfirming"/> on a
    /// confirmation) and, under it, a copy of the spare start code of
    /// each channel that has one — the spare form, or on the scanner page the block that copies the code and
    /// opens the channel. The block is hidden until the person comes back from a channel the page sent them
    /// to, and the script opens the copy of that channel alone.
    /// </summary>
    private const string SpareReturnElementId = "veriqa-spare-return";

    /// <summary>
    /// Identifier of the link of <see cref="SpareReturnElementId"/> that opens the copy of the spare start code.
    /// </summary>
    private const string SpareReturnLinkElementId = "veriqa-spare-return-link";

    /// <summary>
    /// Prefix of the identifiers of the hints of the channels in the block of the started hop (message kind
    /// <c>hop-channel-hint</c>), followed by the position of the channel on the page. Each is hidden until
    /// the script notices the phone on its way into that channel.
    /// </summary>
    private const string HopChannelHintElementIdPrefix = "veriqa-hop-channel-hint-";

    /// <summary>
    /// Name of the address field of the email form, as the start of Email Pull reads it. Duplicates the
    /// field name of the adapter for the reason <see cref="EmailEndpointPaths"/> duplicates its path.
    /// </summary>
    private const string EmailFieldName = "email";

    /// <summary>
    /// Slot of <see cref="AuthPageStrings.EmailCodeSent"/> the masked address goes into.
    /// </summary>
    private const string EmailCodeAddressSlot = "{address}";

    /// <summary>
    /// Identifier of the live region the page script puts the warning of a reopened hop link into
    /// (SPEC-041 L93). The region is on the page, empty, from the load, so its filling is announced.
    /// </summary>
    private const string HopReopenedElementId = "veriqa-hop-reopened";

    /// <summary>
    /// Identifier of the form behind the cancel button. The page script hides the form by it
    /// when the page leaves for its callback with a failed sign-in: that ending writes no page state for
    /// the stylesheet to key on.
    /// </summary>
    private const string CancelFormElementId = "veriqa-cancel";

    /// <summary>
    /// Class of the same form — what the stylesheet spaces it by and hides it by on an expired or failed page.
    /// </summary>
    private const string CancelFormClass = "veriqa-cancel";

    /// <summary>
    /// Prefix of the link of an Email channel in its Push mode — the <c>mailto:</c> of the sign-in mail,
    /// built from the scheme name of <see cref="Uri"/>.
    /// </summary>
    private static readonly string EmailPushLinkPrefix = Uri.UriSchemeMailto + ":";

    /// <summary>
    /// Modifier of the channel container that lays the channels out as a list of buttons, without the
    /// tab strip (SPEC-025 HOP-145). The server renders it where the layout does not depend on the
    /// device; it is a feature of its own, separate from "the QR is hidden": the layout does not touch
    /// the QR, the QR rules do.
    /// </summary>
    private const string ButtonsLayoutClass = "veriqa-channels--buttons";

    /// <summary>
    /// Modifier of the channel container on a page of the unified QR code that keeps the per-channel QR
    /// codes (<see cref="AuthPageRenderContext.UnifiedQrWithChannelQrCodes"/>), while its "Any channel" tab
    /// is selected: the unified QR code is on screen and every channel panel is laid out as a button
    /// without its own QR code, under the tab strip. It is a state of its own, not
    /// <see cref="ButtonsLayoutClass"/>: that modifier takes the tab strip away and makes the script leave
    /// the tabs unbound, while this state keeps both. The server renders it, so the page opens on the "Any
    /// channel" tab without a script; the script takes it off for a channel tab and puts it back for "Any
    /// channel".
    /// </summary>
    private const string AnyChannelStateClass = "veriqa-channels--any-channel";

    /// <summary>
    /// Identifier of the "Any channel" tab — the one tab of the strip that names no channel, which the
    /// script tells apart from the channel tabs by it.
    /// </summary>
    private const string AnyChannelTabElementId = "veriqa-tab-any-channel";

    /// <summary>
    /// Identifier of the block of the unified QR code on a page with the "Any channel" tab: the content
    /// that tab alone controls, which its <c>aria-controls</c> names.
    /// </summary>
    private const string AnyChannelQrElementId = "veriqa-any-channel-qr";

    /// <summary>
    /// Modifier of the block of the unified QR code on a page with the "Any channel" tab: the stylesheet
    /// shows that block only while the channel container is in <see cref="AnyChannelStateClass"/>.
    /// </summary>
    private const string AnyChannelQrClass = "veriqa-qr--any-channel";

    /// <summary>
    /// Modifier of the channel container on a page with the "Any channel" tab whose tab shows the unified QR
    /// code alone (<see cref="AuthPageRenderContext.AnyChannelTabShowsChannelButtons"/> is false): the
    /// stylesheet takes the channel panels away in <see cref="AnyChannelStateClass"/>, and gives them back
    /// where no QR code is shown — a phone under <see cref="QrCodeVisibility.DesktopOnly"/>, whose buttons
    /// are its only way in.
    /// </summary>
    private const string AnyChannelQrOnlyClass = "veriqa-channels--any-channel-qr-only";

    /// <summary>
    /// Class of the element that holds the variants of one wording of the lead instruction on a page with
    /// tabs, one variant per tab: the stylesheet stacks them in one cell, so the element is as tall as the
    /// longest of them whichever tab is selected, and the block under it never moves.
    /// </summary>
    private const string TabWordingsClass = "veriqa-tab-wordings";

    /// <summary>
    /// Class of one variant of a wording of the lead instruction — the one of the tab its
    /// <see cref="TabWordingAttributeName"/> names.
    /// </summary>
    private const string TabWordingClass = "veriqa-tab-wording";

    /// <summary>
    /// Class of a variant of a tab that is not selected: the stylesheet hides it by <c>visibility</c>, which
    /// keeps its place in the cell and takes it out of the accessibility tree. The server renders it on every
    /// variant but the one of the tab selected on the load; the script moves it on a change of tab.
    /// </summary>
    private const string TabWordingHiddenClass = "veriqa-tab-wording--hidden";

    /// <summary>
    /// Attribute of a variant of a wording naming the identifier of the tab it words.
    /// </summary>
    private const string TabWordingAttributeName = "data-veriqa-tab";

    /// <summary>
    /// Modifier of the card of a page with the unified QR code beside the channel buttons (SPEC-025 HOP-187):
    /// on a window wide enough for it the card takes two columns — the QR code on the left, the buttons on
    /// the right — and keeps its width whichever tab is selected. On a narrower window, and on a phone
    /// under <see cref="QrCodeVisibility.DesktopOnly"/>, it stays the one-column card. See
    /// <see cref="ShowsSplitLayout"/> for where the server renders it.
    /// </summary>
    private const string SplitCardClass = "veriqa-container--split";

    /// <summary>
    /// Class of the element around the channel panels of the split card — the right column beside the
    /// unified QR code. An element of its own inside the channel container, as the row of the grid layout
    /// is, so the rules that take the whole channel choice away keep acting on the container alone.
    /// </summary>
    private const string ChannelStackClass = "veriqa-channel-stack";

    /// <summary>
    /// Class of the element of the split card that holds a channel QR code and what stands beside it — the
    /// panel of a channel, or the Push section of Email: under a channel tab it is laid out as the two columns,
    /// the QR code on the left and <see cref="PanelSideClass"/> on the right.
    /// </summary>
    private const string QrPairClass = "veriqa-qr-pair";

    /// <summary>
    /// Class of the element of <see cref="QrPairClass"/> that holds everything after the QR code — the right
    /// column under a channel tab.
    /// </summary>
    private const string PanelSideClass = "veriqa-panel-side";

    /// <summary>
    /// Class of a part of the split card that has no QR code to stand beside — the panel of a channel whose
    /// QR code is not shown (Email Pull, a channel without a picture) and the email form of the Push section:
    /// under a channel tab it is one column in the middle of the card, not a column next to an empty one.
    /// </summary>
    private const string SplitSingleClass = "veriqa-split-single";

    /// <summary>
    /// Modifier of the card of a window that shows every channel at once
    /// (<see cref="ChannelLayout.Grid"/>): the card gives up its one-column width and is sized by the
    /// row of channel panels instead. The stylesheet computes that width from
    /// <see cref="ChannelCountProperty"/>, which the same element carries.
    /// </summary>
    private const string GridCardClass = "veriqa-container--grid";

    /// <summary>
    /// Custom property the card of the grid layout carries: the number of channel panels in its row.
    /// It is a fact of the request, so it travels in the markup; the geometry it is multiplied by
    /// stays in the stylesheet.
    /// </summary>
    private const string ChannelCountProperty = "--veriqa-channel-count";

    /// <summary>
    /// Class of the row the channel panels of the grid layout stand in. The row is an element of its
    /// own INSIDE the channel container, so the rules that take the whole channel choice away — the
    /// start of the hop, a completed page — keep acting on the container and never meet a rule of the
    /// row.
    /// </summary>
    private const string ChannelGridClass = "veriqa-channel-grid";

    /// <summary>
    /// Class of the visible name of a channel over its panel in the grid layout — the element the
    /// panel, a group there, is labelled by.
    /// </summary>
    private const string ChannelNameClass = "veriqa-channel-name";

    /// <summary>
    /// The condition of the buttons layout as a selector matching the channel container: the modifier
    /// the server renders, or the pair of root attributes that hides the QR on a phone (SPEC-025
    /// HOP-146, SPEC-007 UI-020). The stylesheet spells the same condition; the page script receives
    /// this one and asks the container whether it matches, so the device is established once, by the
    /// head script, and never a second time.
    /// </summary>
    private static readonly string ButtonsLayoutSelector =
        $".{ButtonsLayoutClass}, "
        + $"[{QrVisibilityAttributeName}=\"{DesktopOnlyVisibilityValue}\"][{DeviceAttributeName}=\"{MobileDeviceValue}\"] .{ChannelsClass}";

    /// <summary>
    /// The condition "no hop QR code is visible on the page" as a selector matching the root element: the hop
    /// has started, the answer is given elsewhere (<see cref="AnswerElsewherePageStateValue"/>), the address
    /// form of Email is open (<see cref="EmailFormOpenAttributeName"/>), or the QR
    /// code is hidden on a phone when only the desktop shows it (SPEC-025 HOP-152 over
    /// the signs of HOP-146 and SPEC-007 UI-020). The page script asks the root at the moment of a
    /// replacement and replaces nothing while it matches. A page rendered with
    /// <see cref="QrCodeVisibility.Never"/> needs no such sign: it carries no QR code and so hands the script
    /// nothing to replace.
    /// </summary>
    private static readonly string HopQrNotVisibleSelector =
        $"[{HopStartedAttributeName}], "
        + $"[{PageStateAttributeName}=\"{AnswerElsewherePageStateValue}\"], "
        + $"[{EmailFormOpenAttributeName}], "
        + $"[{QrVisibilityAttributeName}=\"{DesktopOnlyVisibilityValue}\"][{DeviceAttributeName}=\"{MobileDeviceValue}\"]";

    /// <summary>
    /// Data-driven localizer over the host locale files — the single source of translations for the
    /// page (TASK-062). The same infrastructure the channel contour uses (SPEC-017 §7.2), so window
    /// texts and channel messages share one translation source.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// Logger of the page — tells the operator about a text or a block the page refused to emit as
    /// markup.
    /// </summary>
    private readonly ILogger<DefaultAuthPageRenderer> _logger;

    /// <summary>
    /// Creates the authentication page renderer.
    /// </summary>
    /// <param name="promptLocalizer">Locale-file localizer — the source of all page translations.</param>
    /// <param name="logger">Logger of the page.</param>
    public DefaultAuthPageRenderer(
        IConfirmationPromptLocalizer promptLocalizer,
        ILogger<DefaultAuthPageRenderer> logger)
    {
        _promptLocalizer = promptLocalizer;
        _logger = logger;
    }

    /// <summary>
    /// Resolves one of this renderer's OWN Natural Keys into the content language, through the text
    /// overrides of the page: a key an owning level overrides takes the override
    /// (<see cref="CorePageTexts.Localize"/>), any other key the shared auth-contour rule — every language,
    /// English included, from the host locale files, with the key's own base text as the fallback.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="naturalKey">Built-in Natural Key (English base text).</param>
    /// <param name="language">Content language code (the result of ResolveContentLanguage).</param>
    /// <returns>Localized text.</returns>
    private string LocalizeOwn(CorePageTexts texts, string naturalKey, string language)
    {
        return texts.Localize(_promptLocalizer, naturalKey, language);
    }

    /// <summary>
    /// Resolves a text a level STATED through a dedicated setting (the title, the instruction, the brand
    /// name, the markup of the design) — a Natural Key like the renderer's own, resolved through the host
    /// locale files by the shared auth-contour rule (<see cref="AuthPageLocalization.Localize"/>): every
    /// language, English included, with the key's own base text as the fallback. The text overrides of the
    /// page do not reach it — a dedicated setting outranks them; the renderer's own keys go through
    /// <see cref="LocalizeOwn"/>.
    /// </summary>
    /// <param name="statedKey">Value the setting states.</param>
    /// <param name="language">Content language code (the result of ResolveContentLanguage).</param>
    /// <returns>Localized text.</returns>
    private string Localize(string statedKey, string language)
    {
        return AuthPageLocalization.Localize(_promptLocalizer, statedKey, language);
    }

    /// <summary>
    /// Returns the text the CALLER stated, falling back to this renderer's own Natural Key when it
    /// stated none.
    /// </summary>
    /// <remarks>
    /// A stated text arrives ready and localized and is used as it is — it comes from the mechanism
    /// that words terminal outcomes, and passing it through the key localizer would ask that
    /// localizer to translate an already translated sentence. The fallback is what keeps the sign-in
    /// path unchanged: it states nothing, so it gets the wording it always had — or the override of it
    /// an owning level states.
    /// </remarks>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="stated">Text stated by the caller; null or empty — none was stated.</param>
    /// <param name="ownNaturalKey">This renderer's own Natural Key for the same line.</param>
    /// <param name="language">Content language code (the result of ResolveContentLanguage).</param>
    /// <returns>Text to render.</returns>
    private string Stated(CorePageTexts texts, string? stated, string ownNaturalKey, string language)
    {
        return string.IsNullOrEmpty(stated) ? LocalizeOwn(texts, ownNaturalKey, language) : stated;
    }

    /// <inheritdoc />
    public string RenderAuthPage(AuthPageRenderContext context)
    {
        // The method builds the complete authentication HTML page

        // The effective design arrives resolved: the levels were combined by the canonical resolver
        // before the page was handed over (SPEC-012 CFG-231).
        var design = context.Design;

        // WCAG 3.1.1: html lang must match the language of the rendered text. The requested language
        // is honoured only when the locale files actually resolve the window's keys; otherwise the
        // page degrades to the English base text as a whole. Resolved ONCE and used both for
        // html lang and for every Localize call below — localizing with context.Language instead
        // would produce a mixed page (some strings translated, the rest English base).
        var lang = AuthPageLocalization.ResolveContentLanguage(
            _promptLocalizer,
            AuthPageStrings.Title,
            context.Language);

        // Preset selection (SPEC-012 §4.2). CFG-023: with a custom design (§4.3) the preset
        // is ignored — we render the base Default, and the styling is set by the custom CSS.
        var preset = AuthPageBranding.ResolvePreset(design);

        // Color scheme: an explicit Theme (Light/Dark) takes priority; Auto follows the OS system
        // setting (prefers-color-scheme), but the Dark preset (SPEC-012 §4.2) also yields dark.
        // The rule is the contour's, shared with the service pages.
        var htmlTheme = CorePageBrandingRules.EffectiveTheme(design.Theme, preset);

        var hasMultipleChannels = context.Channels.Count > 1;

        // The text overrides of the page arrive resolved, like the design: the render only applies them.
        var texts = context.Texts;

        // Title/instruction: the level that owns the text supplies the KEY, the renderer supplies
        // its own when no level states one — and localization is the tail of both, not a branch of
        // the default. A configured value is a Natural Key like any other: a locale file that carries
        // an entry for it translates it, and a value absent from every file resolves to its own base
        // text, so a deployment that states plain wording keeps that wording. Only the renderer's own
        // key goes through the text overrides: the dedicated setting outranks them.
        // The localized value may carry inline markup: the heading takes it as markup, the title of the
        // document — which reads no tag — takes the same value without its tags. The renderer's own title
        // names the act of the transaction, a sign-in or a confirmation; a stated title holds for both.
        var ownTitleKey = IsSignIn(context) ? AuthPageStrings.Title : AuthPageStrings.ConfirmationTitle;
        var titleMarkup = BuildInlineMarkup(
            context.Title is { } statedTitle
                ? Localize(statedTitle, lang)
                : LocalizeOwn(texts, ownTitleKey, lang),
            TitleElementName,
            lang,
            out var titleText);

        // A stated title made of tags alone leaves no text for the title of the document, which then
        // takes the renderer's own; the heading keeps the stated markup.
        if (string.IsNullOrWhiteSpace(titleText))
        {
            _ = BuildInlineMarkup(LocalizeOwn(texts, ownTitleKey, lang), TitleElementName, lang, out titleText);
        }

        // The instruction in its three wordings by what the page shows; each takes the wording a
        // deployment stated through ui_config, or the renderer's own.
        var instructionMarkup = BuildInstructionMarkup(context, lang);

        // QR visibility and channel button visibility: resolved on the server, applied by the page.
        // Each value travels into the document as an attribute and never as a rendering branch — which
        // device is in front of the window is knowable in the browser only (SPEC-007 UI-020).
        var visibilityAttributes =
            $"{QrVisibilityAttributeName}=\"{EffectiveQrVisibility(context).ToString().ToLowerInvariant()}\" "
            + $"{ChannelButtonVisibilityAttributeName}=\"{EffectiveChannelButtonVisibility(context).ToString().ToLowerInvariant()}\"";

        // Branding of the window, built once: the head takes the stylesheet and the colors from it.
        var branding = AuthPageBranding.ToCoreBranding(design, preset, htmlTheme, context.ChannelButtonColors);

        var sb = new StringBuilder(8192);

        // HTML head
        AppendHtmlHead(
            sb,
            lang,
            titleText,
            htmlTheme,
            preset,
            branding,
            visibilityAttributes,
            BuildNonceAttribute(context),
            withStartCodeRules: ShowsAnyStartCodeBlock(context),
            holdsSpareStartCode: ShowsAnySpareStartCodeBlock(context),
            rememberedChannelTypes: OffersRememberCheckbox(context)
                ? [.. context.Channels.Select(channel => channel.ChannelType)]
                : null,
            withChannelHeadingRules: MarksChosenChannel(context),
            withCloseRules: context.OffersOutcomeClose,
            pageState: AnswerLineAtRender(context) is null ? WaitingPageStateValue : AnswerElsewherePageStateValue,
            channelChosen: ChosenChannelAtRender(context) is not null);

        // Page body
        AppendBodyOpen(sb);
        AppendContainer(sb, titleMarkup, instructionMarkup, context, lang, hasMultipleChannels, preset, design, htmlTheme);

        // The attribution line stands under the card, and its guard right after it: the guard takes the
        // element before it as the line to keep, and it has to be watching before the integrator's
        // script, which AppendScript links last (SPEC-007 UI-055).
        sb.AppendLine(CorePageAttribution.BuildLine(lang));
        sb.AppendLine(CorePageAttribution.BuildGuard(context.CspNonce));
        AppendScript(sb, context, lang, design);
        AppendBodyClose(sb);

        return sb.ToString();
    }

    /// <summary>
    /// Builds the HTML head: the inline styles of the window and the integrator's stylesheet link. The
    /// title of the document arrives ready — the text of the page title without tags, or its encoded
    /// form (<see cref="BuildInlineMarkup"/>) — which is why it is not encoded again here. The root carries
    /// the state the page is rendered in (<paramref name="pageState"/>): waiting, or the answer given
    /// elsewhere on a page rendered with the line of the answer of a channel's bot; and, on the scanner page
    /// whose answered channel has a heading, the mode of the chosen channel (<paramref name="channelChosen"/>).
    /// </summary>
    private static void AppendHtmlHead(
        StringBuilder sb,
        string htmlLang,
        string titleText,
        string htmlTheme,
        DesignPreset preset,
        CorePageBranding branding,
        string visibilityAttributes,
        string nonceAttr,
        bool withStartCodeRules,
        bool holdsSpareStartCode,
        IReadOnlyList<string>? rememberedChannelTypes,
        bool withChannelHeadingRules,
        bool withCloseRules,
        string pageState,
        bool channelChosen)
    {
        // The method builds the DOCTYPE, head, and the styling head of the window.
        // Static rules live in the contour stylesheets (token layer + window rules); C# generates
        // only what depends on configuration: the QR box size and the branded primary.
        // The preset and the branding come already resolved from the caller — both the data-preset
        // attribute and the branding derive from that one resolution.
        var presetName = preset.ToString().ToLowerInvariant();
        var generatedTokens = $$"""

            :root { --veriqa-qr-size: {{AuthPageConstants.QrCodeImgAttributeSizePx}}px; }
            """;

        // The rules of the block of the start by a code go out only with a page that shows the block,
        // so the document of a page without such a channel is what it was.
        var windowRules = AuthPageStyles.Css + PageCountdown.Css;
        if (withStartCodeRules)
        {
            windowRules += ChannelStartCodeBlock.Css;
        }

        // The heading of a chosen channel is the heading of the launch pages, and its rules go out only with
        // a page that carries it — the scanner page with a choice of channels.
        if (withChannelHeadingRules)
        {
            windowRules += HopLaunchPageHeading.Css;
        }

        // The rules of the action under the outcome go out only with a page that offers it.
        if (withCloseRules)
        {
            windowRules += CorePageCloseAction.Css;
        }

        // The script sets the mode of the chosen channel with an empty value; the server renders it the same.
        var chosenAttribute = channelChosen ? $" {ChannelChosenAttributeName}=\"\"" : string.Empty;

        sb.Append($$"""
            <!DOCTYPE html>
            <html lang="{{htmlLang}}" data-theme="{{htmlTheme}}" data-preset="{{presetName}}" {{visibilityAttributes}} {{PageStateAttributeName}}="{{pageState}}"{{chosenAttribute}}>
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{titleText}}</title>
                {{CorePageHead.BuildPageHead(branding, windowRules, generatedTokens)}}
            """);

        AppendDeviceDetectionScript(sb, nonceAttr, alwaysDetects: holdsSpareStartCode || rememberedChannelTypes is not null);

        // The page that offers the checkbox reads its cookie right after the device is known, ahead of the
        // markup, so the state of the remembered channel is the first one painted.
        if (rememberedChannelTypes is not null)
        {
            AppendRememberedChannelHoldScript(sb, nonceAttr, rememberedChannelTypes);
        }

        // The spare start codes are kept out of sight by a mark the head sets, ahead of their markup,
        // so a code that is about to be hidden never flashes; the page script opens them. Without
        // scripts the mark is never set and the codes are on screen from the load.
        if (holdsSpareStartCode)
        {
            sb.Append(
                $"\n    <script{nonceAttr}>document.documentElement.setAttribute(\"{SpareCodeHeldAttributeName}\", \"\");</script>");
        }

        sb.AppendLine("\n</head>");
    }

    /// <summary>
    /// Builds the device-detection script of the window — the client half of the two visibility
    /// settings, of the QR code and of the channel buttons (SPEC-007 UI-020). It lives in the head,
    /// ahead of the markup of both, so the verdict precedes the first paint and neither a QR that is
    /// about to be hidden nor a button that is about to be shown ever flashes.
    /// <para>
    /// The verdict needs TWO signals that CSS cannot combine on its own: a media query (a coarse
    /// pointer / no hover) and the User-Agent, which no stylesheet can see. Both are therefore read
    /// here and ANDed — a rule keyed on the media query alone would hide the QR on a touch-screen
    /// desktop, and a page whose only way in is a deep-link button the user cannot reach is worse than
    /// a QR shown where it is useless. The script is emitted for every value of the settings and
    /// returns immediately unless one of them asks for a device check — the QR code on a desktop only,
    /// or the channel buttons on a phone only — or the page holds a spare start code, whose rules of
    /// showing differ on a phone, or the checkbox that remembers the channel, which a phone never shows. The second condition is what keeps the buttons on a phone whose page
    /// shows the QR code everywhere: without it the marker would never be set there.
    /// </para>
    /// <para>
    /// The marker is the one fact both settings read, and each reads it through rules of its own: every
    /// rule of the QR code and of the layout names the QR visibility next to the marker, so a marker
    /// set for the buttons alone changes neither. A browser that runs no script at all leaves the
    /// marker unset: the QR stays shown, and buttons asked for on a phone only stay hidden.
    /// </para>
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="nonceAttr">Ready CSP nonce attribute of an inline script (may be empty).</param>
    /// <param name="alwaysDetects">
    /// Whether the page establishes the device whatever the two settings state — a page holding a spare
    /// start code or the checkbox that remembers the channel, whose script reads the marker.
    /// </param>
    private static void AppendDeviceDetectionScript(StringBuilder sb, string nonceAttr, bool alwaysDetects)
    {
        var settingsGuard = alwaysDetects
            ? string.Empty
            : $$"""
                        if (root.getAttribute("{{QrVisibilityAttributeName}}") !== "{{DesktopOnlyVisibilityValue}}"
                            && root.getAttribute("{{ChannelButtonVisibilityAttributeName}}") !== "{{MobileOnlyVisibilityValue}}") return;
            """;

        sb.Append($$"""

                <script{{nonceAttr}}>
                    (function() {
                        "use strict";
                        var root = document.documentElement;
            {{settingsGuard}}

                        // First signal — the media query, evaluated here instead of in a stylesheet
                        // precisely so that it decides nothing by itself.
                        var query = window.matchMedia;
                        if (!query) return;
                        if (!query("(pointer: coarse)").matches && !query("(hover: none)").matches) return;

                        // Second signal — the User-Agent. A tablet is deliberately NOT a phone here:
                        // userAgentData.mobile is false on tablets, and the fallback pattern matches
                        // phone User-Agents only ("Mobile"/iPhone/iPod). The QR is scanned by another
                        // device's camera, so an uncertain device keeps it.
                        var uaData = navigator.userAgentData;
                        var isPhone = uaData && typeof uaData.mobile === "boolean"
                            ? uaData.mobile
                            : /Mobi|iPhone|iPod/i.test(navigator.userAgent || "");
                        if (isPhone) root.setAttribute("{{DeviceAttributeName}}", "{{MobileDeviceValue}}");
                    })();
                </script>
            """);
    }

    /// <summary>
    /// Builds the head script that holds the channels of a page opening in the state of the remembered channel
    /// (<see cref="RememberedChannelHeldAttributeName"/>). The page script applies that state only once the
    /// document has been read up to it, and the browser may paint the page before then — every channel on
    /// screen, then the one remembered. The script reads the cookie of the checkbox the way the page script
    /// does: only a channel of the page counts, and a phone, which never shows the checkbox, is left as it is.
    /// The load of the page releases the hold whatever happens, so a page script that never ran does not leave
    /// the channels out of sight.
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="nonceAttr">Ready CSP nonce attribute of an inline script (may be empty).</param>
    /// <param name="channelTypes">Channel types of the page.</param>
    private static void AppendRememberedChannelHoldScript(
        StringBuilder sb,
        string nonceAttr,
        IReadOnlyList<string> channelTypes)
    {
        sb.Append($$"""

                <script{{nonceAttr}}>
                    (function() {
                        "use strict";
                        var root = document.documentElement;
                        if (root.getAttribute("{{DeviceAttributeName}}") === "{{MobileDeviceValue}}") return;
                        var channelTypes = {{JsonSerializer.Serialize(channelTypes)}};
                        var prefix = "{{RememberedChannelCookies.RememberedChannelName}}=";
                        try {
                            var pairs = document.cookie.split("; ");
                            for (var i = 0; i < pairs.length; i++) {
                                if (pairs[i].indexOf(prefix) !== 0) continue;
                                if (channelTypes.indexOf(decodeURIComponent(pairs[i].substring(prefix.length))) === -1) return;
                                root.setAttribute("{{RememberedChannelHeldAttributeName}}", "");
                                window.addEventListener("load", function() {
                                    root.removeAttribute("{{RememberedChannelHeldAttributeName}}");
                                });
                                return;
                            }
                        } catch (e) {
                            // No cookie can be read: the page opens with every channel, as the page script will.
                        }
                    })();
                </script>
            """);
    }

    /// <summary>
    /// Builds the opening body tag.
    /// </summary>
    private static void AppendBodyOpen(StringBuilder sb)
    {
        sb.AppendLine("<body>");
    }

    /// <summary>
    /// Appends the brand row of the window — one path for every preset that shows the row
    /// (SPEC-007 UI-100). The shipped default is a PAIR: when no level states either the logo or the
    /// brand name, the row carries the product's diamond and wordmark; a single value stated by the
    /// integrator replaces that default as a whole, so an integrator's logo never stands beside the
    /// product's name. The two keys themselves still resolve on their own, so all four combinations of
    /// the integrator's values are legitimate: with a name the row is a <c>veriqa-brand-row</c> holding
    /// what there is, and without one the logo stands alone. The name is plain text and is encoded
    /// here; the logo markup is built by <see cref="LogoMarkup"/>.
    /// <para>
    /// A logo counts as stated when a level states it for either theme, even if its URL is not
    /// displayable: the value is the integrator's, and replacing a broken logo with the product's mark
    /// would put the product's name on the integrator's page.
    /// </para>
    /// </summary>
    /// <param name="sb">Buffer of the page.</param>
    /// <param name="design">Effective design settings.</param>
    /// <param name="htmlTheme">Theme of the page, as it was put into the data-theme attribute.</param>
    /// <param name="lang">Content language of the page.</param>
    private void AppendBrandRow(
        StringBuilder sb,
        AuthPageDesignOptions design,
        string htmlTheme,
        string lang)
    {
        var statesLogo = design.GetLogoUrl(CorePageThemes.Light) is not null
            || design.GetLogoUrl(CorePageThemes.Dark) is not null;

        if (!statesLogo && string.IsNullOrWhiteSpace(design.BrandName))
        {
            sb.AppendLine(
                $"        <div class=\"veriqa-brand-row\">{CorePageBrandMark.VeriqaDiamondSvg}<span>{HtmlEncode(CorePageBrandMark.VeriqaBrandName)}</span></div>");
            return;
        }

        var logoMarkup = LogoMarkup(design, htmlTheme);

        if (string.IsNullOrWhiteSpace(design.BrandName))
        {
            if (logoMarkup is not null)
            {
                sb.AppendLine($"        {logoMarkup}");
            }

            return;
        }

        var brandName = HtmlEncode(Localize(design.BrandName, lang));

        sb.AppendLine(
            $"        <div class=\"veriqa-brand-row\">{logoMarkup}<span>{brandName}</span></div>");
    }

    /// <summary>
    /// Builds the markup of the client's logo in the brand row, or null when the page displays no logo.
    /// The logo is cut by theme, and how many values the markup carries depends on what the
    /// server knows about the theme:
    /// <list type="bullet">
    /// <item><description>
    /// the theme is known (the page mode is Light or Dark) — one <c>&lt;img&gt;</c> with the value
    /// resolved for THAT theme, the chain of the key having already fallen back to the level's common
    /// value when the theme states none of its own;
    /// </description></item>
    /// <item><description>
    /// the theme is the browser's to pick (mode auto) and the two values differ — a
    /// <c>&lt;picture&gt;</c> handing both to the browser, the canonical HTML way to switch an image
    /// by <c>prefers-color-scheme</c> without a line of script;
    /// </description></item>
    /// <item><description>
    /// otherwise — one <c>&lt;img&gt;</c> with the single value there is (the light one when the dark
    /// URL has no form a <c>srcset</c> list can carry), exactly the markup the page carried before
    /// the theme dimension existed.
    /// </description></item>
    /// </list>
    /// A value that is unset or whose URL scheme is not allowed leaves the page without a logo — the
    /// client brands the page itself.
    /// </summary>
    /// <param name="design">Effective design settings.</param>
    /// <param name="htmlTheme">Theme of the page, as it was put into the data-theme attribute.</param>
    /// <returns>Markup of the logo, or null when the page displays none.</returns>
    private static string? LogoMarkup(AuthPageDesignOptions design, string htmlTheme)
    {
        if (!string.Equals(htmlTheme, CorePageThemes.Auto, StringComparison.Ordinal))
        {
            return LogoImageMarkup(DisplayableLogo(design, htmlTheme));
        }

        var light = DisplayableLogo(design, CorePageThemes.Light);
        var dark = DisplayableLogo(design, CorePageThemes.Dark);

        if (light is not null
            && dark is not null
            && !string.Equals(light, dark, StringComparison.Ordinal)
            && SrcsetCandidate(dark) is { } darkCandidate)
        {
            return $"<picture><source srcset=\"{HtmlEncode(darkCandidate)}\" media=\"(prefers-color-scheme: dark)\" />"
                + $"<img class=\"veriqa-logo\" src=\"{HtmlEncode(light)}\" alt=\"\" /></picture>";
        }

        return LogoImageMarkup(light ?? dark);
    }

    /// <summary>
    /// The URL as one candidate of a <c>srcset</c> list, or null when it cannot be written as one.
    /// The grammar of the attribute is not that of <c>src</c>: a candidate ends at whitespace (a
    /// descriptor may follow) and at a comma (the next candidate follows), so HTML escaping alone
    /// leaves such a URL silently dropped by the browser together with the whole dark variant.
    /// <para>
    /// A space is encoded rather than rejected: it is not a legal URI character at all, and the
    /// browser encodes it the very same way for <c>src</c>, so both themes still request one file.
    /// Anything else structural stays as it is and the page falls back to a single image — rewriting
    /// a legal URL would ask the deployment's server for a different address than the one configured.
    /// </para>
    /// </summary>
    /// <param name="logoUrl">Displayable logo URL.</param>
    /// <returns>Candidate for the attribute, or null when the URL has no safe form.</returns>
    private static string? SrcsetCandidate(string logoUrl)
    {
        var candidate = logoUrl.Replace(" ", "%20", StringComparison.Ordinal);

        return candidate.Any(char.IsWhiteSpace) || candidate.Contains(',', StringComparison.Ordinal)
            ? null
            : candidate;
    }

    /// <summary>
    /// Logo URL the page may display for a theme: unset, blank or a URL whose scheme is outside the
    /// allowlist all mean "no logo" (protection against injection through configuration).
    /// </summary>
    /// <param name="design">Effective design settings.</param>
    /// <param name="theme">Theme the logo is asked for.</param>
    /// <returns>Displayable URL, or null.</returns>
    private static string? DisplayableLogo(AuthPageDesignOptions design, string theme) =>
        design.GetLogoUrl(theme) is { } logoUrl && CorePageHead.IsAllowedUrlScheme(logoUrl)
            ? logoUrl
            : null;

    /// <summary>
    /// Builds the logo image; a null URL yields no markup at all.
    /// </summary>
    /// <param name="logoUrl">Displayable logo URL.</param>
    /// <returns>Markup of the image, or null.</returns>
    private static string? LogoImageMarkup(string? logoUrl) =>
        logoUrl is null
            ? null
            : $"<img class=\"veriqa-logo\" src=\"{HtmlEncode(logoUrl)}\" alt=\"\" />";

    /// <summary>
    /// Builds the page container with QR codes, buttons, and status. The title and the instruction
    /// arrive as ready markup (<see cref="BuildInlineMarkup"/>): the instruction is either one text or
    /// the wordings built by <see cref="BuildInstructionMarkup"/>, which is why neither is encoded
    /// again here.
    /// </summary>
    private void AppendContainer(
        StringBuilder sb,
        string titleMarkup,
        string instructionMarkup,
        AuthPageRenderContext context,
        string lang,
        bool hasMultipleChannels,
        DesignPreset preset,
        AuthPageDesignOptions design,
        string htmlTheme)
    {
        // The method builds the main container with the authentication card

        // How the channels share the page is decided ahead of the card, because one of the layouts
        // sizes the card itself.
        // A page the server lays out as buttons carries the modifier of the channel container and gets
        // no tabs at all: no strip, no tab roles on the panels, no panel hidden behind an unselected
        // tab. A page whose layout the device decides is rendered with tabs, and the stylesheet turns
        // them into buttons on a phone. The grid is the third arrangement of the same panels: where the
        // tabs would be, every panel is on screen under the name of its channel.
        // A page of the unified QR code that keeps the per-channel QR codes has tabs as well, the first
        // of them "Any channel": it opens on that tab, in the state of the container that shows the
        // unified QR code over every panel laid out as a button, and a channel tab is the panel of that
        // channel alone with its QR code, as on any page with tabs. The panels are not duplicated — the
        // two are states of the same container.
        var serverButtonsLayout = ServerChoosesButtonsLayout(context);
        var gridLayout = ShowsChannelGrid(context);
        var hasTabs = ShowsTabs(context);
        var anyChannelTab = HasAnyChannelState(context);

        // The split card of the unified QR code: the unified QR code and the stack of the channel panels as
        // two columns, and — on a page with the "Any channel" tab — each channel panel as its own QR code
        // beside the rest of it, or as one column in the middle where its QR code is not shown.
        var splitLayout = ShowsSplitLayout(context);
        var splitPanels = splitLayout && anyChannelTab;

        // The card of the grid states how many panels its row holds; the stylesheet widens it by that.
        sb.AppendLine(gridLayout
            ? $"    <div class=\"veriqa-container {GridCardClass}\" role=\"main\" style=\"{ChannelCountProperty}: {context.Channels.Count};\">"
            : splitLayout
                ? $"    <div class=\"veriqa-container {SplitCardClass}\" role=\"main\">"
                : "    <div class=\"veriqa-container\" role=\"main\">");

        // Brand row (SPEC-007 UI-100): the server does not emit it in the effective Minimal preset —
        // leaving hidden markup with the integrator's name for a stylesheet rule to hide would make the
        // preset a second home of the row. Every other preset takes the one path of the row.
        if (preset is not DesignPreset.Minimal)
        {
            AppendBrandRow(sb, design, htmlTheme, lang);
        }

        // Title and instruction (eyebrow badge removed — docs/design-rules.md §9)
        sb.AppendLine($"        <h1 class=\"veriqa-title\">{titleMarkup}</h1>");

        // The scanner page with a choice of channels says where the person went once they tapped a channel
        // (SPEC-025 HOP-193): the headings are rendered ready, one per channel the page leads into, hidden;
        // the script shows the one of the channel tapped, and the stylesheet takes the title above away
        // meanwhile, so one heading is on screen at a time. A page whose channel has already answered comes
        // with the heading of that channel shown and the root in that mode (ChosenChannelAtRender).
        var marksChosenChannel = MarksChosenChannel(context);
        if (marksChosenChannel)
        {
            AppendChosenChannelHeadings(sb, context, lang);
        }

        // What is being confirmed, when the deployment asked this surface to show it (SPEC-039 R38): right
        // under the title, ahead of the instruction, as on the launch pages of the hop — the page names the
        // action first and says what to do with it next. The text primitive of the card the instruction
        // uses, set larger than it — the subject is a sentence addressed to the user, not a control of its
        // own. It is worded in the requested language and does not fall back with the page, so its line
        // states its own lang when the two differ.
        var subjectLine = ConfirmationSubjectText.Markup(context.SubjectText, context.Language, lang);
        if (subjectLine.Length > 0)
        {
            sb.AppendLine($"        {subjectLine}");
        }

        // The lead instruction carries a modifier of its own: it describes the channel choice, so it
        // leaves together with it once the hop has started, while the subject above stays.
        sb.AppendLine($"        <p class=\"veriqa-instruction veriqa-instruction--lead\">{instructionMarkup}</p>");

        // The instruction of the address form of Email, where a tap opens that form: rendered ready and
        // hidden, it stands in for the lead instruction while the form is open, since the lead instruction
        // describes the channel choice that leaves meanwhile. One text for a sign-in and a confirmation, the
        // one the panel of Email Pull with no QR code is worded by.
        if (EmailChannelWithFormOnTap(context, hasMultipleChannels) is { } emailFormChannel)
        {
            sb.AppendLine(
                $"        <p class=\"veriqa-instruction veriqa-instruction--lead {EmailFormInstructionClass}\">{EmailFormInstructionMarkup(context.Texts, emailFormChannel, lang)}</p>");
        }

        // The way back from the state of the remembered channel stands right under the instruction that names
        // the channel, behind the "OR" divider, so the other channels are offered before the remembered one is
        // used. Rendered hidden: the script shows it in that state alone.
        if (OffersRememberCheckbox(context))
        {
            var orText = LocalizeOwn(context.Texts, AuthPageStrings.OrDivider, lang);
            var backText = LocalizeOwn(context.Texts, AuthPageStrings.ChooseAnotherChannel, lang);
            sb.AppendLine(
                $"        <div class=\"{RememberBackClass}\" hidden><div class=\"veriqa-divider\">{HtmlEncode(orText)}</div><button class=\"veriqa-text-link\" id=\"{RememberBackElementId}\" type=\"button\">{HtmlEncode(backText)}</button></div>");
        }

        // Everything that leads into a channel — the tab strip with its tooltip host and every channel
        // panel — lives in ONE container, so the page can take the whole choice away with one rule
        // once the hop has started.
        var channelsClass = serverButtonsLayout
            ? $"{ChannelsClass} {ButtonsLayoutClass}"
            : anyChannelTab
                ? AnyChannelStateShowsChannelButtons(context)
                    ? $"{ChannelsClass} {AnyChannelStateClass}"
                    : $"{ChannelsClass} {AnyChannelStateClass} {AnyChannelQrOnlyClass}"
                : ChannelsClass;
        // Focusable by the script alone, and only on a page that can bring the choice back — after the hop
        // has started, or on the scanner page after a channel was chosen: the focus of the button that does
        // it moves here.
        var channelsFocus = context.IsScannerPage && !marksChosenChannel ? string.Empty : " tabindex=\"-1\"";
        sb.AppendLine($"        <div class=\"{channelsClass}\" id=\"{ChannelsElementId}\"{channelsFocus}>");

        // Channel tabs (only when there are multiple channels and the server does not lay them out
        // as buttons)
        if (hasTabs)
        {
            // 4+ tabs are laid out as a 2×2 grid (modifier --grid), 2–3 — in a single row. A strip with
            // the "Any channel" tab is a grid whatever their number: that tab on a row of its own, the
            // channel tabs two to a row under it. From five channel tabs on, either grid holds three to a row.
            var threeColumnsClass = context.Channels.Count >= ThreeColumnTabsThreshold
                ? $" {ThreeColumnTabsClass}"
                : string.Empty;
            var tabsGridClass = anyChannelTab
                ? $" veriqa-tabs--grid {AnyChannelTabsClass}{threeColumnsClass}"
                : context.Channels.Count >= FourTabGridThreshold
                    ? $" veriqa-tabs--grid{threeColumnsClass}"
                    : string.Empty;
            // A clipped label gets the contour's tooltip instead of a native title. The strip clips its own
            // content, so the bubble lives in a host around the strip and is shared by its tabs — only the
            // clipped ones become triggers, and without one the host is not rendered at all.
            // The label of a tab with a QR code depends on the channel only by its slot: the template is
            // localized and cut at its marks once per page.
            var qrTabLabelPieces = anyChannelTab ? ChannelTabWithQrCodePieces(context.Texts, lang) : null;
            var qrCodeTabs = context.Channels
                .Select(channel => qrTabLabelPieces is not null && PanelShowsQrCode(channel, context))
                .ToArray();
            // A tab with a QR code builds its label from the template as well, so an override of it clips too.
            var clippedTabs = context.Channels
                .Select((channel, i) => ClipsLabel(
                    context.Texts, channel, lang, qrCodeTabs[i] ? [AuthPageStrings.ChannelTabWithQrCode] : []))
                .ToArray();
            var tabsNeedTooltip = clippedTabs.Contains(true);
            if (tabsNeedTooltip)
            {
                sb.AppendLine("        <div class=\"veriqa-tooltip-host\">");
            }

            // The strip of a page that keeps the per-channel QR codes for the remembering checkbox alone is
            // rendered hidden: the page looks as the page of the unified QR code, and the script selects a
            // channel tab of it only as the state of the remembered channel.
            var stripHidden = HidesTabStrip(context) ? " hidden" : string.Empty;
            sb.AppendLine($"        <div class=\"veriqa-tabs{tabsGridClass}\" role=\"tablist\"{stripHidden}>");

            // The "Any channel" tab stands first and is the active tab on initial render. It names no
            // channel, so it carries neither a channel type nor a brand icon; what it controls is the
            // block of the unified QR code — the one content no channel tab shows.
            if (anyChannelTab)
            {
                var anyChannelText = LocalizeOwn(context.Texts, AuthPageStrings.AnyChannelTab, lang);
                sb.AppendLine($"            <button class=\"veriqa-tab active\" id=\"{AnyChannelTabElementId}\" role=\"tab\" aria-selected=\"true\" aria-controls=\"{AnyChannelQrElementId}\" tabindex=\"0\"><span>{HtmlEncode(anyChannelText)}</span></button>");
            }

            for (var i = 0; i < context.Channels.Count; i++)
            {
                var channel = context.Channels[i];
                // The first channel is the active tab on initial render — unless the "Any channel" tab
                // stands before it; a single predicate drives its active class, aria-selected and
                // roving tabindex so they can never diverge.
                var isActive = i is 0 && !anyChannelTab;
                var activeClass = isActive ? " active" : string.Empty;
                // Tab name: the name of the channel in the language of the page — the name of a built-in
                // channel or the label an adapter declared via IChannelDisplayMetadata (fallback — the raw
                // channel type).
                var tabName = ChannelName(context.Texts, channel, lang);
                // Clipping and the tooltip apply to a label that is not the core's own text (ClipsLabel): the
                // name of a built-in channel is our own translated text, which wraps rather than overflows,
                // and a bubble repeating it would only cover the page with a duplicate of what is readable.
                var clipsTabLabel = clippedTabs[i];
                // The bubble carries the whole label, the text of the template around the name included, so
                // nothing the clipping takes is out of reach.
                var tabText = qrTabLabelPieces is not null && qrCodeTabs[i]
                    ? ChannelTabTextWithQrCode(qrTabLabelPieces, tabName)
                    : tabName;
                var tabTooltip = clipsTabLabel ? $" data-veriqa-tooltip=\"{HtmlEncode(tabText)}\"" : string.Empty;
                var tabLabelClass = clipsTabLabel ? " class=\"veriqa-tab-label\"" : string.Empty;
                // Next to the "Any channel" tab, the tab of a channel whose panel shows its own QR code says
                // so: its label carries the glyph of a QR code (SPEC-025 HOP-187). A tab whose panel shows
                // none keeps the bare name — the glyph would promise a QR code that is not there.
                var tabLabel = qrTabLabelPieces is not null && qrCodeTabs[i]
                    ? ChannelTabLabelWithQrCode(qrTabLabelPieces, tabName, tabLabelClass)
                    : $"<span{tabLabelClass}>{HtmlEncode(tabName)}</span>";
                var ariaSelected = isActive ? "true" : "false";
                // Roving tabindex (WAI-ARIA Tabs pattern): only the active tab is in the tab order
                // (0), the rest are -1. The active tab is the first one on first render, so the state
                // is correct from the markup alone — no init script call is needed.
                var tabIndex = isActive ? "0" : "-1";
                var tabId = ChannelTabElementId(i);
                var panelId = $"{PanelElementIdPrefix}{HtmlEncode(channel.ChannelType)}";
                // Channel brand icon in the tab (decorative; empty for an unknown channel)
                var tabIcon = ChannelNameIcon(channel);
                sb.AppendLine($"            <button class=\"veriqa-tab{activeClass}\" id=\"{tabId}\" data-channel=\"{HtmlEncode(channel.ChannelType)}\" role=\"tab\" aria-selected=\"{ariaSelected}\" aria-controls=\"{panelId}\" tabindex=\"{tabIndex}\"{tabTooltip}>{tabIcon}{tabLabel}</button>");
            }
            sb.AppendLine("        </div>");

            if (tabsNeedTooltip)
            {
                sb.AppendLine("            <span class=\"veriqa-tooltip\" aria-hidden=\"true\" hidden></span>");
                sb.AppendLine("        </div>");
            }
        }

        // The unified QR code stands over the buttons, or to their left on the split card (SPEC-025 HOP-187),
        // and under the tab strip where the page has one — the "Any channel" tab. It is part of the channel
        // choice, so the same rules hide it — the QR visibility on a phone, the start of the hop and the
        // expiry; on a page with the "Any channel" tab it is shown in that tab's state alone, and that tab
        // names it.
        if (ShowsUnifiedQrCode(context) && context.UnifiedQrCodeBase64 is { } unifiedQrCodeBase64)
        {
            AppendQrBlock(
                sb,
                unifiedQrCodeBase64,
                LocalizeOwn(context.Texts, AuthPageStrings.QrCodeAlt, lang),
                UnifiedHopQrEntryOf(context),
                anyChannelTab ? AnyChannelQrElementId : null,
                anyChannelTab ? AnyChannelQrClass : null);
        }

        // The row of the grid layout — an element of its own around the panels, inside the container. The
        // split card has one too: the column of the panels beside the unified QR code.
        if (gridLayout)
        {
            sb.AppendLine($"        <div class=\"{ChannelGridClass}\">");
        }
        else if (splitLayout)
        {
            sb.AppendLine($"        <div class=\"{ChannelStackClass}\">");
        }

        // Channel panels
        for (var i = 0; i < context.Channels.Count; i++)
        {
            var channel = context.Channels[i];
            // The panel of an unselected tab is hidden by a class rather than an inline style: the
            // buttons layout shows every panel with an ordinary rule, and an inline display would
            // outweigh it. Under the selected "Any channel" tab no channel tab is selected, so every
            // panel carries the class, and the state of the container shows them all.
            var inactiveClass = hasTabs && (anyChannelTab || i is not 0) ? " veriqa-panel--inactive" : string.Empty;
            var tabId = ChannelTabElementId(i);
            var panelId = $"{PanelElementIdPrefix}{HtmlEncode(channel.ChannelType)}";

            var channelNameId = $"veriqa-channel-name-{i}";

            // The panel is what its layout makes it. Behind a tab it is a tab panel labelled by that
            // tab, with a stop of its own in the tab order (WAI-ARIA Tabs pattern). In the grid nothing
            // is switched, so there is no tab pattern to announce: the panel is a group labelled by the
            // visible name of its channel, and it takes no stop of its own. In the buttons layout it is
            // neither, and so is it under the selected "Any channel" tab, which shows every panel as a
            // button: a panel labelled there by a tab that is not selected would announce a state the
            // page is not in. The script gives the panels the attributes of a tab panel back once a
            // channel tab is selected.
            var panelAriaAttributes = hasTabs && !anyChannelTab
                ? $" role=\"tabpanel\" aria-labelledby=\"{tabId}\" tabindex=\"0\""
                : gridLayout
                    ? $" role=\"group\" aria-labelledby=\"{channelNameId}\""
                    : string.Empty;

            // On the split card of a page with the "Any channel" tab the panel is laid out by what it shows:
            // its QR code beside the rest of it, or — with no QR code of its own — one column in the middle.
            // The QR code of the Email Push panel stands in its Push section, which takes the pair instead.
            var panelShowsQrCode = PanelShowsQrCode(channel, context);
            var pairPanel = splitPanels && panelShowsQrCode && channel.ChannelType is not ChannelTypes.Email;
            var splitClass = pairPanel
                ? $" {QrPairClass}"
                : splitPanels && !panelShowsQrCode
                    ? $" {SplitSingleClass}"
                    : string.Empty;

            var emailClass = channel.ChannelType is ChannelTypes.Email ? $" {EmailPanelClass}" : string.Empty;

            sb.AppendLine($"        <div class=\"veriqa-panel{inactiveClass}{splitClass}{emailClass}\" id=\"{panelId}\"{panelAriaAttributes}>");

            // The name of the channel over its panel: with no tab to carry it, the grid states it here,
            // by the rule the tabs are named with and under the same brand icon. A heading, so the
            // channels are reachable the way sections of a page are.
            if (gridLayout)
            {
                sb.AppendLine(
                    $"            <h2 class=\"{ChannelNameClass}\" id=\"{channelNameId}\">{ChannelNameIcon(channel)}<span>{HtmlEncode(ChannelName(context.Texts, channel, lang))}</span></h2>");
            }

            // The attribute that makes a tap on the channel link send the signal of the chosen channel —
            // only on a scanner page that sends it (SPEC-025 HOP-156).
            var launchAttribute = SendsChannelLaunchedSignal(context)
                ? $" {ChannelLaunchAttributeName}=\"{HtmlEncode(channel.ChannelType)}\""
                : string.Empty;

            // The attributes of the link into the channel: the signal above, and on the scanner page with a
            // choice of channels the channel whose heading a tap on the link shows (SPEC-025 HOP-193).
            var linkAttributes = marksChosenChannel
                ? $"{launchAttribute} {ContinueChannelAttributeName}=\"{HtmlEncode(channel.ChannelType)}\""
                : launchAttribute;

            if (channel.ChannelType is ChannelTypes.Email)
            {
                // Push mode is determined by the DeepLinkUrl scheme and rendered as a QR + "open mail
                // client" button; Pull (relative /auth/email/start path) is the inline email entry form.
                if (IsEmailPushLink(channel.DeepLinkUrl))
                {
                    AppendEmailPushPanel(
                        sb,
                        context.Texts,
                        channel,
                        context.SessionId,
                        EmailStartUrl(context),
                        lang,
                        ShowsQrCodeOf(channel, context),
                        linkAttributes,
                        ChannelHopQrEntry(channel, context),
                        splitPanels && panelShowsQrCode,
                        IsSignIn(context));
                }
                else
                {
                    // The Pull form gets a button of its own only where there is a choice to make: a
                    // page whose single channel is Email Pull shows the form straight away.
                    AppendEmailPanel(
                        sb,
                        context.Texts,
                        context.SessionId,
                        EmailStartUrl(context),
                        lang,
                        EmailPullForm.MailOf(channel),
                        withRevealButton: hasMultipleChannels,
                        isSignIn: IsSignIn(context));
                }
            }
            else
            {
                var qrAlt = LocalizeOwn(context.Texts, AuthPageStrings.QrCodeAlt, lang);
                var btnText = ChannelButton.Text(
                    _promptLocalizer, context.Texts, channel.ChannelType, channel.DisplayName, IsSignIn(context), lang);
                var btnClass = ChannelButton.CssClass(channel.ChannelType);
                // Channel brand icon in the CTA (color — button text via currentColor).
                // A third-party channel may supply its own glyph via IChannelDisplayMetadata;
                // without one the button renders without an icon.
                var btnIcon = ChannelIconSvg.Render(channel.ChannelType, "veriqa-btn-icon", channel.IconSvgPath);
                // Same rule as for the tab: only a label that is not the core's own text is clipped and gets a
                // tooltip; the localized CTA of a built-in channel keeps wrapping (its length is our own,
                // translated and pseudo-locale-checked text, not an arbitrary SPI string or override).
                var clipsBtnLabel = ClipsLabel(
                    context.Texts,
                    channel,
                    lang,
                    ChannelButton.SourceTexts(channel.ChannelType, channel.DisplayName, IsSignIn(context)));
                var btnTooltip = clipsBtnLabel ? $" data-veriqa-tooltip=\"{HtmlEncode(btnText)}\"" : string.Empty;
                var btnLabelMarkup = clipsBtnLabel
                    ? $"<span class=\"veriqa-btn-label\">{HtmlEncode(btnText)}</span>"
                    : HtmlEncode(btnText);

                if (ShowsQrCodeOf(channel, context))
                {
                    AppendQrBlock(sb, channel.QrCodeBase64, qrAlt, ChannelHopQrEntry(channel, context));
                }

                // The rest of a paired panel — its way into the channel and its hint — is the column beside
                // the QR code.
                if (pairPanel)
                {
                    sb.AppendLine($"            <div class=\"{PanelSideClass}\">");
                }

                if (ShowsStartCodeBlock(channel))
                {
                    // The link of this channel carries no transaction, so a bare "open" button would
                    // lead into a chat where nothing starts. The block stands where the button would,
                    // in every layout: the message to send, on screen from the load, and under it the
                    // button that copies the message and opens the chat. The button is the channel
                    // button of this panel — same class, same color, same visibility setting, same
                    // signal of the chosen channel — and only its words and its copy differ. The
                    // message is not a control and is hidden by no setting.
                    AppendStartCodeBlock(sb, context.Texts, channel, btnClass, btnIcon, linkAttributes, lang);
                }
                else
                {
                    if (clipsBtnLabel)
                    {
                        sb.AppendLine("            <span class=\"veriqa-tooltip-host\">");
                    }

                    sb.AppendLine($"            <a href=\"{HtmlEncode(channel.DeepLinkUrl)}\" class=\"veriqa-btn veriqa-btn-channel {btnClass}\"{ChannelLinkTargetAttributes(channel, context)}{btnTooltip}{linkAttributes}>");
                    sb.AppendLine($"                {btnIcon}{btnLabelMarkup}");
                    sb.AppendLine("            </a>");
                    if (clipsBtnLabel)
                    {
                        sb.AppendLine("                <span class=\"veriqa-tooltip\" aria-hidden=\"true\" hidden></span>");
                        sb.AppendLine("            </span>");
                    }

                    if (ShowsSpareStartCodeBlock(channel))
                    {
                        // The link of this channel carries the transaction, so its button and its QR
                        // code stay what they are. The platform does not always deliver what the link
                        // carries, though: the chat opens and nothing starts. The message under the
                        // button is the way on from there — sent into that chat, it starts the same
                        // transaction. Its button only copies and is not the channel button, so the
                        // setting that hides the channel button leaves it alone.
                        AppendSpareStartCodeBlock(sb, context.Texts, channel, lang);
                    }
                }
            }

            // The channel hint closes its own panel — one insertion point for every channel type,
            // including both Email sections. Living inside the panel is what makes "the active tab
            // only" fall out of the composition: the hidden panel hides its hint with it, so the
            // multi-channel screen needs neither a script nor a state of its own. Where every panel is on
            // screen as a button no channel is open, and the stylesheet takes the hints away there; a page
            // of one channel keeps its hint. Absent hint —
            // absent element, so a deployment that sets none renders exactly today's markup. The text
            // is the operator's and may carry inline markup, so it goes through the same check as the
            // title and the lead instruction; it is not localized.
            if (!string.IsNullOrEmpty(channel.Hint))
            {
                var hintMarkup = BuildInlineMarkup(
                    channel.Hint,
                    string.Format(CultureInfo.InvariantCulture, HintElementNameFormat, channel.ChannelType),
                    lang,
                    out _);
                sb.AppendLine($"            <p class=\"veriqa-hint\">{hintMarkup}</p>");
            }

            if (pairPanel)
            {
                sb.AppendLine("            </div>");
            }

            sb.AppendLine("        </div>");
        }

        if (gridLayout || splitLayout)
        {
            sb.AppendLine("        </div>");
        }

        sb.AppendLine("        </div>");

        // The way back to the channel choice of the scanner page once a channel was chosen, right under the
        // choice it brings back and above the status line, so the spare start code under that line stands
        // below it. Rendered ready, hidden by the stylesheet until a channel is chosen: it leaves the
        // transaction as it is and asks the server nothing.
        if (marksChosenChannel)
        {
            var backText = LocalizeOwn(context.Texts, AuthPageStrings.BackToChannelSelection, lang);
            sb.AppendLine(
                $"        <button class=\"veriqa-text-link {ChosenBackClass}\" id=\"{ChosenBackElementId}\" type=\"button\">{HtmlEncode(backText)}</button>");
        }

        // What the page shows once the hop has started, rendered ready and hidden: the root attribute
        // the script sets reveals it, no script builds it. A flow element rather than a paragraph — the
        // integrator's markup may carry paragraphs of its own, and a paragraph cannot hold one. Not a
        // live region: the hop code that reveals the block also puts its text on the status line, which
        // announces it; a second live region would announce the same switch twice. Focusable by
        // the script alone, so the focus the channel choice held does not drop to the body when the
        // choice leaves the screen. The scanner page has it off (SPEC-025 HOP-186) and does not carry it.
        // Above the block, out of the channel container, stands the live region of the warning of a
        // reopened hop link (SPEC-041 L93): empty and on screen from the load, filled by the script on the
        // channel status reporting the reopening. A region added at that moment would not be announced. The
        // scanner page — the phone of the first scan — does not carry it either.
        // The block ends with the way back to the channel choice: a button of the page's own, standing
        // after the instruction and never inside it — the instruction may be the integrator's markup,
        // and the button is not theirs to word or to drop. It leaves the transaction as it is and asks
        // the server nothing; the script takes the start of the hop back off the root.
        // Between the instruction and that button stand the spare start codes of the channels that have
        // one, each a second copy of the block of its panel: the panel is out of sight once the hop has
        // started, and the code is what the person on the phone may need then. The script opens the copy
        // of the channel the phone went into, and never moves the focus onto it.
        // Right under the instruction stand the hints of the channels the levels word one for, each hidden
        // until the script notices the phone on its way into its channel.
        if (!context.IsScannerPage)
        {
            var chooseAnotherText = LocalizeOwn(context.Texts, AuthPageStrings.HopChooseAnother, lang);
            sb.AppendLine(
                $"        <div id=\"{HopReopenedElementId}\" role=\"status\" aria-live=\"polite\"></div>");
            sb.AppendLine(
                $"        <div class=\"veriqa-hop-instruction\" id=\"{HopInstructionElementId}\" tabindex=\"-1\">{BuildHopInstructionMarkup(context.Texts, design, lang)}{HopChannelHintMarkup(context)}{SpareStartCodesMarkup(context, lang)}<button class=\"veriqa-text-link\" id=\"{HopChooseAnotherElementId}\" type=\"button\">{HtmlEncode(chooseAnotherText)}</button></div>");
        }

        // Spinner (SPEC-007 UI-045)
        sb.AppendLine("        <div class=\"veriqa-spinner\" id=\"veriqa-spinner\" aria-hidden=\"true\"></div>");

        // Status (SPEC-007 UI-048). Focusable by the script alone: it is the one part of the card that stays on
        // every page once the answer is given elsewhere (AnswerElsewherePageStateValue), so a focus standing
        // in the part that leaves moves here instead of dropping to the body. The cancel button is no such
        // target: Enter on it would cancel the transaction.
        // A page rendered while the answer is awaited in a channel carries the line of that answer from the
        // start, in the look the script gives it when the answer arrives, and the root carries the state that
        // takes the channel choice away (AppendHtmlHead): the page — without its script as well — shows what
        // the replay of the answer would turn it into, and the replay finds nothing left to change.
        var answerLine = AnswerLineAtRender(context);
        var statusText = answerLine ?? LocalizeOwn(context.Texts, AuthPageStrings.WaitingForScan, lang);
        var statusClass = answerLine is null ? StatusClass : AnsweredStatusClass;
        sb.AppendLine($"        <div class=\"{statusClass}\" id=\"veriqa-status\" role=\"status\" aria-live=\"polite\" tabindex=\"-1\">{HtmlEncode(statusText)}</div>");

        // The spare start code on a phone (SPEC-007 UI-119): under the status line, hidden, a link and a
        // copy of the spare start code of each channel that has one — on the scanner page the block that
        // copies the code and opens the channel (SPEC-025 HOP-194). The script shows the link once the person
        // comes back from such a channel the page sent them to, and the link opens the copy of that
        // channel alone. With scripts off the block stays hidden — the panels show their forms then.
        if (ShowsAnySpareStartCodeBlock(context))
        {
            var linkText = LocalizeOwn(
                context.Texts, IsSignIn(context) ? AuthPageStrings.ProblemSigningIn : AuthPageStrings.ProblemConfirming, lang);
            sb.AppendLine(
                $"        <div class=\"veriqa-spare-return\" id=\"{SpareReturnElementId}\" hidden><button class=\"veriqa-spare-return-link\" id=\"{SpareReturnLinkElementId}\" type=\"button\" aria-expanded=\"false\">{HtmlEncode(linkText)}</button>{SpareStartCodesMarkup(context, lang)}</div>");
        }

        // Countdown (SPEC-007 UI-094)
        sb.AppendLine($"        {PageCountdown.Markup}");

        // The one way out of an expired page, and of a failed one, rendered ready and hidden: the state attribute shows it,
        // no script builds it. It stands NEXT TO the status line rather than inside it — the line is a
        // live region whose text is reassigned on every status, which would take a child element with
        // it, and a link announced as part of a status message is not what the region is for.
        if (!string.IsNullOrEmpty(context.RestartUrl))
        {
            var restartText = LocalizeOwn(context.Texts, AuthPageStrings.RestartSignIn, lang);
            sb.AppendLine(
                $"        <a class=\"veriqa-btn veriqa-btn-restart\" id=\"veriqa-restart\" href=\"{HtmlEncode(context.RestartUrl)}\">{HtmlEncode(restartText)}</a>");
        }

        // The action under the outcome, rendered ready and hidden: the status block of the script shows it
        // with an ending of the transaction. Without scripts the page shows no ending, and the row stays
        // hidden with it.
        if (context.OffersOutcomeClose)
        {
            sb.AppendLine(
                $"        {OutcomeCloseAction.Row(context.OutcomeCloseApplicationName, hidden: true, _promptLocalizer, context.Texts, lang)}");
        }

        // The way back to the application, where the caller states one: under the channels and the
        // status, above the integrator's footer.
        AppendCancelForm(sb, context, lang);

        // The checkbox that remembers the channel, under the way back to the application and out of the
        // channel container, so neither the start of the hop nor the answer of a channel takes it away
        // (SPEC-025 HOP-187); without the way back it stands under the status line. The server renders it
        // ready and hidden, worded for the first visit; the script shows it on a desktop and words it by the
        // state of the page; the way back from the remembered channel stands under the lead instruction.
        // Without a script the page has no checkbox on screen. The browser does not
        // restore its state on a reload: the cookie alone decides whether it is ticked on the load.
        if (OffersRememberCheckbox(context))
        {
            var rememberText = LocalizeOwn(
                context.Texts,
                IsSignIn(context) ? AuthPageStrings.RememberSignInChannel : AuthPageStrings.RememberConfirmChannel,
                lang);
            sb.AppendLine(
                $"        <div class=\"veriqa-remember\" id=\"{RememberElementId}\" hidden><div class=\"veriqa-remember-option\"><input type=\"checkbox\" id=\"{RememberCheckboxElementId}\" autocomplete=\"off\"><label for=\"{RememberCheckboxElementId}\" id=\"{RememberLabelElementId}\">{HtmlEncode(rememberText)}</label></div></div>");
        }

        // The integrator's footer block closes the card, in every preset.
        AppendFooter(sb, design, lang);

        sb.AppendLine("    </div>");
    }

    /// <summary>
    /// Builds the form behind the cancel button when the caller states one
    /// (<see cref="AuthPageRenderContext.CancelForm"/>). A plain form post, so the button works on a page
    /// whose script never ran: the session identifier and the AntiForgery token travel as hidden fields.
    /// The button is the secondary variant of the button primitive (SPEC-015 §4.6) — leaving is not the
    /// action the window is opened for. By the type of the transaction only its words differ: "Cancel
    /// sign-in" or "Cancel confirmation".
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="context">Page rendering context.</param>
    /// <param name="lang">Content language of the page.</param>
    private void AppendCancelForm(StringBuilder sb, AuthPageRenderContext context, string lang)
    {
        if (context.CancelForm is not { } cancelForm)
        {
            return;
        }

        var cancelText = LocalizeOwn(
            context.Texts, IsSignIn(context) ? AuthPageStrings.CancelSignIn : AuthPageStrings.CancelConfirmation, lang);

        sb.AppendLine(
            $"        <form class=\"{CancelFormClass}\" id=\"{CancelFormElementId}\" method=\"post\" action=\"{HtmlEncode(context.PathBase + cancelForm.ActionUrl)}\">");
        sb.AppendLine(
            $"            <input type=\"hidden\" name=\"{OidcConstants.SessionIdParameterName}\" value=\"{HtmlEncode(context.SessionId)}\" />");
        sb.AppendLine(
            $"            <input type=\"hidden\" name=\"{HtmlEncode(cancelForm.AntiforgeryFieldName)}\" value=\"{HtmlEncode(cancelForm.AntiforgeryToken)}\" />");
        sb.AppendLine(
            $"            <button type=\"submit\" class=\"veriqa-btn veriqa-btn-decline\">{HtmlEncode(cancelText)}</button>");
        sb.AppendLine("        </form>");
    }

    /// <summary>
    /// Builds the integrator's footer block — the last element of the card — when a level states one.
    /// The text reaching the page is the TRANSLATION of the stated value, which the domain of the key
    /// never saw, so its shape is checked again here: a translation outside it leaves the page without
    /// the block and is reported, never emitted.
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="design">Effective design of the page.</param>
    /// <param name="lang">Content language of the page.</param>
    private void AppendFooter(StringBuilder sb, AuthPageDesignOptions design, string lang)
    {
        if (string.IsNullOrWhiteSpace(design.FooterHtml))
        {
            return;
        }

        var markup = Localize(design.FooterHtml, lang);

        if (!AuthPageFooterMarkup.IsValid(markup))
        {
            _logger.LogWarning(
                "The text of {ConfigKey} for language {Language} is not admissible footer markup; the "
                + "sign-in page is rendered without the footer block",
                AuthServerConfigKeys.PageFooterHtml.Name,
                lang);

            return;
        }

        // Markup the page did not build: the value is the integrator's, it is not encoded, and
        // AuthPageFooterMarkup is what stands between it and the page.
        sb.AppendLine($"        <footer class=\"veriqa-footer\">{markup}</footer>");
    }

    /// <summary>
    /// Builds the content of the block shown once the hop has started: the markup a level states,
    /// otherwise this renderer's own text. The stated value is a Natural Key, and the text reaching the
    /// page is its TRANSLATION, which the domain of the key never saw — so its shape is checked again
    /// here, exactly as the footer's is. A translation outside that shape gives the page the default
    /// text and is reported: unlike the footer, the block cannot simply be left out, because it is what
    /// replaces the channel choice.
    /// </summary>
    /// <param name="texts">Text overrides of the page — they reach the renderer's own text only.</param>
    /// <param name="design">Effective design of the page.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the block's content.</returns>
    private string BuildHopInstructionMarkup(CorePageTexts texts, AuthPageDesignOptions design, string lang)
    {
        if (!string.IsNullOrWhiteSpace(design.HopInstructionHtml))
        {
            var markup = Localize(design.HopInstructionHtml, lang);

            // The second place the page emits markup it did not build; AuthPageFooterMarkup stands
            // between it and the page, the same guard as for the footer.
            if (AuthPageFooterMarkup.IsValid(markup))
            {
                return markup;
            }

            _logger.LogWarning(
                "The text of {ConfigKey} for language {Language} is not admissible markup; the sign-in "
                + "page shows the default instruction instead",
                AuthServerConfigKeys.PageHopInstructionHtml.Name,
                lang);
        }

        return HtmlEncode(LocalizeOwn(texts, AuthPageStrings.HopInstruction, lang));
    }

    /// <summary>
    /// The one place a text of the page that stands inside a heading or a paragraph — the title, the
    /// lead instruction, the hint of a channel — becomes markup. The value is the integrator's or a
    /// translation of it, so it is judged here, where it is emitted: a value that is plain text or
    /// inline markup of the admitted shape (<see cref="AuthPageFooterMarkup.IsValidInline"/>) goes into
    /// the page as it is, any other one goes in whole as encoded text and is reported — the element
    /// stays on the page either way. The value itself is never logged.
    /// </summary>
    /// <param name="value">Localized value of the text.</param>
    /// <param name="element">Name of the element, for the log.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <param name="text">
    /// The same value for a place that reads no tag, ready to emit: the admitted value without its
    /// tags, or the encoded form of a refused one.
    /// </param>
    /// <param name="textOnlySlot">
    /// Slot the caller fills after this check; a value carrying it inside a tag is refused. Null — the
    /// text has no slot.
    /// </param>
    /// <returns>Ready markup of the text.</returns>
    private string BuildInlineMarkup(
        string value,
        string element,
        string lang,
        out string text,
        string? textOnlySlot = null)
    {
        if (AuthPageFooterMarkup.IsValidInline(value, out var admittedText, textOnlySlot))
        {
            text = admittedText;

            return value;
        }

        // A blank value is no text at all. The values of the pseudo-locale end in a marker of angle
        // brackets by design, on every text of every render — neither is something to report.
        if (!string.IsNullOrWhiteSpace(value)
            && !string.Equals(lang, AuthPageLanguageRegistry.PseudoLocale, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "The text of the {Element} of the sign-in page for language {Language} is not admissible "
                + "inline markup; the page shows it as plain text",
                element,
                lang);
        }

        text = HtmlEncode(value);

        return text;
    }

    /// <summary>
    /// Builds the lead instruction in its three wordings: for a page that shows the QR
    /// code and the channel button, the QR code alone, and the button alone (SPEC-007 UI-020). All three
    /// go into the markup, and the stylesheet shows the one that matches what the page displays — which
    /// of the two the device in front of it gets is knowable in the browser only. The server leaves no
    /// device with neither — a page that hides the QR code shows the buttons — so there is no fourth wording
    /// by device. A panel may still hold neither: the Email panel in Pull mode holds the address form, and
    /// inside each wording it has a text of its own (<see cref="ChannelInstructionMarkup"/>). Each wording is
    /// the one of a sign-in or of a confirmation, by the transaction of the page.
    /// <para>
    /// A wording the deployment stated through ui_config (<see cref="StatedInstruction"/>) is taken as it is:
    /// its author knows the values of the settings, and no tab re-phrases it. Every other wording is the
    /// renderer's own.
    /// </para>
    /// <para>
    /// Which text the renderer's own wording takes follows what the page offers (SPEC-007 UI-121). A page with one channel
    /// names it — its brand icon and its name. A page of the unified QR code without the "Any channel" tab
    /// shows one QR code whatever the number of its channels and names no channel. A page whose channels
    /// share the screen at once — the grid, the buttons layout — asks the person to choose one. A page with
    /// tabs words the tab that is selected: a channel tab names its channel, the "Any channel" tab asks for
    /// the QR code or the button of the person's messenger. The wording of the button alone is never one of
    /// a tab: a page that hides the QR code is a phone, where the tabs become the buttons layout.
    /// </para>
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the three wordings.</returns>
    private string BuildInstructionMarkup(AuthPageRenderContext context, string lang)
    {
        // The wording of the QR code alone carries both conditions: it holds while the QR code is shown
        // and the button is not. The wording of the button alone needs one — a page that hides the QR
        // code always shows the button.
        return BuildInstructionWording(
                WordingMarkup(InstructionWording.QrAndButton),
                $"{QrShownTextClass} {ButtonShownTextClass}")
            + BuildInstructionWording(
                WordingMarkup(InstructionWording.QrOnly),
                $"{QrShownTextClass} {ButtonHiddenTextClass}")
            + BuildInstructionWording(
                WordingMarkup(InstructionWording.ButtonOnly),
                QrHiddenTextClass);

        string WordingMarkup(InstructionWording wording) =>
            StatedInstruction(context, wording) is { } instruction
                ? BuildInlineMarkup(Localize(instruction, lang), InstructionElementName, lang, out _)
                : InstructionWordingMarkup(context, wording, lang);
    }

    /// <summary>
    /// The instruction a deployment stated through ui_config for one wording: the text of that wording
    /// where one is stated, the instruction of every wording otherwise.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <param name="wording">The wording.</param>
    /// <returns>The stated instruction; null — the wording is the renderer's own.</returns>
    private static string? StatedInstruction(AuthPageRenderContext context, InstructionWording wording)
    {
        var ofWording = wording switch
        {
            InstructionWording.QrOnly => context.InstructionWithoutButton,
            InstructionWording.ButtonOnly => context.InstructionWithoutQr,
            _ => null
        };

        return ofWording ?? context.Instruction;
    }

    /// <summary>
    /// Builds the text of one wording of the renderer's own instruction by what the page offers (see
    /// <see cref="BuildInstructionMarkup"/>): one text, or on a page with tabs one variant per tab.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <param name="wording">The wording.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the wording's text.</returns>
    private string InstructionWordingMarkup(AuthPageRenderContext context, InstructionWording wording, string lang)
    {
        var isSignIn = IsSignIn(context);

        if (context.Channels.Count is 1)
        {
            return ChannelInstructionMarkup(context, context.Channels[0], wording, lang);
        }

        if (ShowsUnifiedQrCode(context) && !HasAnyChannelState(context))
        {
            return UnifiedInstructionMarkup(context.Texts, wording, isSignIn, lang);
        }

        if (!WordsTabs(context) || wording is InstructionWording.ButtonOnly)
        {
            return HasAnyChannelState(context)
                ? UnifiedInstructionMarkup(context.Texts, wording, isSignIn, lang)
                : MultiChannelInstructionMarkup(context.Texts, wording, isSignIn, lang);
        }

        // One variant per tab, the one of the tab selected on the load on screen: "Any channel" where the page
        // has it, the first channel otherwise. Under "Any channel" the QR code and the buttons of every channel
        // stand together — or the unified QR code alone, where that tab shows no button. A page whose strip is
        // hidden words that state as the page of the unified QR code does: no tab is named there.
        var variants = new List<(string TabElementId, string Markup)>(context.Channels.Count + 1);
        if (HidesTabStrip(context))
        {
            variants.Add((AnyChannelTabElementId, UnifiedInstructionMarkup(context.Texts, wording, isSignIn, lang)));
        }
        else if (HasAnyChannelState(context))
        {
            variants.Add((
                AnyChannelTabElementId,
                wording is InstructionWording.QrAndButton && context.AnyChannelTabShowsChannelButtons
                    ? BuildInstructionText(
                        LocalizeOwn(
                            context.Texts,
                            isSignIn ? AuthPageStrings.AnyChannelInstruction : AuthPageStrings.ConfirmAnyChannelInstruction,
                            lang),
                        null,
                        lang)
                    : UnifiedInstructionMarkup(context.Texts, InstructionWording.QrOnly, isSignIn, lang)));
        }

        for (var i = 0; i < context.Channels.Count; i++)
        {
            variants.Add((
                ChannelTabElementId(i),
                ChannelInstructionMarkup(context, context.Channels[i], wording, lang)));
        }

        var markup = new StringBuilder($"<span class=\"{TabWordingsClass}\">");
        for (var i = 0; i < variants.Count; i++)
        {
            var hiddenClass = i is 0 ? string.Empty : $" {TabWordingHiddenClass}";
            markup.Append(
                $"<span class=\"{TabWordingClass}{hiddenClass}\" {TabWordingAttributeName}=\"{variants[i].TabElementId}\">{variants[i].Markup}</span>");
        }

        return markup.Append("</span>").ToString();
    }

    /// <summary>
    /// Builds a wording that names one channel — the instruction of a page with one channel and of a channel
    /// tab: its brand icon and its name at the slot of the text. The text follows what the panel of the
    /// channel holds under the wording (SPEC-007 UI-121): the QR code where the wording shows QR codes and the
    /// panel has one — on a page with one channel the unified hop QR code too — and the button where the
    /// panel shows one. The button of a messenger and of Email Push obeys the setting of the channel buttons.
    /// The panel of Email Pull with no QR code beside it holds the address form alone — neither a QR code
    /// nor a link into a channel — so it asks for the address, in one text for a sign-in and a confirmation,
    /// whatever the wording. A panel left with nothing — a channel whose QR code could not be drawn, on a
    /// page that hides the channel buttons — has no instruction: no wording would be true of it.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="wording">The wording.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the text.</returns>
    private string ChannelInstructionMarkup(
        AuthPageRenderContext context,
        ChannelDisplayInfo channel,
        InstructionWording wording,
        string lang)
    {
        var isSignIn = IsSignIn(context);
        var showsQrCode = wording is not InstructionWording.ButtonOnly
            && (PanelShowsQrCode(channel, context) || (context.Channels.Count is 1 && ShowsUnifiedQrCode(context)));
        var showsButton = wording is not InstructionWording.QrOnly || !LeadsIntoChannel(channel);

        if (!showsQrCode && !showsButton)
        {
            return string.Empty;
        }

        if (!showsQrCode && !LeadsIntoChannel(channel))
        {
            return EmailFormInstructionMarkup(context.Texts, channel, lang);
        }

        var localized = LocalizeOwn(
            context.Texts,
            (showsQrCode, showsButton) switch
            {
                (true, true) => isSignIn
                    ? AuthPageStrings.SingleChannelInstruction
                    : AuthPageStrings.ConfirmSingleChannelInstruction,
                (true, false) => isSignIn
                    ? AuthPageStrings.SingleChannelInstructionWithoutButton
                    : AuthPageStrings.ConfirmSingleChannelInstructionWithoutButton,
                _ => isSignIn
                    ? AuthPageStrings.SingleChannelInstructionWithoutQr
                    : AuthPageStrings.ConfirmSingleChannelInstructionWithoutQr
            },
            lang);

        return BuildInstructionText(localized, BuildInstructionChannelMarkup(context.Texts, channel, lang), lang);
    }

    /// <summary>
    /// Builds the instruction of the address form of Email: it asks for the address and names what the mail
    /// carries, in one text for a sign-in and a confirmation.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">The channel of Email.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the text.</returns>
    private string EmailFormInstructionMarkup(CorePageTexts texts, ChannelDisplayInfo channel, string lang)
    {
        var instruction = LocalizeOwn(
            texts,
            EmailPullForm.MailOf(channel) switch
            {
                EmailPullForm.Mail.Code => AuthPageStrings.EmailFormInstructionCode,
                EmailPullForm.Mail.LinkAndCode => AuthPageStrings.EmailFormInstructionLinkAndCode,
                _ => AuthPageStrings.EmailFormInstruction
            },
            lang);

        return BuildInstructionText(instruction, null, lang);
    }

    /// <summary>
    /// The channel of Email whose address form a tap opens: the link under the button of the Push section,
    /// where the channel accepts a typed address, and the button of the Pull panel, which a page renders
    /// where there is a choice of channels. Null — the page has no such form: a page without Email, a Push
    /// panel without Pull, a page whose only channel is Email Pull and shows the form straight away.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <param name="hasMultipleChannels">Whether the page offers more than one channel.</param>
    /// <returns>The channel, or null.</returns>
    private static ChannelDisplayInfo? EmailChannelWithFormOnTap(AuthPageRenderContext context, bool hasMultipleChannels) =>
        context.Channels.FirstOrDefault(channel => channel.ChannelType is ChannelTypes.Email
            && (IsEmailPushLink(channel.DeepLinkUrl) ? channel.EmailPullAvailable : hasMultipleChannels));

    /// <summary>
    /// Builds a wording of the unified QR code — one QR code for every channel, beside a button per channel.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="wording">The wording.</param>
    /// <param name="isSignIn">true — the page serves a sign-in.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the text.</returns>
    private string UnifiedInstructionMarkup(CorePageTexts texts, InstructionWording wording, bool isSignIn, string lang)
    {
        var localized = wording switch
        {
            InstructionWording.QrAndButton => LocalizeOwn(
                texts, isSignIn ? AuthPageStrings.Instruction : AuthPageStrings.ConfirmInstruction, lang),
            InstructionWording.QrOnly => LocalizeOwn(
                texts,
                isSignIn ? AuthPageStrings.InstructionWithoutButton : AuthPageStrings.ConfirmInstructionWithoutButton,
                lang),
            _ => LocalizeOwn(
                texts, isSignIn ? AuthPageStrings.InstructionWithoutQr : AuthPageStrings.ConfirmInstructionWithoutQr, lang)
        };

        return BuildInstructionText(localized, null, lang);
    }

    /// <summary>
    /// Builds a wording of a page whose channels, each with a QR code of its own, are on screen at once: the
    /// person chooses one first.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="wording">The wording.</param>
    /// <param name="isSignIn">true — the page serves a sign-in.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the text.</returns>
    private string MultiChannelInstructionMarkup(CorePageTexts texts, InstructionWording wording, bool isSignIn, string lang)
    {
        var localized = wording switch
        {
            InstructionWording.QrAndButton => LocalizeOwn(
                texts,
                isSignIn ? AuthPageStrings.MultiChannelInstruction : AuthPageStrings.ConfirmMultiChannelInstruction,
                lang),
            InstructionWording.QrOnly => LocalizeOwn(
                texts,
                isSignIn
                    ? AuthPageStrings.MultiChannelInstructionWithoutButton
                    : AuthPageStrings.ConfirmMultiChannelInstructionWithoutButton,
                lang),
            _ => LocalizeOwn(
                texts,
                isSignIn
                    ? AuthPageStrings.MultiChannelInstructionWithoutQr
                    : AuthPageStrings.ConfirmMultiChannelInstructionWithoutQr,
                lang)
        };

        return BuildInstructionText(localized, null, lang);
    }

    /// <summary>
    /// Builds one wording of the renderer's own instruction: its ready markup under the classes the
    /// stylesheet shows and hides it by.
    /// </summary>
    /// <param name="markup">Ready markup of the wording (<see cref="BuildInstructionText"/>).</param>
    /// <param name="conditionClasses">Classes the stylesheet shows and hides the wording by.</param>
    /// <returns>Ready markup of the wording.</returns>
    private static string BuildInstructionWording(string markup, string conditionClasses)
    {
        return $"<span class=\"{conditionClasses}\">{markup}</span>";
    }

    /// <summary>
    /// Builds the text of one wording of the renderer's own instruction. The wording is a text an owning
    /// level may override, so it is judged like any other text of the page before the channel is put into
    /// it — and the channel replaces the slot only afterwards: the markup of the channel is the renderer's
    /// own and would not pass a check meant for the integrator's. The slot is replaced as a literal,
    /// not as a format item, so a wording without it, or with braces of its own, is emitted as it is.
    /// </summary>
    /// <param name="localized">Localized text of the wording.</param>
    /// <param name="channelMarkup">Ready markup of the channel the wording names; null — it names none.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the text.</returns>
    private string BuildInstructionText(
        string localized,
        string? channelMarkup,
        string lang)
    {
        var wording = BuildInlineMarkup(
            localized,
            InstructionElementName,
            lang,
            out _,
            channelMarkup is null ? null : InstructionChannelSlot);

        // Whichever form the wording took, its slot now stands in text: an admitted value carries it
        // outside every tag, and a refused one is encoded whole and has no tag left.
        if (channelMarkup is not null)
        {
            wording = wording.Replace(InstructionChannelSlot, channelMarkup, StringComparison.Ordinal);
        }

        return wording;
    }

    /// <summary>
    /// Builds the channel as the instruction names it: the brand icon of its tab and its name — the
    /// same two the tab shows. A channel without a glyph is named by its name alone.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">Channel display data.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup of the channel.</returns>
    private string BuildInstructionChannelMarkup(CorePageTexts texts, ChannelDisplayInfo channel, string lang)
    {
        return $"<span class=\"{InstructionChannelClass}\">{ChannelNameIcon(channel)}{HtmlEncode(ChannelName(texts, channel, lang))}</span>";
    }

    /// <summary>
    /// The address the email form of the page posts to. The address field stands in a real form — a named
    /// field of a form is what a browser offers its remembered addresses for — while the page script
    /// still sends the request itself and keeps the page. The form states its method and its target
    /// all the same, with the session as a hidden field: a page whose script did not run then posts the
    /// address to the start of Email Pull instead of putting it into the URL of this page.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The address, path base included.</returns>
    private static string EmailStartUrl(AuthPageRenderContext context) =>
        context.PathBase + EmailEndpointPaths.Start;

    /// <summary>
    /// The hidden step of the code of the email form (<see cref="EmailPullForm.CodeStepMarkup"/>) for a mail
    /// that carries a code; empty for a mail of the magic link alone — the form then never asks for a code.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="mail">What the mail of the form carries.</param>
    /// <param name="lang">Language code.</param>
    /// <returns>The markup, or empty.</returns>
    private string EmailCodeStep(CorePageTexts texts, EmailPullForm.Mail mail, string lang) =>
        mail is EmailPullForm.Mail.Link
            ? string.Empty
            : EmailPullForm.CodeStepMarkup(
                LocalizeOwn(texts, AuthPageStrings.EmailCodeLabel, lang),
                LocalizeOwn(texts, AuthPageStrings.EmailCodeConfirm, lang),
                LocalizeOwn(texts, AuthPageStrings.EmailSendAgain, lang));

    /// <summary>
    /// Builds the Email Push-mode HTML panel: QR code (where the page shows channel QR codes),
    /// "Open mail client" button, and — where the channel accepts a typed address — an embedded Pull form
    /// as a fallback (toggled on click, without a separate page).
    /// The Push section (#veriqa-push-section) and Pull section (#veriqa-pull-section) are toggled by JS.
    /// With Pull off the fallback is left out whole: the divider, its link and the form.
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">Channel data with the QR code and the <c>mailto:</c> deep link of the sign-in mail.</param>
    /// <param name="sessionId">Transaction identifier for the Pull form (data-session-id).</param>
    /// <param name="startUrl">Address the Pull form posts to, path base included.</param>
    /// <param name="lang">Language code.</param>
    /// <param name="withQr">Whether the Push section carries the QR code of the channel.</param>
    /// <param name="linkAttributes">
    /// Ready markup of the further attributes of the mail-client link (leading space included): the one that
    /// makes a tap on it send the signal of the chosen channel, and the one naming the channel whose heading a
    /// tap shows on the scanner page; empty — the link carries neither.
    /// </param>
    /// <param name="hopQrEntry">
    /// Entry the page replaces the QR code of the channel by before its hop token expires; null — the QR code
    /// is not replaced.
    /// </param>
    /// <param name="split">
    /// Whether the Push section stands on the split card as its QR code beside the rest of it, and the email
    /// form as one column in the middle; false — the sections are laid out as on any other card.
    /// </param>
    /// <param name="isSignIn">Whether the transaction is a sign-in: the link to the email form names the act.</param>
    private void AppendEmailPushPanel(
        StringBuilder sb,
        CorePageTexts texts,
        ChannelDisplayInfo channel,
        string sessionId,
        string startUrl,
        string lang,
        bool withQr,
        string linkAttributes,
        string? hopQrEntry,
        bool split,
        bool isSignIn)
    {
        // The method renders the Push section (QR + button) and the hidden Pull section (email entry).
        // INVARIANT: exactly one email panel is rendered on the page (Push OR Pull —
        // see the branching by IsEmailPushLink in AppendContainer). Both panels use identical
        // ids (veriqa-email-input/submit/msg) — a joint render would produce duplicate ids
        // and break getElementById (review feedback).
        var qrAlt = LocalizeOwn(texts, AuthPageStrings.QrCodeAlt, lang);
        var btnText = LocalizeOwn(texts, AuthPageStrings.SendConfirmationEmail, lang);
        var orText = LocalizeOwn(texts, AuthPageStrings.OrDivider, lang);
        // The link to the form and the button of the form name what the mail carries (SPEC-016 §4): the user
        // is given one button, and the integrator chooses what stands behind it.
        var mail = EmailPullForm.MailOf(channel);
        var fallbackText = LocalizeOwn(
            texts,
            (mail, isSignIn) switch
            {
                (EmailPullForm.Mail.Code, true) => AuthPageStrings.EmailLoginByCode,
                (EmailPullForm.Mail.Code, false) => AuthPageStrings.EmailConfirmByCode,
                (EmailPullForm.Mail.LinkAndCode, true) => AuthPageStrings.EmailLoginByMail,
                (EmailPullForm.Mail.LinkAndCode, false) => AuthPageStrings.EmailConfirmByMail,
                (_, true) => AuthPageStrings.EmailLoginByLink,
                _ => AuthPageStrings.EmailConfirmByLink
            },
            lang);
        // The back link names the QR code, which this very page may be hiding — so it travels as a
        // pair of wordings the page chooses between by the QR code. One button (the ids are an invariant here), two
        // spans inside it: the hidden one leaves the accessible name with it. A section without a QR
        // code of its own — the scanner page, a page with the unified QR code — takes the wording without it.
        var backMarkup = withQr
            ? BuildQrDependentText(
                LocalizeOwn(texts, AuthPageStrings.EmailBackToQr, lang),
                LocalizeOwn(texts, AuthPageStrings.EmailBackWithoutQr, lang))
            : HtmlEncode(LocalizeOwn(texts, AuthPageStrings.EmailBackWithoutQr, lang));
        var inputLabel = LocalizeOwn(texts, AuthPageStrings.EmailInputLabel, lang);
        var submitText = LocalizeOwn(
            texts,
            mail switch
            {
                EmailPullForm.Mail.Code => AuthPageStrings.EmailSendCode,
                EmailPullForm.Mail.LinkAndCode => AuthPageStrings.EmailSendMail,
                _ => AuthPageStrings.EmailSendLink
            },
            lang);
        var placeholder = AuthPageStrings.EmailInputPlaceholder;
        // Envelope icon in the mail-client CTA (color — button text via currentColor)
        var emailBtnIcon = ChannelIconSvg.Render(ChannelTypes.Email, "veriqa-btn-icon");

        // On the split card the Push section is the pair — its QR code, and beside it the button with the
        // fallback link — and the Pull section, which has no QR code, is one column in the middle.
        sb.AppendLine(split
            ? $"        <div id=\"veriqa-push-section\" class=\"{QrPairClass}\">"
            : "        <div id=\"veriqa-push-section\">");
        if (withQr)
        {
            AppendQrBlock(sb, channel.QrCodeBase64, qrAlt, hopQrEntry);
        }

        if (split)
        {
            sb.AppendLine($"            <div class=\"{PanelSideClass}\">");
        }

        // The end of the Push section, the column beside its QR code included.
        var pushSectionEnd = split ? "            </div>\n        </div>" : "        </div>";

        sb.AppendLine($$"""
                        <a href="{{HtmlEncode(channel.DeepLinkUrl)}}" class="veriqa-btn veriqa-btn-channel veriqa-btn-email" target="_blank" rel="noopener noreferrer"{{linkAttributes}}>
                            {{emailBtnIcon}}{{HtmlEncode(btnText)}}
                        </a>
            """);

        // A form that leads to a start refusing every request is a way to sign in that cannot work:
        // without Pull the panel ends at the mail-client button.
        if (!channel.EmailPullAvailable)
        {
            sb.AppendLine(pushSectionEnd);
            return;
        }

        var pullSectionClass = split ? $" class=\"{SplitSingleClass}\"" : string.Empty;

        sb.AppendLine($$"""
                        <div class="veriqa-divider">{{HtmlEncode(orText)}}</div>
                        <button class="veriqa-text-link" id="veriqa-show-pull" type="button">{{HtmlEncode(fallbackText)}}</button>
            {{pushSectionEnd}}
                    <div id="veriqa-pull-section"{{pullSectionClass}} style="display:none;">
                        <button class="veriqa-text-link" id="veriqa-show-push" type="button">{{backMarkup}}</button>
                        <form class="veriqa-email-form" method="post" action="{{HtmlEncode(startUrl)}}">
                            <input type="hidden" name="{{OidcConstants.SessionIdParameterName}}" value="{{HtmlEncode(sessionId)}}" />
                            <input
                                class="veriqa-email-input"
                                id="veriqa-email-input"
                                type="email"
                                name="{{EmailFieldName}}"
                                autocomplete="email"
                                placeholder="{{HtmlEncode(placeholder)}}"
                                aria-label="{{HtmlEncode(inputLabel)}}"
                                required
                            />
                            <button
                                class="veriqa-btn"
                                id="veriqa-email-submit"
                                type="submit"
                                data-session-id="{{HtmlEncode(sessionId)}}"
                            >
                                {{HtmlEncode(submitText)}}
                            </button>
            {{EmailCodeStep(texts, mail, lang)}}
                            <div class="veriqa-email-msg" id="veriqa-email-msg" role="status" aria-live="polite"></div>
                        </form>
                    </div>
            """);
    }

    /// <summary>
    /// Builds the Email-channel HTML panel with an email-address entry form.
    /// <para>
    /// Email Pull has no addressable point to send the user to, so in the buttons layout its button
    /// reveals the form in place instead of leading anywhere (SPEC-025 HOP-145) — the same form, not a
    /// second one. The button is rendered directly ahead of the form, collapsed; the stylesheet shows
    /// it, and hides the form behind it, in the buttons layout only, so the tab layout keeps the form
    /// standing in its panel as it always has.
    /// </para>
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="sessionId">Session identifier to send together with the email.</param>
    /// <param name="startUrl">Address the form posts to, path base included.</param>
    /// <param name="lang">Language code.</param>
    /// <param name="mail">What the mail of the form carries: the button of the form names it.</param>
    /// <param name="withRevealButton">Whether the form gets the button that reveals it.</param>
    /// <param name="isSignIn">Whether the transaction is a sign-in: the button that reveals the form names the act.</param>
    private void AppendEmailPanel(
        StringBuilder sb,
        CorePageTexts texts,
        string sessionId,
        string startUrl,
        string lang,
        EmailPullForm.Mail mail,
        bool withRevealButton,
        bool isSignIn)
    {
        // The method renders the email-address entry form with a "Get link" button.
        // INVARIANT: exactly one email panel per page — the ids match AppendEmailPushPanel
        // (see the comment there, review feedback).

        var inputLabel = LocalizeOwn(texts, AuthPageStrings.EmailInputLabel, lang);
        var btnText = LocalizeOwn(
            texts,
            mail switch
            {
                EmailPullForm.Mail.Code => AuthPageStrings.EmailSendCode,
                EmailPullForm.Mail.LinkAndCode => AuthPageStrings.EmailSendMail,
                _ => AuthPageStrings.EmailSendLink
            },
            lang);
        // The placeholder is not localized — the email format is the same for all languages
        var placeholder = AuthPageStrings.EmailInputPlaceholder;

        if (withRevealButton)
        {
            // The channel button of Email — the button primitive with the channel modifier and the
            // channel icon, like every other channel button, and worded by the act as they are
            // (SPEC-007 UI-121). A disclosure: aria-expanded is its state, the script flips it and the
            // stylesheet reads it.
            var revealText = LocalizeOwn(
                texts, isSignIn ? AuthPageStrings.SignInWithEmail : AuthPageStrings.ConfirmWithEmail, lang);
            var revealIcon = ChannelIconSvg.Render(ChannelTypes.Email, "veriqa-btn-icon");
            sb.AppendLine(
                $"            <button class=\"veriqa-btn veriqa-btn-channel veriqa-btn-email veriqa-email-reveal\" id=\"veriqa-email-reveal\" type=\"button\" aria-expanded=\"false\">{revealIcon}{HtmlEncode(revealText)}</button>");
        }

        sb.AppendLine($$"""
                    <form class="veriqa-email-form" method="post" action="{{HtmlEncode(startUrl)}}">
                        <input type="hidden" name="{{OidcConstants.SessionIdParameterName}}" value="{{HtmlEncode(sessionId)}}" />
                        <input
                            class="veriqa-email-input"
                            id="veriqa-email-input"
                            type="email"
                            name="{{EmailFieldName}}"
                            autocomplete="email"
                            placeholder="{{HtmlEncode(placeholder)}}"
                            aria-label="{{HtmlEncode(inputLabel)}}"
                            required
                        />
                        <button
                            class="veriqa-btn"
                            id="veriqa-email-submit"
                            type="submit"
                            data-session-id="{{HtmlEncode(sessionId)}}"
                        >
                            {{HtmlEncode(btnText)}}
                        </button>
            {{EmailCodeStep(texts, mail, lang)}}
                        <div class="veriqa-email-msg" id="veriqa-email-msg" role="status" aria-live="polite"></div>
                    </form>
            """);
    }

    /// <summary>
    /// Builds the script part of the window: the configuration of THIS request, the SignalR client, the
    /// drawing function of the countdown (<see cref="PageCountdown"/>), the reaction of the status block
    /// (<see cref="PageStatusBlock"/>) and the window's own script (the connection, the countdown, the
    /// channel tabs, the email form).
    /// <para>
    /// The script itself is not written here — it lives in its own file and arrives as an embedded
    /// resource (<see cref="AuthPageScript"/>). What this method composes is everything the request
    /// decides: the values are collected into one <see cref="AuthPageScriptConfig"/> and handed over
    /// in a JSON data block, so the script stays free of server substitutions and the page keeps the
    /// shape it had — inline scripts under the page's nonce, no extra request (SPEC-015 §1).
    /// </para>
    /// </summary>
    private void AppendScript(
        StringBuilder sb,
        AuthPageRenderContext context,
        string lang,
        AuthPageDesignOptions design)
    {
        // The statuses the script shows are texts of the window like any other, so the text overrides of
        // the page reach them the same way.
        var texts = context.Texts;
        var msgConfirmed = LocalizeOwn(texts, AuthPageStrings.ConfirmedProcessing, lang);

        // The four terminal lines: the caller's wording where it stated one, this renderer's
        // sign-in wording where it did not (SPEC-039 R51). A refusal by the user is told from a fault
        // by the reason code on every page, a sign-in included: only the words differ by the
        // transaction, not whether a refusal reads as one.
        var msgSuccess = Stated(texts, context.TerminalConfirmedText, AuthPageStrings.LoginSuccess, lang);
        var msgExpired = Stated(texts, context.TerminalExpiredText, AuthPageStrings.SessionExpired, lang);
        var msgDeclined = Stated(texts, context.TerminalDeclinedText, AuthPageStrings.SignInDeclined, lang);
        var msgError = Stated(texts, context.TerminalErrorText, AuthPageStrings.LoginError, lang);
        var msgEmailSent = LocalizeOwn(texts, AuthPageStrings.EmailSentMessage, lang);

        // What the mail of the email form of the page carries — one Email channel at most on a page. A mail
        // with a code is named as the mail by the line of a failed sending, and only such a form gets the step
        // of the code.
        var emailMail = context.Channels.FirstOrDefault(channel => channel.ChannelType is ChannelTypes.Email) is { } emailChannel
            ? EmailPullForm.MailOf(emailChannel)
            : EmailPullForm.Mail.Link;
        var msgEmailError = LocalizeOwn(
            texts,
            emailMail is EmailPullForm.Mail.Link ? AuthPageStrings.EmailSendError : AuthPageStrings.EmailSendMailError,
            lang);
        var msgEmailSendAgain = LocalizeOwn(texts, AuthPageStrings.EmailSendAgain, lang);
        var msgEmailOpened = LocalizeOwn(
            texts, IsSignIn(context) ? AuthPageStrings.EmailLinkOpened : AuthPageStrings.EmailLinkOpenedConfirmation, lang);
        var msgEmailReturnWait = LocalizeOwn(texts, AuthPageStrings.EmailReturnWait, lang);
        var msgEmailReceived = LocalizeOwn(texts, AuthPageStrings.EmailMailReceived, lang);
        var msgEmailVerified = LocalizeOwn(
            texts, IsSignIn(context) ? AuthPageStrings.EmailSenderVerified : AuthPageStrings.EmailSenderVerifiedConfirmation, lang);
        var msgHopScanned = LocalizeOwn(texts, AuthPageStrings.HopScanned, lang);
        var msgHopInterstitialOpened = LocalizeOwn(texts, AuthPageStrings.HopInterstitialOpened, lang);
        var msgClaimFormOnPhone = LocalizeOwn(texts, AuthPageStrings.ClaimFormOnPhone, lang);
        var msgHopReopened = LocalizeOwn(texts, AuthPageStrings.HopReopened, lang);

        // Names of the page's channels for a hint naming the channel being opened — by the rule the
        // tabs are named with, so the hint and the tab never call one channel two names. An indexer
        // rather than Add: a channel type repeated in the set names the same channel twice.
        var channelNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var channel in context.Channels)
        {
            channelNames[channel.ChannelType] = ChannelName(texts, channel, lang);
        }

        // The lines of the channels of the page arrive ready, resolved by the caller over the levels of the
        // transaction: the line once the answer is awaited in the channel, the line of the scan of its QR
        // code and the line once the transition into it began. A sign-in and a confirmation have them alike —
        // only the wording of the act follows the transaction. A line the caller could not resolve is left
        // out, and an event of that channel then takes the line without a channel of the maps below.
        var channelAnswerLines = new Dictionary<string, string>(StringComparer.Ordinal);
        var scannedLines = new Dictionary<string, string>(StringComparer.Ordinal);
        var launchedLines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var channel in context.Channels)
        {
            if (!context.ChannelLines.TryGetValue(channel.ChannelType, out var lines))
            {
                continue;
            }

            AddLine(channelAnswerLines, channel.ChannelType, lines.Answer);
            AddLine(scannedLines, channel.ChannelType, lines.Scanned);
            AddLine(launchedLines, channel.ChannelType, lines.Launched);
        }

        // A verified sender the transaction refuses, because it is bound to the subject of another channel:
        // the line names the transaction the window is for.
        var isSignIn = IsSignIn(context);
        var msgEmailSubjectMismatch = LocalizeOwn(
            texts,
            isSignIn ? AuthPageStrings.EmailSubjectMismatchSignIn : AuthPageStrings.EmailSubjectMismatch,
            lang);

        // The page's own tap on the mail-client button tells what to do with the opened mail: the line of the
        // start of the transition into the Email channel, the one any page shows when it hears that start.
        var emailMailOpened = launchedLines.GetValueOrDefault(ChannelTypes.Email, string.Empty);

        // The messages of the start by a code the page shows, by the channel type their buttons carry.
        // They reach the script as data of the page and are not written into the markup of a button.
        var startMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var channel in context.Channels)
        {
            if ((ShowsStartCodeBlock(channel) || ShowsSpareStartCodeBlock(channel))
                && channel.StartMessage is { } startMessage)
            {
                startMessages[channel.ChannelType] = startMessage;
            }
        }

        // The remainder in milliseconds, as an invariant integer: the page adds it to its own clock
        // reading, so the number crossing into the script must not depend on the culture that formats
        // it. Rounded down — a countdown may not promise a second the transaction does not have.
        var remainingMs = (long)Math.Floor(Math.Max(0, context.RemainingSeconds) * 1000);

        // CSP nonce attribute for inline scripts (SPEC-007 §6.3)
        var nonceAttr = BuildNonceAttribute(context);

        // Path to the SignalR client: a configurable override or the bundled resource by default —
        // the rule every page connecting to the hub shares.
        var signalRClientPath = SignalRClientLocation.Resolve(design.SignalRClientPath, context.PathBase);

        // Status fallback-polling URL and the deterministic callback (the same one SignalR sends
        // in RedirectUrl on Completed — built from session_id, carries no sensitive data).
        // The values are root-relative: the PathBase prefix is added by the client helper withBase()
        // (a single normalization point, also for URLs from SignalR messages).
        var statusPollUrl = Endpoints.TransactionStatusEndpoint.BuildStatusPath(context.SessionId);

        // A page rendered without a navigation target does not get one built: its transaction has no
        // browser callback, and the callback of the sign-in path is not a place it may be sent to.
        var callbackUrl = context.NavigatesAway
            ? $"{OidcEndpoints.AuthorizeCallback}?{OidcConstants.SessionIdParameterName}={Uri.EscapeDataString(context.SessionId)}"
            : string.Empty;

        // The one destination such a page does have: the core page that asks the confirming question
        // (SPEC-039 E29). It is taken as the caller stated it and only in the mode without callback
        // navigation — on the sign-in path the question is asked past the callback, and a second
        // destination there would be a second way out of the window.
        var confirmationQuestionUrl = context.NavigatesAway
            ? string.Empty
            : context.ConfirmationQuestionUrl ?? string.Empty;

        var config = new AuthPageScriptConfig
        {
            SessionId = context.SessionId,
            BasePath = context.PathBase,
            RemainingMs = remainingMs,
            PollIntervalMs = PollingIntervalMs,
            NavigatesAway = context.NavigatesAway,
            StatusUrl = statusPollUrl,
            CallbackUrl = callbackUrl,
            ConfirmUrl = confirmationQuestionUrl,
            EmailStartUrl = EmailEndpointPaths.Start,
            EmailCode = emailMail is EmailPullForm.Mail.Link
                ? null
                : new AuthPageScriptEmailCode(
                    Url: EmailEndpointPaths.Code,
                    CodeSent: LocalizeOwn(texts, AuthPageStrings.EmailCodeSent, lang),
                    AddressSlot: EmailCodeAddressSlot,
                    CheckError: LocalizeOwn(texts, AuthPageStrings.EmailCodeCheckError, lang),
                    Length: ClaimEmailCode.Length),
            PageStateAttribute = PageStateAttributeName,
            ExpiredPageState = ExpiredPageStateValue,
            FailedPageState = FailedPageStateValue,
            ButtonsLayoutSelector = ButtonsLayoutSelector,
            ChannelsElementId = ChannelsElementId,
            PanelElementIdPrefix = PanelElementIdPrefix,
            RememberChannel = RememberChannelOf(context, lang),
            AnyChannelTab = HasAnyChannelState(context)
                ? new AuthPageScriptAnyChannelTab(
                    TabElementId: AnyChannelTabElementId,
                    StateClass: AnyChannelStateClass)
                : null,
            TabWordings = WordsTabs(context)
                ? new AuthPageScriptTabWordings(
                    WordingClass: TabWordingClass,
                    HiddenClass: TabWordingHiddenClass,
                    TabAttribute: TabWordingAttributeName)
                : null,
            HopStartedAttribute = HopStartedAttributeName,
            EmailFormOpenAttribute = EmailFormOpenAttributeName,
            HopInstructionElementId = HopInstructionElementId,
            HopChooseAnotherElementId = HopChooseAnotherElementId,
            HopInstructionDisabled = context.IsScannerPage,
            ChosenChannel = MarksChosenChannel(context)
                ? new AuthPageScriptChosenChannel(
                    StateAttribute: ChannelChosenAttributeName,
                    ChannelAttribute: ContinueChannelAttributeName,
                    HeadingsElementId: ChosenHeadingsElementId,
                    BackElementId: ChosenBackElementId)
                : null,
            CancelFormElementId = CancelFormElementId,
            HopReopened = new AuthPageScriptHopReopened(
                StatusCode: HopChannelStatusKeys.Reopened,
                ElementId: HopReopenedElementId,
                BadgeClass: HopReopenedWarning.BadgeClass),
            HopStatusCodes = HopChannelStatusKeys.All,
            HopLaunchCodes = new AuthPageScriptHopLaunchCodes(
                InterstitialOpened: HopChannelStatusKeys.InterstitialOpened,
                ChannelLaunched: HopChannelStatusKeys.ChannelLaunched),
            // A page that does not leave ends on its success line, whatever the type of its transaction.
            CompletedPageState = context.NavigatesAway ? string.Empty : CompletedPageStateValue,
            AnswerElsewherePageState = AnswerElsewherePageStateValue,
            // Only the page with nowhere to go on AwaitingWebConfirmation gets the line sending the person
            // back to the window (SPEC-025 HOP-186, HOP-179): every other page leaves for the question.
            ReturnToSignInWindowText = SendsBackToSignInWindow(context)
                ? LocalizeOwn(texts, AuthPageStrings.ReturnToSignInWindow, lang)
                : string.Empty,
            AnswerInLinkBrowserText = msgEmailOpened,
            HopQrReissue = HopQrReissueOf(context),
            ChannelLaunch = SendsChannelLaunchedSignal(context)
                ? new AuthPageScriptChannelLaunch(
                    Attribute: ChannelLaunchAttributeName,
                    SignalField: HopClientSignal.SignalField,
                    ChannelField: HopClientSignal.ChannelField,
                    Signal: HopChannelStatusKeys.ChannelLaunched)
                : null,
            EmailMailOpenedText = emailMailOpened,
            EmailReturnWaitText = msgEmailReturnWait,
            HopTransition = HopLaunchTargetOf(context) is null
                ? null
                : new AuthPageScriptHopTransition(
                    EventName: HopInterstitialScript.TransitionEventName,
                    ReturnedField: HopInterstitialScript.TransitionReturnedField,
                    PressEventName: HopInterstitialScript.PressEventName),
            StartMessage = startMessages.Count > 0
                ? ChannelStartCodeBlock.ScriptSettings(_promptLocalizer, texts, startMessages, lang)
                : null,
            SpareCode = ShowsAnySpareStartCodeBlock(context)
                ? new AuthPageScriptSpareCode(
                    Selector: "." + SpareCodeClass,
                    ChannelAttribute: SpareCodeChannelAttributeName,
                    OpenClass: SpareCodeOpenClass,
                    ChannelButtonSelector: ChannelButtonSelector,
                    IdleDelayMs: SpareStartCodeDelays.IdleMilliseconds,
                    AfterTransitionDelayMs: SpareStartCodeDelays.AfterTransitionMilliseconds,
                    PhoneByPage: context.IsScannerPage,
                    DeviceAttribute: DeviceAttributeName,
                    PhoneDeviceValue: MobileDeviceValue,
                    HopQrChannels: HopQrChannelsOf(context),
                    ReturnElementId: SpareReturnElementId,
                    ReturnLinkElementId: SpareReturnLinkElementId)
                : null,
            HopChannelHints = HopChannelHintIds(context),
            ChannelAnswer = new AuthPageScriptChannelAnswer(
                StatusCode: ChannelAnswerStatuses.ChannelAnswered,
                Lines: channelAnswerLines,
                AnsweredChannel: context.AnsweredChannelType),
            DeclinedReasonCode = TransactionErrorCodes.DeclinedByUser,
            SupersededReasonCode = TransactionErrorCodes.SupersededByNewSignIn,
            // Named arguments: these carry same-typed values whose ORDER is the only thing telling
            // them apart, and a silent swap of two texts is exactly the defect this page cannot show
            // in a test — it renders, it just says the wrong thing.
            Statuses = new AuthPageScriptStatuses(
                Confirmed: TransactionStatusNames.Confirmed,
                AwaitingWebConfirmation: TransactionStatusNames.AwaitingWebConfirmation,
                ClaimCompletion: TransactionStatusNames.ClaimCompletion,
                Completed: TransactionStatusNames.Completed,
                Expired: TransactionStatusNames.Expired,
                Failed: TransactionStatusNames.Failed),
            // The member names, spelled by ToString() as the status message and the status surface
            // spell them — the one source of the values the page compares.
            ClaimCompletion = new AuthPageScriptClaimCompletion(
                PhoneStep: ClaimCompletionStep.Phone.ToString(),
                InitiatingPageSurface: WebStepSurface.InitiatingPage.ToString(),
                CrossDevicePageSurface: WebStepSurface.CrossDevicePage.ToString()),
            Texts = new AuthPageScriptTexts(
                Confirmed: msgConfirmed,
                Success: msgSuccess,
                Expired: msgExpired,
                Error: msgError,
                Declined: msgDeclined,
                EmailSent: msgEmailSent,
                EmailError: msgEmailError,
                EmailSendAgain: msgEmailSendAgain,
                HopReopened: msgHopReopened),
            Hub = new AuthPageScriptHub(
                Path: SignalRConstants.AuthHubPath,
                StatusMethod: SignalRConstants.OnStatusChangedMethod,
                ChannelStatusMethod: SignalRConstants.OnChannelStatusChangedMethod,
                JoinMethod: SignalRConstants.JoinTransactionMethod,
                ReissueHopQrMethod: SignalRConstants.ReissueHopQrMethod),
            // ONE map for every channel (SPEC-025 HOP-161): the event is not filtered by its channel
            // type, and the codes of the families (Email, hop, claims completion) never share a name.
            ChannelStatusTexts = StatusTexts(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [EmailChannelStatusKeys.PullOpened] = msgEmailOpened,
                    [EmailChannelStatusKeys.PushMailReceived] = msgEmailReceived,
                    [EmailChannelStatusKeys.PushVerified] = msgEmailVerified,
                    [EmailChannelStatusKeys.PushSubjectMismatch] = msgEmailSubjectMismatch,
                    [HopChannelStatusKeys.Scanned] = msgHopScanned,
                    [HopChannelStatusKeys.InterstitialOpened] = msgHopInterstitialOpened,
                    [ClaimCompletionChannelStatusKeys.FormOnPhone] = msgClaimFormOnPhone
                },
                context.FallbackChannelLaunchedLine),
            // The lines worded by the channel of the event, each the line the caller resolved for that
            // channel of the page — the start of the transition into the Email channel among them, the same
            // line the page carrying the mail-client button shows on its own tap.
            ChannelStatusTextsByChannel = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                [HopChannelStatusKeys.Scanned] = scannedLines,
                [HopChannelStatusKeys.ChannelLaunched] = launchedLines
            },
            ChannelNames = channelNames
        };

        // Everything of the request reaches the script through ONE data block, placed ahead of it.
        // A block of type application/json is not executed, so no value of the transaction is ever
        // parsed as code, and each value is encoded for exactly one context instead of being spliced
        // into a JS string literal of its own.
        sb.Append($$"""
                <script type="application/json" id="{{AuthPageScript.ConfigElementId}}"{{nonceAttr}}>{{AuthPageScript.SerializeConfig(config)}}</script>
                <script src="{{HtmlEncode(signalRClientPath)}}"{{nonceAttr}}></script>
                <script{{nonceAttr}}>{{PageCountdown.Js}}</script>
                <script{{nonceAttr}}>{{PageStatusBlock.Js}}</script>

            """);

        // The copying function of the block of the start by a code, ahead of the script that calls it
        // and only on a page that shows the block.
        if (config.StartMessage is not null)
        {
            sb.Append($"    <script{nonceAttr}>{ChannelStartCodeBlock.Js}</script>\n");
        }

        // The script of the action under the outcome binds its button, whatever the page script does.
        if (context.OffersOutcomeClose)
        {
            sb.Append($"    {CorePageCloseAction.Script(context.CspNonce)}\n");
        }

        sb.Append($"    <script{nonceAttr}>\n");

        // The script itself: inlined from its own file, never served over a URL (SPEC-015 §1), and
        // carrying the page's nonce like every other inline script of the window (SPEC-007 §6.3).
        sb.Append(AuthPageScript.Js);
        sb.Append("        </script>");

        // The automatic launch of the scanner page of one channel (SPEC-025 HOP-195): the script of the
        // launch page, after the script of the page so that the announcement of the transition finds its
        // listener there. It sends no channel_launched — the script of the page reports the transition as a
        // tap on the mail-client button — and carries the signal of the opened page with the fingerprint
        // when that is collected (HOP-090), the launch waiting for it to be queued (HOP-066c).
        var launchTarget = HopLaunchTargetOf(context);
        if (launchTarget is not null)
        {
            var signalScript = context.CollectsHopFingerprint ? HopSignalScript.FingerprintOnly : HopSignalScript.None;
            sb.Append($"\n    <script{nonceAttr}>{HopInterstitialScript.Build(launchTarget, signalScript)}</script>");
        }
        else if (context.IsScannerPage && context.CollectsHopFingerprint)
        {
            // The browser fingerprint of the scanner page (SPEC-025 HOP-090): a script of its own, after the
            // script of the page, so that neither waits for the other and a tap on a channel button is never
            // held by the computation.
            sb.Append($"\n    <script{nonceAttr}>{HopInterstitialScript.BuildFingerprintOnly()}</script>");
        }

        // Inclusion of custom JavaScript (SPEC-007 UI-055)
        if (!string.IsNullOrEmpty(design.CustomJsPath) && CorePageHead.IsAllowedUrlScheme(design.CustomJsPath))
        {
            var sriAttr = !string.IsNullOrEmpty(design.JsSriHash)
                ? $" integrity=\"{HtmlEncode(design.JsSriHash)}\" crossorigin=\"anonymous\""
                : string.Empty;
            sb.Append($"\n        <script src=\"{HtmlEncode(design.CustomJsPath)}\"{sriAttr}{nonceAttr}></script>");
        }
    }

    /// <summary>
    /// Adds the line of a channel to a map of the script, unless the caller could not resolve it: an empty
    /// line would stand in the way of the line without a channel.
    /// </summary>
    /// <param name="lines">Map of the script, by channel type.</param>
    /// <param name="channelType">Channel type.</param>
    /// <param name="line">The line; empty — none.</param>
    private static void AddLine(Dictionary<string, string> lines, string channelType, string line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            lines[channelType] = line;
        }
    }

    /// <summary>
    /// The lines of the channel statuses that do not depend on the channel of the event, with the line of a
    /// begun transition into a channel outside the page when the caller resolved one: without it that status
    /// is left out of the map, and an event of it leaves the status line alone.
    /// </summary>
    /// <param name="statusLines">The lines of the other statuses.</param>
    /// <param name="channelLaunched">Line of a begun transition, with the slot of the name; null — none.</param>
    /// <returns>The map of the script.</returns>
    private static Dictionary<string, string> StatusTexts(Dictionary<string, string> statusLines, string? channelLaunched)
    {
        if (!string.IsNullOrEmpty(channelLaunched))
        {
            statusLines[HopChannelStatusKeys.ChannelLaunched] = channelLaunched;
        }

        return statusLines;
    }

    /// <summary>
    /// Tells whether the panel of a channel shows the block of the start by a code where its button would
    /// stand: the channel starts by a message and its link carries no transaction. The Email panels are
    /// built on their own path and never show it.
    /// </summary>
    /// <param name="channel">Channel display data.</param>
    /// <returns>true — the panel shows the block.</returns>
    private static bool ShowsStartCodeBlock(ChannelDisplayInfo channel)
    {
        return channel.StartsByMessageOnly && channel.ChannelType is not ChannelTypes.Email;
    }

    /// <summary>
    /// Tells whether the panel of a channel shows the spare form of the block under its button: the link
    /// of the channel carries the transaction, and the channel also has a message with a code to start by
    /// when the link did not deliver. A channel whose code could not be issued has no message and keeps
    /// its panel as it is. The Email panels are built on their own path and never show it.
    /// </summary>
    /// <param name="channel">Channel display data.</param>
    /// <returns>true — the panel shows the spare form of the block.</returns>
    private static bool ShowsSpareStartCodeBlock(ChannelDisplayInfo channel)
    {
        return channel is { StartMessage: not null, StartMessageIsFallback: true }
            && channel.ChannelType is not ChannelTypes.Email;
    }

    /// <summary>
    /// Tells whether any panel of the page shows the block of the start by a code, in either form — the
    /// condition its rules and its copying function go out by.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true — at least one panel shows the block.</returns>
    private static bool ShowsAnyStartCodeBlock(AuthPageRenderContext context)
    {
        return context.Channels.Any(channel => ShowsStartCodeBlock(channel) || ShowsSpareStartCodeBlock(channel));
    }

    /// <summary>
    /// Appends the spare form of the block of one channel (<see cref="ChannelStartCodeBlock"/>): the
    /// caption telling where to send the code, the message and the button that copies it.
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">Channel display data.</param>
    /// <param name="lang">Content language of the page.</param>
    private void AppendSpareStartCodeBlock(
        StringBuilder sb,
        CorePageTexts texts,
        ChannelDisplayInfo channel,
        string lang)
    {
        sb.AppendLine(SpareStartCodeMarkup(texts, channel, lang));
    }

    /// <summary>
    /// Builds the spare form of the block of one channel inside the element the page script shows it by:
    /// the element names the channel, and the stylesheet keeps it out of sight until the script opens it.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">Channel display data.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup.</returns>
    private string SpareStartCodeMarkup(CorePageTexts texts, ChannelDisplayInfo channel, string lang)
    {
        var block = ChannelStartCodeBlock.Markup(
            ChannelStartCodeBlock.CaptionText(_promptLocalizer, texts, channel.BotName, lang),
            channel.StartMessageDisplay ?? channel.StartMessage ?? string.Empty,
            ChannelStartCodeBlock.CopyAction(
                ChannelStartCodeBlock.CopyText(_promptLocalizer, texts, lang),
                channel.ChannelType),
            isSpare: true);

        return SpareCodeElement(channel, block);
    }

    /// <summary>
    /// Builds the copy of the spare start code of one channel the scanner page shows under its status line
    /// (SPEC-025 HOP-194, SPEC-007 UI-119): the block whose button copies the message and opens the channel,
    /// as a launch page of the hop offers it — the phone that holds the page is the one that sends the code.
    /// It stands inside the same element as the spare form does, so the script opens it the same way.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">Channel display data.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup.</returns>
    private string SpareStartCodeOpenMarkup(CorePageTexts texts, ChannelDisplayInfo channel, string lang)
    {
        var block = ChannelStartCodeBlock.OpenBlockMarkup(
            ChannelStartCodeBlock.CaptionText(_promptLocalizer, texts, channel.BotName, lang),
            channel.StartMessageDisplay ?? channel.StartMessage ?? string.Empty,
            channel.DeepLinkUrl,
            channel.ChannelType,
            ChannelStartCodeBlock.CopyAndOpenText(_promptLocalizer, texts, ChannelName(texts, channel, lang), lang),
            extraAttributes: string.Empty);

        return SpareCodeElement(channel, block);
    }

    /// <summary>
    /// Wraps a block of the spare start code of one channel into the element the page script shows it by:
    /// the element names the channel, and the stylesheet keeps it out of sight until the script opens it.
    /// </summary>
    /// <param name="channel">Channel display data.</param>
    /// <param name="block">Ready markup of the block.</param>
    /// <returns>Ready markup.</returns>
    private static string SpareCodeElement(ChannelDisplayInfo channel, string block)
    {
        return $"<div class=\"{SpareCodeClass}\" {SpareCodeChannelAttributeName}=\"{HtmlEncode(channel.ChannelType)}\">{block}</div>";
    }

    /// <summary>
    /// Builds the copies of the spare start codes shown outside the panels — in the block of the started
    /// hop and in the block under the status line: one per channel of the page that has a spare start code.
    /// The scanner page offers each copy as the block that copies and opens the channel; every other page,
    /// as the spare form that only copies.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Ready markup; empty — no channel of the page has a spare start code.</returns>
    private string SpareStartCodesMarkup(AuthPageRenderContext context, string lang)
    {
        return string.Concat(context.Channels
            .Where(ShowsSpareStartCodeBlock)
            .Select(channel => context.IsScannerPage
                ? SpareStartCodeOpenMarkup(context.Texts, channel, lang)
                : SpareStartCodeMarkup(context.Texts, channel, lang)));
    }

    /// <summary>
    /// Appends the headings of the scanner page with a choice of channels, one per channel a link of the page
    /// leads into, each hidden until the script shows the one of the channel tapped (SPEC-025 HOP-193) — but
    /// the one of a channel that has already answered, which comes shown (<see cref="ChosenChannelAtRender"/>). The
    /// heading is the one of the launch pages of the hop (<see cref="HopLaunchPageHeading"/>), worded
    /// "Continue in" whatever the transaction: it says where the person went. The Email Pull panel leads into
    /// no channel — its button reveals the form — and has no heading.
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="context">Page rendering context.</param>
    /// <param name="lang">Content language of the page.</param>
    private void AppendChosenChannelHeadings(StringBuilder sb, AuthPageRenderContext context, string lang)
    {
        sb.AppendLine($"        <div id=\"{ChosenHeadingsElementId}\">");

        // The heading of a channel that has already answered is rendered shown, as the tap on its link left it.
        var chosen = ChosenChannelAtRender(context);

        foreach (var channel in context.Channels.Where(LeadsIntoChannel))
        {
            var text = HopLaunchPageHeading.ContinueText(
                channel.ChannelType, channel.DisplayName, _promptLocalizer, context.Texts, lang);
            var hidden = string.Equals(channel.ChannelType, chosen, StringComparison.Ordinal) ? string.Empty : " hidden";
            sb.AppendLine(
                $"            <div {ContinueChannelAttributeName}=\"{HtmlEncode(channel.ChannelType)}\"{hidden}>{HopLaunchPageHeading.Markup(channel.ChannelType, channel.IconSvgPath, text)}</div>");
        }

        sb.AppendLine("        </div>");
    }

    /// <summary>
    /// Builds the hints of the channels in the block of the started hop, each hidden until the script
    /// notices the phone on its way into its channel. The texts arrive ready (<see cref="AuthPageChannelLines.HopHint"/>).
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>Ready markup; empty — no channel of the page has a hint.</returns>
    private static string HopChannelHintMarkup(AuthPageRenderContext context)
    {
        var sb = new StringBuilder();

        foreach (var hint in HopChannelHintsOf(context))
        {
            sb.Append($"<p id=\"{hint.ElementId}\" hidden>{HtmlEncode(hint.Text)}</p>");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The identifiers of the hints of the channels by channel type, as the script of the page reveals them.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The identifiers; null — the page carries no hint.</returns>
    private static IReadOnlyDictionary<string, string>? HopChannelHintIds(AuthPageRenderContext context)
    {
        var hints = HopChannelHintsOf(context);

        return hints.Count is 0
            ? null
            : hints.ToDictionary(hint => hint.ChannelType, hint => hint.ElementId, StringComparer.Ordinal);
    }

    /// <summary>
    /// The hints of the channels of the page, in the order of the channels: one per channel type with a
    /// non-empty hint, identified by the position of the channel on the page. The scanner page carries none
    /// — it is the phone itself.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The hints; empty — none.</returns>
    private static IReadOnlyList<HopChannelHintLine> HopChannelHintsOf(AuthPageRenderContext context)
    {
        if (context.IsScannerPage)
        {
            return [];
        }

        var hints = new List<HopChannelHintLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < context.Channels.Count; i++)
        {
            var channelType = context.Channels[i].ChannelType;

            if (seen.Add(channelType)
                && context.ChannelLines.TryGetValue(channelType, out var lines)
                && !string.IsNullOrEmpty(lines.HopHint))
            {
                hints.Add(new HopChannelHintLine(
                    channelType, HopChannelHintElementIdPrefix + i.ToString(CultureInfo.InvariantCulture), lines.HopHint));
            }
        }

        return hints;
    }

    /// <summary>
    /// The channels of the page whose QR code is a hop QR code: every channel under the unified QR code,
    /// otherwise each channel whose own QR code carries a hop token. A page that keeps the per-channel QR
    /// codes under its "Any channel" tab is the second case: a channel tab shows the channel's own QR code,
    /// which is a hop QR code only when the channel's own token is one. A page that keeps them for the
    /// remembering checkbox alone (<see cref="HidesTabStrip"/>) is the first case: it has no channel tab to
    /// show, and the state of the remembered channel is its exception. On a computer the spare start code
    /// of such a channel is offered once the phone went into it (SPEC-025 HOP-172), never by the delay from
    /// the load.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The channel types.</returns>
    private static IReadOnlyList<string> HopQrChannelsOf(AuthPageRenderContext context)
    {
        return context.Channels
            .Where(channel => (ShowsUnifiedQrCode(context) && (!HasAnyChannelState(context) || HidesTabStrip(context)))
                || (ShowsQrCodeOf(channel, context) && channel.QrContentExpiresAt is not null))
            .Select(channel => channel.ChannelType)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Tells whether any panel of the page shows the spare form of the block — the condition the page
    /// keeps its spare start codes out of sight by.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true — at least one panel shows the spare form.</returns>
    private static bool ShowsAnySpareStartCodeBlock(AuthPageRenderContext context)
    {
        return context.Channels.Any(ShowsSpareStartCodeBlock);
    }

    /// <summary>
    /// Appends the block of the start by a code of one channel (<see cref="ChannelStartCodeBlock"/>): the
    /// caption naming the channel, the message and the button that copies it and opens the chat.
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">Channel display data.</param>
    /// <param name="btnClass">Color modifier of the channel button.</param>
    /// <param name="btnIcon">Ready markup of the icon of the channel button; empty — none.</param>
    /// <param name="linkAttributes">
    /// Ready markup of the further attributes of the button (leading space included): the one that makes a tap
    /// on it send the signal of the chosen channel, and the one naming the channel whose heading a tap shows on
    /// the scanner page; empty — the button carries neither.
    /// </param>
    /// <param name="lang">Content language of the page.</param>
    private void AppendStartCodeBlock(
        StringBuilder sb,
        CorePageTexts texts,
        ChannelDisplayInfo channel,
        string btnClass,
        string btnIcon,
        string linkAttributes,
        string lang)
    {
        // By the rule the tabs are named with, so the block and the tab never call one channel two names.
        var channelName = ChannelName(texts, channel, lang);
        var buttonText = ChannelStartCodeBlock.CopyAndOpenText(_promptLocalizer, texts, channelName, lang);

        // Same rule as for the channel button: a label that is not the core's own text is clipped and gets
        // the tooltip of the contour; the text around a built-in brand name is our own and wraps.
        var clipsLabel = ClipsLabel(texts, channel, lang, [ChannelStartCodeBlock.CopyAndOpen]);
        var labelMarkup = clipsLabel
            ? $"<span class=\"veriqa-btn-label\">{HtmlEncode(buttonText)}</span>"
            : HtmlEncode(buttonText);
        var tooltipAttribute = clipsLabel
            ? $" data-veriqa-tooltip=\"{HtmlEncode(buttonText)}\""
            : string.Empty;

        var action = ChannelStartCodeBlock.OpenAction(
            channel.DeepLinkUrl,
            $"veriqa-btn veriqa-btn-channel {btnClass}",
            $"{btnIcon}{labelMarkup}",
            channel.ChannelType,
            tooltipAttribute + linkAttributes);

        if (clipsLabel)
        {
            action = "<span class=\"veriqa-tooltip-host\">"
                + action
                + "<span class=\"veriqa-tooltip\" aria-hidden=\"true\" hidden></span></span>";
        }

        sb.AppendLine(ChannelStartCodeBlock.Markup(
            ChannelStartCodeBlock.CaptionText(_promptLocalizer, texts, channel.BotName, lang),
            channel.StartMessageDisplay ?? channel.StartMessage ?? string.Empty,
            action));
    }

    /// <summary>
    /// Appends the block of one QR code — a channel's own or the unified one. The block is what the rules of
    /// QR visibility, of the start of the hop and of the expiry hide.
    /// </summary>
    /// <param name="sb">StringBuilder for HTML output.</param>
    /// <param name="qrCodeBase64">Base64 PNG of the QR code, without a data URI prefix.</param>
    /// <param name="alt">Text alternative of the image.</param>
    /// <param name="hopQrEntry">
    /// Entry the page replaces the QR code by before its hop token expires, marked on the image for the page
    /// script (SPEC-025 HOP-152); null — the QR code is not replaced and its image carries no mark.
    /// </param>
    /// <param name="blockId">Identifier of the block, for a control that names it; null — the block has none.</param>
    /// <param name="blockModifierClass">Modifier class of the block the stylesheet keys on; null — none.</param>
    private static void AppendQrBlock(
        StringBuilder sb,
        string qrCodeBase64,
        string alt,
        string? hopQrEntry,
        string? blockId = null,
        string? blockModifierClass = null)
    {
        var entryAttribute = hopQrEntry is null
            ? string.Empty
            : $" {HopQrEntryAttributeName}=\"{HtmlEncode(hopQrEntry)}\"";
        var idAttribute = blockId is null ? string.Empty : $" id=\"{blockId}\"";
        var modifierClass = blockModifierClass is null ? string.Empty : $" {blockModifierClass}";
        sb.AppendLine($"            <div class=\"veriqa-qr{modifierClass}\"{idAttribute}>");
        sb.AppendLine($"                <img src=\"{AuthPageConstants.PngDataUriPrefix}{HtmlEncode(qrCodeBase64)}\" alt=\"{HtmlEncode(alt)}\" width=\"{AuthPageConstants.QrCodeImgAttributeSizePx}\" height=\"{AuthPageConstants.QrCodeImgAttributeSizePx}\"{entryAttribute} />");
        sb.AppendLine("            </div>");
    }

    /// <summary>
    /// Builds the closing body and html tags.
    /// </summary>
    private static void AppendBodyClose(StringBuilder sb)
    {
        sb.AppendLine("\n</body>");
        sb.AppendLine("</html>");
    }

    /// <summary>
    /// Tells whether the page lays its channels out behind tabs: several channels, which the server neither
    /// lays out as buttons nor shows all at once in the grid. The stylesheet still turns the tabs into
    /// buttons on a phone that hides the QR code.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true — the page has a tab strip.</returns>
    private static bool ShowsTabs(AuthPageRenderContext context)
    {
        return context.Channels.Count > 1 && !ServerChoosesButtonsLayout(context) && !ShowsChannelGrid(context);
    }

    /// <summary>
    /// Tells whether the lead instruction of the page is worded by the selected tab (SPEC-007 UI-121): the
    /// renderer's own instruction of a page with tabs. A deployment that stated an instruction for every
    /// wording leaves no wording of the renderer's own, and no tab changes it.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true — the instruction carries a variant per tab.</returns>
    private static bool WordsTabs(AuthPageRenderContext context)
    {
        return context.Instruction is null && ShowsTabs(context);
    }

    /// <summary>
    /// Tells whether the SERVER lays the channels out as buttons — the part of the layout decision
    /// that does not depend on the device. <see cref="QrCodeVisibility.Never"/> is a signal to show
    /// the channel buttons only, on any device (SPEC-025 HOP-146), so the page does not wait for the
    /// device check and works without a script; the scanner page is such a page by its effective QR
    /// visibility. A page with the unified QR code lays the buttons out under it, or beside it on the split
    /// card (<see cref="ShowsSplitLayout"/>), on every device (HOP-187) — unless it keeps the per-channel QR
    /// codes (<see cref="HasAnyChannelState"/>), which is a page with tabs. <see cref="QrCodeVisibility.DesktopOnly"/> on a phone gives the same layout, but only
    /// the browser knows the device: the page is rendered with tabs and the stylesheet decides.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the channel container carries the buttons-layout modifier.</returns>
    private static bool ServerChoosesButtonsLayout(AuthPageRenderContext context)
    {
        return EffectiveQrVisibility(context) is QrCodeVisibility.Never
            || (ShowsUnifiedQrCode(context) && !HasAnyChannelState(context));
    }

    /// <summary>
    /// Tells whether the page shows every channel at once (<see cref="ChannelLayout.Grid"/>). The
    /// layout is an arrangement of what the tabs would otherwise hold, so it holds exactly where the
    /// tabs would be: two channels or more, and no layout of buttons chosen by the server — which rules
    /// out a page that hides the QR code on every device, the scanner page and a page with the unified
    /// QR code. The page of the unified QR code that keeps the per-channel QR codes is no exception: its
    /// tabs are the "Any channel" tab and the channel tabs, and a grid has no place for the first of
    /// them. A phone under <see cref="QrCodeVisibility.DesktopOnly"/> is rendered with this markup
    /// as it is with the tabs, and the stylesheet lays it out as buttons there.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the channel panels stand in the row of the grid layout.</returns>
    private static bool ShowsChannelGrid(AuthPageRenderContext context)
    {
        return context.ChannelLayout is ChannelLayout.Grid
            && context.Channels.Count > 1
            && !ServerChoosesButtonsLayout(context)
            && !HasAnyChannelState(context);
    }

    /// <summary>
    /// Names a channel of the page — on its tab, over its panel, in the instruction and in the lines of the
    /// script — by the one rule of the pages (<see cref="ChannelNames.LocalizeChannelName"/>): the name of the
    /// channel — of a built-in messenger, or the label its adapter declared — in the language of the page.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">Channel display data.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>Channel name.</returns>
    private string ChannelName(CorePageTexts texts, ChannelDisplayInfo channel, string lang) =>
        ChannelNames.LocalizeChannelName(_promptLocalizer, texts, channel.ChannelType, channel.DisplayName, lang);

    /// <summary>
    /// Tells whether the page serves a sign-in — the transaction the texts that speak of signing in are
    /// shown for; any other type is a confirmation. A caller that states no type is the sign-in window.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true — a sign-in.</returns>
    private static bool IsSignIn(AuthPageRenderContext context) =>
        context.TransactionType is null || HopLaunchPageHeading.IsSignIn(context.TransactionType);

    /// <summary>
    /// Identifier of the tab of a channel, by the position of the channel in the set.
    /// </summary>
    /// <param name="index">Position of the channel.</param>
    /// <returns>Element identifier.</returns>
    private static string ChannelTabElementId(int index) => $"veriqa-tab-{index}";

    /// <summary>
    /// Renders the brand icon that stands beside the NAME of a channel — in its tab and, in the grid
    /// layout, over its panel. Its classes are the ones the color stated for the button of the channel
    /// paints (<see cref="CorePageChannelButtonColor"/>). Decorative; empty for a channel without a glyph.
    /// </summary>
    /// <param name="channel">Channel display data.</param>
    /// <returns>SVG markup, or an empty string.</returns>
    private static string ChannelNameIcon(ChannelDisplayInfo channel)
    {
        return ChannelIconSvg.Render(
            channel.ChannelType,
            CorePageChannelButtonColor.TabIconCssClass(channel.ChannelType),
            channel.IconSvgPath);
    }

    /// <summary>
    /// Tells whether the page shows the unified hop QR code its caller stated (SPEC-025 HOP-183). The
    /// scanner page shows none, whatever the caller stated.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the channel container carries the unified QR code.</returns>
    private static bool ShowsUnifiedQrCode(AuthPageRenderContext context)
    {
        return !context.IsScannerPage && !string.IsNullOrEmpty(context.UnifiedQrCodeBase64);
    }

    /// <summary>
    /// Tells whether the page of the unified QR code keeps the per-channel QR codes as well: tabs, the first
    /// of them "Any channel" with the unified QR code and the buttons of every channel, then a tab per channel
    /// with its own QR code. The page opens in the state of the "Any channel" tab. Two settings give it: the
    /// QR mode of hop (<see cref="AuthPageRenderContext.UnifiedQrWithChannelQrCodes"/>), and the checkbox that
    /// remembers the channel (<see cref="AuthPageRenderContext.RememberChannelCheckbox"/>), whose state of the
    /// remembered channel is the state of a channel tab. Under the checkbox alone the strip of the tabs is
    /// hidden (<see cref="HidesTabStrip"/>), so the page looks as the page of the unified QR code does. A page
    /// of one channel has no choice to make between the two, and a page that shows no QR code
    /// (<see cref="QrCodeVisibility.Never"/>) has no channel QR code to keep: both stay the page of the
    /// unified QR code.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the page opens in the state of the "Any channel" tab.</returns>
    private static bool HasAnyChannelState(AuthPageRenderContext context)
    {
        return ShowsUnifiedQrCode(context)
            && (context.UnifiedQrWithChannelQrCodes || context.RememberChannelCheckbox)
            && context.Channels.Count > 1
            && EffectiveQrVisibility(context) is not QrCodeVisibility.Never;
    }

    /// <summary>
    /// Tells whether the page keeps the per-channel QR codes for the checkbox that remembers the channel alone
    /// (SPEC-025 HOP-187): a page whose QR mode of hop shows the unified QR code without them. Its strip of
    /// tabs is rendered hidden — the page looks as it does without the checkbox, with the panels of the
    /// channels as the buttons under the unified QR code — and the script shows a channel tab only as the
    /// state of the remembered channel. Its "Any channel" state therefore keeps the instruction and the
    /// buttons of the page of the unified QR code.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the strip of the tabs is rendered hidden.</returns>
    private static bool HidesTabStrip(AuthPageRenderContext context)
    {
        return HasAnyChannelState(context) && !context.UnifiedQrWithChannelQrCodes;
    }

    /// <summary>
    /// Tells whether the state of the "Any channel" tab shows the buttons of the channels beside the unified QR
    /// code: as the setting of the tab states (<see cref="AuthPageRenderContext.AnyChannelTabShowsChannelButtons"/>),
    /// and always where the strip is hidden (<see cref="HidesTabStrip"/>) — that page is the page of the
    /// unified QR code, which the setting of the tab does not touch.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the channel buttons stand beside the unified QR code.</returns>
    private static bool AnyChannelStateShowsChannelButtons(AuthPageRenderContext context)
    {
        return context.AnyChannelTabShowsChannelButtons || HidesTabStrip(context);
    }

    /// <summary>
    /// Tells whether the page offers the checkbox that remembers the channel the user went through (SPEC-025
    /// HOP-187): its setting is on and the page keeps the per-channel QR codes, whose state of a channel tab
    /// is the state of the remembered channel (<see cref="HasAnyChannelState"/>). That is a page of the
    /// unified QR code with two channels or more in the window or on the entry page of a confirmation — never
    /// the scanner page. The server renders the checkbox hidden; the script shows it on a desktop alone.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the page carries the checkbox.</returns>
    private static bool OffersRememberCheckbox(AuthPageRenderContext context)
    {
        return context.RememberChannelCheckbox && HasAnyChannelState(context);
    }

    /// <summary>
    /// The QR visibility the page is rendered with: the one the caller resolved, except on the scanner
    /// page, which shows no QR code whatever the setting states — a QR code is of no use to the phone
    /// that scanned it (SPEC-025 HOP-146). Handing the page <see cref="QrCodeVisibility.Never"/> there
    /// is what gives it the wordings for a page without the QR code and the buttons layout, by the same
    /// rules as any page that hides the code.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The effective QR visibility.</returns>
    private static QrCodeVisibility EffectiveQrVisibility(AuthPageRenderContext context)
    {
        return context.IsScannerPage ? QrCodeVisibility.Never : context.QrVisibility;
    }

    /// <summary>
    /// The visibility of the channel buttons the page is rendered with: the one the caller resolved,
    /// except on the scanner page, where the buttons are shown whatever the setting states — that page
    /// carries no QR code (<see cref="EffectiveQrVisibility"/>), so the buttons are its only way further
    /// (SPEC-025 HOP-146).
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The effective visibility of the channel buttons.</returns>
    private static ChannelButtonVisibility EffectiveChannelButtonVisibility(AuthPageRenderContext context)
    {
        return context.IsScannerPage ? ChannelButtonVisibility.Always : context.ChannelButtonVisibility;
    }

    /// <summary>
    /// Tells whether the channel panels carry QR codes of their own. A page whose effective visibility is
    /// <see cref="QrCodeVisibility.Never"/> carries none, not even hidden ones: it is the buttons-only
    /// page (SPEC-025 HOP-145, HOP-146), and the scanner page is such a page by its effective
    /// visibility. A page with the unified QR code carries none either: that one code is the QR code of
    /// every channel (HOP-187) — unless the page keeps the per-channel QR codes behind its channel tabs
    /// (<see cref="HasAnyChannelState"/>).
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when every channel with an addressable entry shows its QR code.</returns>
    private static bool ShowsChannelQrCodes(AuthPageRenderContext context)
    {
        return EffectiveQrVisibility(context) is not QrCodeVisibility.Never
            && (!ShowsUnifiedQrCode(context) || HasAnyChannelState(context));
    }

    /// <summary>
    /// Tells whether the panel of a channel carries its QR code: the page shows the QR codes of its
    /// channels and this channel has a picture. A channel whose QR code could not be drawn comes without
    /// one and is shown by its button alone.
    /// </summary>
    /// <param name="channel">The channel of the panel.</param>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the panel shows the QR code of the channel.</returns>
    private static bool ShowsQrCodeOf(ChannelDisplayInfo channel, AuthPageRenderContext context)
    {
        return ShowsChannelQrCodes(context) && !string.IsNullOrEmpty(channel.QrCodeBase64);
    }

    /// <summary>
    /// What the page script needs to replace the hop QR codes of the page before their tokens expire
    /// (SPEC-025 HOP-152): the entries of the QR codes the page shows whose tokens expire before the
    /// transaction, each with the remainder of its token by the server's clock at the render.
    /// </summary>
    /// <remarks>
    /// The server's moment of the render is taken from the values the page was handed — the expiry of the
    /// transaction less its remainder — rather than from a second reading of a clock, so the remainder of a
    /// token and the remainder of the transaction are counted from one moment.
    /// </remarks>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The replacement of the page; null — the page shows no such QR code and replaces nothing.</returns>
    private static AuthPageScriptHopQrReissue? HopQrReissueOf(AuthPageRenderContext context)
    {
        var renderedAt = context.ExpiresAt - TimeSpan.FromSeconds(Math.Max(0, context.RemainingSeconds));
        var entries = new List<AuthPageScriptHopQrEntry>();

        if (UnifiedHopQrEntryOf(context) is { } unifiedEntry && context.UnifiedQrExpiresAt is { } unifiedExpiresAt)
        {
            entries.Add(new AuthPageScriptHopQrEntry(
                unifiedEntry, PageCountdown.RemainingMs(unifiedExpiresAt, renderedAt)));
        }

        foreach (var channel in context.Channels)
        {
            // A channel type repeated in the list is one entry: its images are replaced together.
            if (ChannelHopQrEntry(channel, context) is { } channelEntry
                && channel.QrContentExpiresAt is { } channelExpiresAt
                && !entries.Exists(existing => string.Equals(existing.Entry, channelEntry, StringComparison.Ordinal)))
            {
                entries.Add(new AuthPageScriptHopQrEntry(
                    channelEntry, PageCountdown.RemainingMs(channelExpiresAt, renderedAt)));
            }
        }

        if (entries.Count is 0)
        {
            return null;
        }

        return new AuthPageScriptHopQrReissue(
            Fraction: HopModeConstants.ReissueFraction,
            RetryDelayMs: HopModeConstants.ReissueRetryDelayMs,
            PngDataUriPrefix: AuthPageConstants.PngDataUriPrefix,
            ImageAttribute: HopQrEntryAttributeName,
            NotVisibleSelector: HopQrNotVisibleSelector,
            Entries: entries);
    }

    /// <summary>
    /// Tells whether a hop QR code the page shows is replaced before its token expires: only a token that
    /// expires before the transaction is (SPEC-025 HOP-152, HOP-153).
    /// </summary>
    /// <param name="tokenExpiresAt">Own expiry of the token the QR code carries; null — the QR code carries none.</param>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the page replaces the QR code.</returns>
    private static bool ReissuesHopQr(DateTimeOffset? tokenExpiresAt, AuthPageRenderContext context)
    {
        return tokenExpiresAt is { } expiresAt
            && HopReissueSchedule.ExpiresBeforeTransaction(expiresAt, context.ExpiresAt);
    }

    /// <summary>
    /// The entry the page replaces its unified hop QR code by, when it shows one that is replaced.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The entry of the unified QR code; null — the page replaces no unified QR code.</returns>
    private static string? UnifiedHopQrEntryOf(AuthPageRenderContext context)
    {
        return ShowsUnifiedQrCode(context) && ReissuesHopQr(context.UnifiedQrExpiresAt, context)
            ? UnifiedHopQrEntry
            : null;
    }

    /// <summary>
    /// The entry the page replaces the QR code of a channel by, when its panel shows a QR code that is
    /// replaced. The panel of an Email channel in Pull mode shows the form and no QR code.
    /// </summary>
    /// <param name="channel">The channel of the panel.</param>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The channel type; null — the panel shows no QR code the page replaces.</returns>
    private static string? ChannelHopQrEntry(ChannelDisplayInfo channel, AuthPageRenderContext context)
    {
        return PanelShowsQrCode(channel, context) && ReissuesHopQr(channel.QrContentExpiresAt, context)
            ? channel.ChannelType
            : null;
    }

    /// <summary>
    /// Tells whether the panel of a channel actually shows a QR code of the channel. It is
    /// <see cref="ShowsQrCodeOf"/> narrowed by the panel: the panel of an Email channel in Pull mode shows the
    /// form and no QR code, whatever the picture the channel came with. The one condition behind the
    /// replacement of the QR code, the layout of the panel on the split card and the mark of a QR code in the
    /// label of its tab.
    /// </summary>
    /// <param name="channel">The channel of the panel.</param>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the panel shows the QR code of the channel.</returns>
    private static bool PanelShowsQrCode(ChannelDisplayInfo channel, AuthPageRenderContext context)
    {
        return ShowsQrCodeOf(channel, context) && LeadsIntoChannel(channel);
    }

    /// <summary>
    /// Tells whether the card is the split card of the unified QR code (SPEC-025 HOP-187): the page shows the
    /// unified QR code — with the "Any channel" tab or without — and the channel buttons on a desktop. Without
    /// the buttons there (<see cref="ChannelButtonVisibility.MobileOnly"/>, <see cref="ChannelButtonVisibility.Never"/>)
    /// the right column would have nothing to hold on any tab, and the card stays the one-column card. Whether
    /// the window is wide enough for two columns is the browser's to know: the stylesheet lays the split card
    /// out as one column on a narrower window and on a phone that hides the QR code.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the card carries <see cref="SplitCardClass"/>.</returns>
    private static bool ShowsSplitLayout(AuthPageRenderContext context)
    {
        return ShowsUnifiedQrCode(context)
            && EffectiveQrVisibility(context) is not QrCodeVisibility.Never
            && EffectiveChannelButtonVisibility(context) is ChannelButtonVisibility.Always;
    }

    /// <summary>
    /// Cuts the label of a channel tab whose panel shows a QR code of its own —
    /// <see cref="AuthPageStrings.ChannelTabWithQrCode"/> — into the pieces of text between its marks. The tab
    /// spaces its items itself, so the spaces around a mark are not part of a piece.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>The pieces in order; a label overridden without the mark is a single piece.</returns>
    private string[] ChannelTabWithQrCodePieces(CorePageTexts texts, string lang)
    {
        var label = LocalizeOwn(texts, AuthPageStrings.ChannelTabWithQrCode, lang);
        return Array.ConvertAll(label.Split(QrCodeMark), piece => piece.Trim());
    }

    /// <summary>
    /// The text of the label of a channel tab whose panel shows a QR code of its own: the pieces of
    /// <see cref="ChannelTabWithQrCodePieces"/> with the name of the channel at the slot, spaced as the tab
    /// spaces them, without the glyph. It is what the tooltip of a clipped label says.
    /// </summary>
    /// <param name="pieces">Pieces of the label, as <see cref="ChannelTabWithQrCodePieces"/> cut them.</param>
    /// <param name="channelName">Name of the channel, by the rule the tabs are named with.</param>
    /// <returns>The plain text of the label.</returns>
    private static string ChannelTabTextWithQrCode(string[] pieces, string channelName) =>
        string.Join(
            ' ',
            pieces
                .Where(piece => piece.Length > 0)
                .Select(piece => piece.Replace(TabChannelSlot, channelName, StringComparison.Ordinal)));

    /// <summary>
    /// Builds the label of a channel tab whose panel shows a QR code of its own, on a page with the "Any
    /// channel" tab: the pieces of <see cref="ChannelTabWithQrCodePieces"/> with the name of the channel at
    /// the slot and the glyph of a QR code between them. The text is encoded, the glyph is the renderer's own
    /// markup; each piece of text is a span of its own, so the clipping of a label takes the text
    /// and never the glyph. A label overridden without the mark has no glyph.
    /// </summary>
    /// <param name="pieces">Pieces of the label, as <see cref="ChannelTabWithQrCodePieces"/> cut them.</param>
    /// <param name="channelName">Name of the channel, by the rule the tabs are named with.</param>
    /// <param name="labelClassAttribute">Ready class attribute of a clipped label (leading space); empty — none.</param>
    /// <returns>Ready markup of the label.</returns>
    private static string ChannelTabLabelWithQrCode(string[] pieces, string channelName, string labelClassAttribute)
    {
        var markup = new StringBuilder();

        // The slot is filled inside each piece, after the split: a channel name carrying the mark itself
        // stays text and gets no glyph.
        for (var i = 0; i < pieces.Length; i++)
        {
            if (i > 0)
            {
                markup.Append(TabQrGlyph);
            }

            if (pieces[i].Length > 0)
            {
                var encoded = HtmlEncode(pieces[i]).Replace(TabChannelSlot, HtmlEncode(channelName), StringComparison.Ordinal);
                markup.Append($"<span{labelClassAttribute}>{encoded}</span>");
            }
        }

        return markup.ToString();
    }

    /// <summary>
    /// Tells whether the link of an Email channel is its Push mode — the <c>mailto:</c> of the sign-in mail
    /// (SPEC-016 §5.3): the panel shows a QR code and the "open mail client" button. Otherwise — Pull, a
    /// relative path of the inline email entry form.
    /// </summary>
    /// <param name="deepLinkUrl">Deep link of the Email channel.</param>
    /// <returns>true for the Push mode.</returns>
    internal static bool IsEmailPushLink(string deepLinkUrl)
    {
        return deepLinkUrl.StartsWith(EmailPushLinkPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The target the page opens by itself (<see cref="AuthPageRenderContext.HopLaunchTarget"/>): only on the
    /// scanner page, and only when its caller states one.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The target; null — the page does not open the channel by itself.</returns>
    private static string? HopLaunchTargetOf(AuthPageRenderContext context)
    {
        return context.IsScannerPage && !string.IsNullOrEmpty(context.HopLaunchTarget)
            ? context.HopLaunchTarget
            : null;
    }

    /// <summary>
    /// The attributes that say in which tab the button of a channel opens it. The scanner page of the
    /// unified hop QR code opens a channel whose chat is a page of the core in its own tab: that page shows
    /// the outcome itself, and a tab a link opened is one a browser refuses to close by script, so a new
    /// tab would leave the phone with two (SPEC-025 HOP-186). Every other page and every other channel keep
    /// the new tab: leaving the sign-in window or the entry page of a confirmation in the same tab ends the
    /// live transaction on it, and under a messenger or a mail client the scanner page stays to show the
    /// outcome.
    /// </summary>
    /// <param name="channel">The channel of the button.</param>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The attributes, each preceded by a space.</returns>
    private static string ChannelLinkTargetAttributes(ChannelDisplayInfo channel, AuthPageRenderContext context)
    {
        return context.IsScannerPage && channel.ChatIsCorePage ? SameTabLinkAttributes : NewTabLinkAttributes;
    }

    /// <summary>
    /// Tells whether a tap on a channel link sends the client signal of the chosen channel: only on the
    /// scanner page, and only when its caller states that the signal is accepted (SPEC-025 HOP-156).
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the channel links carry the signal attribute.</returns>
    private static bool SendsChannelLaunchedSignal(AuthPageRenderContext context)
    {
        return context.IsScannerPage && context.SendsChannelLaunchedSignal;
    }

    /// <summary>
    /// What the page script remembers of the channel the user went through, and when
    /// (<see cref="AuthPageScriptRememberChannel"/>). The window and the entry page of a confirmation remember a
    /// channel once it has shown it works; the scanner page, which hears no answer meant for it, on a tap on a
    /// link into the channel. A sign-in and a confirmation are given the same, the label included; the checkbox
    /// differs by the words that name the action alone.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <returns>The settings; null — the page remembers nothing.</returns>
    private AuthPageScriptRememberChannel? RememberChannelOf(AuthPageRenderContext context, string lang)
    {
        var offersCheckbox = OffersRememberCheckbox(context);
        if (!context.RememberChannelHint && !offersCheckbox)
        {
            return null;
        }

        var isSignIn = IsSignIn(context);
        return new AuthPageScriptRememberChannel(
            ChannelTypes: [.. context.Channels.Select(channel => channel.ChannelType)],
            AnswerStatusCodes: context.IsScannerPage ? [] : WorkingChannelStatusCodes,
            TapSelector: context.IsScannerPage ? ChannelButtonSelector : null,
            Hint: context.RememberChannelHint
                ? new AuthPageScriptLastChannelHint(
                    Cookie: RememberedChannelCookies.LastChannel,
                    ButtonSelector: "." + ChannelButtonClass,
                    LabelClass: $"{HopReopenedWarning.BadgeClass} {LastUsedBadgeClass}",
                    LabelText: LocalizeOwn(context.Texts, AuthPageStrings.LastUsedChannel, lang))
                : null,
            Checkbox: offersCheckbox
                ? new AuthPageScriptRememberCheckbox(
                    Cookie: RememberedChannelCookies.RememberedChannel,
                    ElementId: RememberElementId,
                    InputElementId: RememberCheckboxElementId,
                    LabelElementId: RememberLabelElementId,
                    BackElementId: RememberBackElementId,
                    RememberText: LocalizeOwn(
                        context.Texts,
                        isSignIn ? AuthPageStrings.RememberSignInChannel : AuthPageStrings.RememberConfirmChannel,
                        lang),
                    AlwaysText: LocalizeOwn(
                        context.Texts,
                        isSignIn ? AuthPageStrings.AlwaysSignInWithChannel : AuthPageStrings.AlwaysConfirmInChannel,
                        lang),
                    DeviceAttribute: DeviceAttributeName,
                    PhoneDeviceValue: MobileDeviceValue,
                    HeldAttribute: RememberedChannelHeldAttributeName)
                : null);
    }

    /// <summary>
    /// The line of the answer of the bot the page is rendered with: the line of
    /// <see cref="AuthPageRenderContext.AnsweredChannelType"/> when the answer is awaited in a channel of the
    /// page that has one — the line the script would put on the status line once the answer is replayed to it
    /// (<see cref="AuthPageScriptChannelAnswer"/>), looked up by the same rule as its map of lines. A channel
    /// outside the page or without a line leaves the page as it is rendered otherwise, as the replay does.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The line, not encoded; null — the page is rendered waiting.</returns>
    private static string? AnswerLineAtRender(AuthPageRenderContext context) =>
        context.AnsweredChannelType is { } channelType
        && context.Channels.Any(channel => string.Equals(channel.ChannelType, channelType, StringComparison.Ordinal))
        && context.ChannelLines.TryGetValue(channelType, out var lines)
        && !string.IsNullOrEmpty(lines.Answer)
            ? lines.Answer
            : null;

    /// <summary>
    /// The channel whose heading the scanner page is rendered with (SPEC-025 HOP-193): the channel that has
    /// already answered, when the page renders its line (<see cref="AnswerLineAtRender"/>) and carries a
    /// heading for it — the page a tap on that channel's link and its answer would have left, minus the way
    /// back the answer takes away. The tap itself is not kept, so before the answer the page is rendered as
    /// loaded.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>The channel type; null — the page is rendered without a chosen channel.</returns>
    private static string? ChosenChannelAtRender(AuthPageRenderContext context) =>
        MarksChosenChannel(context)
        && AnswerLineAtRender(context) is not null
        && context.AnsweredChannelType is { } channelType
        && context.Channels.Any(channel =>
            string.Equals(channel.ChannelType, channelType, StringComparison.Ordinal) && LeadsIntoChannel(channel))
            ? channelType
            : null;

    /// <summary>
    /// Tells whether a tap on a channel link turns the page into the state of the chosen channel — its
    /// heading in place of the title and the lead instruction, the channel choice away and the way back to
    /// it on screen (SPEC-025 HOP-193): the scanner page with two channels or more. The scanner page of one
    /// channel — the page of the Email buttons (SPEC-025 HOP-179) — has nothing to choose between and stays
    /// as it is.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true — the page carries the headings of its channels and the way back to the choice.</returns>
    private static bool MarksChosenChannel(AuthPageRenderContext context)
    {
        return context.IsScannerPage && context.Channels.Count > 1;
    }

    /// <summary>
    /// Tells whether the panel of a channel holds a link into the channel: every channel but Email Pull,
    /// whose button reveals the form for a typed address and leads nowhere. The mail-client button of Email
    /// Push is such a link.
    /// </summary>
    /// <param name="channel">Channel display data.</param>
    /// <returns>true — the panel holds a link into the channel.</returns>
    private static bool LeadsIntoChannel(ChannelDisplayInfo channel)
    {
        return channel.ChannelType is not ChannelTypes.Email || IsEmailPushLink(channel.DeepLinkUrl);
    }

    /// <summary>
    /// Tells whether the page sends the person back to the window that started the transaction once the
    /// core asks its question there: the scanner page of a transaction with no destination of its own — the
    /// scanner of a sign-in, whose question the callback of the browser that started it asks (SPEC-025
    /// HOP-186, HOP-179, HOP-008). The reason is OIDC mechanics, not the type: the scanner of a confirmation
    /// has the question of the core as its destination and goes there.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>true when the page shows the line sending the person back to the window.</returns>
    private static bool SendsBackToSignInWindow(AuthPageRenderContext context)
    {
        return context.IsScannerPage && string.IsNullOrEmpty(context.ConfirmationQuestionUrl);
    }

    /// <summary>
    /// Tells whether a label of a channel is clipped and gets the tooltip of the contour (SPEC-015 §4.16) — the
    /// one rule of the tabs, the channel button and the button of the block of the start by a code. A label is
    /// the core's own text, and wraps, when the name of the channel is the core's own
    /// (<see cref="ChannelNames.IsCoreOwnName"/>) and no text the page built the label from — the name and the
    /// texts of the place — is replaced by an override of <c>AuthPage.Texts</c> that applies in the language of
    /// the page. Any other label — a name an adapter declared, a raw channel type, a text an integrator stated —
    /// has a length the core does not control, so it is clipped.
    /// </summary>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channel">Channel display data.</param>
    /// <param name="lang">Content language of the page.</param>
    /// <param name="placeTexts">Natural Keys the place builds the label from besides the name.</param>
    /// <returns>true — the label is clipped.</returns>
    private bool ClipsLabel(
        CorePageTexts texts,
        ChannelDisplayInfo channel,
        string lang,
        IReadOnlyList<string> placeTexts)
    {
        if (!ChannelNames.IsCoreOwnName(channel.ChannelType, channel.DisplayName))
        {
            return true;
        }

        if (ChannelNames.NameKeyOf(channel.ChannelType, channel.DisplayName) is { } nameKey
            && texts.TryOverride(_promptLocalizer, nameKey, lang, out _))
        {
            return true;
        }

        return placeTexts.Any(text => texts.TryOverride(_promptLocalizer, text, lang, out _));
    }

    /// <summary>
    /// Builds the CSP nonce attribute of an inline script (SPEC-007 §6.3): every inline script of the
    /// window gets it from one place, so a newly added one cannot be left out of the policy and
    /// silently blocked. No nonce from the host — an empty attribute.
    /// </summary>
    /// <param name="context">Page rendering context.</param>
    /// <returns>Attribute with a leading space, or an empty string.</returns>
    private static string BuildNonceAttribute(AuthPageRenderContext context)
    {
        return !string.IsNullOrEmpty(context.CspNonce)
            ? $" nonce=\"{HtmlEncode(context.CspNonce)}\""
            : string.Empty;
    }

    /// <summary>
    /// Builds a piece of page text in its two wordings: the one that holds while the QR code is shown
    /// and the one for a page that hides it (SPEC-007 UI-020). Both go into the markup, and the page
    /// shows the wording that matches what it actually displays — the server cannot choose, it does not
    /// know the device, and the same two attributes that hide the QR hide the sentence about it. The
    /// wording that does not hold is removed with <c>display: none</c>, so it leaves the accessibility
    /// tree as well and no reader is promised an element that is not there.
    /// </summary>
    /// <param name="withQr">Wording for a page that shows the QR code.</param>
    /// <param name="withoutQr">Wording for a page that hides it.</param>
    /// <returns>Ready HTML of the two wordings.</returns>
    private static string BuildQrDependentText(string withQr, string withoutQr)
    {
        return $"<span class=\"{QrShownTextClass}\">{HtmlEncode(withQr)}</span>"
            + $"<span class=\"{QrHiddenTextClass}\">{HtmlEncode(withoutQr)}</span>";
    }

    /// <summary>
    /// HTML-encodes a string for safe embedding in HTML.
    /// </summary>
    private static string HtmlEncode(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }

    /// <summary>
    /// The three wordings of the lead instruction, by what the page displays (SPEC-007 UI-020).
    /// </summary>
    private enum InstructionWording
    {
        /// <summary>The QR code and the channel button are on screen.</summary>
        QrAndButton,

        /// <summary>The QR code is on screen, the channel button is not.</summary>
        QrOnly,

        /// <summary>The QR code is hidden; the channel button is on screen.</summary>
        ButtonOnly
    }

    /// <summary>
    /// The hint of one channel in the block of the started hop.
    /// </summary>
    /// <param name="ChannelType">Channel the hint concerns.</param>
    /// <param name="ElementId">Identifier of its element.</param>
    /// <param name="Text">The hint, not encoded.</param>
    private sealed record HopChannelHintLine(string ChannelType, string ElementId, string Text);
}
