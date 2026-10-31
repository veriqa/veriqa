// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// Client script of the button that closes the tab of a page of the "Core" contour.
//
// The file ships as an embedded resource of Veriqa.Core.ChannelAdapter and is INLINED into the
// generated document inside a <script> tag carrying the page's CSP nonce. Every comment of
// this file reaches the end user's browser verbatim, so the norms its code implements are
// named in CorePageCloseAction (C#), not here.
(function() {
    "use strict";

    // The ids below are emitted from the constants CorePageCloseAction.CloseButtonId and
    // CorePageCloseAction.CloseHintId; rename each pair together.
    var button = document.getElementById("veriqa-close");
    var hint = document.getElementById("veriqa-close-hint");
    if (!button || !hint) return;

    // Time the browser is given to close the window before the page concludes it stayed open.
    var closeGraceMs = 300;

    button.addEventListener("click", function() {
        window.close();
        setTimeout(function() {
            if (!window.closed) hint.hidden = false;
        }, closeGraceMs);
    });
})();
