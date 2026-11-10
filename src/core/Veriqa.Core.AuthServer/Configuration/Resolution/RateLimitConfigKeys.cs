// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// Registry of resolver keys for rate-limit VALUES (CFG-223).
/// Re-leveling concerns only the numeric limit VALUES ("stricter" = a smaller PermitLimit). Every key
/// here declares the core level alone today, so the ceiling completes the CFG-223 contract for a future
/// per-tenant/per-app track rather than describing a live multi-level resolution. The enforcement
/// machinery (algorithm/partitions/policies/rejection, CFG-230) stays in the core and is NOT split
/// across levels.
/// <para>
/// Each key states the core level BY ADDRESS — the member of <see cref="RateLimitOptions.SectionName"/>
/// an operator has always written the limit at — and the level always states a value: what the section
/// holds, or the limit the product ships with. That is what the binding over <c>IOptions</c> did, and
/// it is load-bearing here: a permit limit that resolved to the default of its type would be zero, and
/// a limiter built on it would reject every request.
/// </para>
/// </summary>
public static class RateLimitConfigKeys
{
    /// <summary>
    /// Catalog of the declarations of this registry — what the one registrar of the deployment declares
    /// and binds. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// The rate limits AS THE PRODUCT SHIPS THEM — a default-constructed options object, read for one
    /// thing only: the value a core level yields when the section states nothing. The shipped numbers
    /// are taken off the options class rather than restated here, so the two cannot drift.
    /// </summary>
    private static readonly RateLimitOptions Shipped = new();

    /// <summary>Authorize limit ceiling.</summary>
    public static ConfigKey<int> AuthorizePermitLimit { get; } = PermitCeiling(
        "RateLimit.AuthorizePermitLimit",
        nameof(RateLimitOptions.AuthorizePermitLimit),
        Shipped.AuthorizePermitLimit);

    /// <summary>SignalR limit ceiling.</summary>
    public static ConfigKey<int> SignalRPermitLimit { get; } = PermitCeiling(
        "RateLimit.SignalRPermitLimit",
        nameof(RateLimitOptions.SignalRPermitLimit),
        Shipped.SignalRPermitLimit);

    /// <summary>Webhook limit ceiling.</summary>
    public static ConfigKey<int> WebhookPermitLimit { get; } = PermitCeiling(
        "RateLimit.WebhookPermitLimit",
        nameof(RateLimitOptions.WebhookPermitLimit),
        Shipped.WebhookPermitLimit);

    /// <summary>Token endpoint limit ceiling.</summary>
    public static ConfigKey<int> TokenPermitLimit { get; } = PermitCeiling(
        "RateLimit.TokenPermitLimit",
        nameof(RateLimitOptions.TokenPermitLimit),
        Shipped.TokenPermitLimit);

    /// <summary>Callback endpoint limit ceiling.</summary>
    public static ConfigKey<int> CallbackPermitLimit { get; } = PermitCeiling(
        "RateLimit.CallbackPermitLimit",
        nameof(RateLimitOptions.CallbackPermitLimit),
        Shipped.CallbackPermitLimit);

    /// <summary>Polling endpoint limit ceiling.</summary>
    public static ConfigKey<int> PollingPermitLimit { get; } = PermitCeiling(
        "RateLimit.PollingPermitLimit",
        nameof(RateLimitOptions.PollingPermitLimit),
        Shipped.PollingPermitLimit);

    /// <summary>Email-start endpoint limit ceiling.</summary>
    public static ConfigKey<int> EmailStartPermitLimit { get; } = PermitCeiling(
        "RateLimit.EmailStartPermitLimit",
        nameof(RateLimitOptions.EmailStartPermitLimit),
        Shipped.EmailStartPermitLimit);

    /// <summary>Web provider sign-in routes limit ceiling.</summary>
    public static ConfigKey<int> WebProviderPagesPermitLimit { get; } = PermitCeiling(
        "RateLimit.WebProviderPagesPermitLimit",
        nameof(RateLimitOptions.WebProviderPagesPermitLimit),
        Shipped.WebProviderPagesPermitLimit);

    /// <summary>Email link pages limit ceiling.</summary>
    public static ConfigKey<int> EmailLinkPagesPermitLimit { get; } = PermitCeiling(
        "RateLimit.EmailLinkPagesPermitLimit",
        nameof(RateLimitOptions.EmailLinkPagesPermitLimit),
        Shipped.EmailLinkPagesPermitLimit);

    /// <summary>Authorize window ceiling (seconds).</summary>
    public static ConfigKey<int> AuthorizeWindowSeconds { get; } = WindowCeiling(
        "RateLimit.AuthorizeWindowSeconds",
        nameof(RateLimitOptions.AuthorizeWindowSeconds),
        Shipped.AuthorizeWindowSeconds);

