// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;

using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Text;

using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// Turns a mail read from the mailbox into the <see cref="InboundEmailMessage"/> every inbound mail
/// is handled as — the counterpart of the JSON parsing of the webhook.
/// </summary>
internal static class EmailImapMessageReader
{
    /// <summary>
    /// Name of the SPF method in an <c>Authentication-Results</c> header (RFC 8601).
    /// </summary>
    private const string SpfMethod = "spf";

    /// <summary>
    /// Name of the DKIM method in an <c>Authentication-Results</c> header.
    /// </summary>
    private const string DkimMethod = "dkim";

    /// <summary>
    /// Name of the DMARC method in an <c>Authentication-Results</c> header.
    /// </summary>
    private const string DmarcMethod = "dmarc";

    /// <summary>
    /// The character that ends the <c>authserv-id</c> and opens the list of results.
    /// </summary>
    private const char AuthservIdTerminator = ';';

    /// <summary>
    /// Builds the inbound message of a mail read from the mailbox.
    /// </summary>
    /// <param name="mail">The mail as the mailbox holds it.</param>
    /// <param name="fallbackMessageId">Identifier used when the mail carries no <c>Message-ID</c>.</param>
    /// <param name="receivedAt">The moment the mailbox received the mail (its <c>INTERNALDATE</c>).</param>
    /// <param name="inbound">Inbound settings of the tenant.</param>
    /// <returns>The inbound message.</returns>
    public static InboundEmailMessage Read(
        MimeMessage mail,
        string fallbackMessageId,
        DateTimeOffset receivedAt,
        EmailInboundOptions inbound)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(inbound);

        var (spf, dkim, dmarc) = ReadTrustedAuthenticationResults(mail, inbound.Imap.AuthservId);

