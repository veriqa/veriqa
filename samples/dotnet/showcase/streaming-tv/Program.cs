// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Runnable sample: a streaming service on a television set. It is not one Veriqa mechanism shown on
// its own — it is the five a product actually strings together, inside one scenario:
//
//   1. sign-in            the viewer is redirected to the Veriqa sign-in page and comes back known;
//   2. the person's data  the card on screen is built from the claims their channel gave;
//   3. recognition        a viewer who has been here before lands straight in the catalogue;
//   4. step-up            buying a title is confirmed in the channel, BY THE VIEWER WHO ASKED;
//   5. enrichment         what no channel knows — the date of birth, the favourite genre — Veriqa
//                         asks for itself, inside the sign-in, and remembers.
//
// One process plays three roles at once, as in samples/dotnet/inproc/step-up: the Veriqa issuer
// (inproc), the relying party the viewer signs in to, and the backend that calls the public
// confirmation API over HTTP. Splitting them into separate hosts changes nothing but the addresses.
//
// The interface is built for a television: a ten-foot layout, focus moved with the remote's arrow
// keys, and no interaction that needs a pointer. It is deliberately NOT dressed as Veriqa — the
// sign-in page and the confirmation are the product's own screens, everything around them belongs
// to the fictional service.

using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

using Veriqa.Core.AuthServer.DependencyInjection;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Sample.DotNet.Showcase.StreamingTv;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Role 1 — the Veriqa issuer, embedded in this process.
// ---------------------------------------------------------------------------------------------
builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    authServer.UseInMemoryOpenIddictStore();

    // UseCollectedClaimsStore keeps what Veriqa collected on its form (the Veriqa:ScopesClaims catalog of
    // appsettings.json): a viewer who gave their date of birth once is not asked at the next sign-in.
    // Veriqa ships no in-memory implementation of this port, so the sample brings its own. A host of
    // the shipped image gets the same from configuration — Veriqa:CollectedClaims:Store:Enabled.
    authServer.ConfigureTransactionEngine(te => te
        .UseInMemoryStore()
        .UseCollectedClaimsStore<InMemoryCollectedClaimsStore>());

    // The six channels this sample registers. No sign-in page offers all of them: the page shows the
    // channels of the ui_config record its request names (OnRedirectToIdentityProvider below), and a
    // record can only narrow what the host has — so the host carries every channel either record lists.
    authServer.AddChannelAdapters(adapters =>
    {
        adapters.AddTelegram();
        adapters.AddMax();
        adapters.AddWhatsApp();
        adapters.AddInstagram();
        adapters.AddMessenger();
        adapters.AddEmail();
    });
});

// Channel identities (channel user id, phone, email, display name) are PII, and Veriqa ships no
// implementation of the port that stores them. Without this registration the sign-in still works,
// but nothing is kept between transactions.
builder.Services.AddSingleton<IChannelIdentityRepository, InMemoryChannelIdentityRepository>();

// Running behind an HTTP tunnel (cloudflared) so a television can reach the sample: the connector
// runs locally and forwards over loopback, so the KnownProxies/KnownIPNetworks restrictions are
// cleared — the source is always localhost. A production deployment behind a real proxy names that
// proxy's address ranges instead. UseHttpsRedirection is deliberately never called: the tunnel
// terminates TLS, and redirecting to HTTPS behind it loops forever.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
});

// The page's own files are revalidated on every load (a 304 when unchanged). Without a
// Cache-Control header a browser caches them by heuristic, and a television that keeps yesterday's
// app.js next to today's index.html runs a script against markup it was not written for.
builder.Services.Configure<StaticFileOptions>(options =>
    options.OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
        Microsoft.Net.Http.Headers.CacheControlHeaderValue.NoCacheString);

var sampleOptions = builder.Configuration.GetSection(SampleOptions.SectionName)
    .Get<SampleOptions>() ?? new SampleOptions();
