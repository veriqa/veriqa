// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Everything the server tells the client script of the sign-in window: the values of THIS request
/// (texts, URLs, the remainder of the transaction) and the application constants the page compares
/// against (status names, hub methods, attribute names). It is the whole seam between the two — the
/// script file itself carries no server value, which is what lets it be a real .js instead of a
/// string literal inside the renderer.
/// <para>
/// The object travels into the page as a JSON data block (<see cref="AuthPageScript.ConfigElementId"/>)
/// and is read once when the script starts. Property names of the JSON are the names of these
/// properties in camelCase — the naming policy is stated in <see cref="AuthPageScript"/>, so a
/// renamed property here renames the field the script reads, and the compiler does not catch it: the
/// pair of names is checked by reading the two files together.
/// </para>
/// </summary>
internal sealed record AuthPageScriptConfig
{
    /// <summary>External session identifier the page tracks.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Application path base (the issuer under a sub-path of the origin, e.g. <c>/demo</c>). Every
    /// root-relative URL of the page gets it applied by the script's own helper, in one place.
    /// </summary>
    public required string BasePath { get; init; }

    /// <summary>
    /// Milliseconds left of the transaction's lifetime when the page was built. The countdown adds it
    /// to the browser's own clock reading, so no clock has to be shared with the server.
    /// </summary>
    public required long RemainingMs { get; init; }

    /// <summary>Interval of the status fallback polling, in milliseconds (SPEC-007 §5.4).</summary>
    public required int PollIntervalMs { get; init; }

    /// <summary>
    /// Whether the page continues on the browser callback of the sign-in path. <see langword="false"/>
    /// — the transaction has no such callback and its outcome is shown in place.
    /// </summary>
    public required bool NavigatesAway { get; init; }

    /// <summary>Root-relative URL of the transaction status surface polled by the fallback.</summary>
    public required string StatusUrl { get; init; }

    /// <summary>
    /// Root-relative callback the page navigates to on a terminal outcome; empty — the page has no
    /// callback of its own.
    /// </summary>
    public required string CallbackUrl { get; init; }

    /// <summary>
    /// Root-relative URL of the core page asking the confirming question — the single destination of a
    /// page without a callback (SPEC-039 E29); empty on the sign-in path.
    /// </summary>
    public required string ConfirmUrl { get; init; }

    /// <summary>Root-relative URL the email form posts the magic-link request to.</summary>
    public required string EmailStartUrl { get; init; }

    /// <summary>
    /// The step of the code of the email form, for a page whose Email mail carries a code; null — the mail
    /// carries the magic link alone, the page has no step of the code and asks for none.
    /// </summary>
    public required AuthPageScriptEmailCode? EmailCode { get; init; }

    /// <summary>
    /// Name of the root attribute carrying the state of the page. The script writes it and the
    /// stylesheet decides what each state shows, so both sides keep one source for the name.
    /// </summary>
    public required string PageStateAttribute { get; init; }

    /// <summary>
    /// Name of the root attribute the script sets on the first hop code it receives. Its presence
    /// alone is the signal: the stylesheet then hides the channel choice and shows the instruction
    /// block, the same mechanism as <see cref="PageStateAttribute"/>.
    /// </summary>
    public required string HopStartedAttribute { get; init; }

    /// <summary>
    /// Codes of the hop family — the channel statuses that set <see cref="HopStartedAttribute"/>.
    /// Email codes are not among them, and the channel type of the event is not consulted.
    /// </summary>
    public required IReadOnlyList<string> HopStatusCodes { get; init; }

    /// <summary>
    /// The two hop codes the automatic launch of the intermediate page sends within milliseconds of each
    /// other; the script holds the line of the first back so that the pair is shown as one line.
    /// </summary>
    public required AuthPageScriptHopLaunchCodes HopLaunchCodes { get; init; }

    /// <summary>
    /// Identifier of the block the stylesheet shows once <see cref="HopStartedAttribute"/> is set. When
    /// the focus was inside the channel container at that moment, the script moves it onto this block.
    /// </summary>
    public required string HopInstructionElementId { get; init; }

    /// <summary>
    /// Identifier of the button of that block that brings the channel choice back: the script takes
    /// <see cref="HopStartedAttribute"/> off the root and moves the focus onto the channel container
    /// (<see cref="ChannelsElementId"/>). A page without the block carries no element of that identifier.
    /// </summary>
    public required string HopChooseAnotherElementId { get; init; }

