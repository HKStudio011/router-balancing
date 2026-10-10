using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Storage;

/// <summary>Auto-migration khi khởi động — end-user không migrate thủ công được nên phải chạy ở đây.</summary>
public static class DbInitializer
{
    // Spec §2: identifier ≤50 ký tự — SQLite không enforce HasMaxLength(50) nên phải cắt ngay khi backfill.
    private const int MaxIdentifierLength = 50;

    /// <summary>Tạo factory theo đường dẫn chuẩn rồi migrate — tiện cho app startup.</summary>
    /// <param name="legacyKeyProtector">Cần để giải mã apiKey cũ trong settings sang bảng ClientKeys.</param>
    public static void Initialize(ISecretProtector? legacyKeyProtector = null)
    {
        var options = new DbContextOptionsBuilder<RouterBalancingDbContext>()
            .UseSqlite($"Data Source={StoragePathProvider.GetDatabasePath()}")
            .Options;
        Initialize(new SimpleFactory(options), legacyKeyProtector);
    }

    public static void Initialize(IDbContextFactory<RouterBalancingDbContext> factory,
        ISecretProtector? legacyKeyProtector = null)
    {
        Directory.CreateDirectory(StoragePathProvider.GetDataDirectory());
        using var db = factory.CreateDbContext();
        DeleteStaleMigrationLock(db);
        db.Database.Migrate();
        SeedFreeProviders(db);
        BackfillIdentifiers(db);
        MigrateLegacyApiKey(db, legacyKeyProtector);
    }

    /// <summary>
    /// Dọn hàng lock kẹt trong <c>__EFMigrationsLock</c> trước khi Migrate.
    /// EF Core 10 giữ migration lock bằng đúng 1 hàng Id=1 trong bảng này và chỉ DELETE
    /// khi dispose êm đẹp — process bị kill giữa <c>Migrate()</c> để hàng kẹt vĩnh viễn.
    /// <c>AcquireDatabaseLock</c> retry <c>INSERT OR IGNORE</c> vô hạn khi hàng còn tồn tại
    /// (changes()=0, không bao giờ ném exception) → app treo im lặng trước khi window được
    /// tạo, try/catch startup không bắt được. <c>SingleInstanceGuard</c> giữ mutex ngay trước
    /// khi chạm DB nên instance này là instance duy nhất của file — hàng kẹt chắc chắn là
    /// di sản của process đã chết, không có migrator nào đang sống thật sự để tranh lock.
    /// </summary>
    private static void DeleteStaleMigrationLock(RouterBalancingDbContext db)
    {
        // File chưa tồn tại = DB mới tinh, không thể có lock kẹt. Bắt buộc skip tại đây,
        // không chỉ check bảng: connection mở với Mode=ReadWriteCreate sẽ TẠO file rỗng
        // trước Migrate → Migrator thấy DatabaseCreator.Exists() = true → bỏ qua Create()
        // (nơi chạy PRAGMA journal_mode='wal') → DB chạy rollback-journal thay vì WAL,
        // writer kẹt với reader → lỗi "database is locked" ngẫu nhiên ở pipeline.
        var dataSource = db.Database.GetDbConnection().DataSource;
        if (!File.Exists(dataSource)) return;

        // Bảng được EF tạo lázay trong lần Migrate đầu tiên — file có thể tồn tại nhưng
        // chưa từng migrate, DELETE sẽ ném "no such table" nếu không kiểm tra trước.
        var lockTableExists = db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'")
            .ToList();
        if (lockTableExists.Count == 0) return;

        // Đọc Timestamp trước khi xóa để log ghi được dấu vết thời điểm process đã chết
        // giữ lock — cần cho lần điều tra sau (hàng chỉ tồn tại khi bị kill giữa migrate).
        var leftover = db.Database
            .SqlQueryRaw<string>("SELECT \"Timestamp\" FROM \"__EFMigrationsLock\"")
            .ToList();
        if (leftover.Count == 0) return;

        db.Database.ExecuteSqlRaw("DELETE FROM \"__EFMigrationsLock\"");

        // Chỉ ghi startup-error.log khi đang vận hành DB production — test chạy trên DB tạm
        // trong thư mục temp, không được ghi nhiễu (và hiểu sai) log lỗi khởi động thật.
        if (string.Equals(
                dataSource,
                StoragePathProvider.GetDatabasePath(),
                StringComparison.OrdinalIgnoreCase))
        {
            StartupErrorReporter.Append(
                $"STALE MIGRATION LOCK CLEARED — __EFMigrationsLock còn sót {leftover.Count} hàng " +
                $"(Timestamp: {string.Join(", ", leftover)}). " +
                "Process bị kill giữa Migrate ở lần chạy trước.",
                StartupErrorReporter.DefaultLogPath);
        }
    }

    /// <summary>
    /// Seed 4 provider free preset (spec provider-free §6.4) — chạy đúng 1 lần cho đời sống DB:
    /// guard theo "DB đã có hàng preset nào chưa" thay vì match từng Name, để hàng user đã
    /// đổi tên (§13: không nhân đôi) hay sửa URL không bao giờ bị đụng tới/ghé lại.
    /// Chạy trước BackfillIdentifiers để Identifier được slugify từ Name ngay lần đầu.
    /// </summary>
    private static void SeedFreeProviders(RouterBalancingDbContext db)
    {
        if (db.Providers.Any(p => p.IsPreset)) return;

        foreach (var entry in FreeProviderCatalog.Entries)
        {
            db.Providers.Add(new Provider
            {
                Name = entry.DisplayName,
                BaseUrl = entry.BaseUrl,
                Type = ProviderType.OpenAI, // cả 4 endpoint đều OpenAI-compatible (§4)
                IsPreset = true,
                Enabled = false, // D9: user tự bật — 4 provider lạ không được tự nhận traffic
                MaxConcurrent = 4,
            });
        }
        db.SaveChanges();
    }

