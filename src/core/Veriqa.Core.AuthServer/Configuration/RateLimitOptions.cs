// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Rate limiting settings (configuration section Veriqa:RateLimit).
/// SPEC-007 §6.1.
/// </summary>
/// <remarks>
/// Read a limit below as the budget of ONE BUCKET SHARED BY ALL CALLERS unless the member says whose
/// budget it is. The endpoint policies are registered by name and are not partitioned, so their limit
/// counts every request to the route together; the ones with a key of their own — the two confirmation
/// s2s surfaces (per authenticated client) and the per-user authentication limiter — name that key in
/// their own summary. The per-IP limits of SPEC-007 §6.1 are not what these members configure today.
/// </remarks>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:RateLimit";

    /// <summary>
    /// Policy name for the authorize endpoint.
    /// </summary>
    public const string AuthorizePolicyName = "authorize";

    /// <summary>
    /// Policy name for SignalR connections.
    /// </summary>
    public const string SignalRPolicyName = "signalr";

    /// <summary>
    /// Policy name for webhook endpoints.
    /// </summary>
    public const string WebhookPolicyName = "webhook";

    /// <summary>
    /// Policy name for the Token Endpoint (POST /connect/token).
    /// </summary>
    public const string TokenPolicyName = "token";

    /// <summary>
    /// Policy name for the Callback Endpoint (GET /connect/authorize/callback).
    /// </summary>
    public const string CallbackPolicyName = "callback";

    /// <summary>
    /// Policy name for the Polling Endpoint (GET /api/transaction/{id}/status).
    /// SPEC-007 §5.4.
    /// </summary>
    public const string PollingPolicyName = "polling";

    /// <summary>
    /// Policy name for the Email Start Endpoint (POST /auth/email/start).
    /// Limits the magic link send rate to prevent spam.
    /// </summary>
    public const string EmailStartPolicyName = "email-start";

    /// <summary>
    /// Policy name for the server-to-server confirmation creation endpoint
    /// (POST /api/transaction/confirmation). Unlike every policy above, this one partitions by the
    /// AUTHENTICATED CLIENT rather than globally: the callers are relying parties, and one of them
    /// exhausting a shared budget would stop the others (SPEC-039 N33).
    /// </summary>
    public const string ConfirmationCreatePolicyName = "confirmation-create";

    /// <summary>
    /// Policy name for the authenticated result surface of a confirmation transaction
    /// (GET /api/transaction/{id}/result). Partitioned by the AUTHENTICATED CLIENT, the same key as
    /// the creation entry (SPEC-039 N33/N36) — and a budget of its own, so that reading results and
    /// creating transactions cannot exhaust each other.
    /// </summary>
    public const string ConfirmationResultPolicyName = "confirmation-result";

    /// <summary>
    /// Policy name for the BROWSER pages of the confirmation vertical: the entry page
    /// (GET /auth/transaction, SPEC-039 C15) and the page that asks the question together with its two
    /// answers (GET/POST /auth/transaction/confirm, SPEC-039 R37). One policy over both, because they
    /// are one path a user walks and a budget per page would describe neither of them.
    /// <para>
    /// Its own bucket rather than one of the endpoint policies above: those belong to the sign-in path,
    /// and confirmation traffic spending their budget would slow down signing in. Global like them —
    /// these routes are anonymous and browser-facing, so the authenticated client key of
    /// <see cref="ConfirmationCreatePolicyName"/> has nothing to read here.
    /// </para>
    /// </summary>
    public const string ConfirmationPagesPolicyName = "confirmation-pages";

    /// <summary>
    /// Policy name for the hop resolve endpoint (GET {HopPath}/{hop_token}, SPEC-025 HOP-061): the link a
    /// phone opens after scanning the QR code of a channel. A bucket of its own and global, like
    /// <see cref="ConfirmationPagesPolicyName"/>: the request carries nothing but an opaque token, and
    /// the transaction it names is known only once the token is decoded — which is what the limit
    /// protects. A limit by the address of the scanner belongs to the edge of the deployment; a key of
    /// "address + token" is not used, since every guessed token would open a fresh bucket.
    /// </summary>
    public const string HopResolvePolicyName = "hop-resolve";

    /// <summary>
    /// Policy name for the receiver of client signals of the launch page (POST {HopPath}/{hop_token},
    /// SPEC-025 HOP-061, HOP-066b). Its own bucket, apart from <see cref="HopResolvePolicyName"/>: an
    /// interstitial presentation costs up to two signals on top of the page itself, so a shared bucket would
    /// have to be sized by a multiplier that silently stops holding when the redirect style or the set of
    /// signals changes, and an exhausted budget of the receiver refuses signals only, never presentations.
    /// Global, with the same kind of key as the presentation: no address key and no "address + token" key.
    /// </summary>
    public const string HopSignalPolicyName = "hop-signal";

    /// <summary>
    /// Message shown when the request limit is exceeded.
    /// </summary>
    public const string RateLimitExceededMessage = "Too many requests";

    /// <summary>
    /// RFC 6749 §5.2-compliant JSON error body for the Token Endpoint (Content-Type: application/json).
    /// Used in OnRejected when the endpoint-level rate limit fires on /connect/token.
    /// </summary>
    public const string TokenRateLimitErrorJson =
        "{\"error\":\"temporarily_unavailable\",\"error_description\":\"Too many requests. Please retry later.\"}";

    /// <summary>
    /// Maximum authorize requests per period.
    /// Default: 60 — the bucket is one for the whole instance, not one per browser, and every sign-in
    /// spends one of its requests.
    /// </summary>
    public int AuthorizePermitLimit { get; set; } = 60;

    /// <summary>
    /// Window period for authorize requests (seconds).
    /// Default: 60.
    /// </summary>
    public int AuthorizeWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum SignalR connections per period.
    /// Default: 30.
    /// </summary>
    public int SignalRPermitLimit { get; set; } = 30;

    /// <summary>
    /// Window period for SignalR connections (seconds).
    /// Default: 60.
    /// </summary>
    public int SignalRWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum webhook requests per period.
    /// Default: 100.
    /// </summary>
    public int WebhookPermitLimit { get; set; } = 100;

    /// <summary>
    /// Window period for webhook requests (seconds).
    /// Default: 60.
    /// </summary>
    public int WebhookWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum Token Endpoint requests per period.
    /// Default: 20.
    /// </summary>
    public int TokenPermitLimit { get; set; } = 20;

    /// <summary>
    /// Window period for the Token Endpoint (seconds).
    /// Default: 60.
    /// </summary>
    public int TokenWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum Callback Endpoint requests per period.
    /// Default: 60 — the bucket is one for the whole instance, not one per browser, and a single sign-in
    /// spends several of its requests (the callback itself and the actions of its page).
    /// </summary>
    public int CallbackPermitLimit { get; set; } = 60;

    /// <summary>
    /// Window period for the Callback Endpoint (seconds).
    /// Default: 60.
    /// </summary>
    public int CallbackWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum transaction status polling requests per period.
    /// Default: 600 requests per minute (10 per second).
    /// SPEC-007 §5.4.
    /// </summary>
    public int PollingPermitLimit { get; set; } = 600;

    /// <summary>
    /// Window period for polling requests (seconds).
    /// Default: 60.
    /// </summary>
    public int PollingWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum Email Start requests per period.
    /// Limits the magic link send rate (spam protection).
    /// Default: 5.
    /// </summary>
    public int EmailStartPermitLimit { get; set; } = 5;

    /// <summary>
    /// Window period for Email Start requests (seconds).
    /// Default: 60.
    /// </summary>
    public int EmailStartWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum confirmation creation requests per authenticated client per period.
    /// Default: 60.
    /// </summary>
    public int ConfirmationCreatePermitLimit { get; set; } = 60;

    /// <summary>
    /// Window period for confirmation creation requests (seconds).
    /// Default: 60.
    /// </summary>
    public int ConfirmationCreateWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum confirmation result reads per authenticated client per period.
    /// Default: 60.
    /// </summary>
    public int ConfirmationResultPermitLimit { get; set; } = 60;

    /// <summary>
    /// Window period for confirmation result reads (seconds).
    /// Default: 60.
    /// </summary>
    public int ConfirmationResultWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum requests to the browser pages of the confirmation vertical per period.
    /// Default: 30 — one confirmation costs three requests of this budget (the entry page, the question
    /// and the answer), so the shipped value admits ten walks a minute.
    /// </summary>
    public int ConfirmationPagesPermitLimit { get; set; } = 30;

    /// <summary>
    /// Window period for the browser pages of the confirmation vertical (seconds).
    /// Default: 60.
    /// </summary>
    public int ConfirmationPagesWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum requests to the hop resolve endpoint per period.
    /// Default: 30 — the budget of the browser pages of the confirmation vertical
    /// (<see cref="ConfirmationPagesPermitLimit"/>).
    /// </summary>
    public int HopResolvePermitLimit { get; set; } = 30;

    /// <summary>
    /// Window period for the hop resolve endpoint (seconds).
    /// Default: 60.
    /// </summary>
    public int HopResolveWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum requests to the receiver of client signals of the launch page per period.
    /// Default: 60 — two signals per presentation at the shipped budget of the hop resolve endpoint
    /// (<see cref="HopResolvePermitLimit"/>).
    /// </summary>
    public int HopSignalPermitLimit { get; set; } = 60;

    /// <summary>
    /// Window period for the receiver of client signals of the launch page (seconds).
    /// Default: 60.
    /// </summary>
    public int HopSignalWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum authentication attempts per user per period.
    /// Default: 5.
    /// </summary>
    public int UserAuthPermitLimit { get; set; } = 5;

    /// <summary>
    /// Window period for per-user authentication rate limiting (seconds).
    /// Default: 60.
    /// </summary>
    public int UserAuthWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Waiting queue size (0 — reject immediately).
    /// Default: 0.
    /// </summary>
    public int UserAuthQueueLimit { get; set; } = 0;

    /// <summary>
    /// Maximum number of CONCURRENTLY ACTIVE users in the per-user rate limiter — not of all users seen
    /// over the lifetime of the process. A user not seen for a whole limit window (the effective
    /// <see cref="UserAuthWindowSeconds"/>) leaves the registry and frees its place. Protects against
    /// unbounded memory growth with a large number of unique users: only a new user arriving while the
    /// registry is full of users active within the window falls into a shared "overflow" partition.
    /// Default: 50000.
    /// TASK-016, SPEC-007 §6.1.
    /// </summary>
    public int UserAuthMaxPartitions { get; set; } = 50_000;
}
