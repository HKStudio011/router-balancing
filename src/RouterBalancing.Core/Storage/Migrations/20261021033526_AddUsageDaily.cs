using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <summary>
    /// Thay counter ngày nằm trong bảng cấu hình bằng sổ <c>UsageDaily</c> (backlog C3):
    /// tạo bảng, backfill counter client key/provider account còn giá trị, rồi bỏ các cột
    /// <c>RequestsUsed/TokensUsed/UsageDate</c>. ProviderAccounts hiện chưa từng ghi counter
    /// nhưng vẫn backfill để DB cũ không mất dữ liệu nếu có.
    /// <para>
    /// Một chiều: <see cref="Down"/> tạo lại cột rỗng và xóa bảng, không dựng lại counter
    /// từ UsageDaily (cùng lý do với migration chuẩn hoá dữ liệu trước).
    /// </para>
    /// </summary>
    public partial class AddUsageDaily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsageDaily",
                columns: table => new
                {
                    SubjectKind = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<long>(type: "INTEGER", nullable: false),
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    Requests = table.Column<long>(type: "INTEGER", nullable: false),
                    Tokens = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageDaily", x => new { x.SubjectKind, x.SubjectId, x.Date });
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageDaily_SubjectKind_Date",
                table: "UsageDaily",
                columns: new[] { "SubjectKind", "Date" });

            // Backfill TRƯỚC khi DropColumn. date(UsageDate) chuẩn hoá về yyyy-MM-dd và
            // bỏ qua giá trị hỏng thay vì đưa dòng không đọc được vào DateOnly.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO UsageDaily (SubjectKind, SubjectId, Date, Requests, Tokens)
                SELECT 'client_key', Id, date(UsageDate), RequestsUsed, TokensUsed
                FROM ClientKeys
                WHERE date(UsageDate) IS NOT NULL
                  AND (RequestsUsed <> 0 OR TokensUsed <> 0);

                INSERT OR IGNORE INTO UsageDaily (SubjectKind, SubjectId, Date, Requests, Tokens)
                SELECT 'provider_account', Id, date(UsageDate), RequestsUsed, TokensUsed
                FROM ProviderAccounts
                WHERE date(UsageDate) IS NOT NULL
                  AND (RequestsUsed <> 0 OR TokensUsed <> 0);
                """);

            migrationBuilder.DropColumn(
                name: "RequestsUsed",
                table: "ProviderAccounts");

            migrationBuilder.DropColumn(
                name: "TokensUsed",
                table: "ProviderAccounts");

            migrationBuilder.DropColumn(
                name: "UsageDate",
                table: "ProviderAccounts");

            migrationBuilder.DropColumn(
                name: "RequestsUsed",
                table: "ClientKeys");

            migrationBuilder.DropColumn(
                name: "TokensUsed",
                table: "ClientKeys");

            migrationBuilder.DropColumn(
                name: "UsageDate",
                table: "ClientKeys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Một chiều: usage đã chuyển vào UsageDaily; không khôi phục counter từ đó.
            migrationBuilder.DropTable(
                name: "UsageDaily");

            migrationBuilder.AddColumn<long>(
                name: "RequestsUsed",
                table: "ProviderAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TokensUsed",
                table: "ProviderAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateOnly>(
                name: "UsageDate",
                table: "ProviderAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RequestsUsed",
                table: "ClientKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TokensUsed",
                table: "ClientKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "UsageDate",
                table: "ClientKeys",
                type: "TEXT",
                nullable: true);
        }
    }
}
