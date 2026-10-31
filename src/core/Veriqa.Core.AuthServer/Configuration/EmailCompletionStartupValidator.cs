// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.ClaimCompletion;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Startup check of an email the configuration requires with no way to obtain it (SPEC-041 L92, C96): the
/// core level or a client entry raises the email above <c>IfAvailable</c> while no method of the effective
/// list is available on the page of the initiating device — the surface every web step can fall back to.
/// Such a requirement could never be enforced, and it would be lowered to <c>IfAvailable</c> on every
/// sign-in without a word; the start is refused instead, naming the list and why each of its methods is
/// unavailable.
/// <para>
/// The list is the value of <see cref="ClaimCompletionConfigKeys.EmailMethods"/> the resolver gives: in the
/// core context for the core level, and in the context of the <c>client_id</c> of an entry, without a
/// tenant, for a client entry — the application level is read from <c>Veriqa:OpenIddict:Clients</c>. The
/// availability is the one judgement of the runtime (<see cref="EmailCompletionAvailability"/>) with the
/// capabilities of the implicit tenant. The records of tenants are not read at the start: a tenant is
/// checked on each authorization request, where the requirement is lowered.
/// </para>
/// <para>
/// A lifecycle service rather than a validation of an options class: the resolver and the capabilities of
/// the mail infrastructure are asynchronous. <see cref="StartingAsync"/> fires before the web server binds.
/// </para>
/// </summary>
internal sealed class EmailCompletionStartupValidator : IHostedLifecycleService
{
    /// <summary>
    /// Separator of the reasons in the message of a refusal.
    /// </summary>
    private const string ReasonSeparator = "; ";

    /// <summary>
    /// The one reading of the configured claim levels.
    /// </summary>
    private readonly ClaimRequirementSettings _settings;

    /// <summary>
    /// The one judgement of the ways of obtaining the email.
    /// </summary>
    private readonly EmailCompletionAvailability _availability;

    /// <summary>
    /// Client entries of the deployment.
    /// </summary>
    private readonly OidcClientsOptions _clientsOptions;