        return new InboundEmailMessage(
            MessageId: string.IsNullOrWhiteSpace(mail.MessageId) ? fallbackMessageId : mail.MessageId,
            FromAddress: mail.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
            EnvelopeSender: ReadEnvelopeSender(mail),
            ToAddress: ReadRecipient(mail, inbound.InboundAddress),
            Subject: mail.Subject ?? string.Empty,
            Body: ReadText(mail),
            ReceivedAt: receivedAt.ToUniversalTime(),
            SpfResult: spf,
            DkimResult: dkim,
            DmarcResult: dmarc);
    }

    /// <summary>
    /// Reads the SPF, DKIM and DMARC results the mail server of the mailbox stated for the mail.
    /// </summary>
    /// <remarks>
    /// A sender is free to write an <c>Authentication-Results</c> header with any results into the
    /// mail, so only the header of the mailbox's own server counts: the one whose <c>authserv-id</c>
    /// is the configured one (RFC 8601 §5), and of several such — the topmost, since a server puts
    /// its header above everything the mail arrived with. A mail with no such header has no results.
    /// </remarks>
    /// <param name="mail">The mail.</param>
    /// <param name="authservId">The configured <c>authserv-id</c> of the mailbox's server.</param>
    /// <returns>The three results; each is null when the trusted header does not state it.</returns>
    private static (string? Spf, string? Dkim, string? Dmarc) ReadTrustedAuthenticationResults(
        MimeMessage mail,
        string? authservId)
    {
        if (string.IsNullOrWhiteSpace(authservId))
        {
            return default;
        }

        var trusted = authservId.Trim();

        // Headers are enumerated in the order of the mail, the topmost first.
        foreach (var header in mail.Headers)
        {
            if (header.Id is not HeaderId.AuthenticationResults)
            {
                continue;
            }

            if (!AuthenticationResults.TryParse(header.RawValue, out var results))
            {
                // A header of the trusted server that cannot be read is still the topmost one of
                // that server: the search ends here with no results rather than moving on to a
                // header below it, which the sender may have written.
                if (StartsWithAuthservId(header.Value, trusted))
                {
                    return default;
                }

                continue;
            }

            if (!string.Equals(results.AuthenticationServiceIdentifier, trusted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (
                ResultOf(results, SpfMethod),
                ResultOf(results, DkimMethod),
                ResultOf(results, DmarcMethod));
        }

        return default;
    }

    /// <summary>
    /// Tells whether an unparsed header value opens with the given <c>authserv-id</c>.
    /// </summary>
    /// <param name="headerValue">Value of the header.</param>
    /// <param name="authservId">The <c>authserv-id</c> looked for.</param>
    /// <returns><see langword="true"/> when the value starts with the identifier as a whole word.</returns>
    private static bool StartsWithAuthservId(string? headerValue, string authservId)
    {
        var value = headerValue?.TrimStart() ?? string.Empty;

        if (!value.StartsWith(authservId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Length == authservId.Length
            || value[authservId.Length] is AuthservIdTerminator
            || char.IsWhiteSpace(value[authservId.Length]);
    }

    /// <summary>
    /// Returns the result of one method. A header may state a method several times (one DKIM result
    /// per signature): a pass among them is the result, otherwise the first one stated.
    /// </summary>
    /// <param name="results">The parsed trusted header.</param>
    /// <param name="method">Method name.</param>
    /// <returns>The result, or null when the header does not state the method.</returns>
    private static string? ResultOf(AuthenticationResults results, string method)
    {
        string? first = null;

        foreach (var result in results.Results)
        {
            if (!string.Equals(result.Method, method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(result.Result, EmailAdapterConstants.VerificationResultPass, StringComparison.OrdinalIgnoreCase))
            {
                return EmailAdapterConstants.VerificationResultPass;
            }

            first ??= result.Result;
        }

        return first;
    }

    /// <summary>
    /// Reads the envelope sender from the <c>Return-Path</c> header the delivering server wrote.
    /// </summary>
    /// <param name="mail">The mail.</param>
    /// <returns>The address, or null when the header is absent or empty (a bounce).</returns>
    private static string? ReadEnvelopeSender(MimeMessage mail)
    {
        var returnPath = mail.Headers[HeaderId.ReturnPath];

        return !string.IsNullOrWhiteSpace(returnPath)
            && MailboxAddress.TryParse(returnPath, out var mailbox)
            && !string.IsNullOrWhiteSpace(mailbox.Address)
                ? mailbox.Address
                : null;
    }

    /// <summary>
    /// Picks the recipient the mail was written to: the address among <c>To</c> and <c>Cc</c> that
    /// is the inbound address of the tenant, plus-addressed or not; when there is none, the first
    /// address of <c>To</c>.
    /// </summary>
    /// <param name="mail">The mail.</param>
    /// <param name="inboundAddress">The inbound address of the tenant.</param>
    /// <returns>The recipient address, or an empty string when the mail names none.</returns>
    private static string ReadRecipient(MimeMessage mail, string inboundAddress)
    {
        var inbound = inboundAddress?.Trim() ?? string.Empty;

        foreach (var recipient in mail.To.Mailboxes.Concat(mail.Cc.Mailboxes))
        {
            if (IsInboundAddress(recipient.Address, inbound))
            {
                return recipient.Address;
            }
        }

        return mail.To.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
    }

    /// <summary>
    /// Tells whether an address is the inbound address itself or a plus-addressed form of it
    /// (<c>login+token@example.com</c> for <c>login@example.com</c>).
    /// </summary>
    /// <param name="address">Address of a recipient.</param>
    /// <param name="inboundAddress">The inbound address of the tenant.</param>
    /// <returns><see langword="true"/> when the address leads to the inbound mailbox.</returns>
    private static bool IsInboundAddress(string address, string inboundAddress)
    {
        var addressAt = address.LastIndexOf('@');
        var inboundAt = inboundAddress.LastIndexOf('@');

        if (addressAt <= 0 || inboundAt <= 0)
        {
            return false;
        }

        if (!address.AsSpan(addressAt).Equals(inboundAddress.AsSpan(inboundAt), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var local = address.AsSpan(0, addressAt);
        var inboundLocal = inboundAddress.AsSpan(0, inboundAt);

        if (local.Equals(inboundLocal, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return local.Length > inboundLocal.Length
            && local.StartsWith(inboundLocal, StringComparison.OrdinalIgnoreCase)
            && local[inboundLocal.Length] is EmailAdapterConstants.PlusAddressSeparator;
    }

    /// <summary>
    /// Reads the text of the mail: its plain-text part, or the text of its HTML part when the mail
    /// has no plain-text one.
    /// </summary>
    /// <param name="mail">The mail.</param>
    /// <returns>The text, or null when the mail has neither part.</returns>
    private static string? ReadText(MimeMessage mail)
    {
        if (mail.TextBody is { } text)
        {
            return text;
        }

        return mail.HtmlBody is { } html ? HtmlToText(html) : null;
    }

    /// <summary>
    /// Extracts the character data of an HTML document, leaving out markup, scripts and style
    /// sheets. Every tag becomes a space, so the text of two neighbouring elements never runs
    /// together into one word — the token of a mail is searched for as a run of an exact length.
    /// </summary>
    /// <param name="html">The HTML document.</param>
    /// <returns>The text of the document.</returns>
    private static string HtmlToText(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var text = new StringBuilder(html.Length);
        var insideStyle = false;

        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                if (tag.Id is HtmlTagId.Style)
                {
                    insideStyle = !tag.IsEndTag && !tag.IsEmptyElement;
                }

                text.Append(' ');
            }
            else if (token.Kind is HtmlTokenKind.Data && !insideStyle && token is HtmlDataToken data)
            {
                text.Append(data.Data);
            }
        }

        return text.ToString();
    }
}
