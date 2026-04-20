using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotAd_Globe.Data.Migrations;

/// <summary>
/// SQL Server schema + seeded admin (username: admin, password: admin123, SHA-256 + salt per <see cref="SlotAd_Globe.Services.Sha256PasswordHasher"/>).
/// Apply to an empty database, or drop <c>ReportUploads</c>, <c>Users</c>, and <c>__EFMigrationsHistory</c> first if you created tables manually.
/// </summary>
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                PasswordSalt = table.Column<string>(type: "nvarchar(88)", maxLength: 88, nullable: true),
                IsAdmin = table.Column<bool>(type: "bit", nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastReportUploadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ReportUploads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                OriginalFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                CsvSourceKind = table.Column<int>(type: "int", nullable: false),
                CsvContent = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                SessionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReportUploads", x => x.Id);
                table.ForeignKey(
                    name: "FK_ReportUploads_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReportUploads_Token",
            table: "ReportUploads",
            column: "Token",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ReportUploads_UserId_UploadedUtc",
            table: "ReportUploads",
            columns: new[] { "UserId", "UploadedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Users_UserName",
            table: "Users",
            column: "UserName",
            unique: true);

        // Password: admin123 (SHA-256 with 16-byte salt; matches Sha256PasswordHasher in app)
        migrationBuilder.InsertData(
            table: "Users",
            columns: new[] { "Id", "UserName", "PasswordHash", "PasswordSalt", "IsAdmin", "CreatedUtc", "LastReportUploadId" },
            values: new object[,]
            {
                {
                    new Guid("00000000-0000-0000-0000-000000000001"),
                    "admin",
                    "KP5RRMXlfY1+zEE0yU+fVuQHoTxmEAzmpBiM8s7ZfbI=",
                    "AQIDBAUGBwgJCgsMDQ4PEA==",
                    true,
                    new DateTime(2026, 2, 8, 12, 0, 0, DateTimeKind.Utc),
                    null
                }
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ReportUploads");
        migrationBuilder.DropTable(name: "Users");
    }
}