    /// <summary>
    /// Whether the first hop code leaves <see cref="HopStartedAttribute"/> unset — the scanner page of the
    /// unified hop QR code, which has no block of the started hop: what a tap there changes is its own state
    /// (<see cref="ChosenChannel"/>). The status line still shows the code.
    /// </summary>
    public required bool HopInstructionDisabled { get; init; }

    /// <summary>
    /// The state of the chosen channel of the scanner page with a choice of channels: what a tap on a link into
    /// a channel shows and how the way back to the choice is found; null — the page has no such state (any
    /// other page, and the scanner page of one channel).
    /// </summary>
    public required AuthPageScriptChosenChannel? ChosenChannel { get; init; }

    /// <summary>
    /// Identifier of the form behind the cancel button. The script listens to its submit, and
    /// hides the form on the one ending that writes no <see cref="PageStateAttribute"/> for the
    /// stylesheet to hide it by — a failed sign-in, which takes a page with a callback to it. A page
    /// rendered without the button carries no element of that identifier.
    /// </summary>
    public required string CancelFormElementId { get; init; }

    /// <summary>
    /// The warning of a hop link opened once more from another browser (SPEC-041 L93): the channel status
    /// that reports it and where and as what the script shows it. The text is
    /// <see cref="AuthPageScriptTexts.HopReopened"/>.
    /// </summary>
    public required AuthPageScriptHopReopened HopReopened { get; init; }

    /// <summary>Value of <see cref="PageStateAttribute"/> for a page whose transaction ran out of time.</summary>
    public required string ExpiredPageState { get; init; }

    /// <summary>
    /// Value of <see cref="PageStateAttribute"/> for a page whose transaction failed and which shows that
    /// ending as its last screen: a page with no callback to take it to.
    /// </summary>
    public required string FailedPageState { get; init; }

    /// <summary>
    /// Value of <see cref="PageStateAttribute"/> the script writes when the transaction completes, on a page
    /// that does not leave and ends there on its success line; empty — the page keeps its state on completion.
    /// </summary>
    public required string CompletedPageState { get; init; }

    /// <summary>
    /// Value of <see cref="PageStateAttribute"/> the script writes once the answer is no longer given on the
    /// page: when it shows <see cref="ReturnToSignInWindowText"/>, and when it puts the line of the answer of
    /// a channel's bot (<see cref="AuthPageScriptChannelAnswer"/>) on its status line. The stylesheet takes
    /// the channel choice, the lead instruction, the block of the started hop and the spare code of a phone
    /// away; the status line, the countdown and the cancel button stay.
    /// </summary>
    public required string AnswerElsewherePageState { get; init; }

    /// <summary>
    /// The line the page shows on <c>AwaitingWebConfirmation</c> when it has no destination for that status
    /// — the core asks the question on the window that started the sign-in; empty — the page leaves for the
    /// question or keeps its screen. Once shown, no channel status rewrites the status line; the lifecycle
    /// statuses do.
    /// </summary>
    public required string ReturnToSignInWindowText { get; init; }

    /// <summary>
    /// The line the page shows on <c>AwaitingWebConfirmation</c> when the answer is taken only from the browser
    /// the link of the letter was opened in, where the question is already open: the page does not leave for
    /// it — the question would refuse this browser — and keeps the status line, the countdown and the cancel
    /// button. Once shown, no channel status rewrites the status line; the lifecycle statuses do.
    /// </summary>
    public required string AnswerInLinkBrowserText { get; init; }

    /// <summary>
    /// How the page replaces its hop QR codes before their tokens expire, over the hub method
    /// <see cref="AuthPageScriptHub.ReissueHopQrMethod"/>; null — the page shows no QR code whose token
    /// expires before the transaction, and the script starts no replacement at all.
    /// </summary>
    public required AuthPageScriptHopQrReissue? HopQrReissue { get; init; }

    /// <summary>
    /// How a tap on a channel link sends the client signal of the chosen channel back to the address of the
    /// document; null — the page sends none.
    /// </summary>
    public required AuthPageScriptChannelLaunch? ChannelLaunch { get; init; }

    /// <summary>
    /// The line the page shows by itself on a tap on the mail-client button: the button opens the mail
    /// directly, so no status of the channel reports that tap. Empty — the page has no such line, and the
    /// tap leaves the status line alone.
    /// </summary>
    public required string EmailMailOpenedText { get; init; }

