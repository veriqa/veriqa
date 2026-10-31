// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// One personal field of the initiator context that a deployment may show (SPEC-017 §10, ICC-015):
/// its name in <c>DisplayFields</c>, the server slot it fills, the snapshot members it owns and the way
/// its slot value is read.
/// </summary>
/// <remarks>
/// The value readers take the sanitizer as an argument: the sanitizer belongs to the channel-processing
/// assembly, above this one. Every source part of a value is sanitized separately before the parts are
/// combined.
/// </remarks>
internal sealed class InitiatorContextFieldRow
{
    /// <summary>
    /// Name of the field in the <c>DisplayFields</c> setting, compared case-insensitively.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Name of the server slot the field fills.
    /// </summary>
    public required string SlotName { get; init; }

    /// <summary>
    /// Members of <see cref="InitiatorContextSnapshot"/> the field owns, by <c>nameof</c>: the display
    /// filter clears them when the field is not shown.
    /// </summary>
    public required IReadOnlyList<string> OwnedMembers { get; init; }

    /// <summary>
    /// The slot value read from a snapshot, sanitized by the given sanitizer; null when absent.
    /// </summary>
    public required Func<InitiatorContextSnapshot, Func<string?, string?>, string?> SnapshotValue { get; init; }

    /// <summary>
    /// The slot value read from a detailed confirmation context, sanitized by the given sanitizer; null
    /// when absent.
    /// </summary>
    public required Func<DetailedConfirmationPromptContext, Func<string?, string?>, string?> PromptValue { get; init; }
}

/// <summary>
/// The single table of the displayable initiator context fields. The start-up validator of
/// <c>DisplayFields</c>, the display decision, the assembly of the server slot values and the
/// confirmation context for an adapter all read the fields from here, so a field is added in one place.
/// </summary>
/// <remarks>
/// The application name is not a row: it is the attribution of the transaction, not a personal datum of
/// its initiator, and its slot is valued by the type of the transaction (SPEC-036 TPL-102, TPL-124).
/// <para>
/// Extension point: a distribution built from a fork of the core sources adds its own rows by adding a
/// NEW file with another part of this class that implements <c>AddForkRows</c>. The shipped
/// sources hold no such file, so a fork never edits this one and pulls upstream changes without
/// conflicts. The upstream table carries only the fields the base distribution shows — that is its
/// intended state, not an unfinished one.
/// </para>
/// </remarks>
internal static partial class InitiatorContextFieldTable
{
    /// <summary>
    /// Snapshot members the display filter is able to clear: the nullable best-effort members. A row
    /// claiming any other member is refused, since clearing it would silently not happen.
    /// </summary>
    private static readonly HashSet<string> ClearableMembers = new(StringComparer.Ordinal)
    {
        nameof(InitiatorContextSnapshot.IpAddress),
        nameof(InitiatorContextSnapshot.Browser),
        nameof(InitiatorContextSnapshot.OsPlatform),
        nameof(InitiatorContextSnapshot.DeviceType),
        nameof(InitiatorContextSnapshot.GeoCountry),
        nameof(InitiatorContextSnapshot.GeoCity)
    };

    /// <summary>
    /// All rows: the shipped ones followed by the rows a fork adds.
    /// </summary>
    public static IReadOnlyList<InitiatorContextFieldRow> Rows { get; } = Build();

    /// <summary>
    /// The snapshot with the members of the rows not shown cleared. A member no row owns is kept as
    /// collected.
    /// </summary>
    /// <param name="snapshot">Initiator context as collected.</param>
    /// <param name="shownRows">Rows the display decision shows.</param>
    /// <returns>The snapshot as it may be shown.</returns>
    public static InitiatorContextSnapshot Filter(
        InitiatorContextSnapshot snapshot,
        IReadOnlyCollection<InitiatorContextFieldRow> shownRows)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(shownRows);

        var cleared = Rows
            .Where(row => !shownRows.Contains(row))
            .SelectMany(row => row.OwnedMembers)
            .ToHashSet(StringComparer.Ordinal);

