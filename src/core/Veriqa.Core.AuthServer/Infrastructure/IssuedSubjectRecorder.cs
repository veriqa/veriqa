// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Claims;

using OpenIddict.Abstractions;

using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Infrastructure;

/// <summary>
/// Records the subject an application receives in the collected claims store when it is not the core
/// subject the records are keyed by (SPEC-041 C37) — a substituted claims mapper or identity resolution
/// handed out another one — so the key is found by the subject the application knows
/// (<see cref="ICollectedClaimsStore.FindSubjectsAsync"/>).
/// </summary>
/// <remarks>
/// Every surface that hands the subject of a completed sign-in to the application calls this one place,
/// so the conditions of the record are the same wherever the application learned the subject.
/// </remarks>
internal static class IssuedSubjectRecorder
{
    /// <summary>
    /// Records the subject the mapping produced for the application of the transaction.
    /// </summary>
    /// <remarks>
    /// Recorded whether or not the key has claims records. Nothing is recorded without a registered store,
    /// without an application or when the tenant of the transaction is unknown because its resolution
    /// failed. A store that fails is logged and the caller answers the application all the same (E62):
    /// the warning is written to the caller's logger and ends with the caller's own outcome, since what
    /// the application still receives differs by surface.
    /// </remarks>
    /// <param name="httpContext">HTTP context (source of the store and of the clock).</param>
    /// <param name="transaction">Completed sign-in transaction.</param>
    /// <param name="claims">Claims the mapping produced.</param>
    /// <param name="logger">Logger of the calling surface.</param>
    /// <param name="storeFailureOutcome">
    /// Sentence closing the warning about a failed store: what the calling surface does all the same.
    /// </param>
    /// <returns>Task representing the completion of the operation.</returns>
    public static async Task RecordAsync(
        HttpContext httpContext,
        Transaction transaction,
        IReadOnlyList<Claim> claims,
        ILogger logger,
        string storeFailureOutcome)
    {
        var applicationId = transaction.GetApplicationId();
        var issuedSubject = claims
            .FirstOrDefault(claim => string.Equals(claim.Type, OpenIddictConstants.Claims.Subject, StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrEmpty(applicationId)
            || string.IsNullOrEmpty(issuedSubject)
            || transaction.ChannelIdentitySnapshot is not { } snapshot
            || transaction.HasFailedTenantResolution()
            || httpContext.RequestServices.GetService<ICollectedClaimsStore>() is not { } store)
        {
            return;
        }

        var coreSubject = CoreSubject.From(snapshot);
        if (string.Equals(issuedSubject, coreSubject, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await store.SaveIssuedSubjectAsync(
                transaction.GetTenantId(),
                coreSubject,
                applicationId,
                issuedSubject,
                httpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(),
                httpContext.RequestAborted);
        }
        catch (Exception exception)
            when (!(exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested))
        {
            // Only the abort of the request propagates; a cancellation from inside the store (its own timeout)
            // is a store that did not answer.
            logger.LogWarning(
                exception,
                "The subject issued to the application could not be recorded in the collected claims store; {StoreFailureOutcome} TransactionId: {TransactionId}, ClientId: {ClientId}",
                storeFailureOutcome,
                transaction.Id.ToString(),
                applicationId);
        }
    }
}