    /// <summary>
    /// The line that takes the place of <see cref="EmailMailOpenedText"/> when the page, after its own tap on
    /// the mail-client button, comes back on screen — the document was hidden and is visible again, or it
    /// was restored from the back/forward cache — while that line is still the one shown and the page has
    /// neither ended nor is leaving. The tap is not remembered across a reload.
    /// </summary>
    public required string EmailReturnWaitText { get; init; }

    /// <summary>
    /// How the page hears the automatic launch of its mail-client button and tells that launch of a tap on
    /// the button; null — the page launches nothing by itself.
    /// </summary>
    public required AuthPageScriptHopTransition? HopTransition { get; init; }

    /// <summary>
    /// What the copying function of <see cref="ChannelStartCodeBlock"/> binds on this page: the messages
    /// of the start by a code by channel type, and how their controls and status lines are found; null —
    /// the page shows no such message, and neither the function nor a call of it is on the page.
    /// </summary>
    public required ChannelStartCodeScriptSettings? StartMessage { get; init; }

    /// <summary>
    /// How the page shows the spare start codes of its channels; null — the page has none, and nothing of
    /// it is on the page.
    /// </summary>
    public required AuthPageScriptSpareCode? SpareCode { get; init; }

    /// <summary>
    /// The hints of the channels in the block shown once the hop has started: line identifiers by channel
    /// type; null — the page carries no hint. A hint is revealed when a hop code naming its channel arrives,
    /// at once, and hides the hints of the other channels; the way back to the channel choice hides them
    /// all, and the answer of a channel for good. Every value is the server's, the lines are in the markup.
    /// </summary>
    public required IReadOnlyDictionary<string, string>? HopChannelHints { get; init; }

    /// <summary>
    /// What the page does once the bot of a channel answered the start. It stands apart from
    /// <see cref="SpareCode"/>: the answer acts on a page without spare start codes as well.
    /// </summary>
    public required AuthPageScriptChannelAnswer ChannelAnswer { get; init; }

    /// <summary>
    /// Selector the channel container matches when the stylesheet lays the channels out as buttons
    /// instead of tabs. The script asks the container once and, in that layout, leaves the tabs
    /// unbound and takes the tab roles off the panels — the condition itself, the device included,
    /// stays in one place on each side.
    /// </summary>
    public required string ButtonsLayoutSelector { get; init; }

    /// <summary>
    /// Identifier of the channel container — the element <see cref="ButtonsLayoutSelector"/> is matched
    /// against and the one that leaves the screen once the hop has started.
    /// </summary>
    public required string ChannelsElementId { get; init; }

    /// <summary>
    /// The "Any channel" tab of a page of the unified QR code that keeps the per-channel QR codes: how the
    /// script tells that tab from the channel tabs and which state of the channel container it selects;
    /// null — the page has no such tab.
    /// </summary>
    public required AuthPageScriptAnyChannelTab? AnyChannelTab { get; init; }

    /// <summary>
    /// The variants of the lead instruction by tab — how the script shows those of the selected tab; null —
    /// the instruction of the page is not worded by tab.
    /// </summary>
    public required AuthPageScriptTabWordings? TabWordings { get; init; }

    /// <summary>
    /// Reason code of a transaction the user declined — the one code the terminal line is branched on,
    /// because a refusal and a fault both arrive as a failed transaction.
    /// </summary>
    public required string DeclinedReasonCode { get; init; }

    /// <summary>
    /// Reason code of a sign-in closed because the same user started a newer one. It arrives as a failed
    /// transaction, and the page shows it as an expired one: it stays where it is, with no error line.
    /// </summary>
    public required string SupersededReasonCode { get; init; }

    /// <summary>Lifecycle status names the page compares the incoming status against.</summary>
    public required AuthPageScriptStatuses Statuses { get; init; }

    /// <summary>
    /// Names of the step and of the surfaces of the claims completion that take a page with a callback
    /// onto it (SPEC-041 L44, L52): the page of the completion lives on that callback — the form, or the
    /// page that waits while the form is filled in elsewhere.
    /// </summary>
    public required AuthPageScriptClaimCompletion ClaimCompletion { get; init; }

    /// <summary>Localized texts the page shows, already resolved for the language of the request.</summary>
    public required AuthPageScriptTexts Texts { get; init; }

    /// <summary>Addresses of the SignalR hub the page subscribes to.</summary>
    public required AuthPageScriptHub Hub { get; init; }

