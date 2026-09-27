using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderAccounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModelPatterns = table.Column<string>(type: "TEXT", nullable: true),
                    Weight = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    DailyTokenLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    TokensUsed = table.Column<long>(type: "INTEGER", nullable: false),
                    DailyRequestLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestsUsed = table.Column<long>(type: "INTEGER", nullable: false),
                    UsageDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LastTestSuccess = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastTestMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderAccounts_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccounts_ProviderId_Name",
                table: "ProviderAccounts",
                columns: new[] { "ProviderId", "Name" },
                unique: true);

            // Data migration: key cũ của provider → account "Default" (spec provider-accounts §3).
            // Chạy sau CreateTable (INSERT cần bảng mới) và trước DropColumn (đọc cột cũ).
            migrationBuilder.Sql("""
                INSERT INTO ProviderAccounts
                  (ProviderId, Name, ApiKeyEncrypted, Enabled, Weight, Priority, ModelPatterns,
                   DailyTokenLimit, TokensUsed, DailyRequestLimit, RequestsUsed, UsageDate,
                   LastTestSuccess, LastTestAt, LastTestMessage, CreatedAt, UpdatedAt)
                SELECT Id, 'Default', ApiKeyEncrypted, 1, 100, 0, NULL,
                       NULL, 0, NULL, 0, NULL,
                       NULL, NULL, NULL, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')
                FROM Providers WHERE ApiKeyEncrypted <> '';
                """);

            migrationBuilder.DropColumn(
                name: "ApiKeyEncrypted",
                table: "Providers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeyEncrypted",
                table: "Providers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Data migration ngược: account "Default" → key cũ của provider.
            // Chạy sau AddColumn (cần cột đích) và trước DropTable (cần đọc bảng account).
            migrationBuilder.Sql("""
                UPDATE Providers SET ApiKeyEncrypted = (
                  SELECT a.ApiKeyEncrypted FROM ProviderAccounts a
                  WHERE a.ProviderId = Providers.Id AND a.Name = 'Default'
                  ORDER BY a.Id LIMIT 1)
                WHERE EXISTS (
                  SELECT 1 FROM ProviderAccounts a
                  WHERE a.ProviderId = Providers.Id AND a.Name = 'Default');
                """);

            migrationBuilder.DropTable(
                name: "ProviderAccounts");
        }
    }
}
