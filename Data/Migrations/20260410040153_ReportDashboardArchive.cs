using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotAd_Globe.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportDashboardArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportDashboardArchives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CsvSourceKind = table.Column<int>(type: "int", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EvictedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SessionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PendingKpiJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StatusKpiJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PendingFilteredXlsxBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    StatusFilteredXlsxBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    LegacyGenerateXlsxBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDashboardArchives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportDashboardArchives_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportDashboardArchives_Token",
                table: "ReportDashboardArchives",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportDashboardArchives_UserId_UploadedUtc",
                table: "ReportDashboardArchives",
                columns: new[] { "UserId", "UploadedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportDashboardArchives");
        }
    }
}
