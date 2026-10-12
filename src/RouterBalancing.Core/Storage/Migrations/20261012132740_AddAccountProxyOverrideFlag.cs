using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountProxyOverrideFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProxyOverrideEnabled",
                table: "ProviderAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "ProviderAccounts" SET "ProxyOverrideEnabled" = 1
                WHERE "Id" IN (SELECT DISTINCT "AccountId" FROM "ProviderAccountProxies");

                INSERT INTO "ProviderProxies" ("ProviderId", "ProxyId")
                SELECT DISTINCT pa."ProviderId", ap."ProxyId"
                FROM "ProviderAccountProxies" ap
                JOIN "ProviderAccounts" pa ON pa."Id" = ap."AccountId"
                WHERE NOT EXISTS (
                    SELECT 1 FROM "ProviderProxies" pp
                    WHERE pp."ProviderId" = pa."ProviderId" AND pp."ProxyId" = ap."ProxyId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProxyOverrideEnabled",
                table: "ProviderAccounts");
        }
    }
}
