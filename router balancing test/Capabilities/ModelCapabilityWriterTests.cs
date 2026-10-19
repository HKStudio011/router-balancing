using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Capabilities;

/// <summary>
/// ModelCapabilityWriter — luật ghi theo nguồn (spec capability-model §5):
/// (a) nguồn ngoài User chỉ ghi khi dòng chưa tồn tại hoặc đang mang đúng nguồn đó;
/// (b) KnownAbsent=1 chỉ từ nguồn full-list; (c) ApplyUserEditAsync thay mọi nguồn
/// bằng dòng User / ClearKeys xóa về "chưa rõ"; (d) mọi ghi đóng dấu UpdatedAt.
/// Kèm chuẩn hoá level theo ruling R-T1-4.
/// </summary>
public class ModelCapabilityWriterTests : IDisposable
{
    private static readonly DateTimeOffset OldStamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ModelCapabilityWriter _writer;

    public ModelCapabilityWriterTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _writer = new ModelCapabilityWriter(_db, new NullLog());
    }

    public void Dispose() => _testDb.Dispose();

    // ---------- (a) nguồn ngoài User không đè dòng của nguồn khác ----------

    [Fact]
    public async Task ApplyAsync_CatalogFact_WhenEmpty_WritesRow()
    {
        var modelId = await AddModelAsync("catalog-first");
        var before = DateTimeOffset.UtcNow;

        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog,
            Patch(capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = true }),
            sourceIsCompleteList: false);

        using var db = _db.CreateDbContext();
        var row = Assert.Single(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.Equal(CapabilityKind.Tools, row.Capability);
        Assert.False(row.KnownAbsent);
        Assert.Equal(CapabilitySource.Catalog, row.Source);
        // Luật (d): UpdatedAt do writer đóng dấu tại thời điểm ghi.
        Assert.True(row.UpdatedAt >= before, $"UpdatedAt {row.UpdatedAt:O} sớm hơn thời điểm ghi {before:O}.");
    }

    /// <summary>[RF-2] User đặt tay trước → catalog không được đè lên (C-N).</summary>
    [Fact]
    public async Task ApplyAsync_Catalog_WhenUserRowExists_DoesNotOverwrite()
    {
        var modelId = await AddModelAsync("user-first");
        await SeedCapabilityAsync(modelId, CapabilityKind.Tools,
            knownAbsent: false, CapabilitySource.User, OldStamp);

        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog,
            Patch(capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = false }),
            sourceIsCompleteList: true);

        using var db = _db.CreateDbContext();
        var row = Assert.Single(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.Equal(CapabilitySource.User, row.Source);
        Assert.False(row.KnownAbsent);
        Assert.Equal(OldStamp, row.UpdatedAt); // dòng của User không bị đụng kể cả khi catalog full-list nói khác
    }

    [Fact]
    public async Task ApplyAsync_SameSource_RewritesItsOwnRow()
    {
        var modelId = await AddModelAsync("self-rewrite");
        await SeedCapabilityAsync(modelId, CapabilityKind.Reasoning,
            knownAbsent: false, CapabilitySource.Catalog, OldStamp);
        await SeedModalityAsync(modelId, ModalityDirection.Input, ModalityKind.Image,
            knownAbsent: false, CapabilitySource.Catalog, OldStamp);
        await SeedEffortAsync(modelId, "high", CapabilitySource.Catalog, OldStamp);

        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog,
            Patch(
                capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Reasoning] = false },
                modalities: new Dictionary<(ModalityDirection Direction, ModalityKind Modality), bool>
                {
                    [(ModalityDirection.Input, ModalityKind.Image)] = false,
                },
                efforts: new HashSet<string> { "high" }),
            sourceIsCompleteList: true);

        using var db = _db.CreateDbContext();
        var cap = Assert.Single(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.True(cap.KnownAbsent);
        Assert.Equal(CapabilitySource.Catalog, cap.Source);
        Assert.True(cap.UpdatedAt > OldStamp, "luật (d): same-source rewrite phải đóng dấu UpdatedAt mới.");

        var mod = Assert.Single(db.ModelModalities.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.True(mod.KnownAbsent);
        Assert.True(mod.UpdatedAt > OldStamp);

        var eff = Assert.Single(db.ModelReasoningEfforts.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.Equal("high", eff.Level);
        Assert.True(eff.UpdatedAt > OldStamp);
    }

    // ---------- (b) KnownAbsent chỉ từ full-list ----------

    /// <summary>[RF-1] Probe endpoint (không phải full-list) không được sinh dòng KnownAbsent (C-P).</summary>
    [Fact]
    public async Task ApplyAsync_IncompleteSource_SkipsNegativeFacts()
    {
        var modelId = await AddModelAsync("endpoint-partial");

        await _writer.ApplyAsync(modelId, CapabilitySource.Endpoint,
            Patch(
                capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = false },
                modalities: new Dictionary<(ModalityDirection Direction, ModalityKind Modality), bool>
                {
                    [(ModalityDirection.Input, ModalityKind.Video)] = false,
                }),
            sourceIsCompleteList: false);

        using var db = _db.CreateDbContext();
        Assert.Empty(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        Assert.Empty(db.ModelModalities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
    }

    [Fact]
    public async Task ApplyAsync_CompleteSource_WritesKnownAbsent()
    {
        var modelId = await AddModelAsync("full-list");

        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog,
            Patch(
                capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = false },
                modalities: new Dictionary<(ModalityDirection Direction, ModalityKind Modality), bool>
                {
                    [(ModalityDirection.Input, ModalityKind.Image)] = false,
                }),
            sourceIsCompleteList: true);

        using var db = _db.CreateDbContext();
        var cap = Assert.Single(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.True(cap.KnownAbsent);
        Assert.Equal(CapabilitySource.Catalog, cap.Source);

        var mod = Assert.Single(db.ModelModalities.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.True(mod.KnownAbsent);
        Assert.Equal(ModalityDirection.Input, mod.Direction);
        Assert.Equal(ModalityKind.Image, mod.Modality);
    }

    // ---------- (c) user edit: thay mọi nguồn bằng dòng User, hoặc xóa về "chưa rõ" ----------

    [Fact]
    public async Task ApplyUserEditAsync_ReplacesAnySourceWithUserRow()
    {
        var modelId = await AddModelAsync("user-wins");
        await SeedCapabilityAsync(modelId, CapabilityKind.Tools,
            knownAbsent: true, CapabilitySource.Catalog, OldStamp);
        await SeedModalityAsync(modelId, ModalityDirection.Input, ModalityKind.Image,
            knownAbsent: true, CapabilitySource.Endpoint, OldStamp);
        await SeedEffortAsync(modelId, "high", CapabilitySource.Endpoint, OldStamp);
        // Key không có trong patch → giữ nguyên: user chỉ phát biểu về key mình nêu.
        await SeedCapabilityAsync(modelId, CapabilityKind.Reasoning,
            knownAbsent: false, CapabilitySource.Catalog, OldStamp);

        await _writer.ApplyUserEditAsync(modelId,
            Patch(
                capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = true },
                modalities: new Dictionary<(ModalityDirection Direction, ModalityKind Modality), bool>
                {
                    [(ModalityDirection.Input, ModalityKind.Image)] = true,
                },
                efforts: new HashSet<string> { "high" }));

        using var db = _db.CreateDbContext();
        var cap = Assert.Single(db.ModelCapabilities.AsNoTracking()
            .Where(x => x.ModelId == modelId && x.Capability == CapabilityKind.Tools));
        Assert.False(cap.KnownAbsent);
        Assert.Equal(CapabilitySource.User, cap.Source);
        Assert.True(cap.UpdatedAt > OldStamp);

        var mod = Assert.Single(db.ModelModalities.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.False(mod.KnownAbsent);
        Assert.Equal(CapabilitySource.User, mod.Source);

        var eff = Assert.Single(db.ModelReasoningEfforts.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.Equal(CapabilitySource.User, eff.Source);

        var untouched = Assert.Single(db.ModelCapabilities.AsNoTracking()
            .Where(x => x.ModelId == modelId && x.Capability == CapabilityKind.Reasoning));
        Assert.Equal(CapabilitySource.Catalog, untouched.Source);
        Assert.Equal(OldStamp, untouched.UpdatedAt);
    }

    /// <summary>
    /// "User nói chưa rõ" = ClearKeys: xóa dòng bất kể nguồn, không ghi dòng mới
    /// (model trở về trạng thái chưa biết fact đó).
    /// </summary>
    [Fact]
    public async Task ApplyUserEditAsync_ClearsFactWhenUserSaysUnknown()
    {
        var modelId = await AddModelAsync("user-clears");
        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog,
            Patch(
                capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = true },
                modalities: new Dictionary<(ModalityDirection Direction, ModalityKind Modality), bool>
                {
                    [(ModalityDirection.Input, ModalityKind.Image)] = true,
                },
                efforts: new HashSet<string> { "high" }),
            sourceIsCompleteList: true);

        var clearKeys = new HashSet<(string Kind, string Key)>
        {
            CapabilityPatch.CapabilityClearKey(CapabilityKind.Tools),
            CapabilityPatch.ModalityClearKey(ModalityDirection.Input, ModalityKind.Image),
            CapabilityPatch.EffortClearKey("High"), // R-T1-4: clear cũng phải chuẩn hoá level
        };
        await _writer.ApplyUserEditAsync(modelId, Patch(clearKeys: clearKeys));

        using var db = _db.CreateDbContext();
        Assert.Empty(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        Assert.Empty(db.ModelModalities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        Assert.Empty(db.ModelReasoningEfforts.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
    }

    // ---------- ClearSource / ClearAll: đúng phạm vi ----------

    [Fact]
    public async Task ClearSourceAsync_RemovesOnlyRowsOfThatSource()
    {
        var modelId = await AddModelAsync("clear-source");
        await SeedCapabilityAsync(modelId, CapabilityKind.Tools,
            knownAbsent: false, CapabilitySource.Catalog, OldStamp);
        await SeedEffortAsync(modelId, "high", CapabilitySource.Catalog, OldStamp);
        await SeedCapabilityAsync(modelId, CapabilityKind.Reasoning,
            knownAbsent: false, CapabilitySource.User, OldStamp);
        var otherModelId = await AddModelAsync("clear-source-other");
        await SeedCapabilityAsync(otherModelId, CapabilityKind.Tools,
            knownAbsent: false, CapabilitySource.Catalog, OldStamp);

        await _writer.ClearSourceAsync(modelId, CapabilitySource.Catalog);

        using var db = _db.CreateDbContext();
        Assert.Empty(db.ModelReasoningEfforts.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        var remaining = db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId).ToList();
        var userRow = Assert.Single(remaining);
        Assert.Equal(CapabilitySource.User, userRow.Source);
        // model khác cùng nguồn không bị liên đới
        Assert.Single(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == otherModelId).ToList());
    }

    [Fact]
    public async Task ClearAllAsync_RemovesEveryFactOfModel()
    {
        var modelId = await AddModelAsync("clear-all");
        await SeedCapabilityAsync(modelId, CapabilityKind.Tools,
            knownAbsent: false, CapabilitySource.Catalog, OldStamp);
        await SeedModalityAsync(modelId, ModalityDirection.Input, ModalityKind.Image,
            knownAbsent: false, CapabilitySource.User, OldStamp);
        await SeedEffortAsync(modelId, "high", CapabilitySource.Endpoint, OldStamp);
        var otherModelId = await AddModelAsync("clear-all-other");
        await SeedCapabilityAsync(otherModelId, CapabilityKind.Tools,
            knownAbsent: false, CapabilitySource.Catalog, OldStamp);

        await _writer.ClearAllAsync(modelId);

        using var db = _db.CreateDbContext();
        Assert.Empty(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        Assert.Empty(db.ModelModalities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        Assert.Empty(db.ModelReasoningEfforts.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        Assert.Single(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == otherModelId).ToList());
    }

    // ---------- [RF-5] cascade theo model ----------

    [Fact]
    public async Task ModelDeleted_FactsGone()
    {
        var modelId = await AddModelAsync("cascade-model");
        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog,
            Patch(
                capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = true },
                modalities: new Dictionary<(ModalityDirection Direction, ModalityKind Modality), bool>
                {
                    [(ModalityDirection.Input, ModalityKind.Image)] = true,
                },
                efforts: new HashSet<string> { "high" }),
            sourceIsCompleteList: true);
        await _writer.ApplyUserEditAsync(modelId,
            Patch(capabilities: new Dictionary<CapabilityKind, bool> { [CapabilityKind.Reasoning] = false }));

        using (var db = _db.CreateDbContext())
        {
            // Model nạp về không Include navigation → EF chỉ DELETE hàng Models;
            // 3 bảng sạch chứng minh FK ON DELETE CASCADE ở mức DB thật sự chạy.
            var model = db.Models.Single(m => m.Id == modelId);
            db.Models.Remove(model);
            await db.SaveChangesAsync();
        }

        using var db2 = _db.CreateDbContext();
        Assert.Empty(db2.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        Assert.Empty(db2.ModelModalities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        Assert.Empty(db2.ModelReasoningEfforts.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
    }

    // ---------- chuẩn hoá level (ruling R-T1-4) ----------

    /// <summary>
    /// PK Level là TEXT và SQLite phân biệt hoa/thường — "High"/"high" sẽ thành 2 dòng
    /// nếu writer không chuẩn hoá. Cả hai lần ghi phải về đúng một dòng "high".
    /// </summary>
    [Fact]
    public async Task ApplyAsync_LevelCaseVariants_WritesOneRow()
    {
        var modelId = await AddModelAsync("case-level");

        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog,
            Patch(efforts: new HashSet<string> { "High" }), sourceIsCompleteList: false);
        // Nguồn khác cố ghi biến thể "  HIGH " → bị luật (a) chặn, nhưng biến thể vẫn
        // phải chuẩn hóa về đúng PK của dòng Catalog (không tạo dòng thứ hai).
        await _writer.ApplyAsync(modelId, CapabilitySource.Endpoint,
            Patch(efforts: new HashSet<string> { "  HIGH " }), sourceIsCompleteList: false);

        using var db = _db.CreateDbContext();
        var row = Assert.Single(db.ModelReasoningEfforts.AsNoTracking().Where(x => x.ModelId == modelId));
        Assert.Equal("high", row.Level);
        Assert.Equal(CapabilitySource.Catalog, row.Source);
    }

    // ---------- helpers ----------

    private async Task<long> AddModelAsync(string publicModelId)
    {
        using var db = _db.CreateDbContext();
        var providerId = db.Providers.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).First();
        var model = new Model { ProviderId = providerId, ModelId = publicModelId, CreatedAt = OldStamp };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private async Task SeedCapabilityAsync(long modelId, CapabilityKind kind, bool knownAbsent,
        CapabilitySource source, DateTimeOffset updatedAt)
    {
        using var db = _db.CreateDbContext();
        db.ModelCapabilities.Add(new ModelCapability
        {
            ModelId = modelId,
            Capability = kind,
            KnownAbsent = knownAbsent,
            Source = source,
            UpdatedAt = updatedAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedModalityAsync(long modelId, ModalityDirection direction, ModalityKind modality,
        bool knownAbsent, CapabilitySource source, DateTimeOffset updatedAt)
    {
        using var db = _db.CreateDbContext();
        db.ModelModalities.Add(new ModelModality
        {
            ModelId = modelId,
            Direction = direction,
            Modality = modality,
            KnownAbsent = knownAbsent,
            Source = source,
            UpdatedAt = updatedAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedEffortAsync(long modelId, string level,
        CapabilitySource source, DateTimeOffset updatedAt)
    {
        using var db = _db.CreateDbContext();
        db.ModelReasoningEfforts.Add(new ModelReasoningEffort
        {
            ModelId = modelId,
            Level = level,
            Source = source,
            UpdatedAt = updatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static CapabilityPatch Patch(
        IReadOnlyDictionary<CapabilityKind, bool>? capabilities = null,
        IReadOnlyDictionary<(ModalityDirection Direction, ModalityKind Modality), bool>? modalities = null,
        IReadOnlySet<string>? efforts = null,
        IReadOnlySet<(string Kind, string Key)>? clearKeys = null)
    {
        var patch = new CapabilityPatch(
            capabilities ?? new Dictionary<CapabilityKind, bool>(),
            modalities ?? new Dictionary<(ModalityDirection, ModalityKind), bool>(),
            efforts ?? new HashSet<string>());
        return clearKeys is null ? patch : patch with { ClearKeys = clearKeys };
    }
}
