// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Claims;

using Microsoft.Extensions.Options;

using OpenIddict.Validation.AspNetCore;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Server-to-server creation of a confirmation transaction (POST /api/transaction/confirmation,
/// SPEC-039 C14). The relying party authenticates with a Client Credentials token, states what is
/// being confirmed, and receives the public identifier of the created transaction.
/// <para>
/// The order of the checks is normative (SPEC-039 L20) and runs entirely BEFORE anything is written:
/// bearer → attribution from the client → <c>ui_config</c> → <c>action_type</c> present → its contract
/// declared above the core → <c>locale</c> well-formed → <c>time_zone</c> resolvable → caller values →
/// expected identities → creation. A partial accept is not a thing: the first refusal rejects the whole operation.
/// </para>
/// <para>
/// Nothing of the subject matter leaves the server (N31): the answer carries an identifier and a
/// deadline, the failures carry a code and a message naming no value, and the initiator context is
/// neither collected nor accepted here (N32) — there is no end-user browser on this path, and a
/// context taken on the relying party's word would be an open channel into a security prompt.
/// </para>
/// </summary>
public static class ConfirmationTransactionEndpoint
{
    /// <summary>
    /// What a caller is told when no declaration is visible for the stated action type. It names the
    /// observed fact and NOT a conclusion about who is at fault: the reason is unknown to the server
    /// at the moment of the request, and an audit trail must not assert one.
    /// </summary>
    private const string ActionTypeUnknownMessage =
        "No declaration is visible for the stated action_type. An action type is declared in the "
        + MessageTemplatesOptions.GroupName + " of the calling client's entry in "
        + OidcClientsOptions.SectionName + ":" + nameof(OidcClientsOptions.Clients) + "; the host section "
        + MessageTemplatesOptions.SectionName + " is the core level and declares none.";

    /// <summary>
    /// Registers the confirmation creation endpoint.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name (null — no limiting).</param>
    /// <returns>The route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapConfirmationTransactionEndpoint(
        this IEndpointRouteBuilder endpoints,
        string? rateLimitPolicyName = null)
    {
        var builder = endpoints.MapPost(OidcEndpoints.ConfirmationTransaction, HandleCreateAsync)
            .RequireAuthorization(policy =>
                policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                      .RequireAuthenticatedUser());

        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            builder.RequireRateLimiting(rateLimitPolicyName);
        }

        return endpoints;
    }

    /// <summary>
    /// Runs the creation chain of SPEC-039 L20 and answers 201 or a Problem Details failure.
    /// </summary>
    private static async Task<IResult> HandleCreateAsync(
        ConfirmationTransactionRequest? body,
        HttpContext httpContext,
        ClaimsPrincipal user,
        ITransactionService transactionService,
        OidcClientsOptionsAccessor clientsAccessor,
        AuthPageSettingsResolver pageSettingsResolver,
        IMessageTemplateAccessor messageTemplates,
        CallerSlotValidator callerSlotValidator,
        IChannelDisplayService channelDisplayService,
        HopQrContent hopQrContent,
        IQrModuleMatrixSource qrMatrixSource,
        IConfigurationResolver configResolver,
        IIdentityValueProtector identityValueProtector,
        IOptions<OidcServerOptions> serverOptions,
        ILoggerFactory loggerFactory)
    {
        // The method walks the normative order of checks and translates the request into a creation

        var logger = loggerFactory.CreateLogger(typeof(ConfirmationTransactionEndpoint));
        var cancellationToken = httpContext.RequestAborted;

        // Step 1 — the token must represent the CLIENT. Authentication itself was enforced by the
        // route policy (401); a valid USER token reaching here is a different refusal (403): a person
        // who signed in must not act on behalf of the application (E23).
        if (!ClientCredentialsToken.TryGetClientId(user, out var clientId))
        {
            return Results.Forbid(
                authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme]);
        }

        var request = body ?? new ConfirmationTransactionRequest();

        // Step 2 — attribution. Both dimensions come from the authenticated client and nothing else —
        // the request states no "on whose behalf" parameter (R3): the application is the client itself,
        // and the tenant is the one the client belongs to. No ambient tenant scope is open on this HTTP
        // path, so the tenant is resolved here, once, and stored on the transaction below; every later
        // reader takes it from there. A client stating no tenant answers null — the single default
        // tenant of a self-hosted installation.
        var tenantId = await ClientTenantLookup.ResolveAsync(httpContext, clientId, logger);

        // Step 3 — ui_config: the record has to exist in the catalog and be allowed to the application.
        var uiConfig = await UiConfigSelectorResolver.ResolveAsync(
            tenantId,
            clientId,
            request.UiConfig,
            clientsAccessor.Current,
            pageSettingsResolver,
            logger,
            cancellationToken);

        if (uiConfig.Invalid)
        {
            return Failure(
                OidcErrorCodes.UiConfigInvalid,
                UiConfigSelectorResolver.InvalidSelectorMessage);
        }

        // Step 4 — action_type is stated at all.
        var actionType = request.ActionType;
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return Failure(
                TransactionErrorCodes.ActionTypeMissing,
                "The request states no action_type.");
        }

        // The ownership context of every resolution below is assembled ONCE and stated explicitly. The
        // application level has to take part: by C14/R3 it is the application that picks the contract of
        // the message, and the ambient-tenant context the channel paths use carries no application at
        // all. The ui_config selector is deliberately NOT put in: a record chosen by a request parameter
        // must not widen the set of action types or the slot allowlist (C14/R3, TPL-108).
        var ownership = TransactionResolutionContext.ForClient(tenantId, clientId, uiConfigSelector: null);

        // Step 5 — the allowlist. What is asked is the LEVEL that declared the CONTRACT, not whether
        // anything resolved: the product ships core-level messages of its own, and a request naming one
        // of those must not pass. Both ways of "no declaration visible" — nothing declared it, and the
        // configuration source did not answer — give this one answer and this one security event; the
        // entry neither can nor should tell them apart, and the mechanism logs the reason itself.
        var message = await messageTemplates.FindAsync(
            actionType,
            surface: null,
            channel: null,
            ownership,
            cancellationToken);

        if (message is null || message.ContractLevel is null or ConfigLevel.Core)
        {
            return ActionTypeUnknown(logger, clientId, actionType, message?.ContractLevel, httpContext);
        }

        // Step 6 — locale is a syntactically well-formed tag. A well-formed tag naming a language the
        // deployment does not carry is NOT an error: the render falls back as it always does.
        if (request.Locale is not null && !LanguageTagSyntax.IsWellFormed(request.Locale))
        {
            return Failure(
                TransactionErrorCodes.LocaleInvalid,
                "The stated locale is not a well-formed BCP 47 language tag.");
        }

        // Step 6b — the time zone the moments of the message are shown in. Unlike a locale the
        // deployment does not carry, an unresolvable zone has no degradation that keeps the caller's
        // meaning: the moment would be shown in another zone and read as another moment, silently. So
        // it is refused here, in the same place and the same shape as the locale above.
        if (request.TimeZone is not null && !RequestTimeZone.IsResolvable(request.TimeZone))
        {
            return Failure(
                TransactionErrorCodes.TimeZoneInvalid,
                "The stated time zone is not one this deployment can resolve.");
        }

        // The zone of the transaction: what the request states, else the default of the deployment.
        // The default is resolved over the ownership context that INCLUDES the ui_config selector —
        // the very context this entry validated the selector over a few lines above. That is a
        // presentation question and therefore a legitimate place for the record: the exclusion of the
        // record from `ownership` guards what the question SAYS (the contract, the slot allowlist),
        // not the zone its moments are printed in.
        var timeZone = request.TimeZone
            ?? (await configResolver.ResolveAsync(
                AuthServerConfigKeys.DefaultTimeZone,
                TransactionResolutionContext.ForClient(tenantId, clientId, uiConfig.Selector),
                ConfigDimensionValues.None,
                cancellationToken)).Value;

        // The brand the texts may name, over the same context as the zone above: a presentation value.
        var brandName = (await configResolver.ResolveAsync(
                CorePageBrandingConfigKeys.BrandName,
                TransactionResolutionContext.ForClient(tenantId, clientId, uiConfig.Selector),
                ConfigDimensionValues.None,
                cancellationToken)).Value;

        // Step 7 — caller values, by the contract of the message kind. The engine's validator owns
        // this step whole: which slots exist, which are required, their types and the aggregate size.
        var callerValues = request.SlotValues ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Result<ValidatedCallerValues> validation;
        try
        {
            validation = await callerSlotValidator.ValidateAsync(
                actionType, ownership, callerValues, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The configuration was reloaded between the step above and this one, and the contract is
            // no longer resolvable. No declaration is visible — which is the answer already given for
            // that, and no second branch of its own: the entry cannot tell this apart from "never
            // declared" either.
            return ActionTypeUnknown(logger, clientId, actionType, contractLevel: null, httpContext);
        }

        if (validation.IsFailure)
        {
            return Failure(validation.Error.Code, validation.Error.Message);
        }

        // Step 8 — the expectations of the relying party about who confirms (C22/C23). Their place in
        // the order is HERE, between the caller values and the creation: every type has to be declared
        // for this application and every value has to have a canonical form under the rule of its type,
        // and a refusal rejects the whole operation before anything is written.
        IdentityMatchState? identityMatch = null;
        if (request.ExpectedIdentities is { Count: > 0 })
        {
            var declaredTypes = await configResolver.ResolveAsync(
                IdentityMatchConfigKeys.ComparableTypes,
                ownership,
                ConfigDimensionValues.None,
                cancellationToken);

            var accepted = IdentityMatchExpectations.Accept(
                request.ExpectedIdentities,
                declaredTypes.Value ?? [],
                identityValueProtector);

            if (accepted.IsFailure)
            {
                return Failure(accepted.Error.Code, accepted.Error.Message);
            }

            identityMatch = accepted.Value;
        }

        // SPEC-042 R60: the requested channel may be one whose registration belongs to another tenant.
        // For this tenant such a channel does not exist, so the request is refused before anything is
        // written, the way a channel the transaction may not use is. A channel with an owner that is
        // only among the allowed ones is no refusal: its entry is simply not built for this tenant.
        if (request.RequestedChannelType is { } requestedChannelType
            && ChannelRegistrationOwnership.IsOwnedByAnotherTenant(
                httpContext.RequestServices.GetServices<CoreChannelDeclaration>(), requestedChannelType, tenantId))
        {
            return Failure(
                TransactionErrorCodes.ChannelNotAllowed,
                "The requested channel is not available to the tenant of the calling client.");
        }

        // Step 9 — translate into the existing creation. The snapshot is the transport of the caller
        // values, and it carries the NORMALIZED ones the validator produced rather than the raw text.
        var normalized = validation.Value.Values;

        var createRequest = new CreateTransactionRequest
        {
            Type = TransactionTypes.Confirmation,
            ConfirmationSnapshot = new ConfirmationSnapshot
            {
                ActionType = actionType,
                SlotValues = normalized.Count is 0 ? null : normalized
            },
            RequestContext = new TransactionRequestContext
            {
                TenantId = tenantId,
                ApplicationId = clientId,
                // The name the texts show for this application — its DisplayName, as a sign-in shows it.
                ApplicationName = clientsAccessor.Current.ApplicationNameOf(clientId),
                BrandName = brandName,
                UiLocale = request.Locale,
                UiTimeZone = timeZone,
                UiConfigCode = uiConfig.Selector
            },
            RequestedChannelType = request.RequestedChannelType,
            AllowedChannelTypes = request.AllowedChannelTypes,
            TtlSeconds = request.TtlSeconds,
            IdempotencyKey = request.IdempotencyKey,
            // The engine takes the key and the scope as a pair, and the scope is the authenticated
            // client: the same key from two relying parties denotes two different requests, and no
            // party can address another's scope (R12). A request without a key leaves both empty.
            IdempotencyScope = string.IsNullOrEmpty(request.IdempotencyKey) ? null : clientId,
            CorrelationId = request.CorrelationId,
            ClientContext = request.ClientContext,
            // The expectations travel in their own container and NOT in the snapshot: the snapshot is
            // the transport of the prompt's caller values, and these are shown to nobody (C16, C23).
            IdentityMatch = identityMatch
            // No initiator context: it is neither collected nor accepted on this path (N32).
        };

        var result = await transactionService.CreateTransactionAsync(createRequest, cancellationToken);

        if (result.IsFailure)
        {
            // A dependency that did not answer is not the caller's fault and is the one 5xx of this
            // entry; every other refusal is a decision of the product about this very request.
            if (result.Error.Category is TransactionErrorCategory.Infrastructure)
            {
                logger.LogError(
                    "Confirmation transaction creation failed: {ErrorCode} — {ErrorMessage}",
                    result.Error.Code,
                    result.Error.Message);

                return Results.Problem(
                    detail: "Failed to create the transaction. Try again later.",
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: result.Error.Code);
            }

            return Failure(result.Error.Code, result.Error.Message);
        }

        var transaction = result.Value;

        // The way in is built FROM THE TRANSACTION, over ITS ownership context — which is what makes an
        // idempotent repeat answer with a current entry instead of a stored one (R12). The list of
        // channel entries follows the flag of THIS request, never the one of the first attempt (C52).
        var entryResult = await ChannelEntryBuilder.BuildAsync(
            transaction,
            request.IncludeChannelEntries is true,
            httpContext.Request,
            TransactionResolutionContext.For(transaction),
            channelDisplayService,
            hopQrContent,
            pageSettingsResolver,
            qrMatrixSource,
            configResolver,
            serverOptions.Value,
            cancellationToken);

        if (entryResult.IsFailure)
        {
            // The transaction exists and lives to its TTL; there is simply nothing to show right now.
            // A repeat with the same idempotency key answers with it again, entry rebuilt.
            logger.LogError(
                "Failed to prepare the channel entry for transaction {TransactionId}: {Error}",
                transaction.Id.ToString(),
                entryResult.Error.Message);

            return Results.Problem(
                detail: "Failed to prepare authentication channel data",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: OidcErrorCodes.ChannelDisplayFailed);
        }

        // The answer carries a bearer material for the channel entry that follows it, so it is never
        // cached anywhere along the way (N34, C21).
        httpContext.Response.Headers.CacheControl = "no-store";

        var entries = entryResult.Value;

        return Results.Created(
            (string?)null,
            new ConfirmationTransactionResponse
            {
                TransactionId = SessionIdMapper.ToSessionId(transaction.Id),
                ExpiresAt = transaction.ExpiresAt,
                // Today no entry material of ours expires before its transaction does, so the deadline
                // of the answer is the deadline of the transaction — computed as the earliest of all the
                // moments, not assumed equal to one of them.
                ResponseValidUntil = ResponseValidUntil(transaction.ExpiresAt, entries),
                ChannelEntry = entries.Entry,
                ChannelEntries = entries.Entries
            });
    }

    /// <summary>
    /// The deadline of the answer (C21, C52): the earliest of the transaction's own deadline and the
    /// lifetimes of every entry the answer carries.
    /// </summary>
    /// <param name="expiresAt">Deadline of the transaction.</param>
    /// <param name="entries">Entries the answer carries.</param>
    /// <returns>The earliest of the moments.</returns>
    private static DateTimeOffset ResponseValidUntil(DateTimeOffset expiresAt, ChannelEntrySet entries)
    {
        var earliest = Earlier(expiresAt, entries.Entry.ValidUntil);

        if (entries.Entries is not null)
        {
            foreach (var entry in entries.Entries)
            {
                earliest = Earlier(earliest, entry.ValidUntil);
            }
        }

        return earliest;
    }

    /// <summary>
    /// Answers "no declaration visible" and records the security event that goes with it.
    /// </summary>
    /// <param name="logger">Logger of the entry.</param>
    /// <param name="clientId">Authenticated client that made the request.</param>
    /// <param name="actionType">Action type the request stated.</param>
    /// <param name="contractLevel">Level whose contract was visible under that name, when one was — the
    /// core level is the one case worth naming to an operator.</param>
    /// <param name="httpContext">HTTP context — the source of the request identifier.</param>
    /// <returns>The refusal.</returns>
    private static IResult ActionTypeUnknown(
        ILogger logger,
        string clientId,
        string actionType,
        ConfigLevel? contractLevel,
        HttpContext httpContext)
    {
        // The wording states what was OBSERVED and stops there. "The relying party named an undeclared
        // action" would be a conclusion about a cause the server does not know: the declaration may
        // exist and the source may have failed to serve it, and the mechanism logs which it was. What
        // the operator is told on top of that is WHERE the entry looked, since a declaration written at
        // the host section is the likeliest configuration mistake and is invisible to this entry.
        logger.LogWarning(
            "No declaration for action_type {ActionType} was visible at the moment of the request. "
            + "It is looked up in the entry of client {ClientId}: "
            + OidcClientsOptions.SectionName + ":" + nameof(OidcClientsOptions.Clients) + ":<entry>:"
            + MessageTemplatesOptions.GroupName + ":<action_type>:Contract and :Templates. "
            + "A contract visible at the core level only: {DeclaredAtCoreOnly}. RequestId: {RequestId}",
            actionType,
            clientId,
            contractLevel is ConfigLevel.Core,
            httpContext.TraceIdentifier);

        return Failure(TransactionErrorCodes.ActionTypeUnknown, ActionTypeUnknownMessage);
    }

    /// <summary>
    /// The earlier of two moments — the rule behind the deadline of the answer (C21).
    /// </summary>
    /// <param name="first">First moment.</param>
    /// <param name="second">Second moment.</param>
    /// <returns>The earlier of the two.</returns>
    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;

    /// <summary>
    /// Builds a Problem Details refusal of the entry: the code as the title, a message carrying none
    /// of the subject matter as the detail (N31).
    /// </summary>
    /// <param name="code">Error code.</param>
    /// <param name="detail">Message free of any caller-supplied value.</param>
    /// <returns>The 400 answer.</returns>
    private static IResult Failure(string code, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: code);
}