builder.Services.AddSingleton(sampleOptions);

// ---------------------------------------------------------------------------------------------
// Role 2 — the relying party the viewer signs in to. Plain OpenID Connect, pointed at the issuer
// this very process hosts.
// ---------------------------------------------------------------------------------------------
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        // The issuer's base address: the handler discovers the endpoints and signing keys from
        // {Authority}/.well-known/openid-configuration. Behind a tunnel this is the public address —
        // it has to match the issuer the discovery document names, which is why the tunnel run sets
        // Veriqa__OpenIddict__Server__Issuer and StreamingTvSample__Authority to the same value.
        options.Authority = sampleOptions.Authority;
        options.ClientId = sampleOptions.WebClientId;

        // The web client is registered without a secret — a PUBLIC client, from which Veriqa requires
        // PKCE (S256). Leaving ClientSecret unset is deliberate.
        options.UsePkce = true;
        options.ResponseType = OpenIdConnectResponseType.Code;

        // The authorization code comes back in the query of a plain redirect. The handler's default is
        // form_post, which cannot travel by redirect: the issuer then answers with an auto-submitting
        // page, and the browser shows a "Completing sign-in" screen between the sign-in and the app.
        // With code + PKCE the query is enough — the code is single-use and worthless without the
        // verifier this client holds.
        options.ResponseMode = OpenIdConnectResponseMode.Query;

        // The correlation and nonce cookies go out SameSite=Lax instead of the handler's None. Chromium
        // 51-66 — the engine of webOS 4.x televisions — rejects SameSite=None outright, and the return
        // then fails with "Correlation failed". Lax is enough here: with the code in the query the
        // return is a top-level GET to this same site, which carries Lax cookies in every engine.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;

        // Must match one of the client's AllowedRedirectUris on the issuer exactly: the comparison is
        // literal and wildcards are not supported.
        options.CallbackPath = SampleRoutes.CallbackPath;

        // A viewer who presses "Cancel sign-in" on the Veriqa page comes
        // back with error=access_denied. That is an answer, not a fault: with the path set the handler
        // redirects to the interface, which shows an anonymous viewer the sign-in screen again. With no
        // path set the refusal would go to OnRemoteFailure below, like any other failed return.
        options.AccessDeniedPath = "/";

        // Every other failed return - Veriqa answering error=server_error, say, because the channel did
        // not deliver the confirmation - raises OnRemoteFailure; with no handler for it the return ends
        // in an HTTP 500. The viewer is taken back to the interface, whose sign-in screen then says
        // that the sign-in did not go through. The error text is deliberately not shown to the viewer:
        // it is written to the log instead.
        options.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
                .LogWarning(context.Failure, "The sign-in return carried an error.");
            context.HandleResponse();
            context.Response.Redirect(SampleRoutes.SignInFailed);
            return Task.CompletedTask;
        };

        // Keep the claims under their OIDC names. The handler otherwise renames the well-known ones
        // to the long WS-Federation URIs, and the card below — which reads `given_name`, `picture`,
        // `channel_type` and the rest by the names Veriqa issued them under — would find nothing.
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = VeriqaClaimTypes.Name;

        // openid and profile are requested by the handler already; these are the extras, and every
        // one of them must be listed in the client's AllowedScopes on the issuer.
        options.Scope.Add(VeriqaClaimTypes.Email);
        options.Scope.Add(SampleScopes.Phone);
        options.Scope.Add(VeriqaScopes.Channel);
        options.Scope.Add(VeriqaScopes.Avatar);

        // The sample's own scope: with it the favourite genre arrives, and a viewer who has not been
        // asked about it yet is asked on the form of the sign-in. The date of birth needs no line
        // here — the catalog declares it under profile.
        options.Scope.Add(SampleScopes.Preferences);

        // The sign-in names the ui_config record whose channels the page offers; the record is
        // chosen by the language of the browser. Both records are in the client's AllowedUiConfigs —
        // a code outside that list is refused by the issuer.
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.SetParameter(SampleUiConfigs.ParameterName, SignInUiConfig(context.Request));

            return Task.CompletedTask;
        };

        // The avatar travels in the access token only and is read from /connect/userinfo, so without
        // this line a Telegram viewer's picture never reaches the card.
        options.GetClaimsFromUserInfoEndpoint = true;

        // ...and without this one it is read and thrown away: the handler copies from userinfo only
        // the claims its ClaimActions name, and the default set stops at sub, name, given_name,
        // family_name, profile and email.
        options.ClaimActions.MapUniqueJsonKey(VeriqaClaimTypes.Picture, VeriqaClaimTypes.Picture);
        options.ClaimActions.MapUniqueJsonKey(VeriqaClaimTypes.PreferredUsername, VeriqaClaimTypes.PreferredUsername);

        // Tokens are not saved into the cookie: nothing here reads them back, and the access token
        // carries the avatar a second time — a picture of tens of kilobytes, twice, in every request.
    });

