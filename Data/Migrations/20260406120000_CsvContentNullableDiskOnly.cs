using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlotAd_Globe.Data.Migrations;

/// <summary>Makes <c>CsvContent</c> nullable so new uploads can be disk-only (no duplicate varbinary).</summary>
public partial class CsvContentNullableDiskOnly : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<byte[]>(
            name: "CsvContent",
            table: "ReportUploads",
            type: "varbinary(max)",
            nullable: true,
            oldClrType: typeof(byte[]),
            oldType: "varbinary(max)",
            oldNullable: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE [ReportUploads]
            SET [CsvContent] = 0x
            WHERE [CsvContent] IS NULL
            """);

        migrationBuilder.AlterColumn<byte[]>(
            name: "CsvContent",
            table: "ReportUploads",
            type: "varbinary(max)",
            nullable: false,
            oldClrType: typeof(byte[]),
            oldType: "varbinary(max)",
            oldNullable: true);
    }
}