    /// <summary>
    /// Intermediate channel statuses of every channel and the line each of them puts on the page: the
    /// status code as the channel reports it, the localized text as the value. One map for the Email
    /// codes, the hop family and the code of the claims completion alike — the event is not filtered by its
    /// channel type. A status
    /// missing from the map leaves the status line alone. A text carrying the <c>{0}</c> slot names
    /// the channel of the event (<see cref="ChannelNames"/>) and is not shown for an event without one.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ChannelStatusTexts { get; init; }

    /// <summary>
    /// The lines of a channel status that are worded by the channel of the event: the status code as the
    /// key, and by it the ready line by channel type. A channel type the inner map holds takes its line from
    /// here; any other event of the code — one naming no channel, or a channel the map leaves out — takes
    /// the line of <see cref="ChannelStatusTexts"/>. The server decides the wording by the channel, the
    /// script only looks it up.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ChannelStatusTextsByChannel { get; init; }

    /// <summary>
    /// Names of the page's channels by channel type, by the rule the channel tabs are named with. A
    /// type outside the map is named by itself.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ChannelNames { get; init; }
}

/// <summary>
/// Lifecycle status names as the server spells them, handed to the page so that no status string is
/// hardcoded on the client.
/// </summary>
/// <param name="Confirmed">The user confirmed on the channel; the outcome is being processed.</param>
/// <param name="AwaitingWebConfirmation">The core asks the confirming question on a page of its own.</param>
/// <param name="ClaimCompletion">
/// The user confirmed the sign-in and the core waits for missing claims; the message names the step and
/// the surface of the web step (<see cref="AuthPageScriptClaimCompletion"/>).
/// </param>
/// <param name="Completed">Terminal success.</param>
/// <param name="Expired">The transaction ran out of time.</param>
/// <param name="Failed">Terminal failure — a refusal by the user or a fault, told apart by the reason.</param>
internal sealed record AuthPageScriptStatuses(
    string Confirmed,
    string AwaitingWebConfirmation,
    string ClaimCompletion,
    string Completed,
    string Expired,
    string Failed);

/// <summary>
/// The values of the claims completion status that take a page with a callback onto it, as the server
/// spells them — the names of the step and of the surface members, handed over for the same reason the
/// status names are. Any value alone is enough: the phone step is explained on the page of the
/// completion, the web step shown on the initiating page is that page, and the web step shown on the
/// confirming device leaves this browser the page that waits for it — the sign-in page, with its QR code
/// and channels, has nothing left to offer once the sign-in is confirmed. A web step whose surface is
/// not chosen yet names none of them, and the page keeps waiting.
/// </summary>
/// <param name="PhoneStep">Name of the phone step.</param>
/// <param name="InitiatingPageSurface">Name of the surface of the web step on the page the sign-in was started on.</param>
/// <param name="CrossDevicePageSurface">Name of the surface of the web step on a page of the device that confirmed the sign-in.</param>
internal sealed record AuthPageScriptClaimCompletion(
    string PhoneStep,
    string InitiatingPageSurface,
    string CrossDevicePageSurface);

/// <summary>
/// The page's texts, already localized and, where the caller worded a terminal outcome of its own,
/// already taken from the caller rather than from the renderer's sign-in wording.
/// </summary>
/// <param name="Confirmed">Line shown while a confirmed transaction is being processed.</param>
/// <param name="Success">Terminal line of a completed transaction.</param>
/// <param name="Expired">Terminal line of an expired transaction.</param>
/// <param name="Error">Terminal line of a failure that is not the user's refusal.</param>
/// <param name="Declined">Terminal line of a transaction the user declined.</param>
/// <param name="EmailSent">Confirmation that the letter was sent; carries the <c>{0}</c> slot of the address.</param>
/// <param name="EmailError">Line shown when the letter could not be sent; it names the mail rather than the link where the mail carries a code.</param>
/// <param name="EmailSendAgain">Text of the button of the email form once a letter has been sent.</param>
/// <param name="HopReopened">Warning of a hop link opened once more from another browser.</param>
internal sealed record AuthPageScriptTexts(
    string Confirmed,
    string Success,
    string Expired,
    string Error,
    string Declined,
    string EmailSent,
    string EmailError,
    string EmailSendAgain,
    string HopReopened);

