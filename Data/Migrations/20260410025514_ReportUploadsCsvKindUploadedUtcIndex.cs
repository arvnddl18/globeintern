using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotAd_Globe.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportUploadsCsvKindUploadedUtcIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ReportUploads_CsvSourceKind_UploadedUtc",
                table: "ReportUploads",
                columns: new[] { "CsvSourceKind", "UploadedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReportUploads_CsvSourceKind_UploadedUtc",
                table: "ReportUploads");
        }
    }
}