    /// <summary>SignalR window ceiling (seconds).</summary>
    public static ConfigKey<int> SignalRWindowSeconds { get; } = WindowCeiling(
        "RateLimit.SignalRWindowSeconds",
        nameof(RateLimitOptions.SignalRWindowSeconds),
        Shipped.SignalRWindowSeconds);

    /// <summary>Webhook window ceiling (seconds).</summary>
    public static ConfigKey<int> WebhookWindowSeconds { get; } = WindowCeiling(
        "RateLimit.WebhookWindowSeconds",
        nameof(RateLimitOptions.WebhookWindowSeconds),
        Shipped.WebhookWindowSeconds);

    /// <summary>Token endpoint window ceiling (seconds).</summary>
    public static ConfigKey<int> TokenWindowSeconds { get; } = WindowCeiling(
        "RateLimit.TokenWindowSeconds",
        nameof(RateLimitOptions.TokenWindowSeconds),
        Shipped.TokenWindowSeconds);

    /// <summary>Callback endpoint window ceiling (seconds).</summary>
    public static ConfigKey<int> CallbackWindowSeconds { get; } = WindowCeiling(
        "RateLimit.CallbackWindowSeconds",
        nameof(RateLimitOptions.CallbackWindowSeconds),
        Shipped.CallbackWindowSeconds);

    /// <summary>Polling endpoint window ceiling (seconds).</summary>
    public static ConfigKey<int> PollingWindowSeconds { get; } = WindowCeiling(
        "RateLimit.PollingWindowSeconds",
        nameof(RateLimitOptions.PollingWindowSeconds),
        Shipped.PollingWindowSeconds);

    /// <summary>Email-start endpoint window ceiling (seconds).</summary>
    public static ConfigKey<int> EmailStartWindowSeconds { get; } = WindowCeiling(
        "RateLimit.EmailStartWindowSeconds",
        nameof(RateLimitOptions.EmailStartWindowSeconds),
        Shipped.EmailStartWindowSeconds);

    /// <summary>Web provider sign-in routes window ceiling (seconds).</summary>
    public static ConfigKey<int> WebProviderPagesWindowSeconds { get; } = WindowCeiling(
        "RateLimit.WebProviderPagesWindowSeconds",
        nameof(RateLimitOptions.WebProviderPagesWindowSeconds),
        Shipped.WebProviderPagesWindowSeconds);

    /// <summary>Email link pages window ceiling (seconds).</summary>
    public static ConfigKey<int> EmailLinkPagesWindowSeconds { get; } = WindowCeiling(
        "RateLimit.EmailLinkPagesWindowSeconds",
        nameof(RateLimitOptions.EmailLinkPagesWindowSeconds),
        Shipped.EmailLinkPagesWindowSeconds);

    /// <summary>Confirmation creation endpoint limit ceiling (per authenticated client).</summary>
    public static ConfigKey<int> ConfirmationCreatePermitLimit { get; } = PermitCeiling(
        "RateLimit.ConfirmationCreatePermitLimit",
        nameof(RateLimitOptions.ConfirmationCreatePermitLimit),
        Shipped.ConfirmationCreatePermitLimit);

    /// <summary>Confirmation creation endpoint window ceiling (seconds).</summary>
    public static ConfigKey<int> ConfirmationCreateWindowSeconds { get; } = WindowCeiling(
        "RateLimit.ConfirmationCreateWindowSeconds",
        nameof(RateLimitOptions.ConfirmationCreateWindowSeconds),
        Shipped.ConfirmationCreateWindowSeconds);

    /// <summary>Confirmation result surface limit ceiling (per authenticated client).</summary>
    public static ConfigKey<int> ConfirmationResultPermitLimit { get; } = PermitCeiling(
        "RateLimit.ConfirmationResultPermitLimit",
        nameof(RateLimitOptions.ConfirmationResultPermitLimit),
        Shipped.ConfirmationResultPermitLimit);

    /// <summary>Confirmation result surface window ceiling (seconds).</summary>
    public static ConfigKey<int> ConfirmationResultWindowSeconds { get; } = WindowCeiling(
        "RateLimit.ConfirmationResultWindowSeconds",
        nameof(RateLimitOptions.ConfirmationResultWindowSeconds),
        Shipped.ConfirmationResultWindowSeconds);

    /// <summary>Confirmation browser pages limit ceiling.</summary>
    public static ConfigKey<int> ConfirmationPagesPermitLimit { get; } = PermitCeiling(
        "RateLimit.ConfirmationPagesPermitLimit",
        nameof(RateLimitOptions.ConfirmationPagesPermitLimit),
        Shipped.ConfirmationPagesPermitLimit);

