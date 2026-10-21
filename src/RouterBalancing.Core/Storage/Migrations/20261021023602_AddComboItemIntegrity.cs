using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <summary>
    /// Ràng buộc toàn vẹn cho <c>ComboItems</c> (backlog C2): đúng một target và
    /// <c>Position</c> duy nhất trong combo. Dữ liệu bẩn được chuẩn hoá trước khi
    /// tạo ràng buộc — app tự migrate lúc khởi động nên DB cũ không được chặn app.
    /// <para>
    /// Chuẩn hoá là một chiều: <see cref="Down"/> chỉ bỏ CHECK/unique index, không
    /// phục hồi dòng đã xoá hay target đã bỏ (cùng lý do với migration dời capability).
    /// </para>
    /// </summary>
    public partial class AddComboItemIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Dòng không target: không mang nghĩa (service luôn từ chối) → xoá.
            // 2) Cả hai target: giữ model, bỏ combo (combo lồng là shape thêm sau,
            //    và model không thể tạo chu trình).
            // 3) Trùng Position: đánh số lại 0..n-1 theo (Position, Id) — giữ thứ tự Fallback.
            migrationBuilder.Sql("""
                DELETE FROM ComboItems
                WHERE TargetModelId IS NULL AND TargetComboId IS NULL;

                UPDATE ComboItems
                SET TargetComboId = NULL
                WHERE TargetModelId IS NOT NULL AND TargetComboId IS NOT NULL;

                CREATE TEMP TABLE _combo_item_positions AS
                SELECT Id,
                       ROW_NUMBER() OVER (PARTITION BY ComboId ORDER BY Position, Id) - 1 AS NewPosition
                FROM ComboItems;

                UPDATE ComboItems
                SET Position = (SELECT NewPosition FROM _combo_item_positions WHERE Id = ComboItems.Id);

                DROP TABLE _combo_item_positions;
                """);

            migrationBuilder.DropIndex(
                name: "IX_ComboItems_ComboId_Position",
                table: "ComboItems");

            migrationBuilder.CreateIndex(
                name: "IX_ComboItems_ComboId_Position",
                table: "ComboItems",
                columns: new[] { "ComboId", "Position" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ComboItems_ExactlyOneTarget",
                table: "ComboItems",
                sql: "(TargetModelId IS NULL) <> (TargetComboId IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComboItems_ComboId_Position",
                table: "ComboItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ComboItems_ExactlyOneTarget",
                table: "ComboItems");

            migrationBuilder.CreateIndex(
                name: "IX_ComboItems_ComboId_Position",
                table: "ComboItems",
                columns: new[] { "ComboId", "Position" });
        }
    }
}
