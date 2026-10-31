// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// Client script of the page of a web provider channel: it watches the state of the page and reloads
// the page when the state changes, so a question, a message or an outcome shows without a manual
// reload. Without the script the page shows its state as rendered and answers by plain forms.
//
// The file ships as an embedded resource of Veriqa.Core.ChannelAdapter.WebProviders and is INLINED
// into the generated document inside a <script> tag carrying the page's CSP nonce. Every comment of
// this file reaches the end user's browser verbatim, so the norms its code implements are named in
// WebProviderPageRenderer (C#), not here.
(function() {
    "use strict";

    // The id is emitted from the constant WebProviderPageFields.PageElementId; rename both together.
    var page = document.getElementById("veriqa-web-provider-page");
    if (!page) return;

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