/// <summary>
/// What the page needs for the step of the code of its email form (SPEC-016 §4): the start answers that the
/// mail went out with a code, the form shows the step on the same place and posts the code here.
/// </summary>
/// <param name="Url">Root-relative URL the code is posted to.</param>
/// <param name="CodeSent">Line over the code field once the mail is sent; carries <paramref name="AddressSlot"/>.</param>
/// <param name="AddressSlot">The slot of <paramref name="CodeSent"/> the masked address goes into.</param>
/// <param name="CheckError">Line shown when the code could not be checked and the server stated no reason.</param>
/// <param name="Length">Number of digits of a code: the field keeps no more than that.</param>
internal sealed record AuthPageScriptEmailCode(
    string Url,
    string CodeSent,
    string AddressSlot,
    string CheckError,
    int Length);

/// <summary>
/// The pair of hop codes whose lines the sign-in page shows as one (SPEC-025 HOP-101): the line of the opened
/// intermediate page waits a short hold and is not shown at all once the start of the transition into the
/// channel has been shown. The values are the server's, handed over for the same reason the status names are.
/// </summary>
/// <param name="InterstitialOpened">Channel status code of the opened intermediate page.</param>
/// <param name="ChannelLaunched">Channel status code of the started transition into the channel.</param>
internal sealed record AuthPageScriptHopLaunchCodes(
    string InterstitialOpened,
    string ChannelLaunched);

/// <summary>
/// The answer of the bot of a channel, as the page learns of it: the sender entered the transaction
/// and the answer is now awaited in the chat. The page hears it as a channel status — broadcast when it
/// happens and replayed by the hub to a page that subscribes afterwards — and acts on it once: it stops
/// offering the spare start codes, closes those it has opened and, where a line is given for the channel,
/// puts that line on the status line. Every value is the server's, the line included — the script words
/// nothing.
/// </summary>
/// <param name="StatusCode">Channel status code of the answer.</param>
/// <param name="Lines">
/// The ready line by channel type, for the channels of the page whose answer is awaited in a chat. A
/// channel outside the map leaves the status line alone. A sign-in and a confirmation are given the map
/// alike; only the wording of its lines follows the transaction.
/// </param>
internal sealed record AuthPageScriptChannelAnswer(
    string StatusCode,
    IReadOnlyDictionary<string, string> Lines);

/// <summary>
/// The spare start codes of a page: the message a person sends into a channel when its link opened the chat
/// and nothing started there. The page keeps them out of sight and opens the code of a channel when it has
/// reason to think the person needs it. Every value is the server's — the names of the markup and both
/// delays — so the script states none of its own.
/// <para>
/// When a code is opened. On a phone — the device the head marked as one, and the scanner page by itself —
/// no code of a panel opens: once the person comes back to the page after a press of the button of a channel
/// with a code, the block under the status line shows its link, for that channel alone, and the link opens
/// the copy of that channel's code. On a computer or a tablet, a page nobody has left yet opens the code of
/// every channel whose QR code is not a hop QR code after <paramref name="IdleDelayMs"/> from the load. Once
/// the hop has started, in the block shown then: the code of the channel the phone went into,
/// <paramref name="AfterTransitionDelayMs"/> after the first hop code naming that channel.
/// </para>
/// <para>
/// When a code is closed. The way back to the channel choice closes the codes of the block of the started
/// hop. The start of a transition into a channel closes the codes of every other channel in that block and
/// stops their delays: the person has left those channels. A press of another channel button takes the link
/// under the status line away until the next return. The answer of the bot
/// (<see cref="AuthPageScriptChannelAnswer"/>) closes every code of the page, takes the link away, and none
/// is opened after it: a code is offered only while the bot has not answered.
/// </para>
/// </summary>
/// <param name="Selector">Selector of the element that wraps the spare start code of one channel.</param>
/// <param name="ChannelAttribute">Attribute of that element naming the channel type.</param>
/// <param name="OpenClass">Class the script adds to that element to show the code.</param>
/// <param name="ChannelButtonSelector">Selector of the channel button standing in the same panel as the element.</param>
/// <param name="IdleDelayMs">Delay from the load of the page, in milliseconds.</param>
/// <param name="AfterTransitionDelayMs">Delay from the start of a transition into a channel, in milliseconds.</param>
/// <param name="PhoneByPage">
/// <see langword="true"/> — the page is a phone whatever the head established: the scanner page, opened by
/// the phone that scanned the QR code.
/// </param>
/// <param name="DeviceAttribute">Name of the root attribute the head sets once it has established the device.</param>
/// <param name="PhoneDeviceValue">Value of <paramref name="DeviceAttribute"/> for a phone.</param>
/// <param name="HopQrChannels">The channel types whose QR code on the page is a hop QR code.</param>
/// <param name="ReturnElementId">Identifier of the block under the status line, hidden until the script shows it.</param>
/// <param name="ReturnLinkElementId">Identifier of the link of that block that opens the copy of the code.</param>
internal sealed record AuthPageScriptSpareCode(
    string Selector,
    string ChannelAttribute,
    string OpenClass,
    string ChannelButtonSelector,
    int IdleDelayMs,
    int AfterTransitionDelayMs,
    bool PhoneByPage,
    string DeviceAttribute,
    string PhoneDeviceValue,
    IReadOnlyList<string> HopQrChannels,
    string ReturnElementId,
    string ReturnLinkElementId);

