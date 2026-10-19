using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Capabilities;

/// <summary>
/// Schema fact capability (spec capability-model §6): 3 bảng ModelCapabilities /
/// ModelModalities / ModelReasoningEfforts — PK ghép, enum lưu TEXT chữ thường qua
/// CapabilityNames, FK ON DELETE CASCADE theo model. Kèm hai ràng buộc rẻ §6 trong
/// cùng migration: unique index trên Providers.Name, ApiKeyEncrypted bỏ NOT NULL.
/// </summary>
public class CapabilityFactSchemaTests : IDisposable
{
    private static readonly DateTimeOffset FixedAt = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    // ---------- round-trip qua DbContext ----------

    [Fact]
    public void Facts_AllThreeTables_InsertThenReadBack()
    {
        var factory = InitializeDb();
        var modelId = AddModel(factory);

        using (var db = factory.CreateDbContext())
        {
            AddFacts(db, modelId);
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            var cap = db.ModelCapabilities.AsNoTracking().Single(x => x.ModelId == modelId);
            Assert.Equal(CapabilityKind.Tools, cap.Capability);
            Assert.False(cap.KnownAbsent);
            Assert.Equal(CapabilitySource.User, cap.Source);
            Assert.Equal(FixedAt, cap.UpdatedAt);

            var mod = db.ModelModalities.AsNoTracking().Single(x => x.ModelId == modelId);
            Assert.Equal(ModalityDirection.Input, mod.Direction);
            Assert.Equal(ModalityKind.Image, mod.Modality);
            Assert.True(mod.KnownAbsent);
            Assert.Equal(CapabilitySource.Catalog, mod.Source);
            Assert.Equal(FixedAt, mod.UpdatedAt);

            var eff = db.ModelReasoningEfforts.AsNoTracking().Single(x => x.ModelId == modelId);
            Assert.Equal("high", eff.Level);
            Assert.Equal(CapabilitySource.Endpoint, eff.Source);
            Assert.Equal(FixedAt, eff.UpdatedAt);
        }
    }

    // ---------- PK ghép chặn trùng ----------

    // Source/UpdatedAt khác nhưng PK (ModelId, Capability) trùng → phải bị chặn.
    [Fact]
    public void DuplicateModelCapabilityRow_Rejected()
    {
        var factory = InitializeDb();
        var modelId = AddModel(factory);

        using (var db = factory.CreateDbContext())
        {
            db.ModelCapabilities.Add(new ModelCapability
            {
                ModelId = modelId,
                Capability = CapabilityKind.Tools,
                Source = CapabilitySource.User,
                UpdatedAt = FixedAt,
            });
            db.SaveChanges();
        }

        using var db2 = factory.CreateDbContext();
        db2.ModelCapabilities.Add(new ModelCapability
        {
            ModelId = modelId,
            Capability = CapabilityKind.Tools,
            Source = CapabilitySource.Endpoint,
            UpdatedAt = FixedAt,
        });
        Assert.ThrowsAny<DbUpdateException>(() => db2.SaveChanges());
    }

    // KnownAbsent khác (true vs false) nhưng PK (ModelId, Direction, Modality) trùng →
    // chặn: không được tồn tại song song dòng "có" và "đã biết là không" cho một fact.
    [Fact]
    public void DuplicateModelModalityRow_Rejected()
    {
        var factory = InitializeDb();
        var modelId = AddModel(factory);

        using (var db = factory.CreateDbContext())
        {
            db.ModelModalities.Add(new ModelModality
            {
                ModelId = modelId,
                Direction = ModalityDirection.Input,
                Modality = ModalityKind.Image,
                KnownAbsent = false,
                Source = CapabilitySource.User,
                UpdatedAt = FixedAt,
            });
            db.SaveChanges();
        }

        using var db2 = factory.CreateDbContext();
        db2.ModelModalities.Add(new ModelModality
        {
            ModelId = modelId,
            Direction = ModalityDirection.Input,
            Modality = ModalityKind.Image,
            KnownAbsent = true,
            Source = CapabilitySource.Catalog,
            UpdatedAt = FixedAt,
        });
        Assert.ThrowsAny<DbUpdateException>(() => db2.SaveChanges());
    }