// ---------------------------------------------------------------------------------------------
// Role 3 — the backend that asks Veriqa for a confirmation before a purchase is applied.
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton<CatalogStore>();
// The access token belongs to the client, not to a call: held here for the whole process, it
// survives the short-lived client the typed HttpClient hands out per call.
builder.Services.AddSingleton<VeriqaAccessTokenCache>();
builder.Services.AddHttpClient<VeriqaConfirmationClient>(http => http.BaseAddress = new Uri(sampleOptions.Authority));

var app = builder.Build();

// Before UseVeriqaAuthServer: the scheme and host of the original request have to be restored
// before anything builds an absolute address from them.
app.UseForwardedHeaders();

app.UseVeriqaAuthServer();
app.MapVeriqaAuthServer();

// ---------------------------------------------------------------------------------------------
// Pages.
// ---------------------------------------------------------------------------------------------

// One page for every state of the interface; which screen it shows is decided in the browser from
// /api/session. The script is a file of its own rather than inline: Veriqa's security headers set a
// CSP that admits scripts from this origin only.
app.MapGet("/", (IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, SampleRoutes.IndexFileName), SampleRoutes.HtmlContentType));

// Starting the sign-in: an anonymous visitor is challenged, which redirects to the Veriqa sign-in
// page. Someone already signed in is simply sent back to the interface.
app.MapGet(SampleRoutes.SignIn, (ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated is true
        ? Results.Redirect("/")
        : Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }));

// Local sign-out: drops this application's cookie. Veriqa exposes no end-session endpoint, so the
// issuer's own session is not ended by this — signing in again does not ask the channel anew.
app.MapGet(SampleRoutes.SignOut, () =>
    Results.SignOut(
        properties: new AuthenticationProperties { RedirectUri = "/" },
        authenticationSchemes: [CookieAuthenticationDefaults.AuthenticationScheme]));

// ---------------------------------------------------------------------------------------------
// API. Every route but /api/session answers an anonymous caller with 401 rather than a redirect:
// these are read by fetch(), and a sign-in page arriving as the body of an XHR helps no one.
// ---------------------------------------------------------------------------------------------

app.MapGet(SampleRoutes.Session, (ClaimsPrincipal user, CatalogStore catalogue) =>
{
    var profile = ViewerProfile.FromClaims(user, Age(user), PreferredGenre(user, catalogue));
    if (profile is null)
    {
        return Results.Ok(new SessionResponse(false, false, null));
    }

    return Results.Ok(new SessionResponse(true, catalogue.WelcomeSeen(Subject(user)!), profile));
});

app.MapPost(SampleRoutes.WelcomeSeen, (ClaimsPrincipal user, CatalogStore catalogue) =>
{
    var subject = Subject(user);
    if (subject is null)
    {
        return Unauthenticated();
    }

    catalogue.MarkWelcomeSeen(subject);

    return Results.NoContent();
});

