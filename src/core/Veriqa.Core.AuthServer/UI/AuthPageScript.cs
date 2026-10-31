// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Encodings.Web;
using System.Text.Json;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Access to the client script of the sign-in window shipped as an embedded resource of this
/// assembly, and to the JSON form of the configuration that script reads.
/// <para>
/// The script is INLINED into the generated document, exactly as the stylesheets are: the page must
/// stay self-contained (SPEC-015 §1) and is never made to fetch a second file. What the resource
/// changes is where the source lives — a real .js file instead of a string literal inside the
/// renderer — so the script is reachable by a linter, an editor and a diff.
/// </para>
/// <para>
/// Norms the script implements. They are named here and not in the .js file, whose comments are
/// inlined into the page and reach the end user's browser (see <see cref="CorePageEmbeddedText"/>):
/// </para>
/// <list type="bullet">
/// <item><description>the transaction status arrives over SignalR (SPEC-007 §5) and is applied to the
/// page by one handler for every delivery path (SPEC-007 §5.3); after an automatic reconnect the
/// connection joins the transaction group again (SPEC-007 UI-033);</description></item>
/// <item><description>when the client library does not load, the connection cannot start or closes for
/// good, the page degrades to polling the status endpoint (SPEC-007 §5.4);</description></item>
/// <item><description>no transport leaves the user waiting out the TTL: the page re-reads the status
/// when it becomes visible again and when it is restored from the back/forward cache, and the
/// navigation latch is reset on such a restore (SPEC-007 UI-087);</description></item>
/// <item><description>the countdown runs from the server's remainder of the TTL, and zero on the clock
/// is verified with the server before the page ends as expired (SPEC-007 UI-094);</description></item>
/// <item><description>the intermediate channel statuses are an additive channel that does not touch the
/// lifecycle handling (SPEC-016 §10.1); the page accepts them from any channel and shows the last one
/// to arrive, the hop codes included (the reopening of the hop link is a warning of its own, not a line),
/// and the first hop code switches the page to its instruction block (SPEC-025 HOP-101, HOP-161); the line
/// of the opened intermediate page is held back for a moment and is not shown once the start of the
/// transition into the channel has been — the automatic launch reports the two together (SPEC-025 HOP-101);
/// a line the server worded for the channel of the event (the scan of a channel's QR code, the start of
/// the transition into the Email channel) takes the place of the line of the code (SPEC-025 HOP-161);
/// </description></item>
/// <item><description>a tap on the mail-client button puts the line of the opened mail on the status line by
/// itself, and once the page comes back on screen after that tap — visible again, or restored from the
/// back/forward cache — the line, while still shown, gives way to the wait for the confirmation
/// (SPEC-016 §10.1);</description></item>
/// <item><description>the scanner page of one channel whose hop URL opens the mail by itself carries the
/// script of that launch after this one (SPEC-025 HOP-195): a transition the launch begins is a tap on the
/// mail-client button in all but the tap — the same line, the same press and the same
/// <c>channel_launched</c> with the type of the channel — and one made before the document was shown puts the
/// wait for the confirmation in place of the line of the opened mail or of the first line of the page, never
/// over a line of a state or an ending (SPEC-025 HOP-179); a tap on the button is announced to the launch, which
/// then does not open the mail a second time (SPEC-025 HOP-066c);</description></item>
/// <item><description>in the buttons layout of the channels the tabs are not bound and the panels carry
/// no tab roles; the layout is read from the channel container, not decided a second time, and the
/// Email Pull button reveals the existing form in place (SPEC-025 HOP-145, HOP-146);</description></item>
/// <item><description>on a page of the unified hop QR code that keeps the per-channel QR codes the tabs
/// switch between the state of the "Any channel" tab — the unified QR code with every panel laid out as a
/// button beside it, or with no panel where that tab shows no buttons, the panels without tab roles — and
/// the panel of one channel with its QR code, a tab panel labelled by its tab; the arrows move round all
/// tabs, "Any channel" included (SPEC-025 HOP-187);
/// </description></item>
/// <item><description>on the scanner page of the unified hop QR code the first hop code leaves the channel
/// buttons on screen, a tap on a channel link sends <c>channel_launched</c> with the type of the channel by
/// <c>sendBeacon</c> to the address of the document (SPEC-025 HOP-156);</description></item>
/// <item><description>a page that does not leave — the entry page of a confirmation and the scanner page of
/// either type — takes the channel choice, the lead instruction, the block of the hop and the cancel form off
/// the page once its transaction completes; the title, the subject and the success line stay (SPEC-025
/// HOP-186);</description></item>
/// <item><description>on the scanner page with two channels or more a tap on a link into a channel — the
/// mail-client button included, the button revealing the Email Pull form not — shows the heading "Continue
/// in" of that channel in place of the title and the lead instruction and takes the channel choice away, with
/// the way back to the choice under it; the way back brings the choice back, takes the block of the spare
/// start code away and forgets the tap, without a request, and a tap on another channel shows that channel;
/// the answer of a channel, the line sending the person back to the sign-in window, the confirmation being
/// processed and the endings of the page take the way back away for good; the heading shown stays on the
/// answer of a channel, which is awaited there, and leaves with every other of those lines, through the
/// reaction of the status block the launch page shares (<see cref="PageStatusBlock"/>); a focus left on a
/// part that leaves moves onto the way back, or onto the status line once the way back is gone; the scanner
/// page of one channel — the Email button page — has no such state (SPEC-025 HOP-193,
/// HOP-179);</description></item>
/// <item><description>the scanner page of a sign-in, which has nowhere to go on <c>AwaitingWebConfirmation</c>,
/// puts the line sending the person back to the sign-in window on its status line on that status from any
/// delivery path, once, and takes the channel choice off the page — the Email button page included; from
/// then on no channel status rewrites the line, and the lifecycle statuses do (SPEC-025 HOP-186,
/// HOP-179);</description></item>
/// <item><description>the entry page and the scanner page of a confirmation do not leave for the question on
/// <c>AwaitingWebConfirmation</c> when the status says its answer is taken only from the browser the link of
/// the letter was opened in: they put the line of the opened link on the status line, once, take the channel
/// choice off the page and keep the countdown and the cancel button (SPEC-007 UI-087, SPEC-025 HOP-186);</description></item>
/// <item><description>the line of the confirmation being processed holds the status line the same way: no
/// later channel status, answer of the bot, held line of the opened intermediate page or tap of the page
/// rewrites it, as on the launch page, and the lifecycle statuses do (SPEC-025 HOP-193);</description></item>
/// <item><description>a sign-in page that puts the line of the answer of a channel's bot on its status line
/// takes the channel choice, the lead instruction and the block of the started hop off the page with it; the
/// status line, the countdown and the cancel form stay, and the polling fallback does not read the answer
/// of the bot — the line names the channel, the status surface does not (SPEC-025 HOP-180, SPEC-007
/// UI-119);</description></item>
/// <item><description>both of those states move a focus standing in the part that leaves onto the status
/// line, focusable by the script alone; a focus anywhere else stays;</description></item>
/// <item><description>a hop QR code whose token expires before the transaction is replaced over the hub alone
/// (SPEC-025 HOP-152): the page asks, in one call, for the QR codes whose share of the remainder has passed
/// since the remainder arrived and for the ones whose last attempt failed, only while the connection is up
/// and a hop QR code is visible, and asks again right after the connection is back; an entry the answer
/// leaves out is not asked for any more, and a page on the polling fallback replaces nothing;</description></item>
/// <item><description>the step of the claims completion arrives as a status of its own over both delivery
/// paths; a page with the callback leaves for it once when the step is the phone step or the surface of
/// the web step is chosen — the initiating page or a page of the confirming device alike — and keeps
/// waiting, without announcing a confirmation, while the surface is not chosen yet; a page without the
/// callback ignores it (SPEC-041 L44, L52, L91);</description></item>
/// <item><description>the block shown once the hop has started carries the way back to the channel choice:
/// its button takes the mark of the started hop off the page, without a request and within the same
/// transaction, and the next hop code starts the hop on the page again; the first answer of a channel takes
/// that button off the page for the rest of its life (SPEC-025 HOP-170);</description></item>
/// <item><description>the spare start codes (SPEC-007 UI-119): on a phone — the device the head marked, the
/// scanner page by itself — no code of a panel opens, and once the person comes back after a press of the
/// button of a channel with a code, a link under the status line opens the copy of that channel's code; on
/// a computer or a tablet the codes of the panels open by the delay from the load, except those of the
/// channels whose QR code is a hop QR code, which open in the block of the started hop once the phone went
/// into the channel (SPEC-025 HOP-172); a transition of the phone into Messenger shows the line about the
/// browser page the phone may open first, at once, until the way back to the choice or the answer of a
/// channel; the answer of a channel closes every code and the link for good;</description></item>
/// <item><description>the email form sends its request on submit and is never posted by the browser; after
/// a sent link the address stays editable and the button, renamed to a repeated sending, is held for the
/// interval the server names — in the answer, or in <c>Retry-After</c> of a refusal for asking too
/// soon — and comes back by itself;</description></item>
/// <item><description>a clipped third-party label gets the tooltip of the contour
/// (SPEC-015 §4.16).</description></item>
/// </list>
/// </summary>
internal static class AuthPageScript
{
    /// <summary>
    /// Resource name of the window's script (<c>UI/Scripts/veriqa-auth-page.js</c>).
    /// </summary>
    private const string ResourceName = "Veriqa.Core.AuthServer.UI.Scripts.veriqa-auth-page.js";

