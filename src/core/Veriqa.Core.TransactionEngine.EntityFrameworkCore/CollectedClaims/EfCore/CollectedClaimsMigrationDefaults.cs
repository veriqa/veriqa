// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

/// <summary>
/// Schema constants of the collected claims store shared by everyone who configures its EF Core provider:
/// the host at runtime and the design-time factories of the migrations assemblies.
/// </summary>
public static class CollectedClaimsMigrationDefaults
{
    /// <summary>
    /// Migration history table of the collected claims store. The store may share its database with the
    /// transaction store — the shipped host puts both on one connection string and one migrations assembly
    /// — so it keeps a history of its own: a shared one would make the two sets of migrations overwrite each
    /// other's records. The runtime and the CLI tooling must name the same table — otherwise
    /// <c>dotnet ef database update</c> records a migration in one table while the application, seeing the
    /// other one empty, applies it a second time and fails on an already existing relation.
    /// </summary>
    public const string MigrationsHistoryTable = "__CollectedClaimsMigrationsHistory";
}
