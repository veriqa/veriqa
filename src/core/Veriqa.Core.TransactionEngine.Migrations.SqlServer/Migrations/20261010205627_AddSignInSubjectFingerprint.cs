// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veriqa.Core.TransactionEngine.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSignInSubjectFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignInSubjectFingerprint",
                table: "Transactions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_SignInSubjectFingerprint",
                table: "Transactions",
                column: "SignInSubjectFingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_SignInSubjectFingerprint",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "SignInSubjectFingerprint",
                table: "Transactions");
        }
    }
}