app.MapGet(SampleRoutes.Catalog, (ClaimsPrincipal user, CatalogStore catalogue) =>
{
    var subject = Subject(user);

    return subject is null
        ? Unauthenticated()
        : Results.Ok(new CatalogResponse(
            catalogue.CatalogueFor(subject, Age(user), PreferredGenre(user, catalogue))));
});

app.MapPost(SampleRoutes.Purchase, async (
    string titleId,
    ClaimsPrincipal user,
    CatalogStore catalogue,
    VeriqaConfirmationClient veriqa,
    CancellationToken cancellationToken) =>
{
    var subject = Subject(user);
    if (subject is null)
    {
        return Unauthenticated();
    }

    var title = catalogue.Find(titleId);
    if (title is null)
    {
        return Results.NotFound();
    }

    if (catalogue.Owns(subject, titleId))
    {
        return Results.Json(new ErrorResponse(SampleErrors.AlreadyOwned), statusCode: StatusCodes.Status409Conflict);
    }

    // The age limit is decided here, whatever the page drew: a title closed to this viewer is refused
    // before any confirmation is created. The age is read once, at the start — a purchase already
    // waiting for its confirmation is not looked at again.
    if (title.IsRestrictedFor(Age(user)))
    {
        return Results.Json(new ErrorResponse(SampleErrors.AgeRestricted), statusCode: StatusCodes.Status403Forbidden);
    }

    // The step-up itself: the confirmation is addressed to the very person holding this session, by
    // the channel identity Veriqa issued them. A confirmation by anyone else comes back with no
    // match, and the purchase is not applied.
    var expected = ExpectedIdentity(user);
    if (expected is null)
    {
        // Both claims arrive with the `channel` scope, which the web client requests and its
        // AllowedScopes admit. Nothing to address the step-up with means that wiring is broken, not
        // that the viewer did something unusual.
        return Results.Json(
            new ErrorResponse(SampleErrors.ChannelIdentityUnavailable),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    try
    {
        var created = await veriqa.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SampleSlots.Title] = title.Title,
                [SampleSlots.Price] = $"{title.Price} {title.Currency}"
            },
            expected,
            // The confirmation goes to the channel this session came through — the only one where
            // the expected identity can confirm at all. Naming it makes the QR a deep link straight
            // into that channel; left out, with several channels enabled, the QR would open the
            // channel-choice page instead.
            ViewerProfile.Value(user, VeriqaClaimTypes.ChannelType),
            cancellationToken);

        // Which title a confirmed transaction pays for is remembered HERE: the page is never asked,
        // so no browser can buy one title by confirming another.
        catalogue.StartPurchase(created.TransactionId, title.Id, subject);

        return Results.Ok(new PurchaseStartedResponse(
            created.TransactionId,
            created.ChannelEntry.Qr,
            created.ChannelEntry.ChannelType));
    }
    catch (VeriqaCallException refused)
    {
        return Results.Content(refused.Body, SampleRoutes.JsonContentType, statusCode: refused.StatusCode);
    }
});

app.MapGet(SampleRoutes.PurchaseResult, async (
    string transactionId,
    ClaimsPrincipal user,
    CatalogStore catalogue,
    VeriqaConfirmationClient veriqa,
    CancellationToken cancellationToken) =>
{
    var subject = Subject(user);
    if (subject is null)
    {
        return Unauthenticated();
    }

    var purchase = catalogue.FindPurchase(transactionId, subject);
    if (purchase is null)
    {
        return Results.NotFound();
    }

    // A purchase already decided keeps its verdict: polling it again costs Veriqa nothing.
    if (purchase.State is not PurchaseState.Pending)
    {
        return Results.Ok(purchase);
    }

    try
    {
        var result = await veriqa.GetResultAsync(transactionId, cancellationToken);

        if (result.Outcome == CatalogStore.PendingOutcome)
        {
            return Results.Ok(purchase);
        }

        if (result.Outcome != VeriqaConfirmationClient.ConfirmedOutcome)
        {
            return Results.Ok(catalogue.Conclude(transactionId, result.Outcome, PurchaseState.Refused));
        }

        // The decision a step-up exists for: confirmed is not enough, it has to be the expected
        // person. Confirmed by anyone else, the result carries no match and nothing is bought.
        var expectedType = ExpectedIdentityType(user);
        var state = expectedType is not null && result.MatchedType == expectedType
            ? PurchaseState.Purchased
            : PurchaseState.Mismatched;

        return Results.Ok(catalogue.Conclude(transactionId, result.Outcome, state));
    }
    catch (VeriqaCallException refused)
    {
        return Results.Content(refused.Body, SampleRoutes.JsonContentType, statusCode: refused.StatusCode);
    }
});

