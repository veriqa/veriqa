// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Primitives;

using Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Providers.Twilio;

/// <summary>
/// Check of the <c>X-Twilio-Signature</c> of an inbound webhook with a form body, by the algorithm of the Twilio
/// webhook security documentation: HMAC-SHA1 keyed with the account Auth Token over the webhook URL followed by
/// every form parameter as <c>name + value</c>, the names sorted in case-sensitive (ordinal) order, the result
/// Base64-encoded. The Twilio SDK is not used; this is the one own cryptographic place of the provider.
/// </summary>
internal static class TwilioRequestSignature
{
    /// <summary>
    /// Checks the signature of a webhook.
    /// </summary>
    /// <param name="authToken">Auth Token of the account the webhook is expected from.</param>
    /// <param name="webhookUrl">Webhook URL exactly as it is set in the Twilio console.</param>
    /// <param name="form">Decoded form parameters of the request body.</param>
    /// <param name="signatureHeader">Value of the signature header as received.</param>
    /// <returns>Whether the signature is the one Twilio computes for this URL, form and token.</returns>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "HMAC-SHA1 is the algorithm Twilio signs its webhooks with; the check cannot choose another.")]
    public static bool IsValid(
        string authToken,
        string webhookUrl,
        IReadOnlyDictionary<string, StringValues> form,
        string signatureHeader)
    {
        // The method decodes the received signature into its fixed 20 bytes (anything else is not a Twilio
        // signature), computes the expected one and compares the two in constant time.
        Span<byte> receivedSignature = stackalloc byte[TwilioConstants.SignatureSizeBytes];
        if (!Convert.TryFromBase64String(signatureHeader.Trim(), receivedSignature, out var written)
            || written != TwilioConstants.SignatureSizeBytes)
        {
            return false;
        }

        var computedSignature = HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(authToken),
            Encoding.UTF8.GetBytes(BuildSignedString(webhookUrl, form)));

        return CryptographicOperations.FixedTimeEquals(computedSignature, receivedSignature);
    }

    /// <summary>
    /// Builds the string Twilio signs: the URL, then each parameter name followed by its value, names in
    /// ordinal order. A parameter repeated in the form contributes <c>name + value</c> once per value, values
    /// in ordinal order — the rule of the Twilio helper libraries; a WhatsApp message webhook never repeats one.
    /// </summary>
    /// <param name="webhookUrl">Webhook URL as set in the Twilio console.</param>
    /// <param name="form">Decoded form parameters.</param>
    /// <returns>The string to sign.</returns>
    private static string BuildSignedString(string webhookUrl, IReadOnlyDictionary<string, StringValues> form)
    {
        var builder = new StringBuilder(webhookUrl);

        foreach (var name in form.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var value in form[name].Order(StringComparer.Ordinal))
            {
                builder.Append(name);
                builder.Append(value);
            }
        }

        return builder.ToString();
    }
}
