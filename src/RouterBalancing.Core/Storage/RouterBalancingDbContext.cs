using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Storage;

/// <summary>DbContext chính — SQLite, schema quản lý bằng EF Migration (auto khi khởi động).</summary>
public class RouterBalancingDbContext(DbContextOptions<RouterBalancingDbContext> options)
    : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();

    public DbSet<Model> Models => Set<Model>();

    public DbSet<Combo> Combos => Set<Combo>();

    public DbSet<ComboItem> ComboItems => Set<ComboItem>();

    /// <summary>Bảng tài khoản (API key) của provider — đa tài khoản mỗi nhà cung cấp.</summary>
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();

    public DbSet<LogEntry> LogEntries => Set<LogEntry>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Provider>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Identifier).HasMaxLength(50);
            // Identifier slug unique toàn cục — nhiều NULL vẫn OK (SQLite distinct NULLs),
            // hàng cũ được backfill trong DbInitializer sau Migrate
            e.HasIndex(x => x.Identifier).IsUnique();
            e.Property(x => x.BaseUrl).IsRequired().HasMaxLength(2000);
        });

        modelBuilder.Entity<Model>(e =>
        {
            e.Property(x => x.ModelId).IsRequired().HasMaxLength(500);
            // Một provider không được khai báo 2 lần cùng model id
            e.HasIndex(x => new { x.ProviderId, x.ModelId }).IsUnique();
            e.HasOne(x => x.Provider)
                .WithMany(p => p.Models)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderAccount>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.ApiKeyEncrypted).IsRequired();
            // Đổi tên account trong form có thể trùng account khác cùng provider
            e.HasIndex(x => new { x.ProviderId, x.Name }).IsUnique();
            e.HasOne(x => x.Provider)
                .WithMany(p => p.Accounts)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Combo>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ComboItem>(e =>
        {
            // WithMany(c => c.Items): map đúng navigation của Combo — nếu dùng WithMany()
            // EF sẽ tạo shadow FK thứ hai (ComboId1) song song với ComboId.
            e.HasOne<Combo>().WithMany(c => c.Items).HasForeignKey(x => x.ComboId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Model>().WithMany().HasForeignKey(x => x.TargetModelId)
                .OnDelete(DeleteBehavior.Cascade);
            // Restrict: app tự kiểm tra combo đang được tham chiếu trước khi xóa
            e.HasOne<Combo>().WithMany().HasForeignKey(x => x.TargetComboId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ComboId, x.Position });
        });

        modelBuilder.Entity<LogEntry>(e =>
        {
            e.Property(x => x.Message).IsRequired();
            // SQLite không dịch được WHERE/ORDER BY trên DateTimeOffset
            // ("SQLite does not support expressions of type 'DateTimeOffset'") —
            // lưu UTC ticks (long → INTEGER) để From/To/OrderBy chạy server-side.
            e.Property(x => x.Timestamp)
                .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            e.HasIndex(x => x.Timestamp);
            // Cover query stats: WHERE Category + range time + group theo Provider/Model
            e.HasIndex(x => new { x.Category, x.Timestamp, x.ProviderId, x.ModelId });
        });

        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(200).IsRequired();
            e.Property(x => x.ValueJson).IsRequired();
        });
    }
}