/// <summary>
/// The "Any channel" tab, as the script switches it: the tab standing first in the strip and naming no
/// channel, selected on the load. While it is selected the channel container carries
/// <paramref name="StateClass"/> — the stylesheet then shows the unified QR code and every channel panel as
/// a button without its own QR code — and the panels carry no tab roles; a channel tab takes the class
/// off and gives the panels the attributes of a tab panel back. Every value is the server's, handed over for
/// the same reason the status names are.
/// </summary>
/// <param name="TabElementId">Identifier of the "Any channel" tab.</param>
/// <param name="StateClass">Class of the channel container while the "Any channel" tab is selected.</param>
internal sealed record AuthPageScriptAnyChannelTab(
    string TabElementId,
    string StateClass);

/// <summary>
/// The lead instruction of a page with tabs, as the script switches it by the selected tab (SPEC-007 UI-121): a
/// wording of the instruction carries one variant per tab, each naming the identifier of its tab in
/// <paramref name="TabAttribute"/>. On a change of tab the script puts <paramref name="HiddenClass"/> on every
/// variant but those of the selected tab; the stylesheet keeps a hidden variant's place, so the block does not
/// move, and the change is not announced. Every value is the server's, handed over for the same reason the
/// status names are.
/// </summary>
/// <param name="WordingClass">Class of a variant.</param>
/// <param name="HiddenClass">Class of a variant of a tab that is not selected.</param>
/// <param name="TabAttribute">Name of the attribute of a variant naming the identifier of its tab.</param>
internal sealed record AuthPageScriptTabWordings(
    string WordingClass,
    string HiddenClass,
    string TabAttribute);

/// <summary>
/// The state of the chosen channel of the scanner page (SPEC-025 HOP-193), as the script switches it. A tap on
/// a link of the channel container that carries <paramref name="ChannelAttribute"/> sets
/// <paramref name="StateAttribute"/> on the root — the stylesheet then takes the title of the page, the lead
/// instruction and the channel choice away and shows the way back — and shows, of the headings inside
/// <paramref name="HeadingsElementId"/>, the one whose <paramref name="ChannelAttribute"/> names the channel
/// tapped. The way back takes the attribute off, hides every heading, takes the block of the spare start code
/// away and forgets the tap, without a request. The answer of a channel takes the way back off the page for
/// good and leaves the heading shown; the line sending the person back to the sign-in window, the
/// confirmation being processed and every ending take both away (<see cref="PageStatusBlock"/>). Every value
/// is the server's, handed over for the same reason the status names are; the headings themselves are in the
/// markup.
/// </summary>
/// <param name="StateAttribute">Name of the root attribute of the state; only its presence counts.</param>
/// <param name="ChannelAttribute">Name of the attribute naming the channel type on a link and on a heading.</param>
/// <param name="HeadingsElementId">Identifier of the block of the headings.</param>
/// <param name="BackElementId">Identifier of the button that brings the channel choice back.</param>
internal sealed record AuthPageScriptChosenChannel(
    string StateAttribute,
    string ChannelAttribute,
    string HeadingsElementId,
    string BackElementId);

/// <summary>
/// The warning of a hop link opened once more from another browser, as the sign-in page shows it: a label put
/// into an empty live region when the channel status reporting the reopening arrives. The status line is not
/// touched — the code has no text in <see cref="AuthPageScriptConfig.ChannelStatusTexts"/>. Every value is the
/// server's, handed over for the same reason the status names are.
/// </summary>
/// <param name="StatusCode">Channel status code reporting the reopening.</param>
/// <param name="ElementId">
/// Identifier of the live region the label goes into; a page without the element (the scanner page of the
/// unified hop QR code) ignores the code.
/// </param>
/// <param name="BadgeClass">Class of the label (the StatusBadge primitive).</param>
internal sealed record AuthPageScriptHopReopened(
    string StatusCode,
    string ElementId,
    string BadgeClass);