    /// <summary>
    /// Identifier of the data block carrying <see cref="AuthPageScriptConfig"/> into the page — the
    /// one address the script and the renderer share.
    /// <para>
    /// The name binds two pieces of code that nothing else connects: this constant emits the block,
    /// and a bare literal reads it back (<c>getElementById("veriqa-auth-config")</c> in
    /// <c>UI/Scripts/veriqa-auth-page.js</c>) — a .js file kept free of server substitutions has no
    /// way to be told the name. Nothing checks that pair: the compiler does not see the literal and
    /// the component tests read the markup only, so renaming one side leaves the build and the
    /// tests green while the script dies in the browser on its very first statement, taking the
    /// countdown, SignalR, polling and expiry handling with it. The emitter and the reader are
    /// renamed together; everywhere else the name appears — this comment, the header of the .js
    /// file, the design documents — it is prose about the seam, binds nothing, and follows by a
    /// grep for the old value.
    /// </para>
    /// </summary>
    public const string ConfigElementId = "veriqa-auth-config";

    /// <summary>
    /// How the configuration is written into the data block.
    /// <para>
    /// The encoder is stated rather than left to the default: it is what makes the block safe to put
    /// into the document. The HTML-safe encoder escapes <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>, so a
    /// value can neither close the block early with <c>&lt;/script&gt;</c> nor open an HTML comment,
    /// and it escapes everything outside basic Latin, which covers the line terminators U+2028 and
    /// U+2029 as well. The contents of a data block are raw text, so this encoding IS the protection —
    /// HTML-encoding the block would not protect it, it would corrupt the JSON.
    /// </para>
    /// <para>
    /// Every data block of the contour's pages is written with these options — the script of the page of
    /// the claims completion reads its block the same way.
    /// </para>
    /// </summary>
    internal static readonly JsonSerializerOptions ConfigSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default
    };

    /// <summary>
    /// Source of the window's script, ready to be inlined.
    /// </summary>
    public static string Js => CorePageEmbeddedText.Load(typeof(AuthPageScript).Assembly, ResourceName);

    /// <summary>
    /// Writes the page configuration as the JSON the data block carries.
    /// </summary>
    /// <param name="config">Configuration of the page being rendered.</param>
    /// <returns>JSON safe to place inside the data block as it is.</returns>
    public static string SerializeConfig(AuthPageScriptConfig config)
    {
        return JsonSerializer.Serialize(config, ConfigSerializerOptions);
    }
}
