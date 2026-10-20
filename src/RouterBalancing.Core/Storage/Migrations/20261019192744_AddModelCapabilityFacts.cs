using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddModelCapabilityFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ApiKeyEncrypted",
                table: "ProviderAccounts",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.CreateTable(
                name: "ModelCapabilities",
                columns: table => new
                {
                    ModelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", nullable: false),
                    KnownAbsent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelCapabilities", x => new { x.ModelId, x.Capability });
                    table.ForeignKey(
                        name: "FK_ModelCapabilities_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelModalities",
                columns: table => new
                {
                    ModelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Modality = table.Column<string>(type: "TEXT", nullable: false),
                    KnownAbsent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelModalities", x => new { x.ModelId, x.Direction, x.Modality });
                    table.ForeignKey(
                        name: "FK_ModelModalities_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelReasoningEfforts",
                columns: table => new
                {
                    ModelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelReasoningEfforts", x => new { x.ModelId, x.Level });
                    table.ForeignKey(
                        name: "FK_ModelReasoningEfforts_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Tiền xử lý tên trùng (fix review round 1): DB user đã có Name trùng sẽ fail ở
            // CREATE UNIQUE INDEX dưới đây — app auto-migrate lúc khởi động nên phải dedupe
            // ngay trong Up(). Hàng đầu mỗi nhóm (Id nhỏ nhất) giữ nguyên tên, hàng sau nhận
            // suffix -2, -3… theo Id tăng dần. Giới hạn còn lại (chấp nhận theo ruling): tên
            // sinh ra trùng với tên khác đã tồn tại sẵn ("X" + "X-2" cùng có từ trước) thì index
            // vẫn fail — hiếm; Task 13 thêm validation app-layer chặn trùng tên về sau.
            migrationBuilder.Sql("""
                WITH dup AS (
                    SELECT Id, ROW_NUMBER() OVER (PARTITION BY Name ORDER BY Id) AS rn
                    FROM Providers
                )
                UPDATE Providers
                SET Name = Name || '-' || rn
                FROM dup
                WHERE dup.Id = Providers.Id AND dup.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Providers_Name",
                table: "Providers",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Không hoàn tác dedupe Name đã ghi ở Up(): Down chỉ cần bỏ unique index; các tên
            // đã đổi là dữ liệu user-visible, đảo ngược có thể lại gây đụng độ và không có
            // thông tin gốc để khôi phục chính xác (hàng bị đổi tên không được đánh dấu).
            migrationBuilder.DropTable(
                name: "ModelCapabilities");

            migrationBuilder.DropTable(
                name: "ModelModalities");

            migrationBuilder.DropTable(
                name: "ModelReasoningEfforts");

            migrationBuilder.DropIndex(
                name: "IX_Providers_Name",
                table: "Providers");

            migrationBuilder.AlterColumn<string>(
                name: "ApiKeyEncrypted",
                table: "ProviderAccounts",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
