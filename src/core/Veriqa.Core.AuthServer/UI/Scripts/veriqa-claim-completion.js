// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// Client script of the page that collects the details a sign-in is still missing: it watches
// the status of the sign-in and, once the page on screen no longer matches it, reloads the
// page from the server by one ordinary navigation — asking at an interval and, sooner, whenever
// the hub says the status has changed; and, on a page that carries a countdown, it counts
// down the time the sign-in has left, with the function the sign-in window counts with
// (inlined above this script). On a page with the email block it also drives the block: the
// button that checks the confirmation, the button of the address step and "Continue". A page
// that does not watch the status (the page on the device that confirmed the sign-in) runs the
// part of the email block only.
//
// The file ships as an embedded resource of Veriqa.Core.AuthServer and is INLINED into the
// generated document inside a <script> tag carrying the page's CSP nonce. Every comment of
// this file reaches the end user's browser verbatim, so the norms its code implements are
// named in ClaimCompletionPageScript (C#), not here.
//
// Every value the request decides arrives in the JSON data block the page emits directly
// above it (<script type="application/json" id="veriqa-claim-completion-config">).
(function() {
    "use strict";

    // The id below reads the block emitted by the constant ClaimCompletionPageScript.ConfigElementId;
    // rename the two together.
    var config = JSON.parse(document.getElementById("veriqa-claim-completion-config").textContent);

    // Root-relative URLs get the path base of the application here, in one place.
    function withBase(url) {
        if (!config.basePath || !url) return url;
        return (url.charAt(0) === "/" && url.charAt(1) !== "/") ? config.basePath + url : url;
    }

    var statusUrl = withBase(config.statusUrl);
    var isNavigating = false;
    var formSubmitted = false;
    var timer = null;

    // The id below reads the element emitted by the constant PageCountdown.Markup; rename the
    // two together. A page the deadline no longer decides carries no such element and counts
    // nothing down: it only watches.
    var countdownEl = document.getElementById("veriqa-countdown");

    // The moment the countdown runs to, on THIS browser's clock: the server's remainder added
    // to the browser's own reading of "now", so no clock is shared with the server.
    var deadline = Date.now() + config.remainingMs;

    // Whether the countdown stands at zero. It then stops ticking: the page has asked the
    // server once and goes on asking at the pace of the polling, not at the pace of the clock.
    var atZero = false;

    // The only place that takes the browser off this page. The first caller wins, so a later
    // tick cannot start a second navigation while the browser is already leaving. A page whose
    // form has been sent does not leave on its own either: the form is taking it away, and a
    // navigation of the script would abort the request that carries the answer.
    function reload() {
        if (isNavigating || formSubmitted) return;
        isNavigating = true;
        window.location.href = withBase(config.callbackUrl);
    }

    // Whether the status no longer matches the page on screen. A finished sign-in does, and so
    // does another step or another surface of the completion. A status without the completion
    // part is a sign-in whose details are all given and which is being finished: the page keeps
    // waiting for the outcome instead of reloading into it. A page shown in a finished state
    // learns nothing from that state again: it is outdated once the code of the sign-in is
    // issued or the state is another one.
    function isOutdated(status) {
        if (config.finalStates.indexOf(status.state) !== -1) {
            return status.state !== config.renderedState || status.authorization_code_issued === true;
        }
        var completion = status.claim_completion;
        if (!completion) return false;
        return completion.step !== config.renderedStep
            || (completion.surface || null) !== (config.renderedSurface || null);
    }

    function schedule() {
        if (isNavigating) return;
        clearTimeout(timer);
        timer = setTimeout(check, config.pollIntervalMs);
    }

    // A page that does not watch the status never asks it: the tab brought back and the page
    // restored from the cache call here as well.
    function check() {
        if (isNavigating || !config.watchesStatus) return;
        fetch(statusUrl, { headers: { "Accept": "application/json" } })
            .then(function(resp) {
                // Gone: the server page says what became of the sign-in.
                if (resp.status === 404) {
                    reload();
                    return null;
                }
                // A refusal or a body that is not the status says nothing about the sign-in;
                // the check is simply repeated.
                if (!resp.ok) return null;
                return resp.json().catch(function() { return null; });
            })
            .then(function(status) {
                if (status && status.state && isOutdated(status)) {
                    reload();
                    return;
                }
                // Alive at zero, and the server says how much longer: the local deadline was
                // early (a browser clock running fast, a suspended tab), so the countdown runs
                // again from the server's own remainder. Alive with nothing left to report
                // keeps the zero on screen — naming the end is the server's part.
                if (atZero && status && status.state && status.ttl_seconds > 0) {
                    atZero = false;
                    deadline = Date.now() + status.ttl_seconds * 1000;
                    tick();
                }
                schedule();
            })
            .catch(schedule);
    }

    // Zero on the clock is a question for the server, not an answer: the check is made at
    // once, and what became of the sign-in is said by the page the callback shows.
    function askAtZero() {
        atZero = true;
        check();
    }

    function tick() {
        window.veriqaCountdown(countdownEl, deadline - Date.now(), askAtZero, tick);
    }

    // The values typed into the simple fields are kept in the session storage of the tab, so a
    // reload of the page - after an action of the email block, or by the user - puts them back.
    // Only the fields the page renders empty are filled: a value the page shows is the newer one.
    // A browser that refuses the storage only loses the values on a reload.
    var fields = config.simpleFields;
    var fieldInputs = [];
    if (fields) {
        var fieldsForm = document.getElementById(fields.formId);
        var elements = fieldsForm ? fieldsForm.elements : [];
        for (var i = 0; i < elements.length; i++) {
            var name = elements[i].name;
            if (name && name.indexOf(fields.namePrefix) === 0) fieldInputs.push(elements[i]);
        }
    }

    function keepFields() {
        if (fieldInputs.length === 0) return;
        var values = {};
        fieldInputs.forEach(function(input) { values[input.name] = input.value; });
        try { window.sessionStorage.setItem(fields.storageKey, JSON.stringify(values)); } catch (e) { /* Storage unavailable or full: the values are not kept over a reload. */ }
    }

    function forgetFields() {
        if (!fields) return;
        try { window.sessionStorage.removeItem(fields.storageKey); } catch (e) { /* Storage unavailable: nothing was kept. */ }
    }

    function restoreFields() {
        if (fieldInputs.length === 0) return;
        var values = null;
        try { values = JSON.parse(window.sessionStorage.getItem(fields.storageKey) || "null"); } catch (e) { /* Storage unavailable or the kept value unreadable: nothing to restore. */ }
        if (!values || typeof values !== "object") return;
        fieldInputs.forEach(function(input) {
            if (input.value === "" && typeof values[input.name] === "string") input.value = values[input.name];
        });
    }

    restoreFields();
    fieldInputs.forEach(function(input) {
        input.addEventListener("input", keepFields);
        input.addEventListener("change", keepFields);
    });

    // Whether something is typed into an input of the email block that belongs to the form - a code
    // or an address: "Continue" of such a form is then an action of the block, and the page comes back.
    function blockTyped(form) {
        for (var i = 0; i < form.elements.length; i++) {
            var input = form.elements[i];
            if (input.tagName !== "INPUT" || input.type === "hidden" || input.type === "submit") continue;
            if (!input.name || input.name.indexOf(fields.namePrefix) === 0) continue;
            if (input.value.trim() !== "") return true;
        }
        return false;
    }

    // Any form of the page — the details, the email block, "cancel" — sends the browser away by itself.
    // A submit another handler has called off leaves the page as it was. "Continue" and "Cancel
    // sign-in" - a button that posts to the form's own target - forget the values of the fields: the
    // first hands them to the server, the second ends the sign-in. An action of the email block, and
    // "Continue" that the server takes for one, keep them for the page it comes back to.
    document.addEventListener("submit", function(event) {
        if (event.defaultPrevented) return;
        formSubmitted = true;
        var submitter = event.submitter;
        var form = event.target;
        var blockAction = !submitter || submitter.hasAttribute("formaction")
            || (fields && form.id === fields.formId && blockTyped(form));
        if (blockAction) {
            keepFields();
        } else {
            forgetFields();
        }
    });

    // A tab brought back to the screen checks at once instead of waiting for the next tick. A tab
    // that stayed away long enough also asks again whether the address was confirmed elsewhere: a
    // mobile browser puts a tab in the background to sleep and drops its connection, so the message
    // of the hub may have been lost meanwhile.
    var hiddenSince = null;
    document.addEventListener("visibilitychange", function() {
        if (document.visibilityState !== "visible") {
            hiddenSince = Date.now();
            return;
        }
        check();
        var away = hiddenSince === null ? 0 : Date.now() - hiddenSince;
        hiddenSince = null;
        var action = config.channelAction;
        if (action && away >= action.recheckAfterAwayMs) press(action.buttonId);
    });

    // Restored from the back/forward cache: the navigation the latches guarded is over, and the
    // page on screen starts watching again.
    window.addEventListener("pageshow", function(event) {
        if (!event.persisted) return;
        isNavigating = false;
        formSubmitted = false;
        check();
    });

    // The press the hub asks for: the one the user would make on the button, so its form goes
    // out with whatever was typed into it. A page without the button, a page whose form is on
    // its way and a page that is leaving do not press; the press sends the form, so a repeated
    // status finds the page already sending.
    function press(buttonId) {
        if (isNavigating || formSubmitted) return;
        var button = document.getElementById(buttonId);
        if (!button || button.disabled) return;
        button.click();
    }

    // The email block, when the page shows one. Every element is looked up once; an element the
    // block does not show on its current step is simply absent and left alone.
    var block = config.emailBlock;
    var checkButton = block ? document.getElementById(block.checkButtonId) : null;
    var addressInput = block ? document.getElementById(block.addressInputId) : null;
    var actionButton = block ? document.getElementById(block.addressActionButtonId) : null;
    var continueButton = block ? document.getElementById(block.continueButtonId) : null;

    // The button that checks the confirmation is for a page that cannot learn of the
    // confirmation by itself: hidden while the connection to the hub is alive, as the server
    // renders it, and shown once it has not been alive for the configured time on end.
    var silenceTimer = null;
    function setHubAlive(alive) {
        if (!checkButton) return;
        if (alive) {
            clearTimeout(silenceTimer);
            silenceTimer = null;
            checkButton.hidden = true;
            return;
        }
        if (silenceTimer !== null || !checkButton.hidden) return;
        silenceTimer = setTimeout(function() {
            silenceTimer = null;
            checkButton.hidden = false;
        }, block.hubSilenceMs);
    }

    // The address step: its button stays on screen, enabled only while the input holds an address.
    function hasAddress() {
        return addressInput.value.trim() !== "";
    }
    function followAddress() {
        if (actionButton) actionButton.disabled = !hasAddress();
    }

    // Enter in the address input is the action of the step while it holds an address, and
    // "Continue" while it is empty or the step has no button (the way without verification) —
    // not the default button of the form, which on a page without simple fields is a button of
    // the block.
    function onAddressKey(event) {
        if (event.key !== "Enter" || event.isComposing) return;
        var target = hasAddress() && actionButton ? actionButton : continueButton;
        if (!target) return;
        event.preventDefault();
        target.click();
    }

    if (checkButton) {
        setHubAlive(false);
    }
    if (addressInput) {
        addressInput.addEventListener("input", followAddress);
        addressInput.addEventListener("keydown", onAddressKey);
        followAddress();
    }

    // The hub only hurries the page: a message that the status has changed is a reason to ask
    // the status at once, and what the answer means is decided where every answer is. Nothing
    // here touches the polling, so a missing library, a connection that never starts, a join
    // that fails and a connection lost for good all leave the page watching as it did. A page
    // that names an action for a channel status presses its button when that status arrives;
    // a page that names none listens to no channel status. The connection is alive once it is
    // in the group of the sign-in, and is not from the moment it reconnects or closes.
    function listen() {
        if (!window.signalR || !window.signalR.HubConnectionBuilder) return;
        // Errors only: the hub also sends this group messages the page has no handler for, and
        // the client would warn about each of them in the console.
        var connection = new window.signalR.HubConnectionBuilder()
            .withUrl(withBase(config.hub.path))
            .configureLogging(window.signalR.LogLevel.Error)
            .withAutomaticReconnect()
            .build();
        function join() {
            return connection.invoke(config.hub.joinMethod, config.sessionId)
                .then(function() { setHubAlive(true); });
        }
        if (config.watchesStatus) {
            connection.on(config.hub.statusMethod, function() { check(); });
        }
        var action = config.channelAction;
        if (action) {
            connection.on(action.method, function(message) {
                if (!message || message.channelStatus !== action.channelStatus) return;
                press(action.buttonId);
            });
        }
        connection.onreconnecting(function() { setHubAlive(false); });
        // Back on the wire after an automatic reconnect, but out of the group of the sign-in; a
        // confirmation announced while the connection was down was lost, so it is asked again.
        connection.onreconnected(function() {
            join()
                .then(function() { if (action) press(action.buttonId); })
                .catch(function() { });
        });
        connection.onclose(function() { setHubAlive(false); });
        connection.start().then(join).catch(function() { });
    }

    if (config.watchesStatus) {
        schedule();
        if (countdownEl) tick();
    }
    try { listen(); } catch (e) {
        // The push connection only speeds the check up; the timer above works without it.
    }
})();
