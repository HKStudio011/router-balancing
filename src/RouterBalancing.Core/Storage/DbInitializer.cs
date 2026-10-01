using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
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
        db.Database.Migrate();
        BackfillIdentifiers(db);
        MigrateLegacyApiKey(db, legacyKeyProtector);
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
