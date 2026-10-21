using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <summary>
    /// Chuẩn hoá header provider từ cột JSON <c>Providers.ExtraHeaders</c> sang bảng
    /// <c>ProviderHeaders</c> (backlog C5): PK ghép <c>(ProviderId, Name)</c> với
    /// collation <c>NOCASE</c> để unique không phân biệt hoa thường như HTTP.
    /// <para>
    /// Backfill chỉ nhận JSON object hợp lệ; value không phải string (number/bool/null)
    /// và key rỗng bị bỏ qua. <c>INSERT OR IGNORE</c> xử lý key trùng hoa thường —
    /// dòng đầu theo thứ tự JSON thắng, không làm migration nổ.
    /// </para>
    /// <para>
    /// Một chiều: <see cref="Down"/> tạo lại cột rỗng rồi xoá bảng, không dựng lại JSON
    /// (cùng lý do với migration chuẩn hoá dữ liệu trước).
    /// </para>
    /// </summary>
    public partial class AddProviderHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderHeaders",
                columns: table => new
                {
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderHeaders", x => new { x.ProviderId, x.Name });
                    table.ForeignKey(
                        name: "FK_ProviderHeaders_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Backfill TRƯỚC khi DropColumn — DB cũ có header vẫn giữ nguyên hành vi gửi request.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO ProviderHeaders (ProviderId, Name, Value)
                SELECT p.Id, trim(je.key), je.value
                FROM Providers p
                JOIN json_each(
                    CASE
                        WHEN p.ExtraHeaders IS NULL
                             OR json_valid(p.ExtraHeaders) = 0
                             OR json_type(p.ExtraHeaders) <> 'object' THEN '{}'
                        ELSE p.ExtraHeaders
                    END) AS je
                WHERE je.type = 'text'
                  AND length(trim(je.key)) > 0;
                """);

            migrationBuilder.DropColumn(
                name: "ExtraHeaders",
                table: "Providers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderHeaders");

            migrationBuilder.AddColumn<string>(
                name: "ExtraHeaders",
                table: "Providers",
                type: "TEXT",
                nullable: true);
        }
    }
}