/// <summary>
/// The client signal of the chosen channel as the page sends it: the names of the body fields and the value
/// of the signal, handed over for the same reason the status names are, and the attribute of the channel
/// link that carries the channel type the signal names.
/// </summary>
/// <param name="Attribute">Name of the attribute of a channel link whose tap sends the signal; its value is the channel type.</param>
/// <param name="SignalField">Name of the body field carrying the signal.</param>
/// <param name="ChannelField">Name of the body field carrying the channel type.</param>
/// <param name="Signal">Value of the signal.</param>
internal sealed record AuthPageScriptChannelLaunch(
    string Attribute,
    string SignalField,
    string ChannelField,
    string Signal);

/// <summary>
/// The automatic launch of the scanner page of one channel, as the page and the script of the launch speak
/// to each other over document events. The launch announces a transition — one it begins now, or one made
/// before the document was shown — and the page reacts to a transition it begins as to a tap on the
/// mail-client button, the signal of the chosen channel included; on one made before, it gives the line of
/// the opened mail, or the first line of the page, the place of the wait for the confirmation. A tap on the
/// button is announced to the launch, which then does not open the mail a second time. The names are the
/// server's, handed over for the same reason the status names are.
/// </summary>
/// <param name="EventName">Name of the event the launch announces a transition by.</param>
/// <param name="ReturnedField">Field of the detail of that event: true — the transition was made before the document was shown.</param>
/// <param name="PressEventName">Name of the event the page announces a tap on the mail-client button by.</param>
internal sealed record AuthPageScriptHopTransition(
    string EventName,
    string ReturnedField,
    string PressEventName);

/// <summary>
/// Addresses of the SignalR hub of the window: where to connect and what the methods are called.
/// Application constants, handed over for the same reason the status names are.
/// </summary>
/// <param name="Path">Root-relative path of the hub.</param>
/// <param name="StatusMethod">Hub method announcing a lifecycle status change.</param>
/// <param name="ChannelStatusMethod">Hub method announcing a channel-specific intermediate status.</param>
/// <param name="JoinMethod">Hub method the page calls to join the transaction's group.</param>
/// <param name="ReissueHopQrMethod">Hub method the page calls for the replacement of its hop QR codes.</param>
internal sealed record AuthPageScriptHub(
    string Path,
    string StatusMethod,
    string ChannelStatusMethod,
    string JoinMethod,
    string ReissueHopQrMethod);

/// <summary>
/// The replacement of the hop QR codes of the page before their tokens expire: when to ask the hub, how to
/// draw the answer and where the QR codes are. Every value is the server's, handed over for the same reason
/// the status names are.
/// </summary>
/// <param name="Fraction">
/// Share of the remainder of a token after which the page asks for its replacement, counted from the moment
/// the remainder was received.
/// </param>
/// <param name="RetryDelayMs">Delay after a failed replacement before the page asks again, in milliseconds.</param>
/// <param name="PngDataUriPrefix">Data URI prefix the Base64 PNG of an answer is drawn with.</param>
/// <param name="ImageAttribute">Name of the attribute the image of a replaced QR code carries; its value is the entry.</param>
/// <param name="NotVisibleSelector">
/// Selector the root element matches while no hop QR code is visible on the page; the page asks for nothing
/// while it matches.
/// </param>
/// <param name="Entries">The QR codes the page replaces, with the remainders of their tokens.</param>
internal sealed record AuthPageScriptHopQrReissue(
    double Fraction,
    int RetryDelayMs,
    string PngDataUriPrefix,
    string ImageAttribute,
    string NotVisibleSelector,
    IReadOnlyList<AuthPageScriptHopQrEntry> Entries);

/// <summary>
/// One QR code the page replaces.
/// </summary>
/// <param name="Entry">
/// What the page asks the hub by: the channel type of a per-channel QR code, empty for the unified one.
/// </param>
/// <param name="RemainingMs">Milliseconds left of the token of the QR code by the server's clock at the render.</param>
internal sealed record AuthPageScriptHopQrEntry(string Entry, long RemainingMs);
