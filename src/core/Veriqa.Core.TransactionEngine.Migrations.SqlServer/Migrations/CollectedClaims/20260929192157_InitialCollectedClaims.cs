// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veriqa.Core.TransactionEngine.Migrations.SqlServer.Migrations.CollectedClaims
{
    /// <inheritdoc />
    public partial class InitialCollectedClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectedClaimSubjects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantSegment = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    LastSignInAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectedClaimSubjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CollectedClaimIssuedSubjects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubjectId = table.Column<long>(type: "bigint", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IssuedSubject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectedClaimIssuedSubjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectedClaimIssuedSubjects_CollectedClaimSubjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "CollectedClaimSubjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CollectedClaims",
                columns: table => new
                {
                    SubjectId = table.Column<long>(type: "bigint", nullable: false),
                    Claim = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Verified = table.Column<bool>(type: "bit", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectedClaims", x => new { x.SubjectId, x.Claim });
                    table.ForeignKey(
                        name: "FK_CollectedClaims_CollectedClaimSubjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "CollectedClaimSubjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectedClaimIssuedSubjects_ApplicationId_IssuedSubject",
                table: "CollectedClaimIssuedSubjects",
                columns: new[] { "ApplicationId", "IssuedSubject" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectedClaimIssuedSubjects_SubjectId_ApplicationId_IssuedSubject",
                table: "CollectedClaimIssuedSubjects",
                columns: new[] { "SubjectId", "ApplicationId", "IssuedSubject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectedClaimSubjects_TenantSegment_LastSignInAt",
                table: "CollectedClaimSubjects",
                columns: new[] { "TenantSegment", "LastSignInAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectedClaimSubjects_TenantSegment_Subject",
                table: "CollectedClaimSubjects",
                columns: new[] { "TenantSegment", "Subject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectedClaimIssuedSubjects");

            migrationBuilder.DropTable(
                name: "CollectedClaims");

            migrationBuilder.DropTable(
                name: "CollectedClaimSubjects");
        }
    }
}
