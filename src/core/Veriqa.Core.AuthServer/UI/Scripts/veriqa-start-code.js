// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// The copying function of the pages of the "Core" contour that show the message of the start by
// a code. One function binds the controls of every such page, so the copy and its status line
// behave the same wherever the message is shown.
//
// The file ships as an embedded resource of Veriqa.Core.AuthServer and is INLINED into the
// generated document inside a <script> tag carrying the page's CSP nonce, ahead of the script
// of the page that calls it. Every comment of this file reaches the end user's browser
// verbatim, so the norms its code implements are named in ChannelStartCodeBlock (C#), not here.
(function() {
    "use strict";

    // Binds every control under "root" that carries the attribute named by the server. The
    // value of the attribute is a KEY: the message itself comes from "settings.messages",
    // which the page read from its data block, and is never read back from the markup.
    //
    // The handler only copies. A control that is a link is followed by the browser exactly as
    // it would be without this script — the default action is neither prevented nor delayed,
    // so a clipboard that is missing or refuses takes nothing but the copy away. The write is
    // started inside the press itself, which is the gesture the browser asks for.
    window.veriqaStartMessage = function(root, settings) {
        var messages = settings.messages;
        root.querySelectorAll("[" + settings.attribute + "]").forEach(function(control) {
            control.addEventListener("click", function() {
                var key = control.getAttribute(settings.attribute);
                // An own key only: a key naming something on the prototype of the map is not
                // a message.
                if (!Object.prototype.hasOwnProperty.call(messages, key)) return;
                // No clipboard here (an insecure context, an old browser): the message stays
                // on screen to be typed or selected, and nothing is announced.
                if (!navigator.clipboard || typeof navigator.clipboard.writeText !== "function") return;
                var block = control.closest(settings.blockSelector);
                var status = block ? block.querySelector(settings.statusSelector) : null;
                var written;
                try {
                    written = navigator.clipboard.writeText(messages[key]);
                } catch (e) {
                    return;
                }
                written.then(function() {
                    if (status) status.textContent = settings.copiedText;
                }, function() {
                    // A refused write is not an error of the page: the status line stays empty.
                });
            });
        });
    };
})();
