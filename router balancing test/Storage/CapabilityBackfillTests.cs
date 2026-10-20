using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Task 11 — migration <c>DropLegacyModelCapabilityColumns</c> phải dời dữ liệu cũ sang
/// ba bảng fact TRƯỚC khi bỏ 8 cột. Test pin DB ở schema cuối trước bảng fact
/// (<c>20261019064905_AddModelToolsOutputLimit</c>), seed dữ liệu legacy bằng SQL thô,
/// rồi Migrate() lên latest và kiểm chứng qua reader — đúng đường đọc của app.
/// </summary>
public class CapabilityBackfillTests : IDisposable
{
    // Schema cuối trước Task 1 (chưa có bảng fact, còn đủ 8 cột legacy).
    private const string PreCapabilitySchemaId = "20261019064905_AddModelToolsOutputLimit";
    private const string Timestamp = "'2026-10-01T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task Migrate_BackfillsLegacyColumnsBeforeDroppingThem()
    {
        using (var db = _testDb.CreateFactory().CreateDbContext())
        {
            db.GetService<IMigrator>().Migrate(PreCapabilitySchemaId);
            db.Database.ExecuteSqlRaw($"""
                INSERT INTO Providers (Id, Name, Type, BaseUrl, Enabled, MaxConcurrent, IsPreset, CreatedAt, UpdatedAt)
                VALUES
                    (1, 'User provider', 0, 'https://user.test', 1, 4, 0, {Timestamp}, {Timestamp}),
                    (2, 'Preset provider', 0, 'https://preset.test', 1, 4, 1, {Timestamp}, {Timestamp});

                INSERT INTO Models
                    (Id, ProviderId, ModelId, Enabled, IsManual, ContextWindow, MaxOutputTokens,
                     SupportsVision, SupportsThink, SupportsTools, SupportsAudio, SupportsVideo,
                     ThinkEfforts, InputModalities, OutputModalities, CreatedAt)
                VALUES
                    -- (1) Đủ loại dữ liệu legacy: bool true, tools KnownAbsent, JSON hợp lệ.
                    (1, 1, 'legacy-mixed', 1, 0, 128000, 8192,
                     1, 1, 0, 1, 0,
                     '["low","high"]', '["text","image"]', '["text","audio"]', {Timestamp}),
                    -- (2) Mọi bool false/null + context âm: C-K không được tạo dòng nào.
                    (2, 1, 'legacy-none', 1, 0, -5, NULL,
                     0, 0, NULL, 0, 0,
                     NULL, NULL, NULL, {Timestamp}),
                    -- (3) JSON hỏng / không phải array: migration phải bỏ qua, không ném.
                    (3, 1, 'legacy-garbage', 1, 0, -5, NULL,
                     0, 0, NULL, 0, 0,
                     'not-json', 'garbage', '"scalar"', {Timestamp}),
                    -- (4) JSON của provider preset: nguồn Catalog; token lạ/không phải string bị lọc.
                    (4, 2, 'preset-json', 1, 0, 64000, NULL,
                     0, 0, NULL, 0, 0,
                     '["minimal","high",3]', '["text","audio","file","bogus",null]', '["text"]', {Timestamp});
                """);
        }

        // Migrate() lên latest: AddModelCapabilityFacts rồi DropLegacyModelCapabilityColumns.
        using (var db = _testDb.CreateFactory().CreateDbContext())
        {
            db.Database.Migrate();
        }

        var reader = new ModelCapabilityReader(_testDb.CreateFactory());

        var mixed = await reader.ReadAsync(1);
        Assert.True(mixed.Vision);
        Assert.True(mixed.AudioInput);
        Assert.True(mixed.Reasoning);
        Assert.False(mixed.Tools);
        Assert.Equal(new[] { "high", "low" }, mixed.Efforts);
        Assert.Equal(CapabilitySource.User, mixed.SourceOf(ModalityDirection.Input, ModalityKind.Image));
        Assert.Equal(CapabilitySource.User, mixed.SourceOf(ModalityDirection.Input, ModalityKind.Audio));
        Assert.Equal(CapabilitySource.User, mixed.SourceOf(CapabilityKind.Reasoning));
        Assert.Equal(CapabilitySource.Catalog, mixed.SourceOf(CapabilityKind.Tools));
        Assert.Equal(new[] { "text", "image", "audio" }, mixed.InputModalities);
        Assert.Equal(new[] { "text", "audio" }, mixed.OutputModalities);

        // C-K: false/null cũ không được bịa thành KnownAbsent.
        var none = await reader.ReadAsync(2);
        Assert.True(none.IsEmpty);

        // JSON hỏng + context âm: bỏ qua, không sinh dòng, migration không ném.
        var garbage = await reader.ReadAsync(3);
        Assert.True(garbage.IsEmpty);

        var preset = await reader.ReadAsync(4);
        Assert.True(preset.Supports(ModalityDirection.Input, ModalityKind.Text));
        Assert.Null(preset.Vision);
        Assert.True(preset.Supports(ModalityDirection.Input, ModalityKind.Audio));
        Assert.True(preset.Supports(ModalityDirection.Input, ModalityKind.File));
        Assert.Null(preset.Supports(ModalityDirection.Input, ModalityKind.Image));
        Assert.Equal(new[] { "high", "minimal" }, preset.Efforts);
        Assert.Equal(CapabilitySource.Catalog, preset.SourceOf(ModalityDirection.Input, ModalityKind.Text));
        Assert.Equal(CapabilitySource.Catalog, preset.SourceOf(ModalityDirection.Input, ModalityKind.File));

        using (var check = _testDb.CreateFactory().CreateDbContext())
        {
            var toolsRow = await check.ModelCapabilities.AsNoTracking()
                .SingleAsync(c => c.ModelId == 1 && c.Capability == CapabilityKind.Tools);
            Assert.True(toolsRow.KnownAbsent);
            Assert.Equal(CapabilitySource.Catalog, toolsRow.Source);

            Assert.Equal(
                new[] { CapabilitySource.Catalog, CapabilitySource.Catalog },
                await check.ModelReasoningEfforts.AsNoTracking()
                    .Where(r => r.ModelId == 4)
                    .OrderBy(r => r.Level)
                    .Select(r => r.Source)
                    .ToListAsync());
        }
    }
}