    /// <summary>
    /// Chuyển apiKey 1 đầu trong settings (DPAPI) sang bảng ClientKeys rồi dọn row cũ — chạy 1 lần
    /// sau Migrate, idempotent nhờ guard "bảng đã có row" (spec client-keys §3).
    /// Đọc row settings TRỰC TIẾP qua DbContext (không qua IAppSettingsService — service có thể
    /// đã cache giá trị trước khi migrate). Tên 2 key là storage contract nên dùng literal:
    /// hằng SettingsKeys.ApiKey/ApiKeyEnabled bị gỡ ở task gỡ settings API (Task 5).
    /// </summary>
    private static void MigrateLegacyApiKey(RouterBalancingDbContext db, ISecretProtector? protector)
    {
        if (db.ClientKeys.Any()) return;

        var keyRow = db.AppSettings.AsNoTracking()
            .FirstOrDefault(a => a.Key == "apiKey");
        if (keyRow is null || string.IsNullOrWhiteSpace(keyRow.ValueJson)) return;

        // Không có protector = không giải mã được key cũ. Fail loud để startup dialog hiện rõ,
        // tránh khi UI gỡ settings apiKey (Task 5) auth âm thầm chuyển sang open.
        if (protector is null)
            throw new InvalidOperationException(
                "apiKey cũ tồn tại trong settings nhưng thiếu ISecretProtector để migrate sang ClientKeys.");

        // Unprotect lỗi (DB copy sang máy khác...) cũng phải nổi lên - không bỏ qua âm thầm.
        var plaintext = protector.Unprotect(keyRow.ValueJson);
        if (string.IsNullOrEmpty(plaintext)) return;

        var enabledRow = db.AppSettings.AsNoTracking()
            .FirstOrDefault(a => a.Key == "apiKeyEnabled");
        var enabled = enabledRow is not null
            && bool.TryParse(enabledRow.ValueJson, out var parsed) && parsed;

        db.ClientKeys.Add(new ClientKey
        {
            Name = "Legacy key",
            KeyHash = ClientKeyHasher.Hash(plaintext),
            KeyMask = ClientKeyHasher.Mask(plaintext),
            Enabled = enabled,
        });
        db.AppSettings.Remove(keyRow);
        if (enabledRow is not null) db.AppSettings.Remove(enabledRow);
        // 1 SaveChanges duy nhất = atomic best-effort (spec §3): migrate xong thì row cũ đi kèm,
        // thất bại → DB giữ nguyên, lần khởi động sau retry (idempotent) — không bao giờ mất key.
        db.SaveChanges();
    }

    /// <summary>
    /// Backfill Identifier từ Name cho hàng cũ (migration thêm cột nullable) — idempotent.
    /// Slugify Unicode cần C# (Normalize + bỏ combining mark) nên làm ở đây thay vì SQL trong migration.
    /// </summary>
    internal static void BackfillIdentifiers(RouterBalancingDbContext db)
    {
        var taken = db.Providers.AsNoTracking()
            .Where(p => p.Identifier != null && p.Identifier != "")
            .Select(p => p.Identifier!)
            .ToHashSet(StringComparer.Ordinal);

        var pending = db.Providers
            .Where(p => p.Identifier == null || p.Identifier == "")
            .OrderBy(p => p.Id)
            .ToList();
        if (pending.Count == 0) return;

        foreach (var provider in pending)
        {
            // Backfill chỉ đụng hàng Identifier NULL/"" nên giá trị ghi ở đây không bao giờ
            // được sửa lại — identifier xấu lọt vào là dính vĩnh viễn, không cơ hội vá sau.
            var slug = TruncateSlug(Slugify(provider.Name), MaxIdentifierLength);
            if (slug.Length == 0) slug = $"provider-{provider.Id}";

            var candidate = slug;
            for (var n = 2; taken.Contains(candidate); n++)
            {
                // Suffix -{n} cũng phải nằm trong 50: cắt phần slug còn lại theo độ dài suffix;
                // TrimEnd('-') do cắt sinh ra (kể cả "--" trước suffix) để không phá slug regex.
                var suffix = $"-{n}";
                var fitted = TruncateSlug(slug, MaxIdentifierLength - suffix.Length);
                if (fitted.Length == 0) fitted = $"provider-{provider.Id}";
                candidate = $"{fitted}{suffix}";
            }

            provider.Identifier = candidate;
            taken.Add(candidate);
        }

        db.SaveChanges();
    }

    /// <summary>Cắt slug về tối đa <paramref name="maxLength"/> rồi bỏ dấu '-' còn sót cuối chuỗi; có thể trả về rỗng.</summary>
    private static string TruncateSlug(string slug, int maxLength)
    {
        if (slug.Length <= maxLength) return slug;
        return slug[..maxLength].TrimEnd('-');
    }

    /// <summary>Lowercase ASCII + chữ Việt có dấu → bỏ dấu; ký tự ngoài [a-z0-9] → '-' (collapse, trim).</summary>
    internal static string Slugify(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(ch);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(lower);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        return sb.ToString().Trim('-');
    }

    private sealed class SimpleFactory(DbContextOptions<RouterBalancingDbContext> options)
        : IDbContextFactory<RouterBalancingDbContext>
    {
        public RouterBalancingDbContext CreateDbContext() => new(options);
    }
}
