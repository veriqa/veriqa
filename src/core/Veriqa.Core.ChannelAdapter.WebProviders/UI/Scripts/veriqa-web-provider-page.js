// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// Client script of the page of a web provider channel: it draws the slider that takes "Yes" where
// the page asks for it, and it watches the state of the page and reloads the page when the state
// changes, so a question, a message or an outcome shows without a manual reload. Without the script
// the page shows its state as rendered and answers by plain forms.
//
// The file ships as an embedded resource of Veriqa.Core.ChannelAdapter.WebProviders and is INLINED
// into the generated document inside a <script> tag carrying the page's CSP nonce. Every comment of
// this file reaches the end user's browser verbatim, so the norms its code implements are named in
// WebProviderPageRenderer (C#), not here.
(function() {
    "use strict";

    setUpSlider();

    // Draws the slider in place of the button of "Yes" when the mode of the answers asks for it here,
    // and posts the form of "Yes" once the handle is drawn to the end of its track, or moved or
    // activated without a pointer: by the keyboard or by assistive technology.
    function setUpSlider() {
        // The attribute and its values are emitted from WebProviderPageFields.ConfirmSliderAttribute and
        // the names of WebProviderConfirmSlider; rename them together.
        var answers = document.querySelector("[data-confirm-slider]");
        if (!answers) return;

        var mode = answers.getAttribute("data-confirm-slider");
        var coarse = !!(window.matchMedia && window.matchMedia("(pointer: coarse)").matches);
        if (mode !== "Everywhere" && !(mode === "Mobile" && coarse)) return;

        var slider = answers.querySelector(".veriqa-wp-slider");
        var handle = slider && slider.querySelector(".veriqa-wp-slider-handle");
        var form = slider && slider.parentNode;
        var yes = form && form.querySelector("button[type=submit]");
        if (!handle || !yes) return;

        // How close to the end of the track the handle counts as drawn to it, in pixels.
        var endTolerancePx = 4;

        // The position of the handle as assistive technology reads it: a share of the track, in percent.
        var fullPercent = 100;

        var sent = false;
        var pointerId = null;
        var startX = 0;
        var offset = 0;
        // A pointer pressed the handle. Read only where the browser does not say which pointer a click
        // came from: there the click a press ends with is told from an activation by this flag.
        var pressed = false;

        yes.hidden = true;
        slider.hidden = false;
        answers.classList.add("veriqa-wp-answers-slider");
        handle.setAttribute("role", "slider");
        handle.setAttribute("aria-valuemin", "0");
        handle.setAttribute("aria-valuemax", String(fullPercent));
        handle.setAttribute("aria-valuenow", "0");

        function travel() {
            return Math.max(0, slider.clientWidth - handle.offsetWidth - 2 * handle.offsetLeft);
        }

        // Moves the handle and tells the track how far it is, so the drawn part fills behind the handle
        // and the text of the track fades.
        function place(x) {
            var length = travel();
            offset = Math.max(0, Math.min(x, length));
            var share = length > 0 ? offset / length : 0;
            handle.style.transform = "translateX(" + offset + "px)";
            slider.style.setProperty("--veriqa-wp-slider-offset", offset + "px");
            slider.style.setProperty("--veriqa-wp-slider-share", String(share));
            handle.setAttribute("aria-valuenow", String(Math.round(share * fullPercent)));
            slider.classList.toggle("veriqa-wp-slider-ready", length > 0 && offset >= length - endTolerancePx);
        }

        function send() {
            if (sent) return;
            sent = true;
            slider.classList.add("veriqa-wp-slider-sent");
            place(travel());
            handle.disabled = true;
            yes.click();
        }

        function release(reached) {
            pointerId = null;
            slider.classList.remove("veriqa-wp-slider-dragging");
            if (reached) {
                send();
            } else {
                place(0);
            }
        }

        handle.addEventListener("pointerdown", function(event) {
            if (sent || !event.isPrimary || event.button !== 0) return;
            pressed = true;
            pointerId = event.pointerId;
            startX = event.clientX - offset;
            slider.classList.add("veriqa-wp-slider-dragging");
            handle.setPointerCapture(pointerId);
        });

        handle.addEventListener("pointermove", function(event) {
            if (event.pointerId !== pointerId) return;
            place(event.clientX - startX);
        });

        handle.addEventListener("pointerup", function(event) {
            if (event.pointerId !== pointerId) return;
            release(offset >= travel() - endTolerancePx);
        });

        // The browser took the gesture over (a scroll) or the capture was lost: nothing is sent. No click
        // follows a cancelled gesture, so the next one is an activation again.
        handle.addEventListener("pointercancel", function(event) {
            if (event.pointerId !== pointerId) return;
            pressed = false;
            release(false);
        });
        handle.addEventListener("lostpointercapture", function(event) {
            if (event.pointerId !== pointerId) return;
            release(false);
        });

        // Moving the handle forward by the keyboard moves it to the end at once, and so does assistive
        // technology adjusting the slider up.
        handle.addEventListener("keydown", function(event) {
            pressed = false;
            if (event.key === "ArrowRight" || event.key === "ArrowUp" || event.key === "End" || event.key === "PageUp") {
                event.preventDefault();
                send();
            }
        });

        // A click of no pointer is an activation: Enter and Space of the keyboard, or assistive
        // technology activating the handle; the browser names no pointer type for it. A click a pointer
        // ends with is a press that sends nothing, whether or not the handle was drawn.
        handle.addEventListener("click", function(event) {
            event.preventDefault();
            var byPointer = typeof event.pointerType === "string" ? event.pointerType !== "" : pressed;
            pressed = false;
            if (!byPointer) send();
        });
    }

    // The id is emitted from the constant WebProviderPageFields.PageElementId; rename both together.
    var page = document.getElementById("veriqa-web-provider-page");
    if (!page) return;

    // The request is over and nothing follows it on this page: the tab is closed at once, and the page
    // under it, when there is one, shows the outcome. A tab the browser keeps open - among them the tab
    // the scanner page navigated to the channel - shows this page as it is. The attribute is emitted
    // from WebProviderPageFields.ClosesTabAttribute; rename both together.
    if (page.hasAttribute("data-closes-tab")) {
        window.close();
    }

    var stateUrl = page.getAttribute("data-state-url");
    var renderedFingerprint = page.getAttribute("data-fingerprint");
    var remainingMs = parseInt(page.getAttribute("data-remaining-ms"), 10);
    if (!stateUrl || !renderedFingerprint || !(remainingMs > 0)) return;

    // Pause between two reads of the state.
    var pollIntervalMs = 2000;

    // The answer of a browser the session is gone for.
    var sessionGoneStatus = 403;

    // The session of the page ends at this moment; nothing changes on the page after it.
    var deadline = Date.now() + remainingMs;

    function schedule() {
        if (Date.now() < deadline) {
            setTimeout(poll, pollIntervalMs);
        }
    }

    function poll() {
        fetch(stateUrl, { credentials: "same-origin", cache: "no-store" })
            .then(function(response) {
                // The session is gone for this browser: the page stays as rendered.
                if (response.status === sessionGoneStatus) return null;
                // Any other failure is passing: the next read tries again.
                if (!response.ok) return "";
                return response.text();
            })
            .then(function(fingerprint) {
                if (fingerprint === null) return;
                if (fingerprint !== "" && fingerprint !== renderedFingerprint) {
                    window.location.reload();
                    return;
                }
                schedule();
            })
            .catch(function() {
                // A network failure is passing: the next read tries again.
                schedule();
            });
    }

    schedule();
})();