    [Fact]
    public void DuplicateModelReasoningEffortRow_Rejected()
    {
        var factory = InitializeDb();
        var modelId = AddModel(factory);

        using (var db = factory.CreateDbContext())
        {
            db.ModelReasoningEfforts.Add(new ModelReasoningEffort
            {
                ModelId = modelId,
                Level = "high",
                Source = CapabilitySource.User,
                UpdatedAt = FixedAt,
            });
            db.SaveChanges();
        }

        using var db2 = factory.CreateDbContext();
        db2.ModelReasoningEfforts.Add(new ModelReasoningEffort
        {
            ModelId = modelId,
            Level = "high",
            Source = CapabilitySource.Catalog,
            UpdatedAt = FixedAt,
        });
        Assert.ThrowsAny<DbUpdateException>(() => db2.SaveChanges());
    }

    // ---------- cascade theo model ----------

    /// <summary>[RF-5] Xóa model → cả 3 bảng fact sạch (FK ON DELETE CASCADE ở mức DB,
    /// không phải delete tay từ navigation).</summary>
    [Fact]
    public void DeleteModel_ClearsAllThreeFactTables()
    {
        var factory = InitializeDb();
        var modelId = AddModel(factory);

        using (var db = factory.CreateDbContext())
        {
            AddFacts(db, modelId);
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            // Model nạp về không Include navigation → EF chỉ DELETE hàng Models;
            // sạch 3 bảng fact chứng minh DB thật sự có ON DELETE CASCADE.
            var model = db.Models.Single(m => m.Id == modelId);
            db.Models.Remove(model);
            db.SaveChanges();

            Assert.Empty(db.ModelCapabilities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
            Assert.Empty(db.ModelModalities.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
            Assert.Empty(db.ModelReasoningEfforts.AsNoTracking().Where(x => x.ModelId == modelId).ToList());
        }
    }

    // ---------- enum lưu TEXT chữ thường ----------

    [Fact]
    public void Facts_EnumColumnsStoreLowercaseText()
    {
        var factory = InitializeDb();
        var modelId = AddModel(factory);

        using var db = factory.CreateDbContext();
        AddFacts(db, modelId);
        db.SaveChanges();

        Assert.Equal("tools", RawScalar(db,
            $"SELECT Capability FROM ModelCapabilities WHERE ModelId = {modelId}"));
        Assert.Equal("input", RawScalar(db,
            $"SELECT Direction FROM ModelModalities WHERE ModelId = {modelId}"));
        Assert.Equal("image", RawScalar(db,
            $"SELECT Modality FROM ModelModalities WHERE ModelId = {modelId}"));
    }

    // ---------- từ vựng CapabilityNames ----------

    [Fact]
    public void FromDb_Tools_ReturnsCapabilityKindTools()
    {
        Assert.Equal(CapabilityKind.Tools, CapabilityNames.FromDb<CapabilityKind>("tools"));
    }

    [Theory]
    [InlineData(CapabilityKind.Tools, "tools")]
    [InlineData(CapabilityKind.Reasoning, "reasoning")]
    public void CapabilityVocabulary_RoundTrips(CapabilityKind kind, string token)
    {
        Assert.Equal(token, kind.ToDb());
        Assert.Equal(kind, CapabilityNames.FromDb<CapabilityKind>(token));
    }

    [Theory]
    [InlineData(ModalityKind.Text, "text")]
    [InlineData(ModalityKind.Image, "image")]
    [InlineData(ModalityKind.Audio, "audio")]
    [InlineData(ModalityKind.Video, "video")]
    public void ModalityVocabulary_RoundTrips(ModalityKind modality, string token)
    {
        Assert.Equal(token, modality.ToDb());
        Assert.Equal(modality, CapabilityNames.FromDb<ModalityKind>(token));
    }

    [Theory]
    [InlineData(ModalityDirection.Input, "input")]
    [InlineData(ModalityDirection.Output, "output")]
    public void DirectionVocabulary_RoundTrips(ModalityDirection direction, string token)
    {
        Assert.Equal(token, direction.ToDb());
        Assert.Equal(direction, CapabilityNames.FromDb<ModalityDirection>(token));
    }

    // Giá trị lạ (kể cả sai hoa hoặc tên capability cũ kiểu "vision") phải nổ rõ,
    // không âm thầm map về giá trị đầu enum.
    [Fact]
    public void FromDb_UnknownValue_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => CapabilityNames.FromDb<CapabilityKind>("TOOLS"));
        Assert.Throws<FormatException>(() => CapabilityNames.FromDb<ModalityKind>("vision"));
        Assert.Throws<FormatException>(() => CapabilityNames.FromDb<ModalityDirection>("inbound"));
    }

