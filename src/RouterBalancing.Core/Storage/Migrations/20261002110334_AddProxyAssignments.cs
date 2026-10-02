using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddProxyAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProxyMode",
                table: "Providers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProxyMode",
                table: "ProviderAccounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderAccountProxies",
                columns: table => new
                {
                    AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProxyId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAccountProxies", x => new { x.AccountId, x.ProxyId });
                    table.ForeignKey(
                        name: "FK_ProviderAccountProxies_OutboundProxies_ProxyId",
                        column: x => x.ProxyId,
                        principalTable: "OutboundProxies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProviderAccountProxies_ProviderAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "ProviderAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderProxies",
                columns: table => new
                {
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProxyId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderProxies", x => new { x.ProviderId, x.ProxyId });
                    table.ForeignKey(
                        name: "FK_ProviderProxies_OutboundProxies_ProxyId",
                        column: x => x.ProxyId,
                        principalTable: "OutboundProxies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProviderProxies_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAccountProxies_ProxyId",
                table: "ProviderAccountProxies",
                column: "ProxyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderProxies_ProxyId",
                table: "ProviderProxies",
                column: "ProxyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderAccountProxies");

            migrationBuilder.DropTable(
                name: "ProviderProxies");

            migrationBuilder.DropColumn(
                name: "ProxyMode",
                table: "Providers");

            migrationBuilder.DropColumn(
                name: "ProxyMode",
                table: "ProviderAccounts");
        }
    }
}
