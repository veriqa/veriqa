// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.InitiatorContext;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// OIDC authorization endpoint (GET /connect/authorize).
/// Handles the Authorization Request: if the user is authenticated it issues an authorization code,
/// otherwise it creates a transaction and returns a placeholder page.
/// </summary>
public static class AuthorizeEndpoint
{
    /// <summary>
    /// Closing sentence of the warning about a subject that could not be recorded: the authorization
    /// code is issued whether or not the record was made.
    /// </summary>
    private const string IssuedSubjectStoreFailureOutcome = "the code is issued.";

    /// <summary>
    /// Browser nonce length in bytes.
    /// </summary>
    private const int BrowserNonceByteLength = 32;

    /// <summary>
    /// Longest language preference list taken into account (protection against a malformed header) —
    /// the same bound the language detector applies to the value it is given.
    /// </summary>
    private const int MaxLanguagePreferenceLength = 256;

    /// <summary>
    /// Letters of an alphabetic region subtag of a language tag — RFC 5646 §2.2.4 (<c>"en-GB"</c>).
    /// </summary>
    private const int RegionLetterCount = 2;

    /// <summary>
    /// Digits of a numeric region subtag of a language tag — RFC 5646 §2.2.4 (<c>"es-419"</c>).
    /// </summary>
    private const int RegionDigitCount = 3;

    /// <summary>
    /// <c>error_description</c> of the <c>login_required</c> answer to a <c>prompt=none</c> request.
    /// </summary>
    private const string LoginRequiredDescription =
        "The user must sign in: the server keeps no sign-in session to answer prompt=none.";

