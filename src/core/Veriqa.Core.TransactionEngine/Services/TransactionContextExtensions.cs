// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// The single way to read the request context of a transaction — the tenant and application
/// attribution, the language of the request and the <c>ui_config</c> code (SPEC-039 C19). A
/// transaction started by the
/// OIDC flow carries them on <see cref="OidcContext"/>, a transaction created server-to-server carries
/// them on <see cref="TransactionRequestContext"/>, and a reader must not care which of the two it got.
/// <para>
/// <b>Precedence of the sources</b> is the same for every accessor here: the request-context container
/// first, then the OIDC context, then <c>null</c>. The two are never populated together — their writers
/// are different — so the order matters only as an explicit, single statement of the rule.
/// </para>
/// </summary>
public static class TransactionContextExtensions
{
    /// <summary>
    /// Tenant attribution of the transaction. It is a convenience for resolving tenant-level
    /// configuration keys, not an access boundary: isolation is provided by the application
    /// (<c>client_id</c>), which sits above the tenant.
    /// </summary>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <returns>The tenant identifier, or null when no context states one (the default implicit
    /// tenant of a self-hosted installation).</returns>
    public static string? GetTenantId(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.RequestContext?.TenantId ?? transaction.OidcContext?.TenantId;
    }

    /// <summary>
    /// Whether the tenant of the transaction is unknown because its resolution failed at creation
    /// (<see cref="OidcContext.TenantResolutionFailed"/>), rather than stated as none. The collected claims
    /// store is keyed by the tenant, so such a transaction neither reads nor writes it (SPEC-041 E62).
    /// </summary>
    /// <remarks>
    /// Only the OIDC context can carry the failure: a server-to-server transaction states its tenant in the
    /// request context, which nobody resolves, and the two containers are never populated together.
    /// </remarks>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <returns>true — the tenant resolution failed.</returns>
    internal static bool HasFailedTenantResolution(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.OidcContext is { TenantResolutionFailed: true };
    }

    /// <summary>
    /// Application attribution of the transaction (the calling client's identifier).
    /// </summary>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <returns>The application identifier, or null when no context states one.</returns>
    public static string? GetApplicationId(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.RequestContext?.ApplicationId ?? transaction.OidcContext?.ClientId;
    }

    /// <summary>
    /// Display name of the application of the transaction, taken at creation from the entry of the
    /// client — the same for a sign-in and a confirmation.
    /// </summary>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <returns>The name, or null when the transaction was created without one.</returns>
    public static string? GetApplicationName(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.RequestContext?.ApplicationName ?? transaction.OidcContext?.ApplicationName;
    }

    /// <summary>
    /// Brand name the levels of the transaction stated at its creation, as configured.
    /// </summary>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <returns>The brand name, or null when no level stated one.</returns>
    public static string? GetBrandName(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.RequestContext?.BrandName ?? transaction.OidcContext?.BrandName;
    }

    /// <summary>
    /// Language of the request that created the transaction (IETF tag). It is the fallback step of the
    /// recipient-locale chain, not an override of a locale the channel itself states.
    /// </summary>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <returns>The locale tag, or null when no context states one.</returns>
    public static string? GetUiLocale(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.RequestContext?.UiLocale ?? transaction.OidcContext?.UiLocale;
    }

    /// <summary>
    /// Time zone the moments shown for this transaction are converted to (IANA identifier). It is
    /// what the relying party stated for the request, or the default of the deployment; a
    /// transaction that states none has its moments shown in UTC with the marker that says so.
    /// </summary>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <returns>The time zone identifier, or null when no context states one.</returns>
    public static string? GetUiTimeZone(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.RequestContext?.UiTimeZone ?? transaction.OidcContext?.UiTimeZone;
    }

    /// <summary>
    /// <c>ui_config</c> record code selected for the transaction (SPEC-002 section 4.6).
    /// </summary>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <returns>The record code, or null when no context states one.</returns>
    public static string? GetUiConfigCode(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.RequestContext?.UiConfigCode ?? transaction.OidcContext?.UiConfigCode;
    }

    /// <summary>
    /// Builds the configuration resolution context of the transaction.
    /// </summary>
    /// <remarks>
    /// <paramref name="tenantId"/> is an override for a caller that knows the tenant better than the
    /// transaction does — the channel paths, which run inside an ambient tenant scope — and it wins
    /// over what the transaction states. It carries no default on purpose: a caller with no ambient
    /// scope writes <c>null</c> by hand, which is a statement, whereas an omitted argument is a
    /// dimension nobody noticed was missing.
    /// <para>
    /// The <c>ui_config</c> selector is not a parameter: it is read through
    /// <see cref="GetUiConfigCode"/>, so both containers answer for it exactly as they do for the
    /// tenant and the application. Taking it from one container alone left an OIDC transaction
    /// without the record level it does state.
    /// </para>
    /// <para>
    /// Blank values are normalized to null, so a blank <c>client_id</c> never creates a phantom
    /// application level; a context with no dimension set at all is the shared
    /// <see cref="ResolutionContext.Core"/> instance, which keeps the self-hosted invariant "the
    /// resolver reads the core level alone" (CFG-202).
    /// </para>
    /// </remarks>
    /// <param name="transaction">Transaction whose context is read.</param>
    /// <param name="tenantId">Tenant known from the environment; null — take the one on the transaction.</param>
    /// <returns>The resolution context of the transaction.</returns>
    public static ResolutionContext ToResolutionContext(
        this Transaction transaction,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return ResolutionContext.Of(
            tenantId ?? transaction.GetTenantId(),
            transaction.GetApplicationId(),
            transaction.GetUiConfigCode());
    }
}
