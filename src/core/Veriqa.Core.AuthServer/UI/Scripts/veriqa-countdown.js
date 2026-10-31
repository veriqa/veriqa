// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// The countdown of the pages of the "Core" contour that show the time a sign-in has left: the
// sign-in window and the page that collects the missing details. One function draws the figure
// and plans the next tick for both, so the format and the warning threshold live in one place.
//
// The file ships as an embedded resource of Veriqa.Core.AuthServer and is INLINED into the
// generated document inside a <script> tag carrying the page's CSP nonce, ahead of the script
// of the page that calls it. Every comment of this file reaches the end user's browser
// verbatim, so the norms its code implements are named in PageCountdown (C#), not here.
(function() {
    "use strict";

    // The remainder, in seconds, from which the figure takes the error colouring.
    var WARNING_SECONDS = 30;

    // The longest wait between two figures, in milliseconds.
    var TICK_MS = 1000;

    // Draws what is left and plans what comes next. The page owns the deadline and both
    // callbacks: "left" is the remainder in milliseconds on its own clock, "onZero" is what it
    // does once nothing is left, and "onTick" is its own tick, which calls this function again.
    //
    // The remainder is a MOMENT kept in milliseconds. The figure on screen is whole seconds, but
    // the decision that nothing is left is not: a remainder counted in whole seconds is zero for
    // the whole last second, and a tick almost never lands on the deadline exactly (the first
    // remainder is a fraction, and every timeout drifts late). A page deciding by the figure
    // would ask about an ending that has not happened, be told "alive, 0.4 s left", replan to
    // the same instant and ask again — a burst of requests at the speed of the round trip.
    window.veriqaCountdown = function(element, left, onZero, onTick) {
        var diff = Math.max(0, Math.floor(left / 1000));
        var min = Math.floor(diff / 60);
        var sec = diff % 60;
        element.textContent = (min > 0 ? min + ":" : "") + (sec < 10 ? "0" : "") + sec;
        // The warning is a property of the CURRENT remainder, so it is written in both
        // directions: a deadline replanned back above the threshold must lose the error
        // colouring it took.
        element.className = diff <= WARNING_SECONDS
            ? "veriqa-countdown warning"
            : "veriqa-countdown";
        // Zero on the clock is a QUESTION for the server, not an answer: the figure is drawn
        // first, so the page shows the zero it reached while the page asks.
        if (left <= 0) {
            onZero();
            return;
        }
        // Never past the deadline, and never more than a tick from the next figure to draw —
        // whichever of the two comes first.
        setTimeout(onTick, Math.min(TICK_MS, left));
    };
})();
