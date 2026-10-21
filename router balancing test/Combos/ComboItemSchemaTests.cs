using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Combos;

/// <summary>
/// Ràng buộc DB cho ComboItems (backlog C2): XOR target + unique (ComboId, Position),
/// kèm đường upgrade từ DB cũ có dữ liệu bẩn (app tự migrate lúc khởi động).
/// </summary>
public class ComboItemSchemaTests : IDisposable
{
    private const string PreviousMigration = "20261020120545_DropLegacyModelCapabilityColumns";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void ItemWithBothTargets_Rejected()
    {
        var factory = InitializeDb();
        var (modelId, comboId) = SeedModelAndCombo(factory, "both");

        using var db = factory.CreateDbContext();
        db.ComboItems.Add(new ComboItem
        {
            ComboId = comboId,
            Position = 0,
            TargetModelId = modelId,
            TargetComboId = comboId,
        });

        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void ItemWithNoTarget_Rejected()
    {
        var factory = InitializeDb();
        var (_, comboId) = SeedModelAndCombo(factory, "none");

        using var db = factory.CreateDbContext();
        db.ComboItems.Add(new ComboItem { ComboId = comboId, Position = 0 });

        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void DuplicatePositionWithinCombo_Rejected()
    {
        var factory = InitializeDb();
        var (modelId, comboId) = SeedModelAndCombo(factory, "dup-position");

        using (var db = factory.CreateDbContext())
        {
            db.ComboItems.Add(new ComboItem { ComboId = comboId, Position = 0, TargetModelId = modelId });
            db.SaveChanges();
        }

        using var db2 = factory.CreateDbContext();
        db2.ComboItems.Add(new ComboItem { ComboId = comboId, Position = 0, TargetModelId = modelId });
        Assert.ThrowsAny<DbUpdateException>(() => db2.SaveChanges());
    }

    [Fact]
    public void SamePositionAcrossDifferentCombos_Allowed()
    {
        var factory = InitializeDb();
        var (modelId, comboA) = SeedModelAndCombo(factory, "combo-a");
        var (_, comboB) = SeedModelAndCombo(factory, "combo-b");

        using var db = factory.CreateDbContext();
        db.ComboItems.AddRange(
            new ComboItem { ComboId = comboA, Position = 0, TargetModelId = modelId },
            new ComboItem { ComboId = comboB, Position = 0, TargetModelId = modelId });
        db.SaveChanges();

        Assert.Equal(2, db.ComboItems.Count());
    }

    /// <summary>
    /// DB cũ có thể chứa dòng do import/sửa tay: cả hai NULL (vô nghĩa), cả hai set,
    /// và trùng Position. Migration phải dọn trước khi tạo CHECK/unique index, nếu không
    /// app không khởi động được.
    /// </summary>
    [Fact]
    public void Migration_DirtyRows_NormalizedBeforeConstraints()
    {
        var factory = _testDb.CreateFactory();
        using var db = factory.CreateDbContext();
        db.Database.Migrate(PreviousMigration);

        var provider = new Provider
        {
            Name = "migration-provider",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            Models = { new Model { ModelId = "m1" } },
        };
        var comboA = new Combo { Name = "combo-a" };
        var comboB = new Combo { Name = "combo-b" };
        var comboC = new Combo { Name = "combo-c" };
        db.Providers.Add(provider);
        db.Combos.AddRange(comboA, comboB, comboC);
        db.SaveChanges();

        var modelId = provider.Models[0].Id;
        db.ComboItems.AddRange(
            new ComboItem { ComboId = comboA.Id, Position = 0, TargetModelId = modelId },
            new ComboItem { ComboId = comboA.Id, Position = 0, TargetModelId = modelId },
            new ComboItem { ComboId = comboB.Id, Position = 0 },
            new ComboItem
            {
                ComboId = comboC.Id,
                Position = 0,
                TargetModelId = modelId,
                TargetComboId = comboA.Id,
            });
        db.SaveChanges();

        db.Database.Migrate();

        var items = db.ComboItems.AsNoTracking().ToList();
        Assert.DoesNotContain(items, i => i.TargetModelId is null && i.TargetComboId is null);
        Assert.DoesNotContain(items, i => i.TargetModelId is not null && i.TargetComboId is not null);
        Assert.Equal(new[] { 0, 1 }, db.ComboItems.AsNoTracking()
            .Where(i => i.ComboId == comboA.Id).OrderBy(i => i.Position)
            .Select(i => i.Position).ToList());
        var kept = db.ComboItems.AsNoTracking().Single(i => i.ComboId == comboC.Id);
        Assert.Equal(modelId, kept.TargetModelId);
        Assert.Null(kept.TargetComboId);
    }

    private IDbContextFactory<RouterBalancingDbContext> InitializeDb()
    {
        var factory = _testDb.CreateFactory();
        DbInitializer.Initialize(factory);
        return factory;
    }

    private static (long ModelId, long ComboId) SeedModelAndCombo(
        IDbContextFactory<RouterBalancingDbContext> factory, string comboName)
    {
        using var db = factory.CreateDbContext();
        var provider = new Provider
        {
            Name = $"P-{comboName}",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            Models = { new Model { ModelId = "m1" } },
        };
        var combo = new Combo { Name = comboName };
        db.Providers.Add(provider);
        db.Combos.Add(combo);
        db.SaveChanges();
        return (provider.Models[0].Id, combo.Id);
    }
}