    /// <summary>Confirmation browser pages window ceiling (seconds).</summary>
    public static ConfigKey<int> ConfirmationPagesWindowSeconds { get; } = WindowCeiling(
        "RateLimit.ConfirmationPagesWindowSeconds",
        nameof(RateLimitOptions.ConfirmationPagesWindowSeconds),
        Shipped.ConfirmationPagesWindowSeconds);

    /// <summary>Hop resolve endpoint limit ceiling.</summary>
    public static ConfigKey<int> HopResolvePermitLimit { get; } = PermitCeiling(
        "RateLimit.HopResolvePermitLimit",
        nameof(RateLimitOptions.HopResolvePermitLimit),
        Shipped.HopResolvePermitLimit);

    /// <summary>Hop resolve endpoint window ceiling (seconds).</summary>
    public static ConfigKey<int> HopResolveWindowSeconds { get; } = WindowCeiling(
        "RateLimit.HopResolveWindowSeconds",
        nameof(RateLimitOptions.HopResolveWindowSeconds),
        Shipped.HopResolveWindowSeconds);

    /// <summary>Hop signal receiver limit ceiling.</summary>
    public static ConfigKey<int> HopSignalPermitLimit { get; } = PermitCeiling(
        "RateLimit.HopSignalPermitLimit",
        nameof(RateLimitOptions.HopSignalPermitLimit),
        Shipped.HopSignalPermitLimit);

    /// <summary>Hop signal receiver window ceiling (seconds).</summary>
    public static ConfigKey<int> HopSignalWindowSeconds { get; } = WindowCeiling(
        "RateLimit.HopSignalWindowSeconds",
        nameof(RateLimitOptions.HopSignalWindowSeconds),
        Shipped.HopSignalWindowSeconds);

    /// <summary>Per-user authentication limit ceiling.</summary>
    public static ConfigKey<int> UserAuthPermitLimit { get; } = PermitCeiling(
        "RateLimit.UserAuthPermitLimit",
        nameof(RateLimitOptions.UserAuthPermitLimit),
        Shipped.UserAuthPermitLimit);

    /// <summary>Per-user authentication window ceiling (seconds).</summary>
    public static ConfigKey<int> UserAuthWindowSeconds { get; } = WindowCeiling(
        "RateLimit.UserAuthWindowSeconds",
        nameof(RateLimitOptions.UserAuthWindowSeconds),
        Shipped.UserAuthWindowSeconds);

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Declares a protective ceiling key for a permit limit: "stricter" = a smaller value.
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="member">Member of the rate-limit section the value is written at.</param>
    /// <param name="shipped">Limit the product ships with.</param>
    /// <returns>Setting key.</returns>
    private static ConfigKey<int> PermitCeiling(string name, string member, int shipped) =>
        Ceiling(name, member, shipped, static (u, l) => Math.Min(u, l));

    /// <summary>
    /// Declares a protective ceiling key for a window length: "stricter" = a LARGER value.
    /// At a fixed permit count the request frequency is permits/window, so a longer window is the more
    /// protective (stricter) one — hence <see cref="Math.Max(int, int)"/>, the mirror of the permit
    /// ceiling's <see cref="Math.Min(int, int)"/> (assumption D1). In the current startup core-only mode a
    /// single layer is present, so the combinator does not affect runtime; it completes the re-leveling contract
    /// (CFG-223) for a future per-tenant/per-app rate-limit track.
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="member">Member of the rate-limit section the value is written at.</param>
    /// <param name="shipped">Window the product ships with.</param>
    /// <returns>Setting key.</returns>
    private static ConfigKey<int> WindowCeiling(string name, string member, int shipped) =>
        Ceiling(name, member, shipped, static (u, l) => Math.Max(u, l));

    /// <summary>
    /// Declares one rate-limit ceiling key: the core level at the member of the rate-limit section, the
    /// value the product ships with where the section states nothing, and the combinator that picks the
    /// stricter of two levels.
    /// </summary>
    /// <param name="name">Setting name.</param>
    /// <param name="member">Member of the rate-limit section the value is written at.</param>
    /// <param name="shipped">Value the product ships with.</param>
    /// <param name="stricter">Function picking the stricter value.</param>
    /// <returns>Setting key.</returns>
    private static ConfigKey<int> Ceiling(string name, string member, int shipped, Func<int, int, int> stricter) =>
        Declared
            .Of<int>(name)
            .At(ConfigLevel.Core, RateLimitOptions.SectionName + ":" + member)
            .ProtectiveCeiling(stricter)
            .Default(shipped)
            .Declare();
}
