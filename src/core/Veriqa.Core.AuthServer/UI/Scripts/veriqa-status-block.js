// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// The reaction of the status block of the pages of the "Core" contour that follow a
// transaction to its end: every page carrying the script of the sign-in window, and the
// launch page of a channel. One function writes the status line on each of them and takes
// away the heading of the channel and the controls where the page handed them over — the
// page of the scanner with a chosen channel and the launch page — so those two pages cannot
// react to the same state differently.
//
// The file ships as an embedded resource of Veriqa.Core.AuthServer and is INLINED into the
// generated document inside a <script> tag carrying the page's CSP nonce, ahead of the script
// of the page that calls it. Every comment of this file reaches the end user's browser
// verbatim, so the norms its code implements are named in PageStatusBlock (C#), not here.
(function() {
    "use strict";

    // The status line class for each state. The text of the line is the server's: it comes in
    // "lines" under the same name as the state, and this file holds none of its own.
    var STATUS_CLASSES = {
        returnToWindow: "veriqa-status confirmed",
        answerInLinkBrowser: "veriqa-status confirmed",
        processing: "veriqa-status confirmed",
        completed: "veriqa-status success",
        expired: "veriqa-status error",
        declined: "veriqa-status error",
        error: "veriqa-status error"
    };

    // The states that end the transaction: with them the page shows the row of
    // the action under the outcome, where it carries one.
    var ENDINGS = {
        completed: true,
        expired: true,
        declined: true,
        error: true
    };

    // Binds the block of one page. "parts" are the page's own elements: the status line, the
    // heading of the channel (null on a page that shows none), the controls an ending takes
    // away and the row of the action under the outcome (null on a page that carries none);
    // "lines" are the server's texts, one per state.
    //
    // "show" reacts to one state and nothing else: running once, flags of the ending, the
    // spinner, the countdown and the state attribute of the page stay with the page. A repeated
    // call writes the new line; what was taken away stays away.
    window.veriqaStatusBlock = function(parts, lines) {
        var status = parts.status;
        var hidden = (parts.heading ? [parts.heading] : []).concat(parts.controls || []);

        // Takes an element off the screen. The inline style and not the "hidden" attribute:
        // an element whose own rule sets its display wins over the attribute.
        function takeAway(el) {
            // The focus follows to the status line only where that line can hold it; a page
            // whose line does not leaves the focus where the browser puts it.
            if (status.hasAttribute("tabindex") && el.contains(document.activeElement)) {
                status.focus();
            }
            el.style.display = "none";
        }

        return {
            show: function(state) {
                // An own key only: a name on the prototype of the map is not a state.
                if (!Object.prototype.hasOwnProperty.call(STATUS_CLASSES, state)) return;
                status.textContent = lines[state];
                status.className = STATUS_CLASSES[state];
                status.removeAttribute("hidden");
                hidden.forEach(takeAway);
                if (parts.close && Object.prototype.hasOwnProperty.call(ENDINGS, state)) {
                    parts.close.hidden = false;
                }
            }
        };
    };
})();
