// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// Client script of the sign-in window of the "Core" contour (rendered by
// DefaultAuthPageRenderer): the SignalR subscription, the polling fallback, the countdown,
// the channel tabs and the buttons layout, the tooltip, the email form, the
// signal of the chosen channel and the replacement of the hop QR codes.
//
// The file ships as an embedded resource of Veriqa.Core.AuthServer and is INLINED into the
// generated document inside a <script> tag carrying the page's CSP nonce — it is never served
// over a URL, because the page has to stay self-contained and no second request may stand
// between the user and the window.
//
// Every comment of this file reaches the end user's browser verbatim, so the norms its code
// implements are named in AuthPageScript (C#), not here.
//
// Nothing of the server is spelled into this file. Every value the request decides — texts,
// URLs, status names, timings — arrives in the JSON data block the renderer emits directly
// above it (<script type="application/json" id="veriqa-auth-config">), which is what keeps
// this a real .js file: a linter, an editor and a reviewer read it as code instead of as a
// C# string literal.
(function() {
    "use strict";

    // The one seam with the server (see the header): the page's configuration, read once.
    // The block is emitted by the same renderer that inlines this file, so its absence is not
    // a state the page can be in.
    // The id below reads the block emitted by the constant AuthPageScript.ConfigElementId: the
    // reader and the emitter are what bind, and nothing checks that they agree — not the compiler,
    // not the component tests, which read the markup only. Rename them together; the name as it
    // appears in comments and documents (the header above included) binds nothing and follows by
    // a grep for the old value.
    var config = JSON.parse(document.getElementById("veriqa-auth-config").textContent);
    var sessionId = config.sessionId;

    // Application path base (issuer under a sub-path of the origin, e.g. /demo).
    // All root-relative page URLs (fetch, SignalR, redirect from
    // server messages) are normalized via withBase — the server sends them
    // without the prefix (built from application constants).
    var BASE_PATH = config.basePath;
    function withBase(url) {
        if (!BASE_PATH || !url) return url;
        // Only root-relative paths ("/x", but not protocol-relative "//x")
        return (url.charAt(0) === "/" && url.charAt(1) !== "/") ? BASE_PATH + url : url;
    }

    var statusEl = document.getElementById("veriqa-status");
    // The line the page was loaded with, before the script wrote any: the
    // waiting line, which the wait for the confirmation of the mail may take
    // the place of. A page rendered with the line of the answer of a
    // channel's bot keeps that line, so the value is dropped there (see the
    // answer of the bot below).
    var initialStatusText = statusEl.textContent;
    var spinnerEl = document.getElementById("veriqa-spinner");
    var countdownEl = document.getElementById("veriqa-countdown");

    // The form behind the cancel button, by the identifier the server hands over.
    // A page rendered without the button has no such element.
    var cancelFormEl = document.getElementById(config.cancelFormElementId);

    // The form is sent once. The first request ends the transaction, so a repeat
    // of it — a double click, a key held down on a remote — would be answered
    // with a page saying the transaction is gone instead of its ending. The
    // repeat is dropped here; the first submit is left exactly as the browser
    // makes it, and the button is not disabled: a disabled control is a state
    // some browsers restore together with the page, and this latch is not.
    var cancelSubmitted = false;
    if (cancelFormEl) {
        cancelFormEl.addEventListener("submit", function(event) {
            if (cancelSubmitted) {
                event.preventDefault();
                return;
            }
            cancelSubmitted = true;
            // From here the browser is leaving through the form, and the page
            // does not leave on its own until this life of it is over: its own
            // navigation would abort the request that ends the transaction, or
            // follow it onto a page the cancel has already closed.
            abandonNavigation();
        });
    }

    // The moment the countdown runs to, expressed on THIS browser's clock: the
    // server's remainder added to the browser's own reading of "now". A clock
    // that is minutes off cancels out of the difference, so only its drift over
    // the lifetime of one transaction is left, and that is negligible.
    var deadline = Date.now() + config.remainingMs;
    var msgConfirmed = config.texts.confirmed;
    var msgSuccess = config.texts.success;
    var msgExpired = config.texts.expired;
    var msgError = config.texts.error;
    var msgDeclined = config.texts.declined;

    // The one reason code the terminal line is branched on: a refusal by the user
    // and a fault both arrive as Failed, and only the reason tells them apart
    // (no hardcoding on the client — the code is the server's constant).
    var REASON_DECLINED = config.declinedReasonCode;
    // The reason code of a sign-in closed because the same user started a newer
    // one. It arrives as Failed as well, and is shown as an expired session.
    var REASON_SUPERSEDED = config.supersededReasonCode;

    // Statuses from TransactionStatusNames (no hardcoding on the client)
    var STATUS_CONFIRMED = config.statuses.confirmed;
    var STATUS_AWAITING_WEB = config.statuses.awaitingWebConfirmation;
    var STATUS_CLAIM_COMPLETION = config.statuses.claimCompletion;
    var STATUS_COMPLETED = config.statuses.completed;
    var STATUS_EXPIRED = config.statuses.expired;
    var STATUS_FAILED = config.statuses.failed;

    // The step and the surfaces of the claims completion that take this page
    // onto its callback, spelled by the server (no hardcoding on the client).
    var CLAIM_STEP_PHONE = config.claimCompletion.phoneStep;
    var CLAIM_SURFACE_INITIATING_PAGE = config.claimCompletion.initiatingPageSurface;
    var CLAIM_SURFACE_CROSS_DEVICE_PAGE = config.claimCompletion.crossDevicePageSurface;

    // Intermediate channel statuses of any channel: one map of hint texts,
    // the names of the page's channels for a text that names one, and the
    // codes that mark the start of the hop (no hardcoding)
    var CHANNEL_STATUS_TEXTS = config.channelStatusTexts;
    var CHANNEL_STATUS_TEXTS_BY_CHANNEL = config.channelStatusTextsByChannel;
    var CHANNEL_NAMES = config.channelNames;
    var HOP_STATUS_CODES = config.hopStatusCodes;

    // The warning of a hop link opened once more from another browser: the
    // code that reports it, the live region it goes into and the class of the
    // label, all spelled by the server (no hardcoding)
    var HOP_REOPENED = config.hopReopened;

    var isTerminal = false;

    // The page leaves for the callback exactly once. The status that sends it
    // there is NOT terminal (the transaction stays pending until the user
    // answers on the next page), so isTerminal cannot serve as that guard: a
    // later poll tick or SignalR message would otherwise reassign
    // window.location while the browser is already navigating.
    var isNavigating = false;

    // The layout of the channels: tabs, or a list of buttons. The stylesheet
    // decides it; the script only asks the channel container which one is on
    // screen, by the selector the server hands over, so the device is
    // established once, by the head script, and never a second time here.
    var channelsEl = document.getElementById(config.channelsElementId);
    var isButtonsLayout = !!channelsEl && channelsEl.matches(config.buttonsLayoutSelector);

    // In the buttons layout the tab strip is gone and every panel is on
    // screen, so a panel is no tab panel: it loses the role, the label pointing
    // at a hidden tab and its own stop in the tab order. A page the server lays
    // out as buttons never had them; a phone showing the tab markup as buttons
    // has them taken off here. The same holds for a phone showing the grid
    // markup as buttons: its panels are groups labelled by the channel names the
    // stylesheet hides there, and they lose that role and that label.
    if (isButtonsLayout) {
        document.querySelectorAll('.veriqa-panel').forEach(function(p) {
            p.removeAttribute('role');
            p.removeAttribute('aria-labelledby');
            p.removeAttribute('tabindex');
        });
    }

    // The "Any channel" tab of a page that shows the unified QR code next to
    // the QR code of each channel; null — the page has no such tab. While it is
    // selected the channel container carries the state class named by the
    // server: the stylesheet shows the unified QR code and every panel as a
    // button, and the panels carry no tab roles, as in the buttons layout.
    var anyChannelTab = config.anyChannelTab;

    // The panels of the channel tabs as tab panels, each labelled by its own
    // tab, or as plain blocks. Only a tab that names a channel controls a
    // panel; the "Any channel" tab controls the block of the unified QR code.
    function setTabPanelRoles(asTabPanels) {
        channelTabs.forEach(function(tab) {
            if (anyChannelTab && tab.id === anyChannelTab.tabElementId) return;
            var panel = document.getElementById(tab.getAttribute('aria-controls'));
            if (!panel) return;
            if (asTabPanels) {
                panel.setAttribute('role', 'tabpanel');
                panel.setAttribute('aria-labelledby', tab.id);
                panel.setAttribute('tabindex', '0');
            } else {
                panel.removeAttribute('role');
                panel.removeAttribute('aria-labelledby');
                panel.removeAttribute('tabindex');
            }
        });
    }

    // The lead instruction words the selected tab: every wording carries a
    // variant per tab, and only those of the selected tab stay on screen. The
    // others keep their place (the stylesheet hides them by visibility), so the
    // block does not move; nothing is announced — the reader already says which
    // tab is selected. Null — the instruction is not worded by tab.
    var tabWordings = config.tabWordings;
    function showTabWordings(selected) {
        if (!tabWordings) return;
        document.querySelectorAll('.' + tabWordings.wordingClass).forEach(function(el) {
            if (el.getAttribute(tabWordings.tabAttribute) === selected.id) {
                el.classList.remove(tabWordings.hiddenClass);
            } else {
                el.classList.add(tabWordings.hiddenClass);
            }
        });
    }

    // Tab switching — via addEventListener, without inline onclick.
    // The panel of an unselected tab is hidden by a class, never by an inline
    // style, so the stylesheet can still show every panel in the buttons layout
    // and in the state of the "Any channel" tab. One predicate — the tab being
    // the selected one — drives the active class, aria-selected and the roving
    // tabindex, so they can never diverge.
    function selectTab(selected) {
        if (anyChannelTab && channelsEl) {
            var isAnyChannel = selected.id === anyChannelTab.tabElementId;
            if (isAnyChannel) {
                channelsEl.classList.add(anyChannelTab.stateClass);
            } else {
                channelsEl.classList.remove(anyChannelTab.stateClass);
            }
            setTabPanelRoles(!isAnyChannel);
        }
        showTabWordings(selected);
        document.querySelectorAll('.veriqa-panel').forEach(function(p) { p.classList.add('veriqa-panel--inactive'); });
        channelTabs.forEach(function(t) {
            var isSelected = t === selected;
            if (isSelected) {
                t.classList.add('active');
            } else {
                t.classList.remove('active');
            }
            t.setAttribute('aria-selected', isSelected ? 'true' : 'false');
            t.setAttribute('tabindex', isSelected ? '0' : '-1');
        });
        var channel = selected.getAttribute('data-channel');
        var panel = channel ? panelOf(channel) : null;
        if (panel) panel.classList.remove('veriqa-panel--inactive');
    }

    // The panel of a channel, by the identifier the server builds from the
    // prefix it hands over and the channel type; null — no such panel.
    function panelOf(channelType) {
        return document.getElementById(config.panelElementIdPrefix + channelType);
    }

    // Binding handlers to the tabs: click plus roving arrow-key navigation
    // (WAI-ARIA Tabs pattern, automatic activation — selection follows focus).
    // ArrowRight/ArrowLeft move focus and selection to the adjacent tab in DOM
    // order with wrap-around, the "Any channel" tab included; the switch itself
    // reuses selectTab (no duplicated logic). Behavioral parity with the
    // Site-contour preview (bindArrowNav).
    // The buttons layout has no tabs to bind. Neither has the grid layout: the
    // server renders no tab there, so the list below is empty, nothing calls
    // selectTab and every panel stays on screen.
    var channelTabs = isButtonsLayout ? [] : Array.prototype.slice.call(document.querySelectorAll('.veriqa-tab'));
    channelTabs.forEach(function(tab, index) {
        tab.addEventListener('click', function() {
            selectTab(this);
        });
        tab.addEventListener('keydown', function(e) {
            var forward = e.key === 'ArrowRight';
            var backward = e.key === 'ArrowLeft';
            if (!forward && !backward) return;
            e.preventDefault();
            var next = channelTabs[(index + (forward ? 1 : -1) + channelTabs.length) % channelTabs.length];
            selectTab(next);
            next.focus();
        });
    });

    // The channel the user went through on this device, remembered in a
    // cookie of the host as its type and nothing else. Null — the page
    // remembers nothing. The names, the lifetime and the attributes of the
    // cookie, the moments of the write and the label are all the server's.
    var REMEMBER_CHANNEL = config.rememberChannel;

    // A remembered value counts only where it is a channel of this page: any
    // other value — a channel the page does not carry, a value that is no
    // channel at all — leaves the page as it is without the cookie, and never
    // reaches the markup or a selector.
    function isPageChannel(channelType) {
        return !!REMEMBER_CHANNEL && typeof channelType === 'string'
            && REMEMBER_CHANNEL.channelTypes.indexOf(channelType) !== -1;
    }

    // The channel a cookie names, or null. A browser that refuses the page its
    // cookies (a sandboxed frame) throws on the access, and a value that is not
    // a valid encoding throws on the decoding: either reads as no cookie.
    function readChannelCookie(cookie) {
        var prefix = cookie.name + '=';
        try {
            var pairs = document.cookie.split('; ');
            for (var i = 0; i < pairs.length; i++) {
                if (pairs[i].indexOf(prefix) !== 0) continue;
                var channelType = decodeURIComponent(pairs[i].substring(prefix.length));
                return isPageChannel(channelType) ? channelType : null;
            }
        } catch (e) {
            // No cookie can be read: the page stays as it is without one.
        }
        return null;
    }

    function setChannelCookie(cookie, value, maxAgeSeconds) {
        try {
            document.cookie = cookie.name + '=' + value
                + '; Max-Age=' + maxAgeSeconds + '; ' + cookie.attributes;
        } catch (e) {
            // A browser that refuses cookies leaves nothing remembered.
        }
    }

    function writeChannelCookie(cookie, channelType) {
        setChannelCookie(cookie, encodeURIComponent(channelType), cookie.maxAgeSeconds);
    }

    // A cookie with no lifetime left is deleted, under the same attributes
    // it was written with.
    function deleteChannelCookie(cookie) {
        setChannelCookie(cookie, '', 0);
    }

    // The checkbox that remembers the channel, on a page of the unified QR
    // code; null — the page does not offer it. Its block is in the markup,
    // hidden: it is shown on a desktop alone — never on a device the head
    // marked a phone.
    var REMEMBER_CHECKBOX = REMEMBER_CHANNEL && REMEMBER_CHANNEL.checkbox;
    var rememberEl = REMEMBER_CHECKBOX ? document.getElementById(REMEMBER_CHECKBOX.elementId) : null;
    var rememberShown = !!rememberEl && document.documentElement.getAttribute(REMEMBER_CHECKBOX.deviceAttribute)
        !== REMEMBER_CHECKBOX.phoneDeviceValue;
    var rememberInput = rememberShown ? document.getElementById(REMEMBER_CHECKBOX.inputElementId) : null;
    var rememberLabel = rememberShown ? document.getElementById(REMEMBER_CHECKBOX.labelElementId) : null;
    var rememberBack = rememberShown ? document.getElementById(REMEMBER_CHECKBOX.backElementId) : null;
    // The way back stands in a block of its own with the divider above it:
    // the block is what is shown and hidden.
    var rememberBackBlock = rememberBack ? rememberBack.parentNode : null;
    // The channel that has worked on this page, kept for a tick that comes
    // after it; the channel the page shows alone (the state of the remembered
    // channel), null — the page shows every channel.
    var workingChannel = null;
    var rememberedChannel = null;

    // The one point every way of learning that a channel works goes through —
    // a status of the channel on the window, a tap on the scanner page. Where
    // the checkbox is on screen only it keeps the channel, and only while it is
    // ticked; elsewhere the hint keeps the channel for the next visit.
    function noteWorkingChannel(channelType) {
        if (!isPageChannel(channelType)) return;
        if (rememberShown) {
            workingChannel = channelType;
            if (rememberInput.checked) writeChannelCookie(REMEMBER_CHECKBOX.cookie, channelType);
            return;
        }
        if (REMEMBER_CHANNEL.hint) writeChannelCookie(REMEMBER_CHANNEL.hint.cookie, channelType);
    }

    // The scanner page hears no answer meant for it: a tap on a link into a
    // channel is the moment there. The channel is the one whose panel holds
    // the link.
    if (REMEMBER_CHANNEL && REMEMBER_CHANNEL.tapSelector) {
        REMEMBER_CHANNEL.channelTypes.forEach(function(channelType) {
            var panel = panelOf(channelType);
            if (!panel) return;
            panel.querySelectorAll(REMEMBER_CHANNEL.tapSelector).forEach(function(link) {
                link.addEventListener('click', function() { noteWorkingChannel(channelType); });
            });
        });
    }

    // The label of the remembered channel, put at the end of a tab or of a
    // channel button.
    function appendLastUsedLabel(host, hint) {
        var label = document.createElement('span');
        label.className = hint.labelClass;
        label.textContent = hint.labelText;
        host.appendChild(label);
    }

    // Where the channels stand as buttons, the panel of the remembered channel
    // moves ahead of the panel of the first channel of the set, so the order
    // of the focus is the order on screen, and its button carries the label.
    function putChannelFirst(channelType, hint) {
        var panel = panelOf(channelType);
        var firstPanel = panelOf(REMEMBER_CHANNEL.channelTypes[0]);
        if (!panel || !firstPanel) return;
        if (panel !== firstPanel) firstPanel.parentNode.insertBefore(panel, firstPanel);
        var button = panel.querySelector(hint.buttonSelector);
        if (button) appendLastUsedLabel(button, hint);
    }

    // The hint on the load, by the layout on screen. A page of one channel has
    // nothing to put forward, and the grid of a desktop shows every channel
    // already: it has neither tabs nor the buttons layout, and stays as it is.
    function showLastChannelHint() {
        var hint = REMEMBER_CHANNEL && REMEMBER_CHANNEL.hint;
        if (!hint || REMEMBER_CHANNEL.channelTypes.length < 2) return;
        var channelType = readChannelCookie(hint.cookie);
        if (!channelType) return;
        if (isButtonsLayout) {
            putChannelFirst(channelType, hint);
            return;
        }
        var tab = null;
        channelTabs.forEach(function(t) {
            if (t.getAttribute('data-channel') === channelType) tab = t;
        });
        // Next to the "Any channel" tab the page stays on that tab: the tab of
        // the channel is labelled, and the buttons of its state put the
        // channel first. Behind plain tabs the page opens on its tab.
        if (anyChannelTab) {
            if (tab) appendLastUsedLabel(tab, hint);
            putChannelFirst(channelType, hint);
        } else if (tab) {
            selectTab(tab);
        }
    }

    // The label of the checkbox: the channel it keeps on the page that shows
    // that channel alone while the box is ticked, the offer to remember
    // otherwise. The name of the channel takes the slot of the server's text,
    // through a function so that no "$" pattern of a name is expanded.
    function wordRememberCheckbox() {
        if (rememberedChannel && rememberInput.checked) {
            var name = Object.prototype.hasOwnProperty.call(CHANNEL_NAMES, rememberedChannel)
                ? CHANNEL_NAMES[rememberedChannel]
                : rememberedChannel;
            rememberLabel.textContent = REMEMBER_CHECKBOX.alwaysText.replace('{0}', function() { return name; });
        } else {
            rememberLabel.textContent = REMEMBER_CHECKBOX.rememberText;
        }
    }

    // The strip of the tabs, and whether it is on screen in the state of the
    // "Any channel" tab: the page that keeps the QR codes of its channels for
    // the checkbox alone renders it hidden.
    var anyChannelTabEl = anyChannelTab ? document.getElementById(anyChannelTab.tabElementId) : null;
    var tabStripEl = anyChannelTabEl ? anyChannelTabEl.parentNode : null;
    var tabStripShown = !!tabStripEl && !tabStripEl.hidden;

    // The checkbox on the load. A cookie naming a channel of the page opens
    // the page in the state of the remembered channel: the state of the tab
    // of that channel — its panel alone with its own QR code, worded by its
    // tab — with the strip out of sight, the box ticked and the way back
    // under the lead instruction. The panel is no tab panel there: no tab on
    // screen labels it. Returns whether the page opened in that state.
    function showRememberCheckbox() {
        if (!rememberShown) return false;
        rememberEl.hidden = false;
        var channelType = readChannelCookie(REMEMBER_CHECKBOX.cookie);
        var tab = null;
        channelTabs.forEach(function(t) {
            if (channelType && t.getAttribute('data-channel') === channelType) tab = t;
        });
        if (tab) {
            selectTab(tab);
            setTabPanelRoles(false);
            if (tabStripEl) tabStripEl.hidden = true;
            rememberedChannel = channelType;
            rememberInput.checked = true;
            rememberBackBlock.hidden = false;
        }
        wordRememberCheckbox();
        return !!tab;
    }

    if (rememberShown) {
        // Ticked, the box keeps the channel that has worked already, if one
        // has; unticked, it deletes the cookie at once. The page stays as it
        // is either way — only the label changes.
        rememberInput.addEventListener('change', function() {
            if (rememberInput.checked) {
                if (workingChannel) writeChannelCookie(REMEMBER_CHECKBOX.cookie, workingChannel);
            } else {
                deleteChannelCookie(REMEMBER_CHECKBOX.cookie);
            }
            wordRememberCheckbox();
        });
        // The way back from the remembered channel to every channel: the
        // state of the "Any channel" tab and its strip as the page has it,
        // without asking the server — the transaction is the same one. The box
        // stays as the user left it, and offers to remember again. The focus
        // follows the content that returned.
        rememberBack.addEventListener('click', function() {
            if (isTerminal) return;
            rememberedChannel = null;
            rememberBackBlock.hidden = true;
            if (tabStripEl) tabStripEl.hidden = !tabStripShown;
            if (anyChannelTabEl) selectTab(anyChannelTabEl);
            wordRememberCheckbox();
            if (channelsEl) channelsEl.focus();
        });
    }

    // The remembered channel opens the page in its state, and the hint has
    // nothing to put forward there; any other page shows the hint.
    if (!showRememberCheckbox()) showLastChannelHint();

    // The state of the load is applied: the channels the head held out of sight for it come on screen.
    if (REMEMBER_CHECKBOX) document.documentElement.removeAttribute(REMEMBER_CHECKBOX.heldAttribute);

    // Tooltip of a clipped third-party label: it opens on hover
    // AND on keyboard focus and closes on Escape — the native title attribute
    // did neither, and on a touch surface it never appeared at all. The full
    // label stays in the DOM, so the accessible name is complete without the
    // bubble; the bubble repeats it visually and is hidden from assistive tech
    // (aria-hidden) so the name is not announced twice.

    // Keyboard focus, told apart from focus a mouse click left behind:
    // :focus-visible is exactly that distinction and is already the focus
    // indicator of the contour in CSS. Every target browser supports it, but
    // matches() throws on a selector an older one does not know, and the safe
    // answer there is "not keyboard" - the bubble then closes on pointer leave
    // instead of hanging over the page.
    function hasKeyboardFocus(el) {
        try { return !!el && el.matches(':focus-visible'); } catch (e) { return false; }
    }

    document.querySelectorAll('.veriqa-tooltip-host').forEach(function(host) {
        var bubble = host.querySelector('.veriqa-tooltip');
        var triggers = host.querySelectorAll('[data-veriqa-tooltip]');
        if (!bubble || triggers.length === 0) return;
        // One host serves the whole tab strip, so the bubble has an owner:
        // the trigger whose label it currently shows. "Something inside the
        // host is focused" is a different question and must not be mistaken
        // for it.
        var shownFor = null;
        function hideTooltip() { bubble.hidden = true; shownFor = null; }
        triggers.forEach(function(trigger) {
            // The labels the bubble stands in for - a tab that carries the
            // glyph of a QR code splits its label around it. A label that fits
            // shows its whole text already, and a bubble repeating it would only
            // cover the page; the widths are measured on every opening, because
            // the layout - the tab, the panel, the window - may have changed.
            var labels = trigger.querySelectorAll('.veriqa-tab-label, .veriqa-btn-label');
            function isClipped() {
                for (var i = 0; i < labels.length; i++) {
                    if (labels[i].scrollWidth > labels[i].clientWidth) return true;
                }
                return labels.length === 0;
            }
            function showTooltip() {
                if (!isClipped()) {
                    if (shownFor !== null && shownFor !== trigger) hideTooltip();
                    return;
                }
                bubble.textContent = trigger.getAttribute('data-veriqa-tooltip');
                bubble.hidden = false;
                shownFor = trigger;
            }
            trigger.addEventListener('mouseenter', showTooltip);
            trigger.addEventListener('focus', showTooltip);
            // Losing focus dismisses the bubble only while it still belongs to
            // this trigger: the pointer may have handed it over to another one.
            trigger.addEventListener('blur', function() {
                if (shownFor === trigger) hideTooltip();
            });
        });
        // The pointer may travel from the trigger onto the bubble itself
        // (WCAG 1.4.13, "hoverable"), so dismissal is bound to the host as a
        // whole. The bubble outlives the pointer only while its OWN trigger
        // holds the KEYBOARD focus. Plain "is the active element" is not that
        // test: a click gives the clicked tab the focus too, and the bubble
        // then stayed hanging over the QR code, swallowing clicks until Escape.
        host.addEventListener('mouseleave', function() {
            if (shownFor !== document.activeElement || !hasKeyboardFocus(shownFor)) hideTooltip();
        });
    });

    // Escape dismisses any open tooltip, including one opened by hover
    // (WCAG 1.4.13, "dismissible")
    document.addEventListener('keydown', function(e) {
        if (e.key !== 'Escape') return;
        document.querySelectorAll('.veriqa-tooltip').forEach(function(openBubble) {
            openBubble.hidden = true;
        });
    });

    // Email form: sending the magic link via AJAX. The field stands in a real
    // form, so the request leaves on its submit — the button and the Enter key
    // alike — and the form itself is never posted: the page stays.
    var emailSubmitBtn = document.getElementById("veriqa-email-submit");
    var emailForm = emailSubmitBtn ? emailSubmitBtn.form : null;

    // The button after an answer of the server. A sent link and a refusal by
    // the interval between two mails both say for how long another one will
    // not go out; the button waits that long and comes back by itself, so the
    // address stays editable and nothing is locked for good. No time stated —
    // the button is back at once.
    var emailResendTimer = null;
    function holdEmailSubmit(seconds) {
        clearTimeout(emailResendTimer);
        emailResendTimer = null;
        if (!(seconds > 0)) {
            emailSubmitBtn.disabled = false;
            return;
        }
        emailSubmitBtn.disabled = true;
        emailResendTimer = setTimeout(function() {
            emailResendTimer = null;
            emailSubmitBtn.disabled = false;
        }, seconds * 1000);
    }

    // How long the server asks to wait, in seconds: the figure of the answer
    // body where it carries one, otherwise the Retry-After header of a refusal
    // that came from the request limit and has no such body. Zero — no wait
    // was stated, or what was stated is not a number of seconds.
    function emailRetryAfterSeconds(data, retryAfterHeader) {
        if (data && typeof data.retryAfterSeconds === 'number' && data.retryAfterSeconds > 0) {
            return data.retryAfterSeconds;
        }
        var fromHeader = parseInt(retryAfterHeader, 10);
        return fromHeader > 0 ? fromHeader : 0;
    }

    if (emailForm) {
        emailForm.addEventListener('submit', function(event) {
            event.preventDefault();

            // A terminal page sends nothing. The form is hidden by the state
            // attribute, but hiding is a rule of the stylesheet and this is the
            // handler's own guard: a submit that still reaches it — a keyboard
            // activation racing the status, an integrator's custom CSS — must
            // not start a transaction the server has already closed.
            if (isTerminal) return;

            // A request is in flight or the button is being held: the Enter
            // key must not send what the button would not.
            if (emailSubmitBtn.disabled) return;

            var emailInput = document.getElementById("veriqa-email-input");
            var msgEl = document.getElementById("veriqa-email-msg");
            var emailVal = emailInput ? emailInput.value.trim() : '';
            var sid = emailSubmitBtn.getAttribute('data-session-id') || '';

            if (!emailVal) return;

            clearTimeout(emailResendTimer);
            emailResendTimer = null;
            emailSubmitBtn.disabled = true;
            msgEl.style.display = 'none';
            msgEl.className = 'veriqa-email-msg';

            fetch(withBase(config.emailStartUrl), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ email: emailVal, session_id: sid })
            })
            .then(function(resp) {
                var retryAfterHeader = resp.headers.get('Retry-After');
                function answer(data) {
                    return { status: resp.status, retryAfterHeader: retryAfterHeader, data: data };
                }
                var ct = resp.headers.get('content-type') || '';
                if (ct.indexOf('application/json') !== -1) {
                    return resp.json().then(answer);
                }
                return answer({ error: config.texts.emailError });
            })
            .then(function(result) {
                var data = result.data || {};
                if (data.success) {
                    // The next press sends another mail, and the button says so.
                    emailSubmitBtn.textContent = config.texts.emailSendAgain;
                    holdEmailSubmit(emailRetryAfterSeconds(data, null));
                    // A mail that carries a code is answered on the same place:
                    // the step of the code takes the place of the address.
                    if (data.code_expected && showEmailCodeStep(data.maskedEmail || emailVal)) return;
                    msgEl.textContent = config.texts.emailSent
                        .replace('{0}', data.maskedEmail || emailVal);
                    msgEl.className = 'veriqa-email-msg';
                    msgEl.style.display = 'block';
                } else {
                    msgEl.textContent = data.error || config.texts.emailError;
                    msgEl.className = 'veriqa-email-msg error';
                    msgEl.style.display = 'block';
                    // Only a refusal for asking too soon holds the button; any
                    // other one leaves it ready for the next attempt.
                    holdEmailSubmit(result.status === 429
                        ? emailRetryAfterSeconds(data, result.retryAfterHeader)
                        : 0);
                }
            })
            .catch(function() {
                msgEl.textContent = config.texts.emailError;
                msgEl.className = 'veriqa-email-msg error';
                msgEl.style.display = 'block';
                holdEmailSubmit(0);
            });
        });
    }

    // The step of the code of the email form: shown on the place of the
    // address once the start says the mail went out with a code. Rendered
    // only for a mail that carries one, and hidden: a page whose script did
    // not run never asks for a code.
    var EMAIL_CODE = config.emailCode;
    var emailCodeStep = EMAIL_CODE ? document.getElementById("veriqa-email-code-step") : null;
    var emailCodeInput = emailCodeStep ? document.getElementById("veriqa-email-code") : null;
    var emailCodeBtn = emailCodeStep ? document.getElementById("veriqa-email-code-submit") : null;
    var emailCodeSentEl = emailCodeStep ? document.getElementById("veriqa-email-code-sent") : null;
    var emailResendLink = emailCodeStep ? document.getElementById("veriqa-email-resend") : null;
    // A code is being checked: a status that would take the page away or
    // tell the person where the question is waits for the answer, which may
    // name the page to go to instead (see handleStatus).
    var emailCodePending = false;
    var deferredEmailStatus = null;
    // The code was accepted: the page follows the status from here.
    var emailCodeAccepted = false;

    // The address step and the step of the code take turns on one place.
    function setEmailCodeStep(shown) {
        var emailInput = document.getElementById("veriqa-email-input");
        if (emailInput) emailInput.hidden = shown;
        emailSubmitBtn.hidden = shown;
        emailCodeStep.hidden = !shown;
    }

    function showEmailCodeStep(maskedAddress) {
        if (!emailCodeStep || isTerminal) return false;
        var msgEl = document.getElementById("veriqa-email-msg");
        msgEl.style.display = 'none';
        emailCodeSentEl.textContent = EMAIL_CODE.codeSent
            .replace(EMAIL_CODE.addressSlot, function() { return maskedAddress; });
        emailCodeInput.value = '';
        emailCodeInput.disabled = false;
        emailCodeBtn.disabled = false;
        setEmailCodeStep(true);
        emailCodeInput.focus();
        return true;
    }

    function showEmailCodeError(text) {
        var msgEl = document.getElementById("veriqa-email-msg");
        msgEl.textContent = text;
        msgEl.className = 'veriqa-email-msg error';
        msgEl.style.display = 'block';
    }

    // The answer of the code is in: a status held back for it is applied
    // now, unless the page is already leaving for the place the answer named.
    function releaseEmailStatus() {
        emailCodePending = false;
        var held = deferredEmailStatus;
        deferredEmailStatus = null;
        if (held && !isNavigating) handleStatus.apply(null, held);
    }

    function submitEmailCode() {
        if (isTerminal || emailCodePending || emailCodeAccepted) return;
        var code = emailCodeInput.value.replace(/\s+/g, '');
        if (!code) return;
        emailCodePending = true;
        emailCodeBtn.disabled = true;
        document.getElementById("veriqa-email-msg").style.display = 'none';

        fetch(withBase(EMAIL_CODE.url), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ session_id: emailSubmitBtn.getAttribute('data-session-id') || '', code: code })
        })
        .then(function(resp) {
            var ct = resp.headers.get('content-type') || '';
            return ct.indexOf('application/json') !== -1
                ? resp.json()
                : { error: EMAIL_CODE.checkError };
        })
        .then(function(data) {
            data = data || {};
            if (data.success) {
                // The code did what the link of the same mail does. A named
                // place is where the page goes by itself — the path carries the
                // path base already; without one the page follows the status.
                emailCodeAccepted = true;
                emailCodeInput.disabled = true;
                if (emailResendLink) emailResendLink.hidden = true;
                if (data.next_url && navigateTo(data.next_url, 0, true)) spinnerEl.style.display = "block";
            } else {
                showEmailCodeError(data.error || EMAIL_CODE.checkError);
                emailCodeBtn.disabled = false;
                emailCodeInput.focus();
                emailCodeInput.select();
            }
            releaseEmailStatus();
        })
        .catch(function() {
            showEmailCodeError(EMAIL_CODE.checkError);
            emailCodeBtn.disabled = false;
            releaseEmailStatus();
        });
    }

    if (emailCodeStep && emailForm) {
        // The field keeps the digits of a code and no more of them than a
        // code has; a code pasted from the mail with spaces is taken whole.
        emailCodeInput.addEventListener('input', function() {
            var digits = emailCodeInput.value.replace(/\D+/g, '').slice(0, EMAIL_CODE.length);
            if (digits !== emailCodeInput.value) emailCodeInput.value = digits;
        });
        // Enter in the field checks the code: the field stands in the form of
        // the address, whose own button would send the address once more.
        emailCodeInput.addEventListener('keydown', function(event) {
            if (event.key !== 'Enter') return;
            event.preventDefault();
            submitEmailCode();
        });
        emailCodeBtn.addEventListener('click', submitEmailCode);
        // "Send again" brings the address step back with the address in its
        // field; the button of that step sends the next mail once the interval
        // between two mails allows.
        emailResendLink.addEventListener('click', function() {
            if (emailCodePending) return;
            document.getElementById("veriqa-email-msg").style.display = 'none';
            setEmailCodeStep(false);
            var emailInput = document.getElementById("veriqa-email-input");
            if (emailInput) emailInput.focus();
        });
    }

    // The address form of Email open or closed: the attribute named by the
    // server on the root, on which the stylesheet takes the rest of the
    // channel choice away - the tabs, the unified QR code, the panels of the
    // other channels - and shows the instruction of the form, rendered hidden,
    // in place of the lead instruction. Closing the form gives all of it back,
    // and a hop QR code that fell due while the form hid it is asked for then,
    // as on the way back from a started hop.
    function setEmailFormOpen(open) {
        if (open) {
            document.documentElement.setAttribute(config.emailFormOpenAttribute, "");
        } else {
            document.documentElement.removeAttribute(config.emailFormOpenAttribute);
            reissueHopQr();
        }
    }

    // The field the opened email form takes the focus into: the address
    // field, or the code field when the form comes back on its code step,
    // which hides the address field.
    function focusEmailFormField() {
        var field = emailCodeStep && !emailCodeStep.hidden
            ? emailCodeInput
            : document.getElementById("veriqa-email-input");
        if (field) field.focus();
    }

    // Toggling the Push/Pull sections of the Email panel. Each toggle hides
    // the section that holds the focused button, so the focus is moved on
    // with it instead of falling back to the start of the page: into the
    // field of the form when it opens, onto the link that opens the form
    // when "Back" closes it.
    var pushSection = document.getElementById("veriqa-push-section");
    var pullSection = document.getElementById("veriqa-pull-section");
    var showPullBtn = document.getElementById("veriqa-show-pull");
    var showPushBtn = document.getElementById("veriqa-show-push");
    if (showPullBtn && pushSection && pullSection) {
        showPullBtn.addEventListener('click', function() {
            pushSection.style.display = 'none';
            pullSection.style.display = 'block';
            setEmailFormOpen(true);
            focusEmailFormField();
        });
    }
    if (showPushBtn && pushSection && pullSection) {
        showPushBtn.addEventListener('click', function() {
            pullSection.style.display = 'none';
            // Back to the display of the stylesheet, which lays the section out
            // as two columns on the split card and as a block elsewhere.
            pushSection.style.display = '';
            setEmailFormOpen(false);
            if (showPullBtn) showPullBtn.focus();
        });
    }

    // The Email button of the buttons layout: it reveals the email form in
    // place (a disclosure). The script flips aria-expanded and the state of
    // the open form - the stylesheet shows or hides the form and the rest of
    // the channel choice on them. Opening moves the focus into the field of
    // the form, so the address or the code can be typed without another tap.
    var emailRevealBtn = document.getElementById("veriqa-email-reveal");
    if (emailRevealBtn) {
        emailRevealBtn.addEventListener('click', function() {
            var opening = this.getAttribute('aria-expanded') !== 'true';
            this.setAttribute('aria-expanded', opening ? 'true' : 'false');
            setEmailFormOpen(opening);
            if (!opening) return;
            focusEmailFormField();
        });
    }

    // The signal of the chosen channel: a tap on a channel link that carries
    // the attribute named by the server tells the core which channel the
    // user went into. The beacon goes to the address of this document without
    // its query and fragment, before the browser follows the link, and holds
    // nothing up: a failure of its own is swallowed, the navigation is not
    // delayed, and every tap sends — once-only is the server's business. The
    // names of the fields and the value of the signal come from the server;
    // the channel type comes from the link itself.
    var channelLaunch = config.channelLaunch;
    var sendsChannelLaunch = !!channelLaunch && typeof navigator.sendBeacon === 'function';
    function sendChannelLaunched(link) {
        var body = {};
        body[channelLaunch.signalField] = channelLaunch.signal;
        body[channelLaunch.channelField] = link.getAttribute(channelLaunch.attribute);
        try {
            navigator.sendBeacon(window.location.pathname, JSON.stringify(body));
        } catch (e) {
            // The signal is best-effort: its failure must not stop the
            // browser from following the channel link.
        }
    }
    if (sendsChannelLaunch && channelsEl) {
        channelsEl.querySelectorAll('[' + channelLaunch.attribute + ']').forEach(function(link) {
            link.addEventListener('click', function() { sendChannelLaunched(link); });
        });
    }

    // The mail-client button: its link is the mail itself (mailto:), no page
    // of the channel opens in between, so nothing but this page can tell the
    // user what to do next, and the line is put on a tap, locally. A page that
    // has ended or is leaving keeps its last line, and a page showing a line of
    // a state keeps that line.
    var emailOpenLink = pushSection ? pushSection.querySelector('a.veriqa-btn-email') : null;
    // The channel this page has shown that line for, when the tap is also
    // reported as the signal of the chosen channel: the status that signal
    // comes back as would replace the instruction with the weaker "opening"
    // line. Only the status of that very channel is held back — a channel
    // launched afterwards gets its line as usual.
    var ownEmailLaunchChannel = null;
    function onEmailOpened() {
        if (isTerminal || isNavigating) return;
        // The tap is a press of the Email channel either way; a page
        // without the line keeps its status line and holds back no status
        // of the channel, since there is no instruction to keep.
        notePress({ email: true, channel: null });
        var text = config.emailMailOpenedText;
        if (!text) return;
        clearTimeout(hopOpenedTimer);
        if (!stateLineShown) {
            statusEl.textContent = text;
            statusEl.className = "veriqa-status confirmed";
        }
        if (channelLaunch && emailOpenLink.hasAttribute(channelLaunch.attribute)) {
            ownEmailLaunchChannel = emailOpenLink.getAttribute(channelLaunch.attribute);
        }
    }

    // The page that opens the mail by itself, once per tab: the script of
    // that launch, placed after this one, announces each transition by a
    // document event, and is told of a tap on the button the same way so
    // that it does not open the mail a second time. A transition it begins
    // is a tap on the button in all but the tap: the same line, the same
    // press and the same signal of the chosen channel. One made before this
    // document was shown — back to the page, restored from the cache or
    // reloaded — puts the wait for the confirmation in the place of the line
    // of the opened mail or of the line the page was loaded with; a line of a
    // state, an ending or a page that is leaving keeps its own. The names of
    // the events and of the field come from the server.
    var HOP_TRANSITION = config.hopTransition;
    function announceEmailPress() {
        if (!HOP_TRANSITION) return;
        try {
            document.dispatchEvent(new CustomEvent(HOP_TRANSITION.pressEventName));
        } catch (e) {
            // A launch that is not told opens the mail once more at worst.
        }
    }

    // The person is back from the mail: the wait for the confirmation takes
    // the place of the line of the opened mail — only that very line, since a
    // status heard meanwhile has said more — and, after a transition made
    // before this document was shown, of the line the page was loaded with
    // as well. A page that has ended or is leaving changes nothing.
    function showEmailReturnWait(overInitialLine) {
        if (isTerminal || isNavigating || !config.emailMailOpenedText) return;
        var shown = statusEl.textContent;
        if (shown !== config.emailMailOpenedText && !(overInitialLine && shown === initialStatusText)) return;
        statusEl.textContent = config.emailReturnWaitText;
        statusEl.className = "veriqa-status confirmed";
    }

    if (emailOpenLink) {
        emailOpenLink.addEventListener('click', function() {
            announceEmailPress();
            onEmailOpened();
        });
        if (HOP_TRANSITION) {
            document.addEventListener(HOP_TRANSITION.eventName, function(event) {
                if (event.detail && event.detail[HOP_TRANSITION.returnedField]) {
                    showEmailReturnWait(true);
                    return;
                }
                if (sendsChannelLaunch && emailOpenLink.hasAttribute(channelLaunch.attribute)) {
                    sendChannelLaunched(emailOpenLink);
                }
                onEmailOpened();
            });
        }
    }

    // The way back from the instruction of the hop to the channel choice. The
    // start of the hop is a mark on the root and nothing else, so taking the
    // mark off brings the choice back: the server is not asked and the
    // transaction is the same one. The focus follows the content that returned.
    // A hop QR code the page stopped replacing while it was hidden is asked
    // for again. The next hop code — from the same phone included — starts the
    // hop on the page once more, as a pass of its own: the launch the previous
    // pass showed holds back no line of the next one.
    var hopChooseAnotherBtn = document.getElementById(config.hopChooseAnotherElementId);
    // The way back is taken off the page for good once a channel has answered:
    // that channel works, and the transaction is now bound to its subject, so
    // another channel could only lead into a refusal. A focus standing on the
    // button moves onto the block it stood in.
    function dropHopChooseAnother() {
        if (!hopChooseAnotherBtn) return;
        if (hopChooseAnotherBtn === document.activeElement && hopChooseAnotherBtn.parentNode) {
            hopChooseAnotherBtn.parentNode.focus();
        }
        hopChooseAnotherBtn.style.display = "none";
    }
    // The way back from the remembered channel leaves by the same rule. A
    // focus standing on it moves onto the status line, which tells what has
    // happened.
    function dropRememberBack() {
        if (!rememberBack) return;
        if (rememberBack === document.activeElement) statusEl.focus();
        rememberBackBlock.hidden = true;
    }
    if (hopChooseAnotherBtn) {
        hopChooseAnotherBtn.addEventListener('click', function() {
            if (isTerminal) return;
            document.documentElement.removeAttribute(config.hopStartedAttribute);
            forgetSpareTransitions();
            hideHopChannelHints();
            clearTimeout(hopOpenedTimer);
            hopLaunchShown = false;
            if (channelsEl) channelsEl.focus();
            reissueHopQr();
        });
    }

    // The hints of the channels in the block of the started hop, one line per
    // channel the server worded a hint for. A hint is shown at once when a hop
    // code names its channel, and a hop code naming any channel — one with no
    // hint included — hides the hints of the other channels: the phone is on
    // one way only; the way back to the channel
    // choice hides them all, and none is shown again once a channel has
    // answered. The lines and their channels come from the server.
    var HOP_HINT_IDS = config.hopChannelHints || {};
    var hopChannelHintEls = {};
    Object.keys(HOP_HINT_IDS).forEach(function(channelType) {
        var el = document.getElementById(HOP_HINT_IDS[channelType]);
        if (el) hopChannelHintEls[channelType] = el;
    });
    function noteHopChannelHint(channelType) {
        if (channelAnswered || !channelType) return;
        hideOtherHopChannelHints(channelType);
        if (Object.prototype.hasOwnProperty.call(hopChannelHintEls, channelType)) {
            hopChannelHintEls[channelType].hidden = false;
        }
    }
    function hideOtherHopChannelHints(channelType) {
        Object.keys(hopChannelHintEls).forEach(function(other) {
            if (other !== channelType) hopChannelHintEls[other].hidden = true;
        });
    }
    function hideHopChannelHints() {
        hideOtherHopChannelHints(null);
    }

    // The spare start code of a channel: the message to send when the link of
    // the channel opened the chat and nothing started there. The stylesheet
    // keeps every such code out of sight on a mark the head sets, and a code is
    // shown by a class put on its element here. The names and both delays
    // come from the server (no hardcoding). Showing a code never moves the
    // focus. A code is offered only while the bot of a channel has not
    // answered: the answer closes every code and none is opened after it.
    var SPARE_CODE = config.spareCode;
    var hopSpareScope = document.getElementById(config.hopInstructionElementId);

    // The bot of a channel answered the start: set once, for as long as the
    // document lives.
    var channelAnswered = false;
    // The timer that opens the codes of the panels of a page nobody has left.
    var spareIdleTimer = null;

    // The page is on a phone: the scanner page by itself, any other page by the
    // mark the head set. A phone opens no code of a panel; it offers the code
    // of the channel the person came back from, behind a link.
    var onPhone = !!SPARE_CODE && (SPARE_CODE.phoneByPage
        || document.documentElement.getAttribute(SPARE_CODE.deviceAttribute) === SPARE_CODE.phoneDeviceValue);

    // The codes of one channel inside a part of the page.
    function openSpareCode(scope, channelType) {
        if (!SPARE_CODE || !scope || channelAnswered) return;
        scope.querySelectorAll(SPARE_CODE.selector).forEach(function(el) {
            if (el.getAttribute(SPARE_CODE.channelAttribute) === channelType) {
                el.classList.add(SPARE_CODE.openClass);
            }
        });
    }

    // The channels a transition into which this page has seen begin, each with
    // the timer that opens its code. Own keys only: a channel type is a value
    // of a message.
    var spareTransitions = {};
    function hasSpareTransition(channelType) {
        return Object.prototype.hasOwnProperty.call(spareTransitions, channelType);
    }

    // A transition into a channel began: its code is opened in "scope" once the
    // delay has passed. Only the first such moment per channel counts.
    function noteSpareTransition(scope, channelType) {
        if (channelAnswered) return;
        if (!SPARE_CODE || !scope || !channelType || hasSpareTransition(channelType)) return;
        spareTransitions[channelType] = setTimeout(function() {
            openSpareCode(scope, channelType);
        }, SPARE_CODE.afterTransitionDelayMs);
    }

    // Closes every code inside a part of the page — but those of "keptChannel",
    // when one is given. A focus standing inside a code that leaves the screen
    // is not dropped: it moves onto that part when the part takes a focus,
    // otherwise onto the channel button the code stands under.
    function closeSpareCodes(scope, keptChannel) {
        if (!SPARE_CODE || !scope) return;
        var focusTarget = null;
        scope.querySelectorAll(SPARE_CODE.selector).forEach(function(el) {
            if (keptChannel && el.getAttribute(SPARE_CODE.channelAttribute) === keptChannel) return;
            if (el.contains(document.activeElement)) {
                focusTarget = scope.hasAttribute('tabindex') || !el.parentNode
                    ? scope
                    : el.parentNode.querySelector(SPARE_CODE.channelButtonSelector);
            }
            el.classList.remove(SPARE_CODE.openClass);
        });
        if (focusTarget) focusTarget.focus();
    }

    // A transition into a channel began: the person has left every other
    // channel, so their codes in the block of the started hop are closed and
    // their transitions forgotten — a code opened or still to be opened there
    // is of a chat nobody is in. The hints of the other channels go with them.
    // The transition into the channel itself keeps its count.
    function forgetOtherSpareTransitions(channelType) {
        if (!channelType) return;
        hideOtherHopChannelHints(channelType);
        if (!SPARE_CODE || !hopSpareScope) return;
        Object.keys(spareTransitions).forEach(function(other) {
            if (other === channelType) return;
            clearTimeout(spareTransitions[other]);
            delete spareTransitions[other];
        });
        closeSpareCodes(hopSpareScope, channelType);
    }

    // The way back to the channel choice: the codes of the block of the
    // started hop are closed and the transitions forgotten, so the next hop
    // starts its count anew. The codes of the panels keep the state the delay
    // from the load gave them.
    function forgetSpareTransitions() {
        if (!SPARE_CODE || !hopSpareScope) return;
        Object.keys(spareTransitions).forEach(function(channelType) {
            clearTimeout(spareTransitions[channelType]);
        });
        spareTransitions = {};
        closeSpareCodes(hopSpareScope);
    }

    // The block under the status line of a phone: its link, and under it the
    // copies of the codes, of which the link opens the one of the channel the
    // person came back from. Hidden until then.
    var spareReturnEl = SPARE_CODE ? document.getElementById(SPARE_CODE.returnElementId) : null;
    var spareReturnLink = SPARE_CODE ? document.getElementById(SPARE_CODE.returnLinkElementId) : null;
    var spareReturnChannel = null;

    // The link is offered for one channel: an opened code of another one is
    // closed first. The block already offered for that very channel is left
    // as it stands — the person comes back from the channel its copied code
    // opened, and the code they have just used stays open.
    function showSpareReturn(channelType) {
        if (!spareReturnEl || !spareReturnLink || channelAnswered) return;
        if (!spareReturnEl.hidden && spareReturnChannel === channelType) return;
        closeSpareCodes(spareReturnEl);
        spareReturnChannel = channelType;
        spareReturnLink.setAttribute('aria-expanded', 'false');
        spareReturnEl.hidden = false;
    }

    // The block leaves the screen. A focus standing inside it moves onto the
    // channel choice when that takes a focus and is on screen, otherwise onto
    // the status line — the choice is out of sight once a channel was chosen.
    function hideSpareReturn() {
        if (!spareReturnEl || !spareReturnLink) return;
        var hadFocus = spareReturnEl.contains(document.activeElement);
        closeSpareCodes(spareReturnEl);
        spareReturnChannel = null;
        spareReturnLink.setAttribute('aria-expanded', 'false');
        spareReturnEl.hidden = true;
        if (!hadFocus) return;
        if (channelsEl && channelsEl.hasAttribute('tabindex') && isRendered(channelsEl)) {
            channelsEl.focus();
        } else {
            statusEl.focus();
        }
    }

    if (spareReturnLink) {
        spareReturnLink.addEventListener('click', function() {
            if (!spareReturnChannel || channelAnswered || isTerminal) return;
            openSpareCode(spareReturnEl, spareReturnChannel);
            spareReturnLink.setAttribute('aria-expanded', 'true');
        });
    }

    // The last way into a channel this page sent the person by — the
    // mail-client button, or a channel button of a panel together with the
    // channel of the spare start code standing beside it — and whether the
    // page has been off screen since. Nothing of it outlives the document.
    var lastPress = null;
    var awaySincePress = false;
    function notePress(press) {
        lastPress = press;
        awaySincePress = false;
        hideSpareReturn();
    }

    if (SPARE_CODE && channelsEl) {
        var spareButtons = [];
        channelsEl.querySelectorAll(SPARE_CODE.selector).forEach(function(el) {
            var channelButton = el.parentNode
                ? el.parentNode.querySelector(SPARE_CODE.channelButtonSelector)
                : null;
            if (channelButton) {
                spareButtons.push({ button: channelButton, channel: el.getAttribute(SPARE_CODE.channelAttribute) });
            }
        });
        channelsEl.querySelectorAll(SPARE_CODE.channelButtonSelector).forEach(function(channelButton) {
            // The mail-client button notes its own press above.
            if (channelButton === emailOpenLink) return;
            var channel = null;
            spareButtons.forEach(function(entry) {
                if (entry.button === channelButton) channel = entry.channel;
            });
            channelButton.addEventListener('click', function() {
                notePress({ email: false, channel: channel });
            });
        });
    }

    // The state of the chosen channel: a tap on a link into a channel shows
    // the heading of that channel and puts the attribute named by the server
    // on the root, on which the stylesheet takes the title, the lead
    // instruction and the channel choice away and shows the way back. The
    // headings are in the markup; the script only picks one. A page whose
    // channel has already answered comes from the server in this state, with
    // the heading of that channel shown and without the way back, as after
    // the answer. A focus left in the choice moves onto the way back, or onto
    // the status line when the way back is gone. The way back asks the server nothing: it brings the choice
    // back, takes the block of the spare start code away and forgets the tap,
    // so a return to the page offers nothing until the next tap.
    var CHOSEN = config.chosenChannel;
    var chosenHeadingsEl = CHOSEN ? document.getElementById(CHOSEN.headingsElementId) : null;
    var chosenBackBtn = CHOSEN ? document.getElementById(CHOSEN.backElementId) : null;

    // The status block of the page, built once over the shared reaction
    // function: it alone writes the line of the way back to the sign-in
    // window, of the confirmation being processed and of every ending, and
    // takes the heading of the chosen channel and the way back to the choice
    // away with it. The texts are the ones the server gave this page; a page
    // without a chosen channel hands over neither element. The row of the
    // action under the outcome is the server's too, on a page that offers it;
    // its id is emitted from OutcomeCloseAction.ElementId — renamed together.
    var statusBlock = window.veriqaStatusBlock({
        status: statusEl,
        heading: chosenHeadingsEl,
        controls: chosenBackBtn ? [chosenBackBtn] : [],
        close: document.getElementById("veriqa-outcome-close")
    }, {
        returnToWindow: config.returnToSignInWindowText,
        answerInLinkBrowser: config.answerInLinkBrowserText,
        processing: msgConfirmed,
        completed: msgSuccess,
        expired: msgExpired,
        declined: msgDeclined,
        error: msgError
    });

    // Shows the heading of one channel and hides the others; null hides all.
    function showChosenHeading(channelType) {
        chosenHeadingsEl.querySelectorAll('[' + CHOSEN.channelAttribute + ']').forEach(function(el) {
            el.hidden = el.getAttribute(CHOSEN.channelAttribute) !== channelType;
        });
    }

    if (chosenHeadingsEl && channelsEl) {
        channelsEl.querySelectorAll('[' + CHOSEN.channelAttribute + ']').forEach(function(link) {
            link.addEventListener('click', function() {
                if (isTerminal) return;
                var focusInChoice = channelsEl.contains(document.activeElement);
                showChosenHeading(link.getAttribute(CHOSEN.channelAttribute));
                document.documentElement.setAttribute(CHOSEN.stateAttribute, "");
                if (!focusInChoice) return;
                if (chosenBackBtn && isRendered(chosenBackBtn)) {
                    chosenBackBtn.focus();
                } else {
                    statusEl.focus();
                }
            });
        });
    }
    if (chosenHeadingsEl && chosenBackBtn) {
        chosenBackBtn.addEventListener('click', function() {
            if (isTerminal) return;
            document.documentElement.removeAttribute(CHOSEN.stateAttribute);
            showChosenHeading(null);
            lastPress = null;
            awaySincePress = false;
            hideSpareReturn();
            if (channelsEl) channelsEl.focus();
        });
    }

    // The way back is taken off the page for good once a channel has
    // answered, as the one of the started hop is, and by AwaitingWebConfirmation
    // — on a page that leaves for the confirming question as well, which shows
    // no line of its own — without leaning on a page state the stylesheet may
    // not get. The heading shown stays here: the answer of the bot is awaited
    // in the channel. The heading leaves, together with the way back, with the
    // first line of the status block — back to the sign-in window, confirmed
    // and processing, or an ending (statusBlock above). A focus standing on
    // the way back moves onto the status line: the channel choice is out of
    // sight in this state.
    function dropChosenBack() {
        if (!chosenBackBtn) return;
        if (chosenBackBtn === document.activeElement) statusEl.focus();
        chosenBackBtn.style.display = "none";
    }

    document.addEventListener("visibilitychange", function() {
        if (lastPress && document.visibilityState === "hidden") awaySincePress = true;
    });

    // The person is back on the page after a press that sent them away. After
    // the mail-client button the line telling them to send the mail gives way
    // to the one telling them to wait for its confirmation (showEmailReturnWait).
    // After the button of a channel with a spare start code, a phone offers its
    // link. A page that has ended or is leaving changes nothing.
    function onReturnFromChannel() {
        if (!lastPress || !awaySincePress) return;
        awaySincePress = false;
        if (isTerminal || isNavigating) return;
        if (lastPress.email) {
            showEmailReturnWait(false);
            return;
        }
        if (onPhone && lastPress.channel) showSpareReturn(lastPress.channel);
    }

    // The bot of a channel answered the start — heard as it happens or
    // replayed to a page that subscribed afterwards. It is acted on once: both
    // kinds of timers stop, every opened code is closed, the link of a phone
    // and the hints of the channels leave, and no code is opened
    // again, the way back to the channel choice included. The code of the
    // status and the lines come from the server.
    var CHANNEL_ANSWER = config.channelAnswer;
    // The line of the answer of the bot of a channel; empty for a channel
    // the server gave no line for.
    function channelAnswerLine(channelType) {
        return Object.prototype.hasOwnProperty.call(CHANNEL_ANSWER.lines, channelType)
            ? CHANNEL_ANSWER.lines[channelType]
            : "";
    }
    function onChannelAnswered() {
        if (channelAnswered) return false;
        channelAnswered = true;
        dropHopChooseAnother();
        dropRememberBack();
        dropChosenBack();
        hideHopChannelHints();
        hideSpareReturn();
        clearTimeout(spareIdleTimer);
        Object.keys(spareTransitions).forEach(function(channelType) {
            clearTimeout(spareTransitions[channelType]);
        });
        closeSpareCodes(channelsEl);
        closeSpareCodes(hopSpareScope);
        return true;
    }

    // The line of the answer of the bot is on the status line: the codes of
    // the hop family leave that line alone from then on.
    var answerLineShown = false;

    // The answer of the bot of a channel, the one way the page acts on it —
    // heard from the hub or known from the load. The first one closes the
    // spare start codes and, for a channel the server gave a line for, puts
    // that line on the status line; a repeated one changes nothing. The line
    // takes the channel choice off the page with it: the question waits in
    // that chat now. A page without the line keeps its screen, and so does a
    // page already showing a line of a state. Tells whether the line was put.
    function applyChannelAnswer(channelType) {
        if (!onChannelAnswered()) return false;
        if (stateLineShown) return false;
        var answerLine = channelAnswerLine(channelType);
        if (!answerLine) return false;
        clearTimeout(hopOpenedTimer);
        answerLineShown = true;
        markAnswerElsewhere();
        statusEl.textContent = answerLine;
        // The class of the status line the renderer gives this line
        // (DefaultAuthPageRenderer.AnsweredStatusClass): rename both together.
        statusEl.className = "veriqa-status confirmed";
        return true;
    }

    // A page rendered while the answer was already awaited in a channel comes
    // in the state that answer puts it in, and the script acts on the answer
    // from the load the same way, so its replay by the hub changes nothing on
    // the page.
    if (CHANNEL_ANSWER.answeredChannel && applyChannelAnswer(CHANNEL_ANSWER.answeredChannel)) {
        initialStatusText = null;
    }

    // On a computer or a tablet the codes of the panels open by the delay from
    // the load — except the code of a channel whose QR code is a hop QR code:
    // that one is offered in the block of the hop once the phone went into the
    // channel.
    if (SPARE_CODE && channelsEl && !onPhone) {
        spareIdleTimer = setTimeout(function() {
            if (channelAnswered) return;
            channelsEl.querySelectorAll(SPARE_CODE.selector).forEach(function(el) {
                if (SPARE_CODE.hopQrChannels.indexOf(el.getAttribute(SPARE_CODE.channelAttribute)) === -1) {
                    el.classList.add(SPARE_CODE.openClass);
                }
            });
        }, SPARE_CODE.idleDelayMs);
    }

    // The message of the start by a code: the buttons that copy it are bound by
    // the function the pages showing such a message share, inlined ahead of this
    // script on a page that has one. A page without the message carries neither
    // the settings nor the function.
    if (config.startMessage) {
        window.veriqaStartMessage(document, config.startMessage);
    }

    // Fallback-polling configuration. The polled URL is fetched
    // here, so it is based here; the callback is a navigation target and stays
    // root-relative — navigateTo bases it. withBase is not idempotent: basing
    // twice would send the browser to /demo/demo/... and a 404.
    var STATUS_URL = withBase(config.statusUrl);
    var CALLBACK_URL = config.callbackUrl;
    var POLL_INTERVAL_MS = config.pollIntervalMs;

    // Whether this page continues on the browser callback of the sign-in path.
    // False for a transaction that has no such callback: its outcome is shown here.
    var NAVIGATES_AWAY = config.navigatesAway;

    // The destination of a page without that callback — the core page asking the
    // confirming question. Empty when there is none.
    var CONFIRM_URL = config.confirmUrl;

    // Countdown. It is started HERE, below the polling
    // configuration, and not where it is declared: a page whose remainder is
    // already zero verifies with the status URL on its very first tick, and a
    // start placed above these declarations would read them unassigned.
    //
    // The figure and the next tick are drawn by the function the page shares
    // with the other page that counts down (inlined above this script); what
    // this page keeps is the deadline, a MOMENT in milliseconds, and what zero
    // on the clock does: it asks the server, and the figure stays on screen
    // while the check is in flight.
    function updateCountdown() {
        if (isTerminal) return;
        window.veriqaCountdown(countdownEl, deadline - Date.now(), verifyExpiry, updateCountdown);
    }

    // Whether a verification is already in flight. Without it the poll loop and
    // the countdown could each start one, and two answers would reschedule the
    // same deadline twice.
    var verifyPending = false;

    // The answer that is NOT one: nothing was said about the transaction — the
    // surface refused (a rate limit, a fault of its own or of a proxy in front
    // of it), or what came back cannot be read as its answer. A marker rather
    // than a value, told apart by identity, so that no shape a real response
    // could take is mistaken for it.
    var VERIFY_UNAVAILABLE = {};

    // The check the countdown makes when it runs out. Expiry is a fact of the
    // SERVER, and until this call it is only a fact of the local clock — which is
    // precisely what used to end a live transaction early and, worse, stop the
    // page from listening for the confirmation that was still coming.
    //
    // The order of the questions is normative: the STATE is read before the
    // remaining TTL. A transaction can be terminal with no time left for reasons
    // that are not expiry at all, so a page reading the remainder first would
    // announce the wrong ending. Only two answers end the page: the transaction
    // is gone (404), or the server itself names the state Expired.
    //
    // Past the deadline the surface gives two different answers, and the
    // difference is the one this page lives on. A transaction the deadline still
    // decides — nobody has confirmed it yet — is answered 404 until the sweep
    // records the expiry, and the page ends on that. A transaction already
    // confirmed is not: its deadline ends nothing (what it waits on now is
    // finalization, a wait the TTL does not govern and whose length is the
    // server's business, not the page's), the surface reports the state it is
    // in, and the page lands in the branch below for "alive with nothing left"
    // and keeps asking until the outcome is named — which is precisely the
    // sign-in it is waiting for.
    function verifyExpiry() {
        if (isTerminal || isNavigating || verifyPending) return;
        verifyPending = true;
        fetch(STATUS_URL, { headers: { "Accept": "application/json" } })
            .then(function(resp) {
                // Gone — the one answer that IS the expiry.
                if (resp.status === 404) return null;
                // Any other refusal says nothing ABOUT THE TRANSACTION: a rate
                // limit or a fault means the check did not happen, not that the
                // sign-in ended. Ending here would hand the shared bucket of the
                // status surface a verdict over live pages — and the page's own
                // confirmation, still on its way, would then be dropped by the
                // terminal flag. The check is repeated instead.
                if (!resp.ok) return VERIFY_UNAVAILABLE;
                // Nor does a 200 whose body is not the answer: an intercepting
                // proxy — the very one named above — answers OK with a page of
                // its own. The transport is alive and the check simply did not
                // happen, so it is repeated rather than ended on.
                return resp.json().catch(function() { return VERIFY_UNAVAILABLE; });
            })
            .then(function(data) {
                verifyPending = false;
                if (isTerminal || isNavigating) return;
                if (data === VERIFY_UNAVAILABLE) {
                    setTimeout(verifyExpiry, POLL_INTERVAL_MS);
                    return;
                }
                if (!data || !data.state) { handleExpired(); return; }
                if (data.state === STATUS_EXPIRED) { handleExpired(); return; }
                // Terminal for another reason — the outcome is shown by the one
                // handler that shows outcomes, expiry wording included nowhere.
                if (data.state === STATUS_COMPLETED || data.state === STATUS_FAILED) {
                    handleStatus(data.state, CALLBACK_URL, data.reason_code);
                    return;
                }
                // Alive, and the server says how much longer: the local deadline
                // was early (a browser clock running fast, a suspended tab), so
                // it is replanned from the server's own remainder.
                if (data.ttl_seconds > 0) {
                    deadline = Date.now() + data.ttl_seconds * 1000;
                    updateCountdown();
                    return;
                }
                // Alive with nothing left to report. Two arrivals here, and
                // neither is an ending: the deadline has not passed on the
                // server and the remainder is merely under the tenth of a
                // second the response rounds to, or it has passed on a
                // transaction it does not end — a confirmation awaiting
                // finalization. Either way the page keeps the zero on screen,
                // leaves the status line alone and asks again — naming the end
                // is the server's part.
                setTimeout(verifyExpiry, POLL_INTERVAL_MS);
            })
            .catch(function() {
                // An unreachable server, not a refusing one: there is no
                // transport left to ask again over, so the page ends as expired
                // without the server's word.
                verifyPending = false;
                handleExpired();
            });
    }
    updateCountdown();

    function handleExpired() {
        isTerminal = true;
        // The line goes first, while the way back is still on screen: a focus
        // standing on it is seen there and follows to the status line.
        statusBlock.show("expired");
        // The state of the page, written where every path into expiry passes:
        // the verification above, the SignalR status and the 404 of the poll
        // loop. The stylesheet drops every way IN from the page on this one
        // attribute and shows the way OUT — one decision, one place.
        document.documentElement.setAttribute(
            config.pageStateAttribute, config.expiredPageState);
        countdownEl.style.display = "none";
        // Put out on every page, the one that navigates included: expiry takes
        // no page to the callback, so this screen is the last one here too, and
        // a spinner left on by an earlier confirmation would keep promising a
        // sign-in next to the line saying it is over.
        spinnerEl.style.display = "none";
    }

    // Where a status may take this page. In the mode with the callback — wherever
    // the status brought it. Without it — the confirming question and NOTHING
    // else: a target arriving in a message is ignored, which closes both delivery
    // paths of a target (the field of a status message and the deterministic
    // callback of the polling fallback) with one decision.
    function targetFor(status, redirectUrl) {
        if (NAVIGATES_AWAY) return redirectUrl;
        return status === STATUS_AWAITING_WEB ? CONFIRM_URL : "";
    }

    // The navigation this page has taken and not made yet: the timer between
    // the status that sends it away and the assignment of window.location.
    var navigationTimer = null;

    // The only place where the script takes the browser off this page. Guarded,
    // so the first caller wins and every later tick/message is a no-op; a page
    // with no target for this status stops here as well, and so does a page
    // whose "Cancel sign-in" has been sent — the form is taking it away.
    // Answers whether the navigation was TAKEN, so that a caller can tell the user
    // "hold on, we are moving you" only when the page really is about to move.
    function navigateTo(url, delayMs, carriesBase) {
        if (isNavigating || cancelSubmitted || !url) return false;
        isNavigating = true;
        navigationTimer = setTimeout(function() {
            navigationTimer = null;
            // Every navigation target reaching this point is root-relative —
            // a URL from a SignalR message (built by the background handler
            // without HttpContext) and the deterministic callback used by
            // polling alike — so the path base is applied here, once. The one
            // exception is the place named by the answer to the code of the
            // email form, which the server built with the path base.
            window.location.href = carriesBase ? url : withBase(url);
        }, delayMs);
        return true;
    }

    // Takes back a navigation that was taken and not made yet, when "Cancel
    // sign-in" is sent. The timer is dropped rather than left to check the
    // submit when it fires: a timer the browser freezes together with the page
    // fires in the page's next life, after the submit latch has been lifted,
    // and would make the very navigation the cancel called off.
    // The navigation latch is lifted with it, and is not raised by the submit:
    // a submit the user stopped leaves the page on screen, and it has to go on
    // tracking the status there.
    function abandonNavigation() {
        clearTimeout(navigationTimer);
        navigationTimer = null;
        isNavigating = false;
    }

    // Whether an element holding the focus is on screen at all: the body is
    // no holder, and an element inside a hidden part has no box.
    function isRendered(el) {
        return !!el && el !== document.body && el.getClientRects().length > 0;
    }

    // The answer is no longer given on this page: the question waits on the
    // window that started the sign-in, or in the chat of a channel's bot. The
    // transaction is bound to a subject by then, so the stylesheet takes every
    // way into a channel away on the state the server names; the status line,
    // the countdown and "Cancel sign-in" stay. A focus standing in the part
    // that leaves moves onto the status line, which stays on every page; a
    // focus anywhere else stays where the user put it.
    function markAnswerElsewhere() {
        var focused = document.activeElement;
        var focusShown = isRendered(focused);
        document.documentElement.setAttribute(
            config.pageStateAttribute, config.answerElsewherePageState);
        if (focusShown && !isRendered(focused)) statusEl.focus();
    }

    // The line sending the person back to the window that started the
    // sign-in, given only to a page with nowhere to go when the core asks the
    // question on its own page. Shown once.
    var RETURN_HINT = config.returnToSignInWindowText;
    // The line of a question that takes its answer from the browser the link of
    // the letter was opened in, and from no other: the question is open there
    // already, and this page stays on its own screen.
    var ANSWER_IN_LINK_BROWSER = config.answerInLinkBrowserText;
    var returnHintShown = false;
    // A line of a state of the transaction is on the status line — back to
    // the window, or confirmed and processing; the endings stop the page
    // altogether. From then on no channel status, no answer of the bot, no
    // held line and no tap of the page words the status line, as on the
    // launch page; the lifecycle statuses still do.
    var stateLineShown = false;
    function showReturnHint(state, text) {
        if (!text || returnHintShown || isNavigating || cancelSubmitted) return;
        returnHintShown = true;
        stateLineShown = true;
        // A line held back for a moment would land on top of this one.
        clearTimeout(hopOpenedTimer);
        markAnswerElsewhere();
        statusBlock.show(state);
    }

    // Applying the transaction lifecycle status to the UI.
    // Shared function: called both from the SignalR notification and from fallback
    // polling. The reason code is the third argument because a Failed status alone
    // does not say whether the user refused; both delivery paths already carry it.
    // The fourth argument is the step of the claims completion — { step, surface } —
    // and is read only for that status. The fifth tells, with AwaitingWebConfirmation,
    // that the question takes its answer from one other browser only.
    function handleStatus(status, redirectUrl, reasonCode, completion, answerBrowserBound) {
        if (isTerminal) return;

        // A code of the email form is being checked, and its answer may name
        // the page to go to — the question in this very browser, the form of
        // the claims completion. A status that would send the page elsewhere
        // or put up the line of where the question waits is held until then.
        if (emailCodePending && (status === STATUS_AWAITING_WEB || status === STATUS_CLAIM_COMPLETION)) {
            deferredEmailStatus = [status, redirectUrl, reasonCode, completion, answerBrowserBound];
            return;
        }
        // The page is leaving for the place the accepted code named: no status
        // words this page any more.
        if (emailCodeAccepted && isNavigating) return;

        if (status === STATUS_CONFIRMED) {
            // A line held back for a moment would land on top of this one.
            clearTimeout(hopOpenedTimer);
            stateLineShown = true;
            statusBlock.show("processing");
            spinnerEl.style.display = "block";
        } else if (status === STATUS_AWAITING_WEB) {
            // The core took the confirming question onto its own page: leave for
            // it right away. The transaction is not terminal — the answer is
            // still ahead — so the countdown is left alone; the navigation guard
            // is what stops the polling loop from repeating this. A page that
            // leaves does not rewrite the status line: the user has confirmed
            // nothing yet, and the page they are about to see asks that question.
            // The spinner follows the navigation: a page that stays put keeps
            // waiting for the outcome instead of announcing a move it will not
            // make, and the status tracking goes on — the answer is given
            // elsewhere and comes back here. A page the server gave the line of
            // the way back sends the person to that place: the line goes on the
            // status line and the channel choice leaves, once. The way back to
            // the channel choice leaves either way: the answer is no longer a
            // channel's to give.
            // A question bound to the browser the link of the letter was opened
            // in is open there already and would refuse this one: the page stays
            // and says where the question is.
            dropChosenBack();
            var leaving = !answerBrowserBound && navigateTo(targetFor(status, redirectUrl), 0);
            spinnerEl.style.display = leaving ? "block" : "none";
            if (!leaving) {
                if (answerBrowserBound) showReturnHint("answerInLinkBrowser", ANSWER_IN_LINK_BROWSER);
                else showReturnHint("returnToWindow", RETURN_HINT);
            }
        } else if (status === STATUS_CLAIM_COMPLETION) {
            // The user confirmed, and the core now waits for missing claims. A page
            // without a callback has no page of the completion to go to and ignores
            // the status. A page with one leaves for its callback — the page of the
            // completion lives there — when the step is the phone (its instruction
            // is on that page), when the web step is shown on this device (the form
            // is that page), or when it is shown on the device that confirmed: the
            // callback then shows the page that waits for the form, and the QR code
            // and the channels of this one have nothing left to offer. With the
            // surface not chosen yet the page keeps waiting, and a later message
            // names it. The navigation guard is what makes a repeated message or
            // poll tick a no-op.
            // The status line is not rewritten: the sign-in is not complete yet, and
            // "confirmed" would promise an ending the page cannot keep.
            if (!NAVIGATES_AWAY || !completion) return;
            if (completion.step === CLAIM_STEP_PHONE
                || completion.surface === CLAIM_SURFACE_INITIATING_PAGE
                || completion.surface === CLAIM_SURFACE_CROSS_DEVICE_PAGE) {
                if (navigateTo(CALLBACK_URL, 0)) { spinnerEl.style.display = "block"; }
            }
        } else if (status === STATUS_COMPLETED) {
            isTerminal = true;
            // The line goes first, ahead of the state, for the focus — as on
            // expiry.
            statusBlock.show("completed");
            // A page that ends here takes its channel choice away and leaves
            // the success line as the whole screen; the stylesheet does it on
            // the state attribute, as it does for the expired state.
            if (config.completedPageState) {
                document.documentElement.setAttribute(
                    config.pageStateAttribute, config.completedPageState);
            }
            countdownEl.style.display = "none";
            // A page that leaves for its callback writes no page state, yet the
            // sign-in is over all the same: the button with nothing left to
            // cancel is taken away here, as on a failed sign-in. Left on screen
            // through the pause and the trip to the callback, a press of it
            // would call the navigation off and send the client a refusal of a
            // sign-in the user has just confirmed.
            if (cancelFormEl) { cancelFormEl.style.display = "none"; }
            // Terminal and final: the spinner covers the pause before the
            // navigation and has nothing to cover without one — on a page that
            // stays, the outcome is the last thing shown, and it is shown still.
            spinnerEl.style.display = navigateTo(targetFor(status, redirectUrl), 500) ? "block" : "none";
        } else if (status === STATUS_EXPIRED) {
            handleExpired();
        } else if (status === STATUS_FAILED) {
            // A sign-in the user replaced with a newer one is over without an
            // ending to report: the client is not told, so the page stays here
            // and shows the expired screen — no callback, no error line.
            if (reasonCode === REASON_SUPERSEDED) {
                handleExpired();
                return;
            }
            isTerminal = true;
            // A refusal is told from a fault by the reason and by nothing else: a
            // failure that names no reason is not the user's refusal to state.
            statusBlock.show((reasonCode === REASON_DECLINED) ? "declined" : "error");
            countdownEl.style.display = "none";
            // A sign-in that failed is the client's to learn, and the callback is
            // what tells it — a refusal as a refusal, a fault as a server error:
            // a page with one leaves for it once, whatever the reason and whenever
            // it came — on the confirming question or during the claims
            // completion. It is on its way out and writes no page state, so the
            // button with nothing left to cancel is taken away here. That holds
            // as well when the navigation was not taken by this call — one is
            // already under way, or "Cancel sign-in" has been sent and the form
            // is taking the page away: the page is leaving all the same, and
            // its screen is left as it stands.
            if (CALLBACK_URL) {
                if (cancelFormEl) { cancelFormEl.style.display = "none"; }
                if (navigateTo(CALLBACK_URL, 0)) { spinnerEl.style.display = "block"; }
                return;
            }
            // The last screen of this page — one with no callback to leave for.
            // The stylesheet drops everything that belonged to the sign-in that
            // did not happen on this one attribute, as it does on expiry; the
            // spinner goes for the reason it does there.
            document.documentElement.setAttribute(
                config.pageStateAttribute, config.failedPageState);
            spinnerEl.style.display = "none";
        }
    }

    // Status fallback polling: activated when SignalR is
    // unavailable — the library failed to load, negotiate/start did not succeed,
    // or the connection dropped for good. Polls the REST endpoint
    // without JS dependencies; this reliability tier is below the SignalR transport.
    //
    // The loop that is running RIGHT NOW, held by identity — and the only
    // answer this page has to "is anything still tracking the transaction".
    // A flag raised at the start and never lowered answers a different
    // question — "was polling ever started" — and the two answers part company
    // the moment the loop ends itself, which it does whenever the browser is
    // leaving this page (see pollStatus below). A page brought back from the
    // back/forward cache would then be refused its restart by a flag describing
    // a loop that is no longer running, and would sit out the TTL with a single
    // read behind it — the ending no transport may leave the user with.
    // An identity and not a counter, because a restart may happen while the
    // previous loop's read is still in flight: the continuation of that read
    // belongs to a loop which is no longer the live one, and it must plan no
    // tick of its own — or the page would poll twice over for the rest of its
    // life. Compared by reference, so no two loops can ever be taken for one.
    var livePollLoop = null;
    function startPolling() {
        if (livePollLoop || isTerminal || isNavigating) return;
        livePollLoop = {};
        pollStatus(livePollLoop);
    }
    // ONE read of the status surface, applied to the page. It has two callers,
    // and they differ only in what happens AFTER the answer: the loop below
    // plans the next read, the reconciliation further down plans nothing. What
    // the read itself is — which URL, that a 404 is the expiry, and which of
    // the two response fields carries the status — stays written once.
    function readStatusOnce() {
        if (isTerminal || isNavigating) return Promise.resolve();
        return fetch(STATUS_URL, { headers: { "Accept": "application/json" } })
            .then(function(resp) {
                // 404 — transaction not found/expired: treat as expired
                if (resp.status === 404) { handleExpired(); return null; }
                if (!resp.ok) { return null; }
                return resp.json();
            })
            .then(function(data) {
                // The polling endpoint has no redirectUrl — on Completed we use
                // the deterministic callback built on the server from session_id.
                // "The core is waiting for the answer on its page" is not a
                // state-machine state, so it arrives as its own field rather
                // than inside "state". The step of the claims completion is not a
                // state either, and arrives the same way.
                if (data && data.claim_completion) {
                    handleStatus(STATUS_CLAIM_COMPLETION, CALLBACK_URL, data.reason_code, data.claim_completion);
                } else if (data && data.awaiting_web_confirmation) {
                    handleStatus(STATUS_AWAITING_WEB, CALLBACK_URL, data.reason_code, null, data.answer_browser_bound);
                } else if (data && data.state) {
                    handleStatus(data.state, CALLBACK_URL, data.reason_code);
                }
            });
    }
    // The loop. It carries the identity it was started with and checks it at
    // every point where it could go on: a loop that is no longer the live one
    // stops without touching anything, and the loop that IS the live one
    // releases the identity wherever it decides not to continue. Stopping and
    // saying so are the same act here — that is the whole of the invariant this
    // page needs, because the page that comes back has to be able to ask
    // whether anything is still tracking it and be told the truth.
    function pollStatus(loop) {
        if (loop !== livePollLoop) return;
        if (isTerminal || isNavigating) { livePollLoop = null; return; }
        // Both outcomes plan the next tick: a network error or a rate limit says
        // nothing about the transaction, so it must not terminate the loop.
        function planNext() {
            if (loop !== livePollLoop) return;
            if (isTerminal || isNavigating) { livePollLoop = null; return; }
            setTimeout(function() { pollStatus(loop); }, POLL_INTERVAL_MS);
        }
        readStatusOnce().then(planNext, planNext);
    }

    // Coming back to the page. A phone that froze the tab while the user answered
    // in the messenger can hand it back with a socket that died silently: neither
    // onreconnected nor onclose fires, so nothing resubscribes, and the status
    // broadcast meanwhile was addressed to a group this page had already left.
    // Asking on return is what ends that wait — the server cannot know the
    // connection is gone, and the browser never said so (no transport may leave
    // the user waiting out the TTL).
    // Registered for both transports and above the SignalR branch below, which
    // returns: the read is the same one the fallback loop makes, and a frozen tab
    // throttles that loop's timers just as thoroughly.
    var reconcilePending = false;
    function reconcileStatus() {
        if (isTerminal || isNavigating || reconcilePending) return;
        reconcilePending = true;
        function done() { reconcilePending = false; }
        readStatusOnce().then(done, done);
    }

    // The transport, not only the status. One read tells the page where the
    // transaction stands right now; it says nothing about how the NEXT change
    // would reach it. A transport that delivers nothing at all — a connection
    // the client itself reports as disconnected, or a fallback loop that has
    // stopped — would leave the page with one answer and no way to hear the
    // following one.
    // What comes back is the highest tier still available, which is not always
    // the tier the page was on: the checks below are the conditions under which
    // a tier has to be raised, asked in order. A page that never had a hub gets
    // the fallback loop back; a socket the client reports as Disconnected is
    // revived instead, and the page drops to polling only if that revival
    // fails — so a page that had already degraded through onclose or a failed
    // start is offered the socket again before the loop. Only on return, and
    // only when the page is not being tracked: no periodic probing is
    // introduced, a running fallback loop is left to its schedule, and a
    // connection that is connected, connecting or reconnecting is left to its
    // own machinery. The silent death — the socket the client still believes is
    // open — is not detectable here at all; it is covered by the read above
    // and, beyond it, by SignalR's own keep-alive timeout, which starts running
    // again with the thawed tab and ends in reconnect or in onclose, both of
    // which this page already handles.
    function resumeTransport() {
        if (isTerminal || isNavigating) return;
        // Nothing to revive: the fallback loop is running and already owns the
        // tracking. This is also what keeps a live transport from being polled
        // alongside — reaching the tiers below means the page is NOT being
        // tracked at this moment.
        if (livePollLoop) return;
        // No hub was ever built (the library failed to load), so the fallback
        // loop is the only tier this page has — and it is not running, or the
        // line above would have returned. This is the page restored onto a loop
        // that stopped while the browser was leaving; it gets its tracking back
        // here, and it is the same tier it was on, not a new one.
        if (!connection) { startPolling(); return; }
        var hubStates = window.signalR.HubConnectionState;
        if (!hubStates || connection.state !== hubStates.Disconnected) return;
        connection.start()
            .then(function() {
                // Back on the wire and back in the group: the subscription is
                // what makes the connection useful, and the hub answers it with
                // the transaction's current status.
                return connection.invoke(config.hub.joinMethod, sessionId);
            })
            .then(function() {
                // A replacement of a QR code that fell due while the socket was
                // down is asked for now.
                reissueHopQr();
            })
            .catch(function() {
                // The hub is not coming back for this page — the tier below it
                // keeps the transaction tracked.
                startPolling();
            });
    }

    document.addEventListener("visibilitychange", function() {
        if (document.visibilityState !== "visible") return;
        onReturnFromChannel();
        reconcileStatus();
        resumeTransport();
    });
    // Restored from the back/forward cache: the page did not reload, so nothing
    // else on it runs again — the listeners are the state it woke up with, and
    // so is the navigation latch left standing by the very navigation that put
    // this page into the cache.
    window.addEventListener("pageshow", function(event) {
        if (!event.persisted) return;
        // The navigation latch counts navigations within ONE life of the page: it
        // exists so that a repeat message or poll tick cannot reassign
        // window.location WHILE the browser is already leaving. A restore from
        // the back/forward cache is past that point — the navigation it guarded
        // completed, the user undid it, and the page is on screen again with
        // nothing in flight: a life of its own, in which no navigation has been
        // started yet. Left standing, the latch does not merely forbid a second
        // navigation: it silences the reconciliation below, the expiry check and
        // the polling fallback alike, and the restored page would sit on a frozen
        // countdown until the TTL ran out — the one ending no transport may lead to.
        isNavigating = false;
        // The same goes for the submit of "Cancel sign-in": it was sent in the
        // life that ended, and whether this sign-in is still open to cancel is
        // what the reconciliation below finds out. Lifting it gives the page its
        // own navigation back as well — the submit took it away, and left no
        // timer behind to make it late.
        cancelSubmitted = false;
        // A page restored after a press that sent the person away has been
        // away by definition.
        if (lastPress) awaySincePress = true;
        onReturnFromChannel();
        reconcileStatus();
        resumeTransport();
    });

    // Replacement of the hop QR codes before their tokens run out. It happens
    // over the live hub connection only: a page on the polling fallback
    // replaces nothing and keeps its QR codes as they are, and a page the
    // server gave nothing to replace never starts any of it.
    var hopQrReissue = config.hopQrReissue;

    // One record per QR code the page replaces: the entry the hub is asked by,
    // the moment the replacement is due at and whether the last attempt failed.
    // The moment is a share of the remainder the server sent, counted on THIS
    // browser's clock from the arrival of that remainder — the same reason the
    // countdown needs no shared clock.
    var hopQrEntries = [];
    var hopQrTimer = null;
    var hopQrCallPending = false;

    function hopQrDueAt(remainingMs) {
        return Date.now() + hopQrReissue.fraction * remainingMs;
    }

    if (hopQrReissue) {
        hopQrReissue.entries.forEach(function(item) {
            hopQrEntries.push({ entry: item.entry, dueAt: hopQrDueAt(item.remainingMs), failed: false });
        });
    }

    function findHopQrEntry(entry) {
        for (var i = 0; i < hopQrEntries.length; i++) {
            if (hopQrEntries[i].entry === entry) return hopQrEntries[i];
        }
        return null;
    }

    function dropHopQrEntry(entry) {
        hopQrEntries = hopQrEntries.filter(function(record) { return record.entry !== entry; });
    }

    // A failure says nothing about the QR code: the picture stays, and the
    // entry is asked for again after the delay the server set.
    function failHopQrEntry(record) {
        record.failed = true;
        record.dueAt = Date.now() + hopQrReissue.retryDelayMs;
    }

    // The next look at the entries, planned for the earliest moment among
    // them. A throttled timer of a hidden tab fires late, and the look it
    // makes then is the same one.
    function planHopQrReissue() {
        if (hopQrTimer !== null) {
            clearTimeout(hopQrTimer);
            hopQrTimer = null;
        }
        if (isTerminal || hopQrEntries.length === 0) return;
        var earliest = hopQrEntries[0].dueAt;
        hopQrEntries.forEach(function(record) { earliest = Math.min(earliest, record.dueAt); });
        hopQrTimer = setTimeout(function() {
            hopQrTimer = null;
            reissueHopQr();
        }, Math.max(0, earliest - Date.now()));
    }

    // Every image of the entry takes the new picture; nothing else of the
    // page is touched.
    function replaceHopQrImages(entry, qrCodeBase64) {
        document.querySelectorAll('[' + hopQrReissue.imageAttribute + ']').forEach(function(img) {
            if (img.getAttribute(hopQrReissue.imageAttribute) === entry) {
                img.src = hopQrReissue.pngDataUriPrefix + qrCodeBase64;
            }
        });
    }

    // The answer of the hub. A replaced QR code is planned again from its new
    // remainder while its token still runs out before the transaction; an
    // entry the answer leaves out — an empty answer included — is not replaced
    // any more, and its picture stays.
    function applyHopQrAnswer(asked, items) {
        var answered = [];
        (items || []).forEach(function(item) {
            if (!item || asked.indexOf(item.entry) === -1) return;
            var record = findHopQrEntry(item.entry);
            if (!record) return;
            answered.push(item.entry);
            if (item.failed) {
                failHopQrEntry(record);
                return;
            }
            replaceHopQrImages(item.entry, item.qrCodeBase64);
            if (item.expiresBeforeTransaction) {
                record.failed = false;
                record.dueAt = hopQrDueAt(item.remainingMs);
            } else {
                dropHopQrEntry(item.entry);
            }
        });
        asked.forEach(function(entry) {
            if (answered.indexOf(entry) === -1) dropHopQrEntry(entry);
        });
    }

    // Asks the hub, in one call, for every QR code whose moment has come and
    // for every one whose last attempt failed. Nothing is asked on a page that
    // is over or leaving, while no hop QR code is visible on it, or while the
    // connection is not up: a moment that comes then waits for the connection
    // to come back, and the reconnect and the revival of the socket ask again.
    function reissueHopQr() {
        if (!hopQrReissue || hopQrCallPending || hopQrEntries.length === 0) return;
        if (isTerminal || isNavigating) return;
        var hubStates = window.signalR && window.signalR.HubConnectionState;
        if (!connection || !hubStates || connection.state !== hubStates.Connected) return;
        if (document.documentElement.matches(hopQrReissue.notVisibleSelector)) return;
        var now = Date.now();
        var asked = hopQrEntries
            .filter(function(record) { return record.failed || record.dueAt <= now; })
            .map(function(record) { return record.entry; });
        if (asked.length === 0) {
            planHopQrReissue();
            return;
        }
        // Released on either outcome, so that no answer can leave the page
        // unable to ask again.
        function finish() {
            hopQrCallPending = false;
            planHopQrReissue();
        }
        hopQrCallPending = true;
        connection.invoke(config.hub.reissueHopQrMethod, sessionId, asked)
            .then(function(items) {
                applyHopQrAnswer(asked, items);
            }, function() {
                asked.forEach(function(entry) {
                    var record = findHopQrEntry(entry);
                    if (record) failHopQrEntry(record);
                });
            })
            .then(finish, finish);
    }

    // SignalR connection. If the client library failed
    // to load → immediately and transparently degrade to REST polling
    // (the page keeps tracking the transaction instead of going silent).
    if (!window.signalR || !window.signalR.HubConnectionBuilder) {
        startPolling();
        return;
    }

    var connection = new window.signalR.HubConnectionBuilder()
        .withUrl(withBase(config.hub.path))
        .withAutomaticReconnect()
        .build();

    connection.on(config.hub.statusMethod, function(message) {
        handleStatus(message.status, message.redirectUrl, message.errorCode,
            { step: message.completionStep, surface: message.completionSurface },
            message.answerBrowserBound);
    });

    // The automatic launch of the intermediate page reports the opened page and
    // the start of the transition into the channel within milliseconds of each
    // other, as two requests whose order of arrival is not guaranteed. Shown as
    // they come, the two lines would flicker, so the line of the opened page is
    // held back for a moment and gives way to the line of the transition
    // whenever that one is shown — before the hold runs out or ahead of it.
    var HOP_LAUNCH_CODES = config.hopLaunchCodes;
    var HOP_OPENED_HOLD_MS = 1000;
    var hopOpenedTimer = null;
    var hopLaunchShown = false;

    // Intermediate channel statuses of any channel — the Email statuses and
    // the hop codes alike. An additive channel: does not affect the lifecycle
    // handling above. The last code to arrive is the one shown; the order of
    // arrival is not restored, except for the pair above.
    connection.on(config.hub.channelStatusMethod, function(message) {
        if (isTerminal || !message) return;
        var code = message.channelStatus;
        // A status telling the channel of the event works is the moment the
        // page remembers that channel — whatever the status does to the page
        // below. A repeated one writes the same value again.
        if (REMEMBER_CHANNEL && REMEMBER_CHANNEL.answerStatusCodes.indexOf(code) !== -1) {
            noteWorkingChannel(message.channelType);
        }
        // The answer of the bot is no step of the hop (applyChannelAnswer). A
        // page that is leaving ignores it.
        if (code === CHANNEL_ANSWER.statusCode) {
            if (!isNavigating) applyChannelAnswer(message.channelType);
            return;
        }
        // The first hop code switches the page to the instruction block; the
        // stylesheet does the switching on the root attribute, and setting it
        // again changes nothing. A focus left inside the channel choice would
        // drop to the body with it, so it moves onto the block that takes the
        // choice's place; a focus anywhere else stays where the user put it.
        // A page with the block turned off keeps its channel choice on screen
        // and only shows the code on the status line below. A page already
        // showing a line of a state is not switched by a late code.
        if (!config.hopInstructionDisabled && !stateLineShown && HOP_STATUS_CODES.indexOf(code) !== -1) {
            // The way back from the remembered channel leaves with the choice,
            // so a focus standing on it moves on as a focus in the choice does.
            var focusInChoice = (!!channelsEl && channelsEl.contains(document.activeElement))
                || (!!rememberBackBlock && rememberBackBlock.contains(document.activeElement));
            document.documentElement.setAttribute(config.hopStartedAttribute, "");
            var hopInstructionEl = document.getElementById(config.hopInstructionElementId);
            if (focusInChoice && hopInstructionEl) hopInstructionEl.focus();
            // A hop code that names a channel is the phone on its way into it —
            // the reopening of the link apart, which is no step of that way.
            if (code !== HOP_REOPENED.statusCode) {
                if (code === HOP_LAUNCH_CODES.channelLaunched) forgetOtherSpareTransitions(message.channelType);
                noteSpareTransition(hopSpareScope, message.channelType);
                noteHopChannelHint(message.channelType);
            }
        }
        // The reopening of the hop link puts its warning into the live region
        // that stands empty on the page from the load, so the filling is
        // announced; the status line keeps the hint of the hop. A page without
        // the region (the scanner page) ignores the code. The server reports it
        // once per transaction; a repeated code finds the label already there.
        if (code === HOP_REOPENED.statusCode) {
            var reopenedEl = document.getElementById(HOP_REOPENED.elementId);
            if (!reopenedEl || reopenedEl.firstChild) return;
            var badge = document.createElement('div');
            badge.className = HOP_REOPENED.badgeClass;
            badge.textContent = config.texts.hopReopened;
            reopenedEl.appendChild(badge);
            return;
        }
        // Once the line of the answer of the bot is shown, a hop code no longer
        // words the status line: the person is already in the chat. Once a
        // line of a state is shown, no code does.
        if (answerLineShown && HOP_STATUS_CODES.indexOf(code) !== -1) return;
        if (stateLineShown) return;
        var text = channelStatusText(code, message.channelType);
        if (!text) return;
        // A text with the {0} slot names the channel of the event, so an event
        // without one leaves the line alone. A channel outside the page is named
        // by its own type. The replacement is a function so that no "$" pattern
        // of a name is expanded.
        if (text.indexOf('{0}') !== -1) {
            var channelType = message.channelType;
            if (!channelType) return;
            var channelName = Object.prototype.hasOwnProperty.call(CHANNEL_NAMES, channelType)
                ? CHANNEL_NAMES[channelType]
                : channelType;
            text = text.replace('{0}', function() { return channelName; });
        }
        // A line that is shown takes the place of a held one.
        clearTimeout(hopOpenedTimer);
        if (code === HOP_LAUNCH_CODES.interstitialOpened) {
            if (hopLaunchShown) return;
            hopOpenedTimer = setTimeout(function() {
                // The page has ended, is leaving or shows a line of a state
                // meanwhile: its last line stays.
                if (isTerminal || isNavigating || stateLineShown) return;
                statusEl.textContent = text;
                statusEl.className = "veriqa-status confirmed";
            }, HOP_OPENED_HOLD_MS);
            return;
        }
        if (code === HOP_LAUNCH_CODES.channelLaunched) {
            // The page's own tap on the mail-client button, coming back: its
            // line already says what to do with the opened mail.
            if (ownEmailLaunchChannel && message.channelType === ownEmailLaunchChannel) return;
            hopLaunchShown = true;
        }
        statusEl.textContent = text;
        statusEl.className = "veriqa-status confirmed";
    });

    // The line of a channel status: the one the server worded for the channel
    // of the event where it gave one, otherwise the line of the code. Own keys
    // only: a code or a channel type such as "constructor" must not read the
    // prototype of a map and put a function on the status line.
    function channelStatusText(code, channelType) {
        var hasOwn = Object.prototype.hasOwnProperty;
        if (channelType && hasOwn.call(CHANNEL_STATUS_TEXTS_BY_CHANNEL, code)) {
            var byChannel = CHANNEL_STATUS_TEXTS_BY_CHANNEL[code];
            if (hasOwn.call(byChannel, channelType)) return byChannel[channelType];
        }
        return hasOwn.call(CHANNEL_STATUS_TEXTS, code) ? CHANNEL_STATUS_TEXTS[code] : "";
    }

    // Final connection drop (after auto-reconnect is exhausted) —
    // degrade to polling instead of a dead end.
    connection.onclose(function() {
        if (!isTerminal) { startPolling(); }
    });

    // The replacement of the hop QR codes runs on this connection alone.
    planHopQrReissue();

    connection.start()
        .then(function() {
            return connection.invoke(config.hub.joinMethod, sessionId);
        })
        .then(function() {
            reissueHopQr();
        })
        .catch(function(err) {
            console.error("SignalR error:", err);
            // start/negotiate did not succeed → degrade to REST polling
            startPolling();
        });

    // Heartbeat / reconnect: after an automatic reconnect the connection is back
    // on the wire but out of the transaction group, so it joins the group again,
    // and a replacement of a QR code that fell due meanwhile is asked for then.
    // A join that fails leaves the connection up but outside the group: no
    // broadcast is addressed to it and nothing else would resubscribe it, so the
    // tier below takes over the tracking instead of the page waiting out the TTL.
    connection.onreconnected(function() {
        connection.invoke(config.hub.joinMethod, sessionId)
            .catch(function() { startPolling(); })
            .then(function() {
                reissueHopQr();
            });
    });
})();