    [Fact]
    public void TryFromDb_UnknownValue_ReturnsFalse()
    {
        Assert.False(CapabilityNames.TryFromDb("vision", out ModalityKind modality));
        Assert.Equal(default, modality);
        Assert.True(CapabilityNames.TryFromDb("audio", out modality));
        Assert.Equal(ModalityKind.Audio, modality);
    }

    // ---------- hai ràng buộc rẻ kèm theo (spec §6, backlog B4/B5) ----------

    [Fact]
    public void DuplicateProviderName_Rejected()
    {
        var factory = InitializeDb();

        using var db = factory.CreateDbContext();
        db.Providers.AddRange(
            new Provider { Name = "Trùng tên", Type = ProviderType.OpenAI, BaseUrl = "https://x1.example" },
            new Provider { Name = "Trùng tên", Type = ProviderType.OpenAI, BaseUrl = "https://x2.example" });
        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void AccountWithNullApiKeyEncrypted_Allowed()
    {
        var factory = InitializeDb();
        long providerId;
        using (var db = factory.CreateDbContext())
        {
            providerId = FirstProviderId(db);
            db.ProviderAccounts.Add(new ProviderAccount
            {
                ProviderId = providerId,
                Name = "no-key",
                ApiKeyEncrypted = string.Empty, // sentinel hiện tại của app cho account không key
                CreatedAt = FixedAt,
                UpdatedAt = FixedAt,
            });
            db.SaveChanges();

            // NULL không đến từ EF (property non-nullable) mà từ DB cũ/luồng ngoài —
            // cột đã bỏ NOT NULL nên UPDATE này phải thành công.
            db.Database.ExecuteSqlRaw(
                "UPDATE ProviderAccounts SET ApiKeyEncrypted = NULL WHERE Name = 'no-key'");
        }

        using (var db = factory.CreateDbContext())
        {
            var account = db.ProviderAccounts.AsNoTracking()
                .Single(a => a.ProviderId == providerId && a.Name == "no-key");
            // §6: đọc bình thường, null được coi tương đương rỗng — mọi luồng key của
            // app đã check IsNullOrEmpty trước khi Unprotect.
            Assert.True(string.IsNullOrEmpty(account.ApiKeyEncrypted));
        }
    }

    // ---------- helpers ----------

    /// <summary>Migrate + seed preset qua chính DbInitializer (nguồn DB thật của app).</summary>
    private IDbContextFactory<RouterBalancingDbContext> InitializeDb()
    {
        var factory = _testDb.CreateFactory();
        DbInitializer.Initialize(factory);
        return factory;
    }

    private static long FirstProviderId(RouterBalancingDbContext db) =>
        db.Providers.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).First();

    private static long AddModel(IDbContextFactory<RouterBalancingDbContext> factory)
    {
        using var db = factory.CreateDbContext();
        var model = new Model
        {
            ProviderId = FirstProviderId(db),
            ModelId = "fact-model",
            CreatedAt = FixedAt,
        };
        db.Models.Add(model);
        db.SaveChanges();
        return model.Id;
    }

    private static void AddFacts(RouterBalancingDbContext db, long modelId)
    {
        db.ModelCapabilities.Add(new ModelCapability
        {
            ModelId = modelId,
            Capability = CapabilityKind.Tools,
            KnownAbsent = false,
            Source = CapabilitySource.User,
            UpdatedAt = FixedAt,
        });
        db.ModelModalities.Add(new ModelModality
        {
            ModelId = modelId,
            Direction = ModalityDirection.Input,
            Modality = ModalityKind.Image,
            KnownAbsent = true,
            Source = CapabilitySource.Catalog,
            UpdatedAt = FixedAt,
        });
        db.ModelReasoningEfforts.Add(new ModelReasoningEffort
        {
            ModelId = modelId,
            Level = "high",
            Source = CapabilitySource.Endpoint,
            UpdatedAt = FixedAt,
        });
    }

    private static string? RawScalar(RouterBalancingDbContext db, string sql) =>
        db.Database.SqlQueryRaw<string>(sql).ToList().SingleOrDefault();
}