    /// <summary>
    /// Application configuration of the host — read only to find a list written as an empty array.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Creates the startup check.
    /// </summary>
    /// <param name="settings">Reading of the configured claim levels.</param>
    /// <param name="availability">Judgement of the ways of obtaining the email.</param>
    /// <param name="clientsOptions">Client entries of the deployment.</param>
    /// <param name="configuration">Application configuration of the host.</param>
    public EmailCompletionStartupValidator(
        ClaimRequirementSettings settings,
        EmailCompletionAvailability availability,
        IOptions<OidcClientsOptions> clientsOptions,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(clientsOptions);

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _availability = availability ?? throw new ArgumentNullException(nameof(availability));
        _clientsOptions = clientsOptions.Value;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Refuses the start when an empty list of methods is written, or when the email is required where no
    /// method of its list is available on the page of the initiating device.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">The configuration breaks one of the two rules.</exception>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // The method refuses a written empty list first, then judges the core level, and only when the core
        // level is fine every client entry: a client list is a narrowing of the core one, so a core level
        // with no available method leaves none to any client either, and one refusal names its one source.

        EnsureNoEmptyListIsWritten();

        var capabilities = await _availability.GetCapabilitiesAsync(tenantId: null, cancellationToken);

        if (await UnavailableListAsync(ResolutionContext.Core, capabilities, cancellationToken) is { } coreList)
        {
            throw new InvalidOperationException(Message(
                ScopesClaimsOptions.SectionName,
                ClaimCompletionConfigKeys.CoreEmailMethodsAddress,
                coreList,
                capabilities));
        }

        var refusals = new List<string>();

        foreach (var client in _clientsOptions.Clients)
        {
            if (string.IsNullOrEmpty(client.ClientId))
            {
                continue;
            }

            var context = ResolutionContext.Of(tenantId: null, client.ClientId, uiConfigSelector: null);

            if (await UnavailableListAsync(context, capabilities, cancellationToken) is { } clientList)
            {
                refusals.Add(Message(
                    $"the client '{client.ClientId}'",
                    $"{ClaimCompletionConfigKeys.CoreEmailMethodsAddress} narrowed by "
                    + $"'{ClaimCompletionConfigKeys.EmailMethodsRecordMember}' of its entry in {ClientsAddress}",
                    clientList,
                    capabilities));
            }
        }

        if (refusals.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, refusals));
        }
    }

    /// <summary>
    /// No-op: the check runs in <see cref="StartingAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check runs in <see cref="StartingAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Section of the client entries, as an operator writes it.
    /// </summary>
    private static string ClientsAddress =>
        $"{OidcClientsOptions.SectionName}{ConfigNode.PathSeparator}{nameof(OidcClientsOptions.Clients)}";

    /// <summary>
    /// Refuses a list of methods written as an empty array in the global section or in a client entry.
    /// </summary>
    /// <remarks>
    /// The configuration of the platform keeps an empty JSON array as a key WITHOUT a value, which a read
    /// of the address takes for "states nothing": the level below — the shipped list — would answer in its
    /// place, and the domain of the key, which refuses the empty list, would never be asked. The key is
    /// therefore looked for among the children of the record it is written in.
    /// </remarks>
    /// <exception cref="InvalidOperationException">An empty list is written.</exception>
    private void EnsureNoEmptyListIsWritten()
    {
        var address = ClaimCompletionConfigKeys.CoreEmailMethodsAddress;
        var separator = address.LastIndexOf(ConfigNode.PathSeparator);

        var written = new List<string>();

        if (StatesEmptyList(_configuration.GetSection(address[..separator]), address[(separator + 1)..]))
        {
            written.Add(address);
        }

        foreach (var client in _configuration.GetSection(ClientsAddress).GetChildren())
        {
            if (StatesEmptyList(client, ClaimCompletionConfigKeys.EmailMethodsRecordMember))
            {
                written.Add(
                    $"{client.Path}{ConfigNode.PathSeparator}{ClaimCompletionConfigKeys.EmailMethodsRecordMember}");
            }
        }

        if (written.Count > 0)
        {
            throw new InvalidOperationException(
                $"The list of the email completion methods is empty at {string.Join(", ", written)}: a list "
                + "names at least one of Push, Code, MagicLink and Unverified. To stop requiring the email, "
                + "lower its requirement level (RequireEmail, the Level of its catalog row, ClaimLevels:email) "
                + "instead of emptying the list.");
        }
    }

    /// <summary>
    /// Whether a record states a member that holds neither a value nor children — an empty array.
    /// </summary>
    /// <param name="record">Section of the record.</param>
    /// <param name="member">Name of the member.</param>
    /// <returns><c>true</c> when the member is written empty.</returns>
    private static bool StatesEmptyList(IConfigurationSection record, string member) =>
        record.GetChildren().Any(child =>
            string.Equals(child.Key, member, StringComparison.OrdinalIgnoreCase)
            && child.Value is null
            && !child.GetChildren().Any());

    /// <summary>
    /// The effective list of an ownership context when it requires the email above <c>IfAvailable</c> and
    /// no method of the list is available on the page of the initiating device.
    /// </summary>
    /// <param name="context">Ownership context.</param>
    /// <param name="capabilities">Capabilities of the mail infrastructure of the implicit tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The list that leaves the requirement unenforceable; null — nothing to refuse.</returns>
    private async Task<IReadOnlyList<EmailCompletionMethod>?> UnavailableListAsync(
        ResolutionContext context,
        ClaimEmailCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        using var scope = new ConfigResolutionScope(context);
        var configured = await _settings.ResolveAsync(scope, cancellationToken);

        if (configured.LevelOf(VeriqaClaimTypes.Email) <= ClaimRequirementLevel.IfAvailable)
        {
            return null;
        }

        var list = await _availability.ReadListAsync(scope, cancellationToken);

        return EmailCompletionAvailability.Resolve(list, WebStepSurface.InitiatingPage, capabilities).Count > 0
            ? null
            : list;
    }

    /// <summary>
    /// Words the refusal for one source of the requirement: the list and why each of its methods is
    /// unavailable on the page of the initiating device.
    /// </summary>
    /// <param name="source">What requires the email.</param>
    /// <param name="listAddress">Where the list of methods is written.</param>
    /// <param name="list">Effective list of methods.</param>
    /// <param name="capabilities">Capabilities of the mail infrastructure of the implicit tenant.</param>
    /// <returns>The message.</returns>
    private static string Message(
        string source,
        string listAddress,
        IReadOnlyList<EmailCompletionMethod> list,
        ClaimEmailCapabilities capabilities)
    {
        var reasons = list.Count == 0
            ? "the list is empty: the client entry names no method the list above it allows"
            : string.Join(
                ReasonSeparator,
                list.Select(method => $"{method} — {WhyUnavailable(method, capabilities)}"));

        return $"The email is required by {source} (above {nameof(ClaimRequirementLevel.IfAvailable)}), but no "
            + $"email completion method of {listAddress} is available on the sign-in page: {reasons}. Add a "
            + $"method that is available there ({nameof(EmailCompletionMethod.Unverified)} always is), make "
            + "the mail of the Email adapter able to send, or lower the requirement of the email.";
    }

    /// <summary>
    /// Why one method is unavailable on the page of the initiating device.
    /// </summary>
    /// <param name="method">Method of the list.</param>
    /// <param name="capabilities">Capabilities of the mail infrastructure of the implicit tenant.</param>
    /// <returns>The reason.</returns>
    private static string WhyUnavailable(EmailCompletionMethod method, ClaimEmailCapabilities capabilities) =>
        method switch
        {
            EmailCompletionMethod.Push =>
                $"available only on the {nameof(WebStepSurface.CrossDevicePage)}, the page of the device that "
                + "confirmed the sign-in",
            EmailCompletionMethod.Code or EmailCompletionMethod.MagicLink when !capabilities.CanSend =>
                "no mail can be sent: the Email adapter is not added, or its PullEnabled is off, or its "
                + "credentials do not resolve, or its sender does not support the completion mail",
            _ => "available"
        };
}