        return new InitiatorContextSnapshot
        {
            ClientApplicationName = snapshot.ClientApplicationName,
            InitiatedAt = snapshot.InitiatedAt,
            IpAddress = Keep(cleared, nameof(InitiatorContextSnapshot.IpAddress), snapshot.IpAddress),
            IpTrusted = snapshot.IpTrusted,
            Browser = Keep(cleared, nameof(InitiatorContextSnapshot.Browser), snapshot.Browser),
            OsPlatform = Keep(cleared, nameof(InitiatorContextSnapshot.OsPlatform), snapshot.OsPlatform),
            DeviceType = Keep(cleared, nameof(InitiatorContextSnapshot.DeviceType), snapshot.DeviceType),
            GeoCountry = Keep(cleared, nameof(InitiatorContextSnapshot.GeoCountry), snapshot.GeoCountry),
            GeoCity = Keep(cleared, nameof(InitiatorContextSnapshot.GeoCity), snapshot.GeoCity),
            RawMetadata = snapshot.RawMetadata,
            CapturedAt = snapshot.CapturedAt,
            CollectorVersion = snapshot.CollectorVersion
        };
    }

    /// <summary>
    /// Adds the rows of a fork of the core sources. Implemented only in a file the fork adds; without an
    /// implementation the call is removed by the compiler.
    /// </summary>
    /// <param name="rows">Accumulator the rows are added to, after the shipped ones.</param>
    static partial void AddForkRows(List<InitiatorContextFieldRow> rows);

    /// <summary>
    /// Builds the table and checks it: a row repeating a field or slot name (case-insensitive) — of another
    /// row or of a core field or slot valued outside the table — fails the initialization instead of
    /// silently shadowing it.
    /// </summary>
    /// <returns>The checked rows.</returns>
    private static InitiatorContextFieldRow[] Build()
    {
        var rows = new List<InitiatorContextFieldRow>
        {
            new()
            {
                Name = InitiatorContextFields.DisplayFields.Browser,
                SlotName = SlotNames.Browser,
                OwnedMembers = [nameof(InitiatorContextSnapshot.Browser)],
                SnapshotValue = static (snapshot, sanitize) => sanitize(snapshot.Browser),
                PromptValue = static (context, sanitize) => sanitize(context.Browser)
            },
            new()
            {
                Name = InitiatorContextFields.DisplayFields.Os,
                SlotName = SlotNames.Os,
                OwnedMembers = [nameof(InitiatorContextSnapshot.OsPlatform)],
                SnapshotValue = static (snapshot, sanitize) => sanitize(snapshot.OsPlatform),
                PromptValue = static (context, sanitize) => sanitize(context.OsPlatform)
            },
            new()
            {
                Name = InitiatorContextFields.DisplayFields.Region,
                SlotName = SlotNames.Region,
                OwnedMembers =
                [
                    nameof(InitiatorContextSnapshot.GeoCity),
                    nameof(InitiatorContextSnapshot.GeoCountry)
                ],
                SnapshotValue = static (snapshot, sanitize) =>
                    RegionOf(sanitize(snapshot.GeoCity), sanitize(snapshot.GeoCountry)),
                PromptValue = static (context, sanitize) =>
                    RegionOf(sanitize(context.GeoCity), sanitize(context.GeoCountry))
            }
        };

        AddForkRows(rows);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // The application name is outside the table but shares the DisplayFields namespace with it.
            InitiatorContextFields.DisplayFields.Application
        };
        var slotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Core slots valued outside the table into the same slot values as its rows: a row reusing
            // one of them would silently overwrite it or be overwritten.
            SlotNames.App,
            SlotNames.BrandName,
            SlotNames.OutcomeAt,
            SlotNames.RequestedAt,
            SlotNames.ValidUntil,
            SlotNames.Link,
            SlotNames.Lang,
            SlotNames.QrCid,
            SlotNames.ChannelName,
            SlotNames.BotName,
            SlotNames.EmailMasked,
            SlotNames.PhoneMasked,
            SlotNames.UsernameMasked,
            SlotNames.DisplayNameMasked
        };

        foreach (var row in rows)
        {
            if (!names.Add(row.Name))
            {
                throw new InvalidOperationException(
                    $"Initiator context field '{row.Name}' is declared more than once.");
            }

            if (!slotNames.Add(row.SlotName))
            {
                throw new InvalidOperationException(
                    $"Server slot '{row.SlotName}' of initiator context field '{row.Name}' is already filled by another field or by the core.");
            }

            foreach (var member in row.OwnedMembers)
            {
                if (!ClearableMembers.Contains(member))
                {
                    throw new InvalidOperationException(
                        $"Initiator context field '{row.Name}' owns '{member}', which is not a clearable snapshot member.");
                }
            }
        }

        return [.. rows];
    }

    /// <summary>
    /// A member value, or null when the member is cleared.
    /// </summary>
    /// <param name="cleared">Names of the cleared members.</param>
    /// <param name="member">Name of the member.</param>
    /// <param name="value">Value as collected.</param>
    /// <returns>The value as it may be shown.</returns>
    private static string? Keep(HashSet<string> cleared, string member, string? value) =>
        cleared.Contains(member) ? null : value;

    /// <summary>
    /// Assembles the region from sanitized city and country: "City, Country", or whichever of the two is
    /// known.
    /// </summary>
    /// <param name="city">Sanitized city of the initiator.</param>
    /// <param name="country">Sanitized country of the initiator.</param>
    /// <returns>Region string, or null when no geo data is available.</returns>
    private static string? RegionOf(string? city, string? country)
    {
        if (city is not null && country is not null)
        {
            return $"{city}, {country}";
        }

        return country ?? city;
    }
}
