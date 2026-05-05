using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotAd_Globe.Data.Migrations
{
    /// <inheritdoc />
    public partial class ToolsAuditSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolAuditSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    WeekStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolAuditSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolAuditSessions_Users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ToolAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TechnicianName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RawValue = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolAuditEntries_ToolAuditSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ToolAuditSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ToolAuditEntries_SessionId_TechnicianName",
                table: "ToolAuditEntries",
                columns: new[] { "SessionId", "TechnicianName" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolAuditEntries_SessionId_ToolName_Status",
                table: "ToolAuditEntries",
                columns: new[] { "SessionId", "ToolName", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolAuditSessions_UploadedByUserId_UploadedUtc",
                table: "ToolAuditSessions",
                columns: new[] { "UploadedByUserId", "UploadedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolAuditSessions_WeekStartDate",
                table: "ToolAuditSessions",
                column: "WeekStartDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToolAuditEntries");

            migrationBuilder.DropTable(
                name: "ToolAuditSessions");
        }
    }
}
