// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veriqa.Core.TransactionEngine.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddHopObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HopChannelLaunchedAt",
                table: "Transactions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HopFirstResolvedAt",
                table: "Transactions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HopInterstitialOpenedAt",
                table: "Transactions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScannerContextSnapshotJson",
                table: "Transactions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScannerFingerprint",
                table: "Transactions",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HopChannelLaunchedAt",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "HopFirstResolvedAt",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "HopInterstitialOpenedAt",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ScannerContextSnapshotJson",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ScannerFingerprint",
                table: "Transactions");
        }
    }
}