    /// <summary>
    /// Registers the authorization endpoint with rate limiting (SPEC-007 §6.1).
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <returns>Router for chaining.</returns>
    public static IEndpointRouteBuilder MapAuthorizeEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(OidcEndpoints.Authorize, HandleAuthorizeAsync)
            .RequireRateLimiting(RateLimitOptions.AuthorizePolicyName);
        return endpoints;
    }

    /// <summary>
    /// Handles the authorization request.
    /// </summary>
    private static async Task<IResult> HandleAuthorizeAsync(
        HttpContext httpContext,
        ITransactionService transactionService,
        ITransactionStore transactionStore,
        IClaimsMapper claimsMapper,
        OidcClientsOptionsAccessor clientsAccessor,
        IOptions<VeriqaOptions> veriqaOptions,
        IChannelDisplayService channelDisplayService,
        IAuthPageRenderer authPageRenderer,
        IInitiatorContextCollector initiatorContextCollector,
        IAuthPageLanguageRegistry languageRegistry,
        AuthPageSettingsResolver pageSettingsResolver,
        TimeProvider timeProvider,
        IHostEnvironment environment)
    {
        // The method handles the OIDC authorization request

        var request = httpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            return Results.BadRequest(OidcErrorCodes.OidcRequestInvalid);
        }

        // Check whether the user is authenticated (via the intermediate cookie)
        var cookieResult = await httpContext.AuthenticateAsync(OidcConstants.CookieAuthScheme);
        if (cookieResult.Succeeded && cookieResult.Principal is not null)
        {
            var signedInTransaction = await FindSignedInTransactionAsync(
                httpContext, cookieResult.Principal, transactionStore);

            if (signedInTransaction is not null
                && !string.Equals(signedInTransaction.GetApplicationId(), request.ClientId, StringComparison.Ordinal))
            {
                // The cookie is shared by every authorization request of the browser, but the sign-in it
                // refers to belongs to the client that started it: another client gets neither its code
                // nor its identity. The cookie and the transaction stay in place, so the sign-in of their
                // own client still completes, and this request is treated as an unauthenticated one.
                var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(AuthorizeEndpoint));
                logger.LogWarning(
                    "An authorization request carried the sign-in cookie of a transaction attributed to "
                    + "another application. ClientId: {ClientId}, RequestId: {RequestId}",
                    request.ClientId,
                    httpContext.TraceIdentifier);
            }
            else
            {
                // Remove the intermediate cookie — it is single-use, whether or not it still leads anywhere
                await httpContext.SignOutAsync(OidcConstants.CookieAuthScheme);

                if (signedInTransaction is not null)
                {
                    if (IsRequestOfSignedInTransaction(request, signedInTransaction.OidcContext!))
                    {
                        // The user is already authenticated — issue an authorization code
                        var issued = await IssueAuthorizationCodeAsync(
                            httpContext, signedInTransaction, claimsMapper, transactionService);
                        if (issued is not null)
                        {
                            return issued;
                        }

                        // The sign-in was redeemed by a parallel request carrying the same cookie, or its
                        // transaction left the store in between: no code for this one, and it goes on as
                        // an unauthenticated request.
                    }
                    else
                    {
                        // SPEC-002 §4.2: the cookie was set for one authorization request of its client,
                        // and this is another one of the same client — its code would reach a request the
                        // user never signed in for. The cookie is gone (removed above), the transaction is
                        // left to its TTL, and this request is treated as an unauthenticated one. The
                        // parameter values stay out of the log.
                        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger(typeof(AuthorizeEndpoint));
                        logger.LogWarning(
                            "An authorization request carried the sign-in cookie of a transaction started by "
                            + "another authorization request of the same application. "
                            + "ClientId: {ClientId}, RequestId: {RequestId}",
                            request.ClientId,
                            httpContext.TraceIdentifier);
                    }
                }

                // The cookie leads to no sign-in that can still issue a code — its transaction was reaped,
                // has already issued its code, or is stored by another instance without a shared store:
                // the request is treated as an unauthenticated one.
            }
        }

        // SPEC-002 §4.2: Veriqa keeps no sign-in session — the intermediate cookie is single-use and only
        // carries the return leg of a sign-in that has just completed — so a request that must not show
        // any UI (prompt=none, OIDC Core §3.1.2.1) and got no code above can only be answered
        // login_required, before a transaction or a page exists. OpenIddict itself rejects "none"
        // combined with other prompt values.
        if (request.HasPromptValue(OpenIddictConstants.PromptValues.None))
        {
            return LoginRequiredError();
        }

        // The user is not authenticated — create a transaction.
        // The clients snapshot comes from the guarded accessor, not from IOptions<OidcClientsOptions>:
        // IOptions binds once, at its first read (the client seeder makes it at host start), and never
        // re-binds, so reading it here would resolve the
        // client's DefaultUiConfig from start-time configuration while the ui_config level checked a few
        // lines below reads the live one — the same request would answer from two different generations
        // of the same section. The accessor serves that live value and falls back to the last valid
        // snapshot when the section is broken, instead of surfacing the failed read.
        return await CreateTransactionAndRespondAsync(
            httpContext, request, transactionService, clientsAccessor.Current,
            channelDisplayService, authPageRenderer, initiatorContextCollector,
            pageSettingsResolver, veriqaOptions.Value.Localization.DefaultLanguage,
            languageRegistry.SupportedLanguages, timeProvider, environment);
    }

    /// <summary>
    /// Checks that the authorization request is the one the signed-in transaction was started for: its
    /// <c>redirect_uri</c>, <c>state</c>, <c>nonce</c> and <c>code_challenge</c> match the OIDC context of
    /// the transaction (ordinal comparison). The callback rebuilds the request from that context and
    /// leaves out the parameters it holds as null, so an absent parameter and an empty one are equal.
    /// </summary>
    /// <param name="request">The current authorization request.</param>
    /// <param name="oidcContext">OIDC context of the signed-in transaction.</param>
    /// <returns>True when all four parameters match.</returns>
    private static bool IsRequestOfSignedInTransaction(OpenIddictRequest request, OidcContext oidcContext)
    {
        return IsSameParameter(request.RedirectUri, oidcContext.RedirectUri)
            && IsSameParameter(request.State, oidcContext.State)
            && IsSameParameter(request.Nonce, oidcContext.Nonce)
            && IsSameParameter(request.CodeChallenge, oidcContext.CodeChallenge);
    }

    /// <summary>
    /// Compares a parameter of the request with the value stored on the transaction: ordinal, with an
    /// absent value equal to an empty one.
    /// </summary>
    /// <param name="requested">Value of the current request.</param>
    /// <param name="stored">Value stored on the transaction.</param>
    /// <returns>True when the values are the same.</returns>
    private static bool IsSameParameter(string? requested, string? stored)
    {
        return string.IsNullOrEmpty(requested)
            ? string.IsNullOrEmpty(stored)
            : string.Equals(requested, stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds the transaction the intermediate cookie refers to, provided it can still issue a code.
    /// </summary>
    /// <remarks>
    /// The cookie written by the callback carries the session identifier of the transaction and no
    /// claims (SPEC-002 §4.3). A usable transaction is a completed sign-in with its OIDC context and
    /// both completion snapshots that has not issued its code yet; anything else — no identifier, no
    /// transaction, another state, a code already issued — yields null, and the caller treats the
    /// request as unauthenticated instead of failing it.
    /// <para>
    /// The transaction stays in the store after its code is issued, for as long as any completed one,
    /// so a repeated cookie is turned away here by the mark and not by a missing record. This read
    /// only spares a redeemed cookie the claims mapping: of two simultaneous requests both may pass
    /// it, and the conditional write of the mark decides between them
    /// (<see cref="IssueAuthorizationCodeAsync"/>).
    /// </para>
    /// </remarks>
    /// <param name="httpContext">HTTP context.</param>
    /// <param name="principal">Principal of the intermediate cookie.</param>
    /// <param name="transactionStore">Transaction store.</param>
    /// <returns>The transaction to issue the code for, or null.</returns>
    private static async Task<Transaction?> FindSignedInTransactionAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        ITransactionStore transactionStore)
    {
        var transactionId = SessionIdMapper.ToTransactionId(
            principal.FindFirstValue(OidcConstants.SessionIdParameterName));
        if (transactionId is null)
        {
            return null;
        }

        var transaction = await transactionStore.GetByIdAsync(transactionId.Value, httpContext.RequestAborted);

        return transaction is
        {
            State: TransactionState.Completed,
            OidcContext: not null,
            ResolvedIdentitySnapshot: not null,
            CompletionSnapshot: not null
        }
            && !AuthorizeCallbackEndpoint.HasIssuedAuthorizationCode(transaction)
            ? transaction
            : null;
    }

    /// <summary>
    /// Issues an authorization code for a completed sign-in: builds the claims from the transaction,
    /// marks the transaction as having issued its code and hands the principal to OpenIddict.
    /// </summary>
    /// <remarks>
    /// The transaction is kept: it lives out the retention of any completed transaction, and the mark is
    /// what makes the sign-in redeemable once. The mark is the last step before the principal is handed
    /// over — everything that can refuse (the claims mapping, the scopes of the catalog) runs before it,
    /// so a refusal does not spend the sign-in. A failure of the store while writing the mark is not
    /// caught: the request fails and no code is issued.
    /// </remarks>
    /// <param name="httpContext">HTTP context.</param>
    /// <param name="transaction">Completed sign-in transaction the intermediate cookie referred to.</param>
    /// <param name="claimsMapper">Claims mapper.</param>
    /// <param name="transactionService">Transaction service — writes the mark.</param>
    /// <returns>
    /// SignIn result via OpenIddict, or the claims mapping failure; null — the mark was not won (another
    /// request redeemed the sign-in, or the transaction is no longer stored) and no code is issued.
    /// </returns>
    private static async Task<IResult?> IssueAuthorizationCodeAsync(
        HttpContext httpContext,
        Transaction transaction,
        IClaimsMapper claimsMapper,
        ITransactionService transactionService)
    {
        // The method builds the principal for OpenIddict and issues an authorization code

        // The scopes are those of the stored authorization request — the set the sign-in was started
        // with — so the mapping and the destinations below read the same value.
        var scopeSet = new HashSet<string>(
            (transaction.OidcContext!.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        // The avatar of the channel is asked for here, when the code is issued and only for these scopes;
        // the supplemented identity goes to the mapping and is never stored.
        var resolvedIdentity = await httpContext.RequestServices.GetRequiredService<ChannelAvatarSupplement>()
            .SupplementAsync(
                transaction,
                transaction.ResolvedIdentitySnapshot!,
                transaction.CompletionSnapshot!,
                scopeSet,
                httpContext.RequestAborted);

        // The attribution of the calling relying party is read through the context accessors: a
        // transaction carries it either on the OIDC context or on the request-context container, and
        // reading one of the two directly would make this call site blind to the other.
        var claimsResult = await claimsMapper.MapToClaimsAsync(
            resolvedIdentity,
            transaction.CompletionSnapshot!,
            new ClaimsMappingContext(
                transaction.GetApplicationId(),
                transaction.GetTenantId(),
                scopeSet,
                transaction.GetUiTimeZone()),
            httpContext.RequestAborted);

        if (claimsResult.IsFailure)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AuthorizeEndpoint));

            // transaction.Id.ToString() — a Base62 string (alphabet 0-9, a-z, A-Z), log injection is excluded
            logger.LogError(
                "Claims mapping error. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transaction.Id.ToString(),
                claimsResult.Error.Code);

            return Results.Problem(
                detail: "Failed to complete authentication. Try again later.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: OidcErrorCodes.ClaimsMappingFailed);
        }

        await IssuedSubjectRecorder.RecordAsync(
            httpContext,
            transaction,
            claimsResult.Value,
            httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthorizeEndpoint)),
            IssuedSubjectStoreFailureOutcome);

        var identity = new ClaimsIdentity(
            claimsResult.Value,
            TokenValidationParameters.DefaultAuthenticationType,
            OpenIddictConstants.Claims.Name,
            OpenIddictConstants.Claims.Role);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopeSet);

        // Set destinations for each claim — a substituted mapper is not required to state them. The gate of
        // the catalog applies to its claims as well (SPEC-041 R21).
        var claimScopes = await CatalogClaimScopes.ResolveAsync(
            httpContext.RequestServices.GetRequiredService<IConfigurationResolver>(),
            transaction.GetTenantId(),
            transaction.GetApplicationId(),
            httpContext.RequestAborted);

        foreach (var claim in principal.Claims)
        {
            claim.SetDestinations(ClaimDestinationsHelper.GetDestinations(claim, scopeSet, claimScopes));
        }

        // Hand the sign-in language on to a page of THIS request that is not handed the transaction: the
        // form_post auto-posting page, which the OpenIddict pipeline writes while applying the response
        // below and reaches through the same HTTP context alone (SPEC-007 UI-102).
        var uiLocale = transaction.GetUiLocale();
        if (!string.IsNullOrWhiteSpace(uiLocale))
        {
            httpContext.Items[AuthPageConstants.TransactionUiLocaleItemKey] = uiLocale;
        }

        // Of two simultaneous requests carrying the same cookie exactly one wins the mark; the other is
        // answered as an unauthenticated request. The token is the request's own: the mark is a condition
        // of the issuance, not a cleanup that has to outlive the request.
        if (!await transactionService.TryMarkAuthorizationCodeIssuedAsync(transaction.Id, httpContext.RequestAborted))
        {
            // transaction.Id.ToString() — a Base62 string (alphabet 0-9, a-z, A-Z), log injection is excluded
            httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AuthorizeEndpoint))
                .LogInformation(
                    "The sign-in has already issued its authorization code or is no longer stored; "
                    + "no code is issued for this request. TransactionId: {TransactionId}",
                    transaction.Id.ToString());

            return null;
        }

        return Results.SignIn(
            principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Creates a transaction and returns the authentication page with QR codes and a deep link.
    /// </summary>
    /// <param name="httpContext">HTTP context.</param>
    /// <param name="request">OIDC request.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="clientsSnapshot">OIDC clients snapshot served by the guarded accessor.</param>
    /// <param name="channelDisplayService">Service that prepares channel data.</param>
    /// <param name="authPageRenderer">Authentication page renderer.</param>
    /// <param name="initiatorContextCollector">Initiator context collector (SPEC-017).</param>
    /// <param name="pageSettingsResolver">Resolver of the level-owned sign-in page settings.</param>
    /// <param name="defaultLanguage">Configured default language.</param>
    /// <param name="supportedLanguages">Data-driven registry of supported languages (AuthPageLanguageRegistry).</param>
    /// <param name="timeProvider">Clock the transaction's remaining lifetime is measured against.</param>
    /// <param name="environment">Host environment (decides whether a plain http entry point is admissible).</param>
    /// <returns>Authentication HTML page with QR codes and a deep link.</returns>
    private static async Task<IResult> CreateTransactionAndRespondAsync(
        HttpContext httpContext,
        OpenIddictRequest request,
        ITransactionService transactionService,
        OidcClientsOptions clientsSnapshot,
        IChannelDisplayService channelDisplayService,
        IAuthPageRenderer authPageRenderer,
        IInitiatorContextCollector initiatorContextCollector,
        AuthPageSettingsResolver pageSettingsResolver,
        string defaultLanguage,
        IReadOnlySet<string> supportedLanguages,
        TimeProvider timeProvider,
        IHostEnvironment environment)
    {
        // The method creates a transaction and generates a browser nonce

        var uiConfigLogger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AuthorizeEndpoint));

        // Resolve the tenant of the calling client ONCE, first: every resolution of this request runs
        // in its context, and the same value is stored on the transaction. No ambient tenant scope is
        // open on this HTTP path (only the channel paths — webhook, polling, prompt expiry — open one),
        // so without this answer the page would resolve past the tenant level (SPEC-012 CFG-042), and a
        // reader that meets the transaction later would have no other source. The shipped resolver
        // answers from the client's configuration entry; a client stating no tenant answers null — the
        // single default tenant of a self-hosted installation. A resolver that failed answers null too, and
        // the transaction states the failure: its tenant is unknown rather than none, which keeps the
        // collected claims store off this sign-in (SPEC-041 E62).
        var tenantResolution = await ClientTenantLookup.ResolveWithOutcomeAsync(
            httpContext, request.ClientId, uiConfigLogger);
        var tenantId = tenantResolution.TenantId;

        // Validate the ui_config selector of the request against the ui_config LEVEL of the resolver:
        // the record store has a single reader behind that level, and no other path to it (CFG-231).
        var uiConfigResult = await UiConfigSelectorResolver.ResolveAsync(
            tenantId,
            request.ClientId,
            request.GetParameter(OidcConstants.UiConfigParameterName)?.ToString(),
            clientsSnapshot,
            pageSettingsResolver,
            uiConfigLogger,
            httpContext.RequestAborted);
        if (uiConfigResult.Invalid)
        {
            return UiConfigInvalidError();
        }

        var uiConfigCode = uiConfigResult.Selector;

        // Build the resolution context of the sign-in page ONCE, as soon as all three of its
        // dimensions are known: the tenant of the client resolved above (a resolution layer, not an
        // access boundary — SPEC-003 CA-164), the application (client_id) and the ui_config selector.
        // Everything below — channel data preparation (QR pixel scale, channel hints) and page
        // rendering (QR visibility) — receives it as an explicit argument, so no value travels
        // invisibly and none can leak into the next request on this thread.
        var resolution = TransactionResolutionContext.ForClient(tenantId, request.ClientId, uiConfigCode);

        // Every level-owned setting of the page is resolved here, once, over that context: the design,
        // the page texts, the display override and the transaction lifetime. Below this line there is
        // no record and no global section — only effective values (CFG-235).
        var pageSettings = await pageSettingsResolver.ResolveAsync(resolution, httpContext.RequestAborted);

        // Parse acr_values to filter channels (TASK-005, SPEC-012 CFG-013)
        var acrChannelTypes = ExtractChannelTypesFromAcrValues(request.AcrValues);

        // The requirement set of the transaction is written here, once, before the transaction exists
        // (SPEC-041 C35), and a required phone number narrows the channels of acr_values to those able to
        // provide it (SPEC-041 L48). Both refusals come before creation: nothing is stored for a request
        // that cannot be served.
        var claimRequirements = await httpContext.RequestServices.GetRequiredService<ClaimRequirementsBuilder>()
            .BuildAsync(request, resolution, acrChannelTypes, httpContext.RequestAborted);
        if (claimRequirements.IsFailure)
        {
            return string.Equals(
                claimRequirements.Error.Code, OidcErrorCodes.NoChannelForRequiredPhone, StringComparison.Ordinal)
                ? NoChannelForRequiredPhoneError(claimRequirements.Error.Message)
                : Results.BadRequest(OidcErrorCodes.OidcRequestInvalid);
        }

        var allowedChannelTypes = claimRequirements.Value.AllowedChannelTypes;

        // Generate a browser nonce
        var browserNonceBytes = RandomNumberGenerator.GetBytes(BrowserNonceByteLength);
        var browserNonce = Convert.ToBase64String(browserNonceBytes);

        // Detect the language of the sign-in PAGE. The standard OIDC ui_locales parameter (an explicit
        // language choice made by the user on the RP side) takes priority over Accept-Language.
        // ui_locales is a space-separated list of BCP-47 tags (OIDC Core §3.1.2.1); the shared detector
        // parses comma-separated preference lists, so the separators are normalized before delegating.
        // The value that lands ON THE TRANSACTION is derived from this one — see below.
        var uiLocales = request.UiLocales;
        var languageSource = string.IsNullOrWhiteSpace(uiLocales)
            ? httpContext.Request.Headers.AcceptLanguage.ToString()
            : uiLocales.Replace(' ', ',');
        var language = AuthPageStrings.DetectLanguage(languageSource, defaultLanguage, supportedLanguages);

        // What travels ON THE TRANSACTION is the tag of THAT language as the relying party stated it, with
        // whatever subtags it stated for that language and the region among them when one was asked for:
        // the same language the window is rendered in, and not the first tag of the list. The value
        // answers two questions at once — every later page of this transaction re-clamps it to the
        // supported languages and must land back on the window's language, while a message rendered later
        // reads it as a LOCALE ("en-DE" is English wording under German conventions, and cutting a stated
        // region throws those away — SPEC-036 TPL-016).
        var requestLocaleTag = LocaleTagOfPageLanguage(languageSource, language);

        // The zone the moments of this sign-in are shown in: what the relying party states for the
        // user, else the default of the deployment. A value the platform cannot resolve is IGNORED
        // rather than refused — this is a redirect flow, and an error page instead of a sign-in would
        // be a heavier answer than showing the moments in UTC, exactly as Accept-Language degrades.
        var statedTimeZone = request.GetParameter(OidcConstants.TimeZoneParameterName)?.ToString();
        var uiTimeZone = RequestTimeZone.IsResolvable(statedTimeZone)
            ? statedTimeZone
            : pageSettings.DefaultTimeZone;

        // Build the OidcContext from the request parameters
        var oidcContext = new OidcContext
        {
            ClientId = request.ClientId,
            // The name and the brand the texts of this sign-in show, taken now, as a confirmation takes
            // them: the application slot does not depend on whether an initiator context was collected.
            ApplicationName = clientsSnapshot.ApplicationNameOf(request.ClientId),
            BrandName = pageSettings.Design.BrandName,
            TenantId = tenantId,
            TenantResolutionFailed = tenantResolution.Failed,
            RedirectUri = request.RedirectUri,
            Scope = request.Scope,
            State = request.State,
            Nonce = request.Nonce,
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            ResponseType = request.ResponseType,
            ResponseMode = request.ResponseMode,
            BrowserNonce = browserNonce,
            UiConfigCode = uiConfigCode,
            UiLocale = requestLocaleTag,
            UiTimeZone = uiTimeZone
            // Login confirmation (LoginConfirmationMode) does NOT go through ui_config (security, CFG-049):
            // it is resolved by ownership levels via the shared resolver.
        };

        // Collect the initiator context (SPEC-017 §5.1): once, at the moment the transaction is created.
        // Best-effort: null does not block creation (ICC-011, ICC-033)
        var initiatorContext = initiatorContextCollector.Collect(httpContext, request.ClientId);

        // Create the transaction.
        // TtlSeconds from ui_config: overrides the transaction TTL;
        // null ⇒ value from configuration (self-hosted 1:1).
        var createRequest = new CreateTransactionRequest
        {
            Type = TransactionTypes.Login,
            OidcContext = oidcContext,
            AllowedChannelTypes = allowedChannelTypes,
            ClaimRequirements = claimRequirements.Value.Requirements,
            InitiatorContext = initiatorContext,
            TtlSeconds = pageSettings.TtlSeconds,
            // Trace identifier of the request that started the sign-in, which is what the audit trail
            // means by "end-to-end correlation": ASP.NET Core opens an Activity per request and reads
            // an incoming traceparent itself, so with tracing propagated from the relying party this
            // is the RP's own trace id. Not Activity.Id: that one carries the span id too, and the
            // span changes at every hop while the transaction outlives the request.
            // A host with telemetry switched off has no current Activity and the field stays empty —
            // an identifier of our own invention would correlate with nothing and only imitate a trace.
            CorrelationId = Activity.Current?.TraceId.ToHexString()
        };

        var result = await transactionService.CreateTransactionAsync(createRequest, httpContext.RequestAborted);
        if (result.IsFailure)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AuthorizeEndpoint));
            logger.LogError(
                "Transaction creation error: {ErrorCode} — {ErrorMessage}",
                result.Error.Code,
                result.Error.Message);

            return Results.Problem(
                detail: "Failed to initiate authentication. Try again later.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: OidcErrorCodes.TransactionCreationFailed);
        }

        var transaction = result.Value;
        var sessionId = SessionIdMapper.ToSessionId(transaction.Id);

        // Store the browser nonce in a SameSite=Strict cookie.
        // The cookie lives exactly as long as the transaction it binds: the callback and the cancel
        // route both read it, so a cookie that expired first would turn a live sign-in into a
        // browser_nonce_mismatch. The transaction's expiry is fixed at creation and never extended.
        // The cookie Path includes PathBase: when the issuer is hosted under a sub-path (for example,
        // /demo) the browser matches Path against the full request path — without the prefix the cookie is not sent.
        httpContext.Response.Cookies.Append(
            OidcConstants.BrowserNonceCookieName,
            browserNonce,
            new CookieOptions
            {
                HttpOnly = true,
                // SPEC-002 §12.2: unconditional Secure to protect against interception
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = $"{httpContext.Request.PathBase}{OidcEndpoints.AuthorizeCallback}",
                MaxAge = transaction.ExpiresAt - timeProvider.GetUtcNow()
            });

        // Per-request channel display override (SPEC-002 §4.6, CFG-211): it can only narrow the global
        // display set/order/mode.
        var displayOverride = ChannelDisplayOverride.Of(pageSettings);

        // State the effective external stylesheet and script of this page for its CSP: a
        // CustomCss/JsPath owned by a level above the global section must not be blocked by a policy
        // computed from the global design alone (SPEC-007 UI-054, UI-040). The paths are already the
        // effective ones, and SecurityHeadersMiddleware turns them into origins when it writes the
        // header at the start of the response.
        httpContext.RequestServices.GetRequiredService<CorePageResourceScope>()
            .Declare(pageSettings.Design.CustomCssPath, pageSettings.Design.CustomJsPath);

        // Prepare channel data: deep link + QR codes (SPEC-007 §3, §4)
        var channelDataResult = await channelDisplayService.GetChannelDisplayDataAsync(
            transaction.Id,
            transaction.ExpiresAt,
            transaction.AllowedChannelTypes,
            resolution,
            displayOverride,
            ChannelQrSurface.CorePage,
            httpContext.RequestAborted);

        if (channelDataResult.IsFailure)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AuthorizeEndpoint));
            logger.LogError(
                "Failed to prepare channel data for transaction {TransactionId}: {Error}",
                transaction.Id,
                channelDataResult.Error.Message);

            return Results.Problem(
                detail: "Failed to prepare authentication channel data",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: OidcErrorCodes.ChannelDisplayFailed);
        }

        // The one QR code of the transaction when the unified hop mode applies for this page (SPEC-025
        // HOP-183, HOP-187) — decided and drawn by the function the channel choice page of a
        // confirmation shares; null keeps the per-channel QR codes.
        var unifiedPageQr = httpContext.RequestServices.GetRequiredService<HopUnifiedPageQr>();
        var unifiedQr = await unifiedPageQr.RenderAsync(
            transaction.Id,
            transaction.ExpiresAt,
            channelDataResult.Value,
            resolution,
            pageSettings.QrVisibility,
            httpContext.RequestAborted);

        // The page language was already detected above (ui_locales → Accept-Language); what the
        // transaction carries is the tag of THAT language as stated, with the subtags it was stated with —
        // a region among them only when one was asked for (OidcContext.UiLocale)

        // Render the authentication page (SPEC-007 §7)
        var callbackUrl = $"{OidcEndpoints.AuthorizeCallback}?{OidcConstants.SessionIdParameterName}={Uri.EscapeDataString(sessionId)}";

        // CSP nonce from SecurityHeadersMiddleware (SPEC-007 §6.3)
        var cspNonce = httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string;

        // Where an expired window sends the user to start over. Only this surface has such a target
        // at all — the request that produced the page can simply be made again — so it is built here
        // and never inside the renderer.
        var restartUrl = await ResolveRestartUrlAsync(
            httpContext, pageSettingsResolver, resolution, request.ClientId, environment, uiConfigLogger);

        // The texts of the window the levels of this request override, resolved once over the same context
        // as the rest of the page: the renderer is synchronous and only applies them.
        var texts = await httpContext.RequestServices.GetRequiredService<CorePageTextResolver>()
            .ResolveAsync(resolution, httpContext.RequestAborted);

        // The lines the window shows for its channels — messages resolved over the levels of the transaction
        // and handed to the renderer ready, like the texts.
        var channelLines = await httpContext.RequestServices.GetRequiredService<CorePageChannelLineResolver>()
            .ForWindowAsync(transaction, channelDataResult.Value, texts, language, httpContext.RequestAborted);

        // The "Cancel sign-in" button, where the levels of this request leave it on. Where the refusal goes
        // is the surface's to know — this one returns it to the client — so the form is built here and never
        // inside the renderer.
        var cancelForm = BuildCancelForm(httpContext, pageSettings.CancelEnabled, oidcContext.RedirectUri);

        // The colors the levels state for the buttons of the channels this window shows.
        var channelButtonColors = await httpContext.RequestServices.GetRequiredService<CorePageBrandingResolver>()
            .ResolveChannelButtonColorsAsync(
                resolution,
                [.. channelDataResult.Value.Select(channel => channel.ChannelType)],
                httpContext.RequestAborted);

        var renderContext = new AuthPageRenderContext
        {
            SessionId = sessionId,
            CallbackUrl = callbackUrl,
            ExpiresAt = transaction.ExpiresAt,
            // What the countdown of the page actually runs on: the time LEFT by the server's clock.
            // The moment above stays for the readers that need a moment; comparing it against the
            // browser's clock is what used to end live transactions on a device running fast.
            RemainingSeconds = Math.Max(0, (transaction.ExpiresAt - timeProvider.GetUtcNow()).TotalSeconds),
            RestartUrl = restartUrl,
            CancelForm = cancelForm,
            Channels = channelDataResult.Value,
            ChannelLines = channelLines.Lines,
            FallbackChannelLaunchedLine = channelLines.FallbackLaunched,
            Language = language,
            CspNonce = cspNonce,
            // The design, the title and the instruction arrive ALREADY RESOLVED: a renderer — including
            // an integrator's own — merges no levels and therefore cannot silently lose one.
            Design = pageSettings.Design,
            Title = pageSettings.Title,
            Instruction = pageSettings.Instruction,
            InstructionWithoutQr = pageSettings.InstructionWithoutQr,
            InstructionWithoutButton = pageSettings.InstructionWithoutButton,
            ChannelButtonColors = channelButtonColors,
            Texts = texts,
            // The application path base — used to prefix root-relative page URLs
            // (issuer under an origin sub-path, for example veriqa.app/demo)
            PathBase = httpContext.Request.PathBase.Value ?? string.Empty,
            // The QR visibility arrives already resolved as well (SPEC-007 UI-020), and the visibility
            // of the channel buttons with it — the one reconciled with the QR visibility, not the stated.
            QrVisibility = pageSettings.QrVisibility,
            ChannelButtonVisibility = pageSettings.EffectiveChannelButtonVisibility,
            // The layout of the channels as the levels state it; whether this page has anything to
            // arrange is the renderer's to tell.
            ChannelLayout = pageSettings.ChannelLayout,
            // Both switches of remembering the channel, as the levels state them; which page has a
            // choice to put the channel forward in, or a unified QR code to offer the checkbox under, is
            // the renderer's to tell.
            RememberChannelHint = pageSettings.RememberChannelHint,
            RememberChannelCheckbox = pageSettings.RememberChannelCheckbox,
            UnifiedQrCodeBase64 = unifiedQr?.QrCodeBase64,
            UnifiedQrExpiresAt = unifiedQr?.ExpiresAt,
            UnifiedQrWithChannelQrCodes = unifiedQr is { WithChannelQrCodes: true },
            AnyChannelTabShowsChannelButtons = unifiedQr?.AnyChannelTabShowsChannelButtons ?? true,
            TransactionType = transaction.Type
        };

        var html = authPageRenderer.RenderAuthPage(renderContext);

        // The window is not cached, with the cancel button or without it: it holds the session identifier
        // of the transaction, the QR code and the links into the channels. The directive is the one the
        // anti-forgery tokens of the cancel form already wrote, so Cache-Control reads the same with the button
        // or without it.
        CorePageCaching.Forbid(httpContext.Response);

        return Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8);
    }

    /// <summary>
    /// Builds the form behind the "Cancel sign-in" button of the sign-in window and lets the response of
    /// the window post along the path that button takes.
    /// </summary>
    /// <remarks>
    /// The button posts to the core, and the answer to that post is a redirect to the client's
    /// <c>redirect_uri</c>. A browser applies the <c>form-action</c> directive of the page to the whole
    /// chain of redirects after a form post, so a window carrying the form is the third response that
    /// allows the validated <c>redirect_uri</c> as a form target (SPEC-007 UI-091). The source is stated
    /// only together with the form: a window without the button keeps <c>form-action 'self'</c>.
    /// <para>
    /// A <c>redirect_uri</c> no CSP source can be derived from leaves the window without the button —
    /// the post could not reach the client, and no wider source is ever stated in its place.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">HTTP context — the response the AntiForgery token is issued for.</param>
    /// <param name="enabled">Whether the levels of the request leave the button on.</param>
    /// <param name="redirectUri">The <c>redirect_uri</c> OpenIddict validated for this request.</param>
    /// <returns>The form, or null when the window carries no such button.</returns>
    private static AuthPageCancelForm? BuildCancelForm(HttpContext httpContext, bool enabled, string? redirectUri)
    {
        if (!enabled)
        {
            return null;
        }

        var formActionSource = SecurityHeadersMiddleware.GetFormActionSource(redirectUri);
        if (string.IsNullOrEmpty(formActionSource))
        {
            return null;
        }

        var cancelForm = AuthPageCancelForm.Issue(httpContext, OidcEndpoints.AuthorizeCallbackCancel);
        if (cancelForm is null)
        {
            return null;
        }

        httpContext.Items[SecurityHeaderConstants.FormActionTargetItemKey] = formActionSource;

        return cancelForm;
    }

    /// <summary>
    /// Resolves the address an expired sign-in window offers as the way to start over.
    /// </summary>
    /// <remarks>
    /// Two targets, in this order. FIRST — the client's own <c>initiate_login_uri</c> (OIDC Dynamic
    /// Client Registration 1.0 §2): the entry point the relying party itself publishes for starting a
    /// sign-in, and therefore the one place that can begin a fresh flow with parameters of its own.
    /// SECOND — this very request repeated: the window IS the answer to
    /// <c>GET /connect/authorize</c>, so its own URL creates a new transaction with no new machinery.
    /// The fallback has a known limit, and it is a limit of the relying party's side rather than of
    /// this code: after enough time the RP may refuse the callback by the age of its own
    /// <c>state</c>/<c>nonce</c>/PKCE (the stock ASP.NET handler allows 15 minutes).
    /// <para>
    /// Which stated values are taken is not decided here: the rule is
    /// <see cref="InitiateLoginUriRule"/>, shared with the callback's "session expired" page.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">HTTP context (source of the request's own URL).</param>
    /// <param name="pageSettingsResolver">Resolver of the level-owned sign-in page settings.</param>
    /// <param name="resolution">Ownership context of this request.</param>
    /// <param name="clientId">Client the address is resolved for — named in the log entry.</param>
    /// <param name="environment">Host environment.</param>
    /// <param name="logger">Logger of the endpoint.</param>
    /// <returns>The address to offer; never null on this surface.</returns>
    private static async Task<string> ResolveRestartUrlAsync(
        HttpContext httpContext,
        AuthPageSettingsResolver pageSettingsResolver,
        ResolutionContext resolution,
        string? clientId,
        IHostEnvironment environment,
        ILogger logger)
    {
        var stated = await InitiateLoginUriRule.ResolveAdmissibleAsync(
            pageSettingsResolver, resolution, clientId, environment, logger, httpContext.RequestAborted);

        return stated ?? httpContext.Request.GetEncodedUrl();
    }

    /// <summary>
    /// The locale tag of the language the page was rendered in, as the relying party stated it: a
    /// segment of the preference list whose primary subtag is that language, without its q-weight
    /// (<c>("de-DE,en-GB;q=0.9", "en")</c> → <c>"en-GB"</c>). Among those segments the first one
    /// BEARING A REGION wins; failing that, the first one bearing any other subtag (a script or a
    /// variant); failing that, a bare segment. When the list states that language in no well-formed
    /// segment, the language itself is the answer.
    /// </summary>
    /// <remarks>
    /// The segment of the ALREADY CHOSEN language is what keeps one field able to answer two
    /// questions. Re-clamping the result to the supported languages returns that same language by
    /// construction — every candidate here carries that language as its primary subtag — so every
    /// later page of the transaction speaks the language of the sign-in window instead of drifting to
    /// a fallback; and the region survives, because a message rendered later reads the value as a
    /// locale rather than as a language. The region-bearing segment is preferred over any other
    /// because the region is the only subtag that decides how a moment is written down: a script or a
    /// variant says nothing about the conventions of a date, so <c>"zh-Hant zh-TW"</c> has to travel
    /// as <c>zh-TW</c>. A segment richer than the bare language still wins over the bare one, since
    /// it can only add to what the bare segment already says. The case is left as stated: the
    /// localizer normalizes a tag before looking it up and the culture lookup is case-insensitive, so
    /// normalizing here would only lose what the relying party said. The language itself as the last
    /// answer is deliberate: readers that cannot re-detect a language (the channel messages among
    /// them) would otherwise be left with nothing at all and fall back to the base language while the
    /// window speaks another.
    /// </remarks>
    /// <param name="preferenceList">Comma-separated preference list (ui_locales or Accept-Language).</param>
    /// <param name="language">Language the sign-in page is rendered in.</param>
    /// <returns>The stated tag of that language, or the language itself.</returns>
    private static string LocaleTagOfPageLanguage(string? preferenceList, string language)
    {
        if (string.IsNullOrWhiteSpace(preferenceList))
        {
            return language;
        }

        var source = preferenceList.Length > MaxLanguagePreferenceLength
            ? preferenceList[..MaxLanguagePreferenceLength]
            : preferenceList;

        string? subtaggedMatch = null;
        string? bareMatch = null;

        foreach (var segment in source.Split(','))
        {
            var tag = segment.Split(';')[0].Trim();
            if (!LanguageTagSyntax.IsWellFormed(tag))
            {
                continue;
            }

            var subtagStart = tag.IndexOf('-', StringComparison.Ordinal);
            var primarySubtag = subtagStart < 0 ? tag : tag[..subtagStart];

            if (!primarySubtag.Equals(language, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (subtagStart < 0)
            {
                bareMatch ??= tag;
                continue;
            }

            if (BearsRegion(tag))
            {
                return tag;
            }

            subtaggedMatch ??= tag;
        }

        return subtaggedMatch ?? bareMatch ?? language;
    }

    /// <summary>
    /// Reports whether a well-formed language tag carries a region subtag.
    /// </summary>
    /// <remarks>
    /// A region is exactly two ASCII letters or exactly three digits (RFC 5646 §2.2.4), and it stands
    /// either right after the primary subtag (<c>"zh-TW"</c>) or after the script (<c>"zh-Hant-TW"</c>)
    /// — so the position alone tells nothing and the subtags are read by shape. In a tag of the shape
    /// <see cref="LanguageTagSyntax"/> accepts no other subtag has that shape: a script is four
    /// letters and a variant is five to eight alphanumerics or four starting with a digit.
    /// </remarks>
    /// <param name="tag">Well-formed language tag.</param>
    /// <returns><see langword="true"/> when one of its subtags is a region.</returns>
    private static bool BearsRegion(string tag)
    {
        var subtags = tag.Split('-');

        for (var index = 1; index < subtags.Length; index++)
        {
            var subtag = subtags[index];

            if (subtag.Length == RegionLetterCount
                && char.IsAsciiLetter(subtag[0])
                && char.IsAsciiLetter(subtag[1]))
            {
                return true;
            }

            if (subtag.Length == RegionDigitCount
                && char.IsAsciiDigit(subtag[0])
                && char.IsAsciiDigit(subtag[1])
                && char.IsAsciiDigit(subtag[2]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a 400 ui_config_invalid response (RFC-compliant, code OidcErrorCodes.UiConfigInvalid).
    /// </summary>
    private static IResult UiConfigInvalidError() =>
        Results.Problem(
            detail: UiConfigSelectorResolver.InvalidSelectorMessage,
            statusCode: StatusCodes.Status400BadRequest,
            title: OidcErrorCodes.UiConfigInvalid);

    /// <summary>
    /// Builds the <c>login_required</c> answer to a <c>prompt=none</c> request: a forbid of the server
    /// scheme, which OpenIddict returns to the client's <c>redirect_uri</c> in the response mode of the
    /// request, with its <c>state</c> and <c>iss</c>.
    /// </summary>
    /// <returns>The error answer.</returns>
    private static IResult LoginRequiredError() =>
        Results.Forbid(
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.LoginRequired,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = LoginRequiredDescription
            }),
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    /// <summary>
    /// Builds a 400 no_channel_for_required_phone response (SPEC-041 L48): the phone number is required and
    /// no channel of the request can provide it. The detail names no claim value.
    /// </summary>
    /// <param name="detail">Description of the refusal.</param>
    /// <returns>The problem response.</returns>
    private static IResult NoChannelForRequiredPhoneError(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: OidcErrorCodes.NoChannelForRequiredPhone);

    /// <summary>
    /// Extracts channel types from the acr_values parameter of the OIDC request (TASK-005, SPEC-012 CFG-013).
    /// Supports values in the "channel:{channel_type}" format (for example, "channel:telegram").
    /// Multiple values are separated by a space per the OIDC specification.
    /// </summary>
    /// <param name="acrValues">Space-separated acr_values or null.</param>
    /// <returns>
    /// A list of channel types (for example, ["telegram", "max"]), or null if acr_values contains no channel values.
    /// Null means "all available channels".
    /// </returns>
    private static IReadOnlyList<string>? ExtractChannelTypesFromAcrValues(string? acrValues)
    {
        // The method extracts channel types from acr_values of the form "channel:telegram channel:max"
        if (string.IsNullOrWhiteSpace(acrValues))
        {
            return null;
        }

        // Split by space per the OIDC specification (RFC 6749 §3.3)
        var channelTypes = acrValues
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.StartsWith(OidcConstants.AcrValuesChannelPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(value => value[OidcConstants.AcrValuesChannelPrefix.Length..].ToLowerInvariant())
            .Where(channelType => !string.IsNullOrWhiteSpace(channelType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // If no channel values are found — return null (all channels allowed)
        return channelTypes.Count > 0 ? channelTypes.AsReadOnly() : null;
    }
}
