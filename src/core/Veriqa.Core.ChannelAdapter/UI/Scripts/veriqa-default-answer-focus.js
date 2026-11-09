// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0
//
// Client script of the default answer of a question page of the "Core" contour: the page focuses that
// answer itself, and its focus ring stays off until the user presses a key.
//
// The file ships as an embedded resource of Veriqa.Core.ChannelAdapter and is INLINED into the
// generated document inside a <script> tag carrying the page's CSP nonce. Every comment of
// this file reaches the end user's browser verbatim, so the norms its code implements are
// named in CorePageDefaultAnswerFocus (C#), not here.
(function() {
    "use strict";

    // The attribute is spelled here and in a rule of veriqa-core-base.css; rename both together.
    var quietAttribute = "data-veriqa-focus-quiet";
    var root = document.documentElement;

    root.setAttribute(quietAttribute, "");

    function wake() {
        root.removeAttribute(quietAttribute);
        document.removeEventListener("keydown", wake, true);
    }

    document.addEventListener("keydown", wake, true);
})();
