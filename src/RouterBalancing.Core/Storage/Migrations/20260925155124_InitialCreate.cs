using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Combos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Combos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: true),
                    ModelId = table.Column<long>(type: "INTEGER", nullable: true),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Providers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxConcurrent = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTestSuccess = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastTestMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsManual = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContextWindow = table.Column<int>(type: "INTEGER", nullable: true),
                    SupportsVision = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupportsThink = table.Column<bool>(type: "INTEGER", nullable: false),
                    ThinkEfforts = table.Column<string>(type: "TEXT", nullable: true),
                    InputModalities = table.Column<string>(type: "TEXT", nullable: true),
                    OutputModalities = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComboItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComboId = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetModelId = table.Column<long>(type: "INTEGER", nullable: true),
                    TargetComboId = table.Column<long>(type: "INTEGER", nullable: true),
                    ComboId1 = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComboItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComboItems_Combos_ComboId",
                        column: x => x.ComboId,
                        principalTable: "Combos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ComboItems_Combos_ComboId1",
                        column: x => x.ComboId1,
                        principalTable: "Combos",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ComboItems_Combos_TargetComboId",
                        column: x => x.TargetComboId,
                        principalTable: "Combos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComboItems_Models_TargetModelId",
                        column: x => x.TargetModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComboItems_ComboId_Position",
                table: "ComboItems",
                columns: new[] { "ComboId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_ComboItems_ComboId1",
                table: "ComboItems",
                column: "ComboId1");

            migrationBuilder.CreateIndex(
                name: "IX_ComboItems_TargetComboId",
                table: "ComboItems",
                column: "TargetComboId");

            migrationBuilder.CreateIndex(
                name: "IX_ComboItems_TargetModelId",
                table: "ComboItems",
                column: "TargetModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Combos_Name",
                table: "Combos",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Category_Timestamp_ProviderId_ModelId",
                table: "LogEntries",
                columns: new[] { "Category", "Timestamp", "ProviderId", "ModelId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Timestamp",
                table: "LogEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Models_ProviderId_ModelId",
                table: "Models",
                columns: new[] { "ProviderId", "ModelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "ComboItems");

            migrationBuilder.DropTable(
                name: "LogEntries");

            migrationBuilder.DropTable(
                name: "Combos");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "Providers");
        }
    }
}
