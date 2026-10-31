// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Email.Enums;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Validator of Email adapter settings.
/// Checks the required fields when the adapter is enabled and compliance with security policies.
/// In the Production environment (Core profile), additional security restrictions apply.
/// </summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    /// <summary>
    /// Address of the <c>Outbound</c> group inside the host configuration.
    /// </summary>
    private static readonly string OutboundPath =
        EmailOptions.SectionName + ":" + nameof(EmailOptions.Outbound);

    /// <summary>
    /// Address of the <c>Inbound</c> group inside the host configuration.
    /// </summary>
    private static readonly string InboundPath =
        EmailOptions.SectionName + ":" + nameof(EmailOptions.Inbound);

    /// <summary>
    /// Address of the <c>Outbound:Smtp</c> group inside the host configuration.
    /// </summary>
    private static readonly string SmtpPath =
        OutboundPath + ":" + nameof(EmailOutboundOptions.Smtp);

    /// <summary>
    /// Address of the <c>Inbound:Imap</c> group inside the host configuration.
    /// </summary>
    private static readonly string ImapPath =
        InboundPath + ":" + nameof(EmailInboundOptions.Imap);

    /// <summary>
    /// Address of the <c>Inbound:Imap:OAuth</c> group inside the host configuration.
    /// </summary>
    private static readonly string ImapOAuthPath =
        ImapPath + ":" + nameof(EmailImapOptions.OAuth);

    /// <summary>
    /// The outbound providers this build can actually deliver mail with. The enumeration declares
    /// more members than that — <c>SendGrid</c>, <c>Postmark</c> and <c>Mailgun</c> are reserved
    /// names with no sender behind them (SPEC-016 §7.1) — and a reserved name has always ended the
    /// start rather than left a host running with no way to send: <c>Smtp</c> is the one member the
    /// registration supplies a sender for, and <c>Custom</c> is the member that says the host
    /// supplies its own.
    /// </summary>
    private static readonly EmailOutboundProvider[] ImplementedOutboundProviders =
        [EmailOutboundProvider.Smtp, EmailOutboundProvider.Custom];

    /// <summary>
    /// Names of <see cref="ImplementedOutboundProviders"/> — what an operator may write into the
    /// outbound provider axis, and therefore what both refusals of that axis offer them.
    /// </summary>
    private static readonly string[] ImplementedOutboundProviderNames =
        [.. ImplementedOutboundProviders.Select(static provider => provider.ToString())];

    /// <summary>
    /// Names of the inbound providers this build can receive mail with — every member of the
    /// enumeration except the reserved <c>SmtpRelay</c> (SPEC-016 §7.2).
    /// </summary>
    private static readonly string[] ImplementedInboundProviderNames =
        [.. Enum.GetValues<EmailInboundProvider>()
            .Where(static provider => provider is not EmailInboundProvider.SmtpRelay)
            .Select(static provider => provider.ToString())];

    /// <summary>
    /// Names of the verification policies a mailbox read over IMAP can serve: every member but
    /// <see cref="EmailVerificationPolicy.ProviderVerified"/>, whose input a mailbox does not supply.
    /// </summary>
    private static readonly string[] ImapVerificationPolicyNames =
        [.. Enum.GetValues<EmailVerificationPolicy>()
            .Where(static policy => policy is not EmailVerificationPolicy.ProviderVerified)
            .Select(static policy => policy.ToString())];

    /// <summary>
    /// Host environment — used to separate prod/dev validation rules.
    /// </summary>
    private readonly IHostEnvironment _environment;

    /// <summary>
    /// Creates the validator instance taking the current environment into account.
    /// </summary>
    /// <param name="environment">Host environment (DemoCore profile = dev, Core profile = prod).</param>
    public EmailOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    /// <summary>
    /// Validates the Email adapter settings.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Settings to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        // If the adapter is disabled, no validation is required
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var unknownMembers = UnknownEnumMembers(options);

        if (unknownMembers.Count > 0)
        {
            return ValidateOptionsResult.Fail(unknownMembers);
        }

        // A member of the enumeration that this build ships no sender for. Judged here, right after
        // membership, because it is the same question asked of the same axis — is what the deployment
        // wrote a value this host can run on — and the operator gets one answer for both spellings of
        // "no" instead of a start that succeeds and a login that then fails with no sender resolved.
        var unsupportedOutboundProvider = UnsupportedOutboundProvider(options);

        if (unsupportedOutboundProvider is not null)
        {
            return ValidateOptionsResult.Fail(unsupportedOutboundProvider);
        }

        // PublicBaseUrl is required for generating magic links and QR (EM-053)
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            return ValidateOptionsResult.Fail(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.PublicBaseUrl)} is required when the Email adapter is enabled.");
        }

        var publicBaseUrlResult = ValidatePublicBaseUrl(options.PublicBaseUrl);
        if (publicBaseUrlResult.Failed)
        {
            return publicBaseUrlResult;
        }

        // The interval between two mails belongs to no mode: the sign-in mail of Pull and the mails of
        // the claims completion are both held by it. Zero is a value — the interval switched off.
        if (options.MailResendInterval < TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.MailResendInterval)} must not be negative; "
                + "00:00:00 switches the interval off.");
        }

        // At least one mode must be enabled
        if (!options.PullEnabled && !options.PushEnabled)
        {
            return ValidateOptionsResult.Fail(
                "At least one Email adapter mode must be enabled: Pull or Push.");
        }

        // Pull settings validation
        if (options.PullEnabled)
        {
            var pullResult = ValidatePullOptions(options);
            if (pullResult.Failed)
            {
                return pullResult;
            }
        }

        // Push settings validation
        if (options.PushEnabled)
        {
            var pushResult = ValidatePushOptions(options, _environment.IsProduction());
            if (pushResult.Failed)
            {
                return pushResult;
            }
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// The four enum axes of the section, judged by MEMBERSHIP rather than by the binder alone: an
    /// unknown NAME never reaches this object — the binder stops the start on it — while a raw NUMBER
    /// is converted into a member that does not exist and would otherwise travel on unnamed. Both
    /// spellings of the same mistake end the same way (SPEC-012 §8.2).
    /// <para>
    /// All four are judged here, before the mode-specific branches below: which of the modes is
    /// switched on decides what the section must SUPPLY, not whether what it wrote is a value at all.
    /// </para>
    /// <para>
    /// The outbound provider is judged here and nowhere else. The registration of the sender used to
    /// refuse an unsupported provider on its own while the container was being built — before any
    /// options validation runs — by a message that named the axis in words but not its configuration
    /// address. That refusal is gone: the registration now supplies a sender for the providers it
    /// implements and leaves the judgement of the value to this check, so the axis fails the start
    /// the same way as the three below it, whether the value arrived from the section or from a
    /// Configure delegate in code.
    /// </para>
    /// </summary>
    /// <param name="options">Email adapter settings.</param>
    /// <returns>One message per axis whose value is not a member of its enumeration.</returns>
    private static List<string> UnknownEnumMembers(EmailOptions options)
    {
        var failures = new List<string>();

        Judge(
            Enum.IsDefined(options.PreferredMode),
            EmailOptions.SectionName + ":" + nameof(EmailOptions.PreferredMode),
            options.PreferredMode.ToString(),
            Enum.GetNames<EmailMode>());

        Judge(
            Enum.IsDefined(options.Outbound.Provider),
            OutboundPath + ":" + nameof(EmailOutboundOptions.Provider),
            options.Outbound.Provider.ToString(),
            ImplementedOutboundProviderNames);

        Judge(
            Enum.IsDefined(options.Inbound.Provider),
            InboundPath + ":" + nameof(EmailInboundOptions.Provider),
            options.Inbound.Provider.ToString(),
            Enum.GetNames<EmailInboundProvider>());

        Judge(
            Enum.IsDefined(options.Inbound.VerificationPolicy),
            InboundPath + ":" + nameof(EmailInboundOptions.VerificationPolicy),
            options.Inbound.VerificationPolicy.ToString(),
            Enum.GetNames<EmailVerificationPolicy>());

        return failures;

        void Judge(bool isMember, string path, string stated, string[] admitted)
        {
            if (!isMember)
            {
                failures.Add(
                    $"{path} is '{stated}', which is not a known value. "
                    + $"Allowed values: {string.Join(", ", admitted)}.");
            }
        }
    }

    /// <summary>
    /// Judges the outbound provider by the senders this build ships, one step past membership: the
    /// value is a member of the enumeration, but a reserved one with no implementation behind it.
    /// </summary>
    /// <param name="options">Email adapter settings.</param>
    /// <returns>The refusal message, or <c>null</c> when the provider can deliver mail.</returns>
    private static string? UnsupportedOutboundProvider(EmailOptions options)
    {
        if (Array.IndexOf(ImplementedOutboundProviders, options.Outbound.Provider) >= 0)
        {
            return null;
        }

        return
            $"{OutboundPath}:{nameof(EmailOutboundOptions.Provider)} is "
            + $"'{options.Outbound.Provider}', which this build ships no sender for. "
            + $"Allowed values: {string.Join(", ", ImplementedOutboundProviderNames)} "
            + $"(with {nameof(EmailOutboundProvider.Custom)}, register an IEmailOutboundSender "
            + "in DI beforehand).";
    }

    /// <summary>
    /// Validates the public base URL: absolute URI, HTTPS scheme.
    /// </summary>
    /// <param name="publicBaseUrl">Public base URL.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidatePublicBaseUrl(string publicBaseUrl)
    {
        // Check that the URL is an absolute HTTPS address
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri))
        {
            return ValidateOptionsResult.Fail(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.PublicBaseUrl)} must be a correct absolute URL.");
        }

        // Require HTTPS for secure magic link delivery
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.PublicBaseUrl)} must use HTTPS for secure magic link delivery.");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates the Pull mode settings.
    /// </summary>
    /// <param name="options">Email adapter settings.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidatePullOptions(EmailOptions options)
    {
        // Check that the Outbound section exists (required in the Pull mode)
        if (options.Outbound is null)
        {
            return ValidateOptionsResult.Fail(
                $"The {OutboundPath} section is required when the Email adapter Pull mode is enabled.");
        }

        // Check the required sender address
        if (string.IsNullOrWhiteSpace(options.Outbound.FromAddress))
        {
            return ValidateOptionsResult.Fail(
                $"{OutboundPath}:{nameof(EmailOutboundOptions.FromAddress)} is required when the Email adapter Pull mode is enabled.");
        }

        // Reply-To is optional (SPEC-016 EM-033), but when set it must be a parseable address:
        // fail-fast at startup rather than silently dropping an invalid header at send time.
        if (!string.IsNullOrWhiteSpace(options.Outbound.ReplyToAddress)
            && !MimeKit.MailboxAddress.TryParse(options.Outbound.ReplyToAddress, out _))
        {
            return ValidateOptionsResult.Fail(
                $"{OutboundPath}:{nameof(EmailOutboundOptions.ReplyToAddress)} must be a valid email address when set.");
        }

        // Check that TokenTtl is valid
        if (options.TokenTtl <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.TokenTtl)} must be a positive value.");
        }

        // SMTP: check the required fields
        if (options.Outbound.Provider is EmailOutboundProvider.Smtp)
        {
            var smtpResult = ValidateSmtpOptions(options.Outbound.Smtp);
            if (smtpResult.Failed)
            {
                return smtpResult;
            }
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates the Push mode settings.
    /// </summary>
    /// <param name="options">Email adapter settings.</param>
    /// <param name="isProduction">true — Production environment (Core profile), false — dev (DemoCore profile).</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidatePushOptions(EmailOptions options, bool isProduction)
    {
        // Check that the Inbound section exists (required in the Push mode)
        if (options.Inbound is null)
        {
            return ValidateOptionsResult.Fail(
                $"The {InboundPath} section is required when the Email adapter Push mode is enabled.");
        }

        // SPEC-016 EM-184: SmtpRelay is a reserved name with no receiver behind it. Left to start, the
        // host would register the processor while no transport ever calls it — mail accepted by no
        // path, silently.
        if (options.Inbound.Provider is EmailInboundProvider.SmtpRelay)
        {
            return ValidateOptionsResult.Fail(
                $"{InboundPath}:{nameof(EmailInboundOptions.Provider)} is "
                + $"'{nameof(EmailInboundProvider.SmtpRelay)}', which this build ships no receiver for. "
                + $"Allowed values: {string.Join(", ", ImplementedInboundProviderNames)}.");
        }

        // VerificationPolicy=None is forbidden in the Production environment (EM-030).
        // In the dev environment (DemoCore profile) it is allowed to simplify local debugging.
        if (isProduction && options.Inbound.VerificationPolicy is EmailVerificationPolicy.None)
        {
            return ValidateOptionsResult.Fail(
                $"{InboundPath}:{nameof(EmailInboundOptions.VerificationPolicy)}={nameof(EmailVerificationPolicy.None)} "
                + "is forbidden in the Production environment. "
                + $"Use {nameof(EmailVerificationPolicy.DmarcAlignedPass)} or another verification policy.");
        }

        // InboundAddress is required in Production. Outside it an unset address does not stop the host:
        // the Push sign-in mail then has no recipient, and the channel offers no Push target
        if (isProduction && string.IsNullOrWhiteSpace(options.Inbound.InboundAddress))
        {
            return ValidateOptionsResult.Fail(
                $"{InboundPath}:{nameof(EmailInboundOptions.InboundAddress)} is required when the Email adapter Push mode is enabled.");
        }

        // Plus-addressing puts the correlation token before the domain of InboundAddress, so an address
        // with no domain part silently yields a recipient with no token in it: the reply would reach no
        // transaction to match. Fail at startup instead — a set-but-malformed address is a typo in either
        // environment, unlike an unset one, which does not stop a host outside Production.
        var inboundAddress = options.Inbound.InboundAddress?.Trim();
        if (options.Inbound.UsePlusAddressing && !string.IsNullOrEmpty(inboundAddress))
        {
            var atIndex = inboundAddress.LastIndexOf('@');
            if (atIndex <= 0 || atIndex == inboundAddress.Length - 1)
            {
                return ValidateOptionsResult.Fail(
                    $"{InboundPath}:{nameof(EmailInboundOptions.InboundAddress)} must be a full address with a domain part "
                    + $"when {InboundPath}:{nameof(EmailInboundOptions.UsePlusAddressing)} is enabled "
                    + "(the correlation token goes before the domain).");
            }
        }

        // AllowListOnly requires a non-empty domain list
        if (options.Inbound.VerificationPolicy is EmailVerificationPolicy.AllowListOnly
            && options.Inbound.AllowedDomains.Count is 0)
        {
            return ValidateOptionsResult.Fail(
                $"{InboundPath}:{nameof(EmailInboundOptions.AllowedDomains)} must not be empty when "
                + $"{InboundPath}:{nameof(EmailInboundOptions.VerificationPolicy)}={nameof(EmailVerificationPolicy.AllowListOnly)}.");
        }

        // WebhookSecretToken is required with the Webhook provider in Production
        if (isProduction
            && options.Inbound.Provider is EmailInboundProvider.Webhook
            && string.IsNullOrWhiteSpace(options.Inbound.WebhookSecretToken))
        {
            return ValidateOptionsResult.Fail(
                $"{InboundPath}:{nameof(EmailInboundOptions.WebhookSecretToken)} is required when "
                + $"{InboundPath}:{nameof(EmailInboundOptions.Provider)}={nameof(EmailInboundProvider.Webhook)} (webhook security).");
        }

        // The mailbox section is judged only when the mailbox is what the deployment reads: with any
        // other provider the section is ignored, whatever it holds.
        if (options.Inbound.Provider is EmailInboundProvider.ImapPolling)
        {
            var imapViolations = ImapPollingViolations(options.Inbound);
            if (imapViolations.Count > 0)
            {
                return ValidateOptionsResult.Fail(imapViolations);
            }
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Judges the inbound group of a deployment that reads its mailbox over IMAP: what the
    /// <c>Imap</c> section must supply, and what the rest of the group may not say next to it.
    /// Every violation is reported, not the first one — the section is filled in as a whole.
    /// </summary>
    /// <remarks>
    /// The rule takes the inbound group rather than the whole options because the group is what a
    /// tenant's credentials carry (<see cref="EmailTenantCredentials.Inbound"/>): values resolved
    /// for a tenant at run time never pass through the start-up validation, and the rule stays
    /// applicable to them as it stands. The messages name configuration addresses and the admitted
    /// values only — never the value of a password, a client secret or a refresh token: they end up
    /// in logs.
    /// </remarks>
    /// <param name="inbound">Inbound settings whose provider is the IMAP mailbox.</param>
    /// <returns>One message per violation; empty when the group is acceptable.</returns>
    internal static IReadOnlyList<string> ImapPollingViolations(EmailInboundOptions inbound)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        var providerIsImap =
            $"{InboundPath}:{nameof(EmailInboundOptions.Provider)}={nameof(EmailInboundProvider.ImapPolling)}";
        var failures = new List<string>();

        // The mailbox address is what the user's mail client writes the letter to. A webhook
        // deployment may leave it unset outside Production, with no inbound provider behind it; a
        // deployment that reads a mailbox has one by definition.
        if (string.IsNullOrWhiteSpace(inbound.InboundAddress))
        {
            failures.Add(
                $"{InboundPath}:{nameof(EmailInboundOptions.InboundAddress)} is required when {providerIsImap}.");
        }

        // A mailbox states no verified sender: the value this policy trusts is supplied by an inbound
        // provider calling the webhook, and nothing fills it on this path.
        if (inbound.VerificationPolicy is EmailVerificationPolicy.ProviderVerified)
        {
            failures.Add(
                $"{InboundPath}:{nameof(EmailInboundOptions.VerificationPolicy)}="
                + $"{nameof(EmailVerificationPolicy.ProviderVerified)} cannot be used when {providerIsImap}: "
                + "a mailbox read over IMAP supplies no provider-verified sender. Allowed values: "
                + $"{string.Join(", ", ImapVerificationPolicyNames)}.");
        }

        var imap = inbound.Imap;
        if (imap is null)
        {
            failures.Add($"The {ImapPath} section is required when {providerIsImap}.");
            return failures;
        }

        RequireValue(imap.Host, $"{ImapPath}:{nameof(EmailImapOptions.Host)}", providerIsImap);
        RequireValue(imap.Username, $"{ImapPath}:{nameof(EmailImapOptions.Username)}", providerIsImap);
        RequireValue(imap.Folder, $"{ImapPath}:{nameof(EmailImapOptions.Folder)}", providerIsImap);

        RequireRange(
            imap.Port,
            EmailAdapterConfigDefaults.MinTcpPort,
            EmailAdapterConfigDefaults.MaxTcpPort,
            $"{ImapPath}:{nameof(EmailImapOptions.Port)}");

        RequireRange(
            imap.PollIntervalSeconds,
            EmailAdapterConfigDefaults.MinImapPollIntervalSeconds,
            EmailAdapterConfigDefaults.MaxImapPollIntervalSeconds,
            $"{ImapPath}:{nameof(EmailImapOptions.PollIntervalSeconds)}");

        // SPF, DKIM and DMARC results are read from the header of the mailbox's own server, and the
        // identifier is how that header is told from one the sender wrote. Without it the two
        // policies that judge by those results would refuse every letter.
        if (inbound.VerificationPolicy
                is EmailVerificationPolicy.DmarcAlignedPass or EmailVerificationPolicy.SpfOrDkimPass
            && string.IsNullOrWhiteSpace(imap.AuthservId))
        {
            failures.Add(
                $"{ImapPath}:{nameof(EmailImapOptions.AuthservId)} is required when "
                + $"{InboundPath}:{nameof(EmailInboundOptions.VerificationPolicy)}={inbound.VerificationPolicy} "
                + $"and {providerIsImap}: it names the mail server whose Authentication-Results header is trusted.");
        }

        var authModePath = $"{ImapPath}:{nameof(EmailImapOptions.AuthMode)}";

        switch (imap.AuthMode)
        {
            case EmailImapAuthMode.Password:
                // The OAuth section is not looked at here, whatever it holds.
                RequireValue(
                    imap.Password,
                    $"{ImapPath}:{nameof(EmailImapOptions.Password)}",
                    $"{authModePath}={nameof(EmailImapAuthMode.Password)}");
                break;

            case EmailImapAuthMode.OAuth2:
                // The password is not looked at here, set or not.
                AddOAuthViolations(imap.OAuth, $"{authModePath}={nameof(EmailImapAuthMode.OAuth2)}");
                break;

            default:
                // A raw number outside the enumeration: the binder converts it into a member that
                // does not exist (SPEC-012 §8.2). Judged here rather than with the four axes checked
                // for every deployment, because this axis belongs to a section that is read only
                // when the mailbox is.
                failures.Add(
                    $"{authModePath} is '{imap.AuthMode}', which is not a known value. "
                    + $"Allowed values: {string.Join(", ", Enum.GetNames<EmailImapAuthMode>())}.");
                break;
        }

        return failures;

        void RequireValue(string? value, string path, string condition)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add($"{path} is required when {condition}.");
            }
        }

        void RequireRange(int value, int min, int max, string path)
        {
            if (value < min || value > max)
            {
                failures.Add($"{path} is {value}, which is out of range. Allowed values: {min} to {max}.");
            }
        }

        void AddOAuthViolations(EmailImapOAuthOptions? oauth, string authModeIsOAuth)
        {
            if (oauth is null)
            {
                failures.Add($"The {ImapOAuthPath} section is required when {authModeIsOAuth}.");
                return;
            }

            var tokenEndpointPath = $"{ImapOAuthPath}:{nameof(EmailImapOAuthOptions.TokenEndpoint)}";

            if (string.IsNullOrWhiteSpace(oauth.TokenEndpoint))
            {
                failures.Add($"{tokenEndpointPath} is required when {authModeIsOAuth}.");
            }
            else if (!Uri.TryCreate(oauth.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint))
            {
                failures.Add($"{tokenEndpointPath} must be an absolute {Uri.UriSchemeHttps} URL.");
            }
            else if (!string.Equals(tokenEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                // The request to this address carries the client secret or the refresh token.
                failures.Add(
                    $"{tokenEndpointPath} uses the '{tokenEndpoint.Scheme}' scheme. "
                    + $"Allowed scheme: {Uri.UriSchemeHttps}.");
            }

            RequireValue(
                oauth.ClientId,
                $"{ImapOAuthPath}:{nameof(EmailImapOAuthOptions.ClientId)}",
                authModeIsOAuth);

            // No refresh token means the token is obtained by the credentials of the application
            // (RFC 6749 §4.4), and that grant cannot be made without the secret and the scope. With a
            // refresh token both are optional and sent only when set.
            if (string.IsNullOrWhiteSpace(oauth.RefreshToken))
            {
                var refreshTokenIsEmpty =
                    $"{authModeIsOAuth} and "
                    + $"{ImapOAuthPath}:{nameof(EmailImapOAuthOptions.RefreshToken)} is empty";

                RequireValue(
                    oauth.ClientSecret,
                    $"{ImapOAuthPath}:{nameof(EmailImapOAuthOptions.ClientSecret)}",
                    refreshTokenIsEmpty);
                RequireValue(
                    oauth.Scope,
                    $"{ImapOAuthPath}:{nameof(EmailImapOAuthOptions.Scope)}",
                    refreshTokenIsEmpty);
            }
        }
    }

    /// <summary>
    /// Validates the SMTP settings.
    /// </summary>
    /// <param name="smtp">SMTP settings.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidateSmtpOptions(EmailSmtpOptions? smtp)
    {
        var providerIsSmtp =
            $"{OutboundPath}:{nameof(EmailOutboundOptions.Provider)}={nameof(EmailOutboundProvider.Smtp)}";

        // The SMTP section is required when Provider=Smtp
        if (smtp is null)
        {
            return ValidateOptionsResult.Fail(
                $"The {SmtpPath} section is required when {providerIsSmtp}.");
        }

        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            return ValidateOptionsResult.Fail(
                $"{SmtpPath}:{nameof(EmailSmtpOptions.Host)} is required when {providerIsSmtp}.");
        }

        var credentialsViolation = SmtpCredentialsViolation(smtp);

        return credentialsViolation is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(credentialsViolation);
    }

    /// <summary>
    /// Judges the SMTP credential pair: both set or both empty, and neither made of whitespace only.
    /// </summary>
    /// <remarks>
    /// The rule has one home because it has two callers that must answer it identically: this validator,
    /// which sees the core options at startup, and the SMTP sender, which applies it per send to the
    /// credentials the canonical resolver returned for the current tenant (SPEC-003 §17.4) — those never
    /// pass through the startup validation. The message names configuration addresses only, never the
    /// values: it ends up in logs.
    /// </remarks>
    /// <param name="smtp">SMTP settings.</param>
    /// <returns>The refusal message, or <c>null</c> when the credentials are acceptable.</returns>
    internal static string? SmtpCredentialsViolation(EmailSmtpOptions smtp)
    {
        var usernamePath = $"{SmtpPath}:{nameof(EmailSmtpOptions.Username)}";
        var passwordPath = $"{SmtpPath}:{nameof(EmailSmtpOptions.Password)}";

        // A value made only of spaces is not a credential — but it is not empty either, so both this
        // check and the sender would read it as "set" and send AUTH with it. Rejected before the pair
        // check so the diagnostic names the actual problem instead of an imbalance.
        if (IsWhitespaceOnly(smtp.Username) || IsWhitespaceOnly(smtp.Password))
        {
            return $"{usernamePath} and {passwordPath} must not consist of whitespace: "
                + "such a value counts as set and is sent to the server as a credential. "
                + "Leave both empty for a relay that needs no authentication.";
        }

        // Credentials are all-or-nothing, and both answers come from the options themselves rather than
        // from a condition spelled out here: the sender sends AUTH exactly when RequiresAuthentication()
        // holds, so anything that is neither that nor a deliberate anonymous configuration leaves the
        // connection unauthenticated while a credential sits in the configuration — a value that quietly
        // stops being used. Sharing the predicate with the sender is the point: two spellings of one
        // condition can drift apart.
        if (!smtp.RequiresAuthentication() && !smtp.IsAnonymous())
        {
            return $"{usernamePath} and {passwordPath} must be set together: "
                + "with only one of them set no authentication is attempted at all, so the configured "
                + "value is silently never used. Set both, or leave both empty for an anonymous relay.";
        }

        return null;
    }

    /// <summary>
    /// Tells whether a value is present but carries nothing but whitespace — the shape a quoted
    /// environment variable or an empty <c>.env</c> entry takes, which every emptiness test reads as
    /// a real value.
    /// </summary>
    /// <param name="value">Configured value.</param>
    /// <returns><see langword="true"/> when the value is non-empty and entirely whitespace.</returns>
    private static bool IsWhitespaceOnly(string? value) =>
        !string.IsNullOrEmpty(value) && string.IsNullOrWhiteSpace(value);
}
