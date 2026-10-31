// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net;
using System.Text.Json;

using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// The request page a link of a letter opens — of a sign-in and of a confirmation transaction alike —
/// continued without the press of its button when the link is opened by the browser that took part in the
/// transaction. The button guards against a mail scanner following links; the browser of the transaction is
/// not one, so it is not asked to press it. One mechanism for both types: they differ only in what the press
/// leads to, not in whether the browser is spared it.
/// <para>
/// The browser is recognised only where the transaction already holds what to recognise it by: the scanner
/// context of the transaction with both its browser fingerprint and its IP address — the phone of a hop
/// whose launch page computed the fingerprint and whose request gave the address. No transaction collects
/// either for this page. The page then hides the button, computes the fingerprint by the very functions
/// the launch page used (<see cref="CorePageBrowserFingerprint"/>) and sends the form of the button with
/// the fingerprint and the mark of its own sending (<see cref="EmailAdapterConstants.SameBrowserMarkField"/>).
/// The server goes on as by the button only when the fingerprint equals the stored one character by
/// character and the address of the request equals the stored one, in the form the context collector writes
/// it in; any other answer is the page with the button, and nothing is written.
/// </para>
/// <para>
/// The sending gives nothing the button does not: the same POST without the mark goes on unconditionally.
/// A guessed fingerprint therefore opens no way past the page, and the check decides only whether the person
/// is spared one press. The fingerprint a page sends is neither stored nor logged — the log states whether
/// it matched, nothing more.
/// </para>
/// </summary>
internal static class EmailLinkSameBrowser
{
    /// <summary>
    /// Identifier of the form of the continue button — the form the script sends.
    /// </summary>
    public const string FormId = "veriqa-link-continue-form";

    /// <summary>
    /// Identifier of the continue button — the element the script hides while it computes the fingerprint
    /// and shows again when it does not send the form.
    /// </summary>
    public const string ButtonId = "veriqa-link-continue";

    /// <summary>
    /// Longest wait for the fingerprint, in milliseconds; past it the button is shown. The computation
    /// takes a few milliseconds, so the limit is there for a browser that never settles it, and a page that
    /// launches nothing can afford a wider one than the launch page of the hop mode.
    /// </summary>
    private const int FingerprintTimeLimitMilliseconds = 1000;

    /// <summary>
    /// Value of <see cref="EmailAdapterConstants.SameBrowserMarkField"/>; only its presence is read.
    /// </summary>
    private const string MarkValue = "1";

    /// <summary>
    /// The script. Built once per process.
    /// </summary>
    private static readonly string ScriptText = BuildScript();

    /// <summary>
    /// Whether the transaction holds what the browser of a link is recognised by — the scanner context with
    /// a fingerprint and an IP address. Without either the page shows its button.
    /// </summary>
    /// <param name="transaction">The transaction of the letter; null — none could be read.</param>
    /// <returns>true — the page may continue by itself.</returns>
    public static bool IsOffered(Transaction? transaction) =>
        transaction?.ScannerContextSnapshot is { Fingerprint.Length: > 0, IpAddress.Length: > 0 };

    /// <summary>
    /// Whether the browser that sent the page by itself is the one that took part in the transaction: both
    /// its fingerprint and the address of its request equal the stored ones.
    /// </summary>
    /// <param name="transaction">The transaction of the letter; null — none could be read.</param>
    /// <param name="fingerprint">The fingerprint the page sent; null — none.</param>
    /// <param name="remoteIpAddress">Address of the request, as the connection states it.</param>
    /// <returns>true — the transaction goes on as by the button.</returns>
    public static bool Matches(Transaction? transaction, string? fingerprint, IPAddress? remoteIpAddress) =>
        transaction?.ScannerContextSnapshot is { Fingerprint: { Length: > 0 } storedFingerprint, IpAddress: { Length: > 0 } storedIpAddress }
        && string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal)
        && string.Equals(storedIpAddress, remoteIpAddress?.ToString(), StringComparison.Ordinal);

    /// <summary>
    /// Builds the script element, inlined under the CSP nonce of the request (SPEC-007 UI-040).
    /// </summary>
    /// <param name="cspNonce">CSP nonce of this request; without one the script goes out without the
    /// attribute, as every inline script of the contour does.</param>
    /// <returns>Markup of the script.</returns>
    public static string Script(string? cspNonce)
    {
        var nonceAttribute = string.IsNullOrEmpty(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        return $"<script{nonceAttribute}>{ScriptText}</script>";
    }

    /// <summary>
    /// Builds the script: it hides the button, computes the fingerprint and sends the form with it and the
    /// mark; without a fingerprint it shows the button. A page restored from the back/forward cache after
    /// the sending shows the button and drops the fields the script added, so a press there is a press.
    /// Every value goes in as a JSON literal; the script loads nothing and evaluates no string
    /// (SPEC-007 UI-040, UI-042).
    /// </summary>
    /// <returns>The text of the script.</returns>
    private static string BuildScript() => $$"""
        (function () {
          var form = document.getElementById({{Literal(FormId)}});
          var button = document.getElementById({{Literal(ButtonId)}});
          if (!form || !button) { return; }
          var added = [];
          function show() {
            for (var i = 0; i < added.length; i++) { form.removeChild(added[i]); }
            added = [];
            button.style.display = '';
          }
          function add(name, value) {
            var input = document.createElement('input');
            input.type = 'hidden';
            input.name = name;
            input.value = value;
            form.appendChild(input);
            added.push(input);
          }
          {{CorePageBrowserFingerprint.Functions}}
          window.addEventListener('pageshow', function (event) { if (event.persisted) { show(); } });
          button.style.display = 'none';
          try {
            boundedFingerprint({{FingerprintTimeLimitMilliseconds.ToString(CultureInfo.InvariantCulture)}}).then(function (value) {
              if (!value) { show(); return; }
              add({{Literal(EmailAdapterConstants.SameBrowserFingerprintField)}}, value);
              add({{Literal(EmailAdapterConstants.SameBrowserMarkField)}}, {{Literal(MarkValue)}});
              form.submit();
            });
          } catch (e) { show(); }
        })();
        """;

    /// <summary>
    /// A string as a JSON literal the script can carry.
    /// </summary>
    /// <param name="value">The string.</param>
    /// <returns>The literal.</returns>
    private static string Literal(string value) => JsonSerializer.Serialize(value);
}
