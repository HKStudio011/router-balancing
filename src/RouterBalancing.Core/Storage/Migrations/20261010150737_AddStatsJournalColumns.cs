using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddStatsJournalColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HttpStatus",
                table: "LogEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelKey",
                table: "LogEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                table: "LogEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "LogEntries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HttpStatus",
                table: "LogEntries");

            migrationBuilder.DropColumn(
                name: "ModelKey",
                table: "LogEntries");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "LogEntries");

            migrationBuilder.DropColumn(
                name: "ProviderKey",
                table: "LogEntries");
        }
    }
}
