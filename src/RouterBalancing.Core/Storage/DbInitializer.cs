using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace RouterBalancing.Core.Storage;

/// <summary>Auto-migration khi khởi động — end-user không migrate thủ công được nên phải chạy ở đây.</summary>
public static class DbInitializer
{
    /// <summary>Tạo factory theo đường dẫn chuẩn rồi migrate — tiện cho app startup.</summary>
    public static void Initialize()
    {
        var options = new DbContextOptionsBuilder<RouterBalancingDbContext>()
            .UseSqlite($"Data Source={StoragePathProvider.GetDatabasePath()}")
            .Options;
        Initialize(new SimpleFactory(options));
    }

    public static void Initialize(IDbContextFactory<RouterBalancingDbContext> factory)
    {
        Directory.CreateDirectory(StoragePathProvider.GetDataDirectory());
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        BackfillIdentifiers(db);
    }

    /// <summary>
    /// Backfill Identifier từ Name cho hàng cũ (migration thêm cột nullable) — idempotent.
    ///_slugify Unicode cần C# (Normalize + bỏ combining mark) nên làm ở đây thay vì SQL trong migration.
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
            var slug = Slugify(provider.Name);
            if (slug.Length == 0) slug = $"provider-{provider.Id}";

            var candidate = slug;
            for (var n = 2; taken.Contains(candidate); n++)
            {
                candidate = $"{slug}-{n}";
            }

            provider.Identifier = candidate;
            taken.Add(candidate);
        }

        db.SaveChanges();
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