app.Run();

// The viewer this session belongs to, or null when nobody is signed in.
static string? Subject(ClaimsPrincipal user) => ViewerProfile.Value(user, SampleClaims.Subject);

// The answer an anonymous caller of an API route gets: a status the page can act on, not a redirect
// to a sign-in page it cannot render inside a fetch.
static IResult Unauthenticated() =>
    Results.Json(new ErrorResponse(SampleErrors.Unauthenticated), statusCode: StatusCodes.Status401Unauthorized);

// The viewer's age in full years today, or null when the session carries no usable date of birth.
// "Today" is the UTC day: the sample does not know the viewer's time zone, and a service that does
// would count from the day there.
static int? Age(ClaimsPrincipal user) => ViewerProfile.AgeOn(
    ViewerProfile.Value(user, SampleClaims.Birthdate),
    DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime));

// The viewer's favourite genre, or null when they named none — or named one the catalogue lacks.
static string? PreferredGenre(ClaimsPrincipal user, CatalogStore catalogue) =>
    catalogue.KnownGenre(ViewerProfile.Value(user, SampleClaims.PreferredGenre));

// Who the confirmation expects, as Veriqa reads it: the declared comparable type of the channel this
// session came through, carrying this viewer's identity in that channel.
static Dictionary<string, string>? ExpectedIdentity(ClaimsPrincipal user)
{
    var identityType = ExpectedIdentityType(user);
    var channelUserId = ViewerProfile.Value(user, VeriqaClaimTypes.ChannelUserId);

    return identityType is null || channelUserId is null
        ? null
        : new Dictionary<string, string>(StringComparer.Ordinal) { [identityType] = channelUserId };
}

// The comparable type declared for this session's channel, or null for a channel the sample does not
// offer. The mapping is one table (SampleIdentityTypes), never a literal at the point of use.
static string? ExpectedIdentityType(ClaimsPrincipal user)
{
    var channelType = ViewerProfile.Value(user, VeriqaClaimTypes.ChannelType);

    return channelType is null ? null : SampleIdentityTypes.ForChannel(channelType);
}

// The ui_config record a sign-in started by this request names: the one with MAX when the browser's
// first language is Russian, the other for every other language and for a request that states none.
// "First" is the first entry of Accept-Language as the browser wrote it — the language the person
// put on top — compared by its primary subtag, so `ru` and `ru-RU` are the same answer. A header
// that does not parse reads as an empty list. The choice is presentation, not a restriction: the
// record travels as a request parameter, and anyone may name the other allowed one.
static string SignInUiConfig(HttpRequest request)
{
    var first = request.GetTypedHeaders().AcceptLanguage.FirstOrDefault()?.Value.Value;
    if (first is null)
    {
        return SampleUiConfigs.SignInB;
    }

    var separator = first.IndexOf(SampleUiConfigs.SubtagSeparator);
    var primary = separator < 0 ? first : first[..separator];

    return string.Equals(primary, SampleUiConfigs.SignInALanguage, StringComparison.OrdinalIgnoreCase)
        ? SampleUiConfigs.SignInA
        : SampleUiConfigs.SignInB;
}
