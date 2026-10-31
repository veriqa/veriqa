// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.TransactionEngine.Store;

/// <summary>
/// Mapper between the Transaction domain model and the TransactionEntity EF Core entity.
/// Also used by the Redis store for serialization/deserialization via TransactionEntity.
/// </summary>
internal static class TransactionEntityMapper
{
    /// <summary>
    /// JSON serialization options for snapshots and collections.
    /// PropertyNameCaseInsensitive is enabled for reliable deserialization.
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Cache of JSON representations of immutable snapshots (ConfirmationSnapshot,
    /// InitiatorContextSnapshot, ChannelIdentitySnapshot, ResolvedIdentitySnapshot)
    /// keyed by instance identity. Snapshot instances are immutable (init-only fields,
    /// frozen collections) and over the transaction lifecycle are replaced wholesale
    /// (a new instance is assigned on Confirm/Complete) rather than mutated in place —
    /// so the JSON of each instance is computed exactly once: the service validates
    /// the size, and the store reuses the same JSON when writing to the entity.
    /// </summary>
    private static readonly ConditionalWeakTable<object, string> ImmutableSnapshotJsonCache = new();

    /// <summary>
    /// Serializes an immutable snapshot to JSON with per-instance memoization.
    /// Apply only to snapshots with immutable instances replaced wholesale
    /// (ConfirmationSnapshot, InitiatorContextSnapshot, ChannelIdentitySnapshot,
    /// ResolvedIdentitySnapshot) — for snapshots mutated in place, a per-instance
    /// cache would yield stale JSON.
    /// </summary>
    /// <param name="snapshot">Immutable snapshot.</param>
    /// <returns>JSON representation of the snapshot (store's SerializerOptions).</returns>
    internal static string SerializeImmutableSnapshot(object snapshot)
    {
        return ImmutableSnapshotJsonCache.GetValue(
            snapshot,
            static value => JsonSerializer.Serialize(value, SerializerOptions));
    }

    /// <summary>
    /// Converts the Transaction domain model into a TransactionEntity EF Core entity.
    /// </summary>
    /// <param name="transaction">Transaction domain model.</param>
    /// <returns>Flat EF Core entity.</returns>
    internal static TransactionEntity ToEntity(Transaction transaction)
    {
        // Take the whole transaction as one object first: the same published path a store written
        // outside the Veriqa assemblies uses, so the shipped mapper and a third-party one see exactly
        // the same field perimeter and a field added to the domain cannot be flattened here alone.
        var snapshot = transaction.ToSnapshot();

        // Serialize collections and snapshots into JSON strings for database storage
        return new TransactionEntity
        {
            Id = snapshot.Id.ToString(),
            Type = snapshot.Type,
            State = snapshot.State.ToString(),
            StateReasonCode = snapshot.StateReasonCode,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.UpdatedAt,
            ExpiresAt = snapshot.ExpiresAt,
            CorrelationId = snapshot.CorrelationId,
            IdempotencyKey = snapshot.IdempotencyKey,
            IdempotencyScope = snapshot.IdempotencyScope,
            RequestedChannelType = snapshot.RequestedChannelType,
            ClientContext = snapshot.ClientContext,
            AllowedChannelTypesJson = JsonSerializer.Serialize(
                snapshot.AllowedChannelTypes,
                SerializerOptions),
            ConfirmationSnapshotJson = snapshot.ConfirmationSnapshot is not null
                ? SerializeImmutableSnapshot(snapshot.ConfirmationSnapshot)
                : null,
            ChannelIdentitySnapshotJson = snapshot.ChannelIdentitySnapshot is not null
                ? SerializeImmutableSnapshot(snapshot.ChannelIdentitySnapshot)
                : null,
            SubjectBindingJson = SerializeSubjectBinding(snapshot.SubjectBinding),
            SignInSubjectFingerprint = SignInSubjectScope.Of(transaction)?.ToFingerprint(),
            ResolvedIdentitySnapshotJson = snapshot.ResolvedIdentitySnapshot is not null
                ? SerializeImmutableSnapshot(snapshot.ResolvedIdentitySnapshot)
                : null,
            CompletionSnapshotJson = snapshot.CompletionSnapshot is not null
                ? JsonSerializer.Serialize(snapshot.CompletionSnapshot, SerializerOptions)
                : null,
            ConcurrencyToken = snapshot.ConcurrencyToken,
            OidcContextJson = snapshot.OidcContext is not null
                ? JsonSerializer.Serialize(snapshot.OidcContext, SerializerOptions)
                : null,
            RequestContextJson = snapshot.RequestContext is not null
                ? JsonSerializer.Serialize(snapshot.RequestContext, SerializerOptions)
                : null,
            IdentityMatchJson = snapshot.IdentityMatch is not null
                ? JsonSerializer.Serialize(snapshot.IdentityMatch, SerializerOptions)
                : null,
            InitiatorContextSnapshotJson = snapshot.InitiatorContextSnapshot is not null
                ? SerializeImmutableSnapshot(snapshot.InitiatorContextSnapshot)
                : null,
            ClaimRequirementsJson = snapshot.ClaimRequirements.Count > 0
                ? JsonSerializer.Serialize(snapshot.ClaimRequirements, SerializerOptions)
                : null,
            ClaimCompletionJson = SerializeClaimCompletionMark(snapshot.ClaimCompletion),
            ClaimCompletionSurface = snapshot.ClaimCompletion?.Surface?.ToString(),
            ClaimCompletionResultsJson = snapshot.ClaimCompletion is not null
                ? SerializeClaimCompletionResults(snapshot.ClaimCompletion.Results)
                : null,
            ClaimCompletionFormTokenHash = snapshot.ClaimCompletion?.FormTokenHash,
            StoredClaimValuesJson = SerializeClaimCompletionResults(snapshot.StoredClaimValues),
            AuthorizationCodeIssuedAt = snapshot.AuthorizationCodeIssuedAt,
            ScannerContextSnapshotJson = snapshot.ScannerContextSnapshot is not null
                ? SerializeScannerContext(snapshot.ScannerContextSnapshot)
                : null,
            ScannerFingerprint = snapshot.ScannerContextSnapshot?.Fingerprint,
            HopFirstResolvedAt = snapshot.HopFirstResolvedAt,
            HopInterstitialOpenedAt = snapshot.HopInterstitialOpenedAt,
            HopChannelLaunchedAt = snapshot.HopChannelLaunchedAt,
            HopRepeatResolvedAt = snapshot.HopRepeatResolvedAt
        };
    }

    /// <summary>
    /// Serializes a scanner context snapshot the way the entity stores it: without its fingerprint,
    /// which lives in <see cref="TransactionEntity.ScannerFingerprint"/>.
    /// </summary>
    /// <param name="snapshot">Scanner context snapshot.</param>
    /// <returns>JSON of the snapshot without the fingerprint.</returns>
    internal static string SerializeScannerContext(ScannerContextSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot.WithFingerprint(null), SerializerOptions);

    /// <summary>
    /// Updates the mutable fields of an existing EF Core entity from the domain model.
    /// Called on UpdateAsync — updates only the fields that can change.
    /// </summary>
    /// <remarks>
    /// The hop observation fields, the surface, the outcomes and the form token fingerprint of the claims
    /// completion are left alone on purpose: once the transaction is added, they are written only by the
    /// conditional writes of the store, and an update neither sets nor erases them.
    /// </remarks>
    /// <param name="entity">Existing EF Core entity to update.</param>
    /// <param name="transaction">Updated domain model.</param>
    internal static void UpdateEntity(TransactionEntity entity, Transaction transaction)
    {
        // Update only the mutable fields (fields with internal set in the domain)
        entity.State = transaction.State.ToString();
        entity.StateReasonCode = transaction.StateReasonCode;
        entity.UpdatedAt = transaction.UpdatedAt;
        entity.ChannelIdentitySnapshotJson = transaction.ChannelIdentitySnapshot is not null
            ? SerializeImmutableSnapshot(transaction.ChannelIdentitySnapshot)
            : null;

        // The subject binding is an ordinary field, written by the binding and by the attachment of the
        // channel identity.
        entity.SubjectBindingJson = SerializeSubjectBinding(transaction.SubjectBinding);

        // The lookup fingerprint follows the fields it is derived from: the scope appears with the
        // first bound subject, so it is recomputed on every update rather than only on creation.
        entity.SignInSubjectFingerprint = SignInSubjectScope.Of(transaction)?.ToFingerprint();
        entity.ResolvedIdentitySnapshotJson = transaction.ResolvedIdentitySnapshot is not null
            ? SerializeImmutableSnapshot(transaction.ResolvedIdentitySnapshot)
            : null;
        entity.CompletionSnapshotJson = transaction.CompletionSnapshot is not null
            ? JsonSerializer.Serialize(transaction.CompletionSnapshot, SerializerOptions)
            : null;

        // The identity-match container changes over the lifecycle — the verdict and the erased
        // expectations on completion, the issuance mark after it — so it is rewritten on every update
        // exactly as it is written on creation.
        entity.IdentityMatchJson = transaction.IdentityMatch is not null
            ? JsonSerializer.Serialize(transaction.IdentityMatch, SerializerOptions)
            : null;

        // The completion mark and the list of missing claims are written together with the channel
        // snapshot (SPEC-041 C90); the surface, the outcomes and the form token fingerprint of the
        // completion are not touched.
        entity.ClaimCompletionJson = SerializeClaimCompletionMark(transaction.ClaimCompletion);

        // The values taken from the collected claims store are an ordinary field: written on every update.
        entity.StoredClaimValuesJson = SerializeClaimCompletionResults(transaction.StoredClaimValues);

        // The authorization code issuance mark is an ordinary field as well: the engine sets it once,
        // and every later update carries the value the transaction was read with.
        entity.AuthorizationCodeIssuedAt = transaction.AuthorizationCodeIssuedAt;
        entity.ConcurrencyToken = transaction.ConcurrencyToken;
    }

    /// <summary>
    /// Converts a TransactionEntity EF Core entity back into the Transaction domain model.
    /// </summary>
    /// <param name="entity">Flat EF Core entity.</param>
    /// <returns>Transaction domain model.</returns>
    internal static Transaction ToDomain(TransactionEntity entity)
    {
        // Parse TransactionId from the string
        if (!TransactionId.TryParse(entity.Id, out var transactionId))
        {
            throw new InvalidOperationException(
                $"Invalid TransactionId in the store: '{entity.Id}'");
        }

        // Parse AllowedChannelTypes from the JSON array
        var allowedChannelTypes = DeserializeAllowedChannelTypes(entity.AllowedChannelTypesJson);

        // Restore the domain model from the flat data through the published rehydration path:
        // State, UpdatedAt and ConcurrencyToken are stated, not defaulted, and the transaction comes
        // back exactly as it was stored.
        return Transaction.Restore(new TransactionSnapshot
        {
            Id = transactionId,
            Type = entity.Type,
            State = Enum.Parse<TransactionState>(entity.State),
            StateReasonCode = entity.StateReasonCode,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            ExpiresAt = entity.ExpiresAt,
            CorrelationId = entity.CorrelationId,
            IdempotencyKey = entity.IdempotencyKey,
            IdempotencyScope = entity.IdempotencyScope,
            RequestedChannelType = entity.RequestedChannelType,
            AllowedChannelTypes = allowedChannelTypes,
            ClientContext = entity.ClientContext,
            ConfirmationSnapshot = entity.ConfirmationSnapshotJson is not null
                ? JsonSerializer.Deserialize<ConfirmationSnapshot>(
                    entity.ConfirmationSnapshotJson,
                    SerializerOptions)
                : null,
            ChannelIdentitySnapshot = entity.ChannelIdentitySnapshotJson is not null
                ? JsonSerializer.Deserialize<ChannelIdentitySnapshot>(
                    entity.ChannelIdentitySnapshotJson,
                    SerializerOptions)
                : null,
            SubjectBinding = entity.SubjectBindingJson is not null
                ? JsonSerializer.Deserialize<ChannelSubjectBinding>(
                    entity.SubjectBindingJson,
                    SerializerOptions)
                : null,
            ResolvedIdentitySnapshot = entity.ResolvedIdentitySnapshotJson is not null
                ? JsonSerializer.Deserialize<ResolvedIdentitySnapshot>(
                    entity.ResolvedIdentitySnapshotJson,
                    SerializerOptions)
                : null,
            CompletionSnapshot = entity.CompletionSnapshotJson is not null
                ? JsonSerializer.Deserialize<CompletionSnapshot>(
                    entity.CompletionSnapshotJson,
                    SerializerOptions)
                : null,
            ConcurrencyToken = entity.ConcurrencyToken,
            OidcContext = entity.OidcContextJson is not null
                ? JsonSerializer.Deserialize<OidcContext>(
                    entity.OidcContextJson,
                    SerializerOptions)
                : null,
            RequestContext = entity.RequestContextJson is not null
                ? JsonSerializer.Deserialize<TransactionRequestContext>(
                    entity.RequestContextJson,
                    SerializerOptions)
                : null,
            IdentityMatch = entity.IdentityMatchJson is not null
                ? JsonSerializer.Deserialize<IdentityMatchState>(
                    entity.IdentityMatchJson,
                    SerializerOptions)
                : null,
            InitiatorContextSnapshot = entity.InitiatorContextSnapshotJson is not null
                ? JsonSerializer.Deserialize<InitiatorContextSnapshot>(
                    entity.InitiatorContextSnapshotJson,
                    SerializerOptions)
                : null,
            ClaimRequirements = entity.ClaimRequirementsJson is not null
                ? JsonSerializer.Deserialize<ClaimRequirement[]>(
                    entity.ClaimRequirementsJson,
                    SerializerOptions) ?? []
                : [],
            ClaimCompletion = RestoreClaimCompletion(entity),
            StoredClaimValues = DeserializeClaimCompletionResults(entity.StoredClaimValuesJson),
            AuthorizationCodeIssuedAt = entity.AuthorizationCodeIssuedAt,
            ScannerContextSnapshot = entity.ScannerContextSnapshotJson is not null
                ? JsonSerializer.Deserialize<ScannerContextSnapshot>(
                    entity.ScannerContextSnapshotJson,
                    SerializerOptions)?.WithFingerprint(entity.ScannerFingerprint)
                : null,
            HopFirstResolvedAt = entity.HopFirstResolvedAt,
            HopInterstitialOpenedAt = entity.HopInterstitialOpenedAt,
            HopChannelLaunchedAt = entity.HopChannelLaunchedAt,
            HopRepeatResolvedAt = entity.HopRepeatResolvedAt
        });
    }

    /// <summary>
    /// Serializes the completion mark and the list of missing claims — the part of the claims
    /// completion fields an update writes.
    /// </summary>
    /// <param name="completion">Claims completion fields; null — no mark.</param>
    /// <returns>JSON of the mark, or null.</returns>
    internal static string? SerializeClaimCompletionMark(ClaimCompletionState? completion)
        => completion is null
            ? null
            : JsonSerializer.Serialize(
                new ClaimCompletionMark(completion.ConfirmedAt, [.. completion.Items]),
                SerializerOptions);

    /// <summary>
    /// Reads the list of missing claims out of the JSON of a completion mark.
    /// </summary>
    /// <param name="json">JSON of the mark; null — no mark.</param>
    /// <returns>The list of missing claims, or null when there is no mark.</returns>
    internal static IReadOnlyList<ClaimCompletionItem>? DeserializeClaimCompletionItems(string? json)
        => DeserializeClaimCompletionMark(json) is { } mark
            ? mark.Items ?? []
            : null;

    /// <summary>
    /// Serializes a list of claim records — the outcomes of the claims completion or the values taken from
    /// the collected claims store; an empty list — null.
    /// </summary>
    /// <param name="results">Records to serialize.</param>
    /// <returns>JSON array of the records, or null.</returns>
    internal static string? SerializeClaimCompletionResults(IReadOnlyList<ClaimCompletionResult> results)
        => results.Count > 0 ? JsonSerializer.Serialize(results, SerializerOptions) : null;

    /// <summary>
    /// Reads a list of claim records — the outcomes of the claims completion or the values taken from the
    /// collected claims store; a missing column or document field — none.
    /// </summary>
    /// <param name="json">JSON array of the records; null — none.</param>
    /// <returns>The records.</returns>
    internal static IReadOnlyList<ClaimCompletionResult> DeserializeClaimCompletionResults(string? json)
        => json is not null
            ? JsonSerializer.Deserialize<ClaimCompletionResult[]>(json, SerializerOptions) ?? []
            : [];

    /// <summary>
    /// Reads the name of a web step surface as the entity stores it.
    /// </summary>
    /// <param name="surface">Stored member name; null — no surface.</param>
    /// <returns>The surface, or null.</returns>
    internal static WebStepSurface? ParseWebStepSurface(string? surface)
        => surface is null ? null : Enum.Parse<WebStepSurface>(surface);

    /// <summary>
    /// Puts the claims completion fields back together from the four parts the entity keeps them in.
    /// </summary>
    /// <param name="entity">Stored entity.</param>
    /// <returns>The claims completion fields, or null when the entity carries no mark.</returns>
    private static ClaimCompletionState? RestoreClaimCompletion(TransactionEntity entity)
    {
        var mark = DeserializeClaimCompletionMark(entity.ClaimCompletionJson);
        if (mark is null)
        {
            return null;
        }

        return new ClaimCompletionState
        {
            ConfirmedAt = mark.ConfirmedAt,
            Items = mark.Items ?? [],
            Surface = ParseWebStepSurface(entity.ClaimCompletionSurface),
            Results = DeserializeClaimCompletionResults(entity.ClaimCompletionResultsJson),
            FormTokenHash = entity.ClaimCompletionFormTokenHash
        };
    }

    /// <summary>
    /// Reads the JSON of a completion mark.
    /// </summary>
    /// <param name="json">JSON of the mark; null — no mark.</param>
    /// <returns>The mark, or null.</returns>
    private static ClaimCompletionMark? DeserializeClaimCompletionMark(string? json)
        => json is not null
            ? JsonSerializer.Deserialize<ClaimCompletionMark>(json, SerializerOptions)
            : null;

    /// <summary>
    /// Serializes the bound channel subject.
    /// </summary>
    /// <param name="binding">Bound channel subject; null — none.</param>
    /// <returns>JSON of the binding, or null.</returns>
    private static string? SerializeSubjectBinding(ChannelSubjectBinding? binding)
        => binding is not null ? JsonSerializer.Serialize(binding, SerializerOptions) : null;

    /// <summary>
    /// Deserializes a JSON string array into a FrozenSet for AllowedChannelTypes.
    /// </summary>
    /// <param name="json">JSON string containing an array of channel types.</param>
    /// <returns>Immutable set of allowed channel types.</returns>
    private static IReadOnlySet<string> DeserializeAllowedChannelTypes(string json)
    {
        // Deserialize the JSON array into a string array, then create a FrozenSet
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        }

        var channelTypes = JsonSerializer.Deserialize<string[]>(json, SerializerOptions);

        return (channelTypes ?? Array.Empty<string>())
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Stored form of the part of the claims completion fields an update writes: the moment of the
    /// confirmation and the list of missing claims.
    /// </summary>
    /// <param name="ConfirmedAt">Moment the user confirmed the sign-in.</param>
    /// <param name="Items">Claims missing at that moment.</param>
    private sealed record ClaimCompletionMark(DateTimeOffset ConfirmedAt, ClaimCompletionItem[]? Items);
}
