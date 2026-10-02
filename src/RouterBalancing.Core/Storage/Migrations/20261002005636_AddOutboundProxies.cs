using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundProxies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboundProxies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Scheme = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PasswordEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastTestSuccess = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastTestMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastTestIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundProxies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundProxies_Scheme_Host_Port",
                table: "OutboundProxies",
                columns: new[] { "Scheme", "Host", "Port" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboundProxies");
        }
    }
}
