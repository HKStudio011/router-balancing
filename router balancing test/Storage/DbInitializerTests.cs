using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

public class DbInitializerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Initialize_OnEmptyDatabase_CreatesAllTables()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        var tables = db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table'")
            .ToList();
        Assert.Contains("Providers", tables);
        Assert.Contains("ProviderAccounts", tables);
        Assert.Contains("Models", tables);
        Assert.Contains("Combos", tables);
        Assert.Contains("ComboItems", tables);
        Assert.Contains("LogEntries", tables);
        Assert.Contains("AppSettings", tables);
    }

    [Fact]
    public void Initialize_RunTwice_IsIdempotent()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);
        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        var applied = db.Database
            .SqlQueryRaw<string>("SELECT MigrationId FROM __EFMigrationsHistory")
            .ToList();
        // Idempotent = mỗi migration định nghĩa apply đúng 1 lần sau 2 lần Initialize —
        // không hardcode số migration (thêm migration mới không được phá test này).
        var defined = db.Database.GetMigrations().ToList();
        Assert.Equal(defined.Count, applied.Count);
        Assert.Equal(
            defined.OrderBy(m => m, StringComparer.Ordinal),
            applied.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void Initialize_OnEmptyDatabase_EnablesWalJournalMode()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);

        // WAL là bắt buộc: writer song song với reader trong pipeline (LogService ghi
        // trong request) chỉ không kẹt ở WAL. Migrator chỉ chạy PRAGMA journal_mode='wal'
        // khi DatabaseCreator.Exists() = false — thao tác DB nào tạo file TRƯỚC Migrate
        // sẽ bỏ lỡ Create() và âm thầm rơi về rollback-journal.
        using var db = factory.CreateDbContext();
        var mode = db.Database
            .SqlQueryRaw<string>("PRAGMA journal_mode")
            .ToList();
        Assert.Equal("wal", mode.Single());
    }

    [Fact]
    public async Task Initialize_WhenStaleMigrationLockLeftBehind_ClearsItAndCompletes()
    {
        var factory = _db.CreateFactory();
        // Lần đầu để EF tạo schema + bảng __EFMigrationsLock
        DbInitializer.Initialize(factory);

        using (var db = factory.CreateDbContext())
        {
            // Mô phỏng process bị kill giữa Migrate: hàng lock Id=1 kẹt lại, không ai DELETE
            db.Database.ExecuteSqlRaw(
                "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") " +
                "VALUES (1, '2026-10-10 07:56:29.8728158+00:00')");
        }

        // Không có bước dọn lock: AcquireDatabaseLock retry INSERT OR IGNORE vô hạn →
        // treo. Wait có giới hạn để fail rõ ràng thay vì treo cả suite.
        var initialize = Task.Run(() => DbInitializer.Initialize(factory));
        var finished = await Task.WhenAny(initialize, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(
            ReferenceEquals(finished, initialize),
            "Initialize kẹt ở AcquireDatabaseLock — hàng __EFMigrationsLock kẹt không được dọn trước Migrate");
        await initialize;
    }

    [Fact]
    public void Initialize_WhenComboItemsWritten_BothNavigationAndForeignKeyLoad()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);

        long comboId;
        using (var db = factory.CreateDbContext())
        {
            var combo = new Combo
            {
                Name = "combo-roundtrip",
                Items = [new ComboItem { Position = 0 }],
            };
            db.Combos.Add(combo);
            db.SaveChanges();
            comboId = combo.Id;
        }

        using (var db = factory.CreateDbContext())
        {
            var reloaded = db.Combos.Include(c => c.Items).Single(c => c.Id == comboId);
            Assert.Single(reloaded.Items);

            var byForeignKey = db.ComboItems.Single(i => i.ComboId == comboId);
            Assert.Equal(reloaded.Items[0].Id, byForeignKey.Id);
        }
    }

    [Fact]
    public void Initialize_BackfillsIdentifier_SlugifiesDedupesAndFallsBack()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);

        using (var db = factory.CreateDbContext())
        {
            db.Providers.AddRange(
                new Provider { Name = "Nhà cung cấp A", Type = ProviderType.OpenAI, BaseUrl = "https://a.example" },
                new Provider { Name = "Nhà cung cấp A", Type = ProviderType.OpenAI, BaseUrl = "https://b.example" },
                new Provider { Name = "   ---   ", Type = ProviderType.OpenAI, BaseUrl = "https://c.example" });
            db.SaveChanges();
            // Mô phỏng hàng cũ trước khi có backfill: Identifier NULL
            foreach (var p in db.Providers) p.Identifier = null;
            db.SaveChanges();
        }

        DbInitializer.Initialize(factory);

        using (var db = factory.CreateDbContext())
        {
            // Seed thêm 4 preset (Id nhỏ hơn hàng test) — loại ra để index [0..2] trỏ đúng hàng test
            var providers = db.Providers.Where(p => !p.IsPreset).OrderBy(p => p.Id).ToList();
            Assert.Equal("nha-cung-cap-a", providers[0].Identifier);
            Assert.Equal("nha-cung-cap-a-2", providers[1].Identifier); // dedupe
            Assert.Equal($"provider-{providers[2].Id}", providers[2].Identifier); // slug rỗng

            // Idempotent — chạy lần nữa giá trị không đổi
            DbInitializer.Initialize(factory);
        }
        using (var db = factory.CreateDbContext())
        {
            Assert.Equal("nha-cung-cap-a",
                db.Providers.Where(p => !p.IsPreset).OrderBy(p => p.Id).First().Identifier);
        }
    }

    [Fact]
    public void Initialize_BackfillsIdentifier_CapsAt50KeepsSlugShapeAndStaysIdempotent()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);

        // Tên ≥60 ký tự alphanumeric → slug vượt cap 50 (spec §2: identifier ≤50 ký tự)
        var longName = new string('a', 60);
        using (var db = factory.CreateDbContext())
        {
            db.Providers.AddRange(
                new Provider { Name = longName, Type = ProviderType.OpenAI, BaseUrl = "https://a.example" },
                new Provider { Name = longName, Type = ProviderType.OpenAI, BaseUrl = "https://b.example" },
                // Slug 51 ký tự — cắt tại vị 50 rơi vào '-' cuối, TrimEnd không được để sót dấu '-'
                new Provider { Name = $"{new string('a', 49)} b", Type = ProviderType.OpenAI, BaseUrl = "https://c.example" });
            db.SaveChanges();
            // Mô phỏng hàng cũ trước khi có backfill: Identifier NULL
            foreach (var p in db.Providers) p.Identifier = null;
            db.SaveChanges();
        }

        DbInitializer.Initialize(factory);

        using (var db = factory.CreateDbContext())
        {
            // Seed thêm 4 preset (Id nhỏ hơn hàng test) — loại ra để index [0..2] trỏ đúng hàng test
            var providers = db.Providers.Where(p => !p.IsPreset).OrderBy(p => p.Id).ToList();
            foreach (var p in providers)
            {
                Assert.True(p.Identifier!.Length <= 50,
                    $"'{p.Identifier}' dài {p.Identifier.Length} ký tự, vượt 50");
                Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", p.Identifier);
            }
            Assert.Equal(new string('a', 50), providers[0].Identifier);
            Assert.Equal($"{new string('a', 48)}-2", providers[1].Identifier); // dedupe, suffix vẫn trong 50
            Assert.Equal(new string('a', 49), providers[2].Identifier); // cắt rớt vào '-' → TrimEnd

            // Idempotent — chạy lần nữa giá trị không đổi
            DbInitializer.Initialize(factory);
        }
        using (var db = factory.CreateDbContext())
        {
            Assert.Equal(new string('a', 50),
                db.Providers.Where(p => !p.IsPreset).OrderBy(p => p.Id).First().Identifier);
        }
    }
}
