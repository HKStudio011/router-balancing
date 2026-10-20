using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouterBalancing.Core.Storage.Migrations
{
    /// <summary>
    /// Dời 8 cột capability legacy trên <c>Models</c> sang ba bảng fact rồi bỏ cột.
    /// <para>
    /// Ngữ nghĩa dời dữ liệu (C-K/C-Q): bool <c>true</c> là phát biểu thuận; bool
    /// <c>false</c>/null của vision/think/audio/video không tạo dòng (false cũ = "không
    /// hoặc chưa rõ"). Riêng <c>SupportsTools=false</c> là ngoại lệ có chủ đích —
    /// code cũ ghi giá trị này từ catalog nên dời thành <c>KnownAbsent=1, Source=Catalog</c>.
    /// JSON hỏng/không phải array bị bỏ qua, không làm migration ném.
    /// </para>
    /// <para>
    /// <see cref="Down"/> chỉ tạo lại cột rỗng, không phục hồi dữ liệu: ba bảng fact đã là
    /// nguồn sự thật và migration không giữ bản sao dữ liệu legacy.
    /// </para>
    /// </summary>
    public partial class DropLegacyModelCapabilityColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill TRƯỚC mọi DropColumn. INSERT OR IGNORE để không đè fact đã có
            // (người dùng có thể đã chạy bản Task 1–10 trước khi migration này tới).
            migrationBuilder.Sql("""
                -- SupportsTools: code cũ ghi từ catalog; false = đã biết không có (C-Q).
                INSERT OR IGNORE INTO ModelCapabilities (ModelId, Capability, KnownAbsent, Source, UpdatedAt)
                SELECT Id, 'tools', CASE WHEN SupportsTools = 0 THEN 1 ELSE 0 END, 0,
                       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM Models
                WHERE SupportsTools IS NOT NULL;

                -- SupportsThink=true → capability reasoning; false/null không tạo dòng (C-K).
                INSERT OR IGNORE INTO ModelCapabilities (ModelId, Capability, KnownAbsent, Source, UpdatedAt)
                SELECT Id, 'reasoning', 0, 2,
                       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM Models
                WHERE SupportsThink = 1;

                -- Bool media cũ mô tả input (doc cột: "Model nhận input …") → modality input.
                INSERT OR IGNORE INTO ModelModalities (ModelId, Direction, Modality, KnownAbsent, Source, UpdatedAt)
                SELECT Id, 'input', 'image', 0, 2,
                       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM Models
                WHERE SupportsVision = 1;

                INSERT OR IGNORE INTO ModelModalities (ModelId, Direction, Modality, KnownAbsent, Source, UpdatedAt)
                SELECT Id, 'input', 'audio', 0, 2,
                       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM Models
                WHERE SupportsAudio = 1;

                INSERT OR IGNORE INTO ModelModalities (ModelId, Direction, Modality, KnownAbsent, Source, UpdatedAt)
                SELECT Id, 'input', 'video', 0, 2,
                       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM Models
                WHERE SupportsVideo = 1;

                -- JSON modality: chỉ token thuộc từ vựng, chỉ phần tử string của array.
                -- CASE bọc json_each để JSON hỏng/không phải array không bao giờ chạm json_each
                -- (tránh lỗi "malformed JSON" làm hỏng migration).
                INSERT OR IGNORE INTO ModelModalities (ModelId, Direction, Modality, KnownAbsent, Source, UpdatedAt)
                SELECT m.Id, 'input', je.value, 0,
                       CASE WHEN p.IsPreset = 1 THEN 0 ELSE 2 END,
                       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM Models m
                JOIN Providers p ON p.Id = m.ProviderId
                JOIN json_each(
                    CASE
                        WHEN m.InputModalities IS NULL THEN '[]'
                        WHEN json_valid(m.InputModalities) = 0 THEN '[]'
                        WHEN json_type(m.InputModalities) <> 'array' THEN '[]'
                        ELSE m.InputModalities
                    END
                ) AS je
                WHERE je.type = 'text'
                  AND je.value IN ('text', 'image', 'audio', 'video', 'file');

                INSERT OR IGNORE INTO ModelModalities (ModelId, Direction, Modality, KnownAbsent, Source, UpdatedAt)
                SELECT m.Id, 'output', je.value, 0,
                       CASE WHEN p.IsPreset = 1 THEN 0 ELSE 2 END,
                       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM Models m
                JOIN Providers p ON p.Id = m.ProviderId
                JOIN json_each(
                    CASE
                        WHEN m.OutputModalities IS NULL THEN '[]'
                        WHEN json_valid(m.OutputModalities) = 0 THEN '[]'
                        WHEN json_type(m.OutputModalities) <> 'array' THEN '[]'
                        ELSE m.OutputModalities
                    END
                ) AS je
                WHERE je.type = 'text'
                  AND je.value IN ('text', 'image', 'audio', 'video', 'file');

                -- ThinkEfforts: level tự do (kể cả "toggle"), chỉ nhận string không rỗng.
                INSERT OR IGNORE INTO ModelReasoningEfforts (ModelId, Level, Source, UpdatedAt)
                SELECT m.Id, je.value,
                       CASE WHEN p.IsPreset = 1 THEN 0 ELSE 2 END,
                       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM Models m
                JOIN Providers p ON p.Id = m.ProviderId
                JOIN json_each(
                    CASE
                        WHEN m.ThinkEfforts IS NULL THEN '[]'
                        WHEN json_valid(m.ThinkEfforts) = 0 THEN '[]'
                        WHEN json_type(m.ThinkEfforts) <> 'array' THEN '[]'
                        ELSE m.ThinkEfforts
                    END
                ) AS je
                WHERE je.type = 'text'
                  AND trim(je.value) <> '';
                """);

            migrationBuilder.DropColumn(
                name: "InputModalities",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "OutputModalities",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SupportsAudio",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SupportsThink",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SupportsTools",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SupportsVideo",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SupportsVision",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ThinkEfforts",
                table: "Models");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputModalities",
                table: "Models",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputModalities",
                table: "Models",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsAudio",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsThink",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsTools",
                table: "Models",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsVideo",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsVision",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ThinkEfforts",
                table: "Models",
                type: "TEXT",
                nullable: true);
        }
    }
}
