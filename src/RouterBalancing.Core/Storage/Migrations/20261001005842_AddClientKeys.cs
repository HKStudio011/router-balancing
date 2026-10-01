using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddClientKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ClientKeyId",
                table: "LogEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientKeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    KeyMask = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestsUsed = table.Column<long>(type: "INTEGER", nullable: false),
                    TokensUsed = table.Column<long>(type: "INTEGER", nullable: false),
                    UsageDate = table.Column<string>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RatePerMinute = table.Column<int>(type: "INTEGER", nullable: true),
                    TokensPerMinute = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientKeys_KeyHash",
                table: "ClientKeys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientKeys");

            migrationBuilder.DropColumn(
                name: "ClientKeyId",
                table: "LogEntries");
        }
    }
}
