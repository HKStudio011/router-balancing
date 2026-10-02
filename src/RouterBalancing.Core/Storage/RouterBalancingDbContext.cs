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

    /// <summary>API key inbound của client - chỉ lưu hash.</summary>
    public DbSet<ClientKey> ClientKeys => Set<ClientKey>();

    /// <summary>Bảng proxy outbound toàn cục — pool round-robin đọc khi Invalidate (spec proxy-pool §3.1).</summary>
    public DbSet<OutboundProxy> OutboundProxies => Set<OutboundProxy>();

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

        modelBuilder.Entity<ClientKey>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.KeyHash).IsRequired().HasMaxLength(64);
            e.Property(x => x.KeyMask).IsRequired().HasMaxLength(16);
            e.HasIndex(x => x.KeyHash).IsUnique();
            // DateOnly → "yyyy-MM-dd" TEXT: tường minh, không phụ thuộc mapping mặc định của provider
            e.Property(x => x.UsageDate).HasConversion(
                d => d.HasValue ? d.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
                s => s == null ? null : DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
        });

        modelBuilder.Entity<OutboundProxy>(e =>
        {
            e.Property(x => x.Scheme).IsRequired().HasMaxLength(10);
            e.Property(x => x.Host).IsRequired().HasMaxLength(255);
            e.Property(x => x.Username).HasMaxLength(200);
            e.Property(x => x.LastTestMessage).HasMaxLength(500);
            e.Property(x => x.LastTestIp).HasMaxLength(64);
            // Backstop chống trùng endpoint — service check Host.ToLower() ordinal-ignore-case
            // là chủ yếu (SQLite unique index không case-insensitive được, xem Design decision 6)
            e.HasIndex(x => new { x.Scheme, x.Host, x.Port }).IsUnique();
        });

        // Provider ↔ proxy M2M: account override provider (D1) — account gán rỗng
        // kế thừa provider, nên 2 junction độc lập, không shadow column.
        // ToTable plural "ProviderProxies"/"ProviderAccountProxies" nhất quán
        // với các bảng hiện hữu (OutboundProxies, Providers, ProviderAccounts).
        modelBuilder.Entity<ProviderProxy>(e =>
        {
            e.ToTable("ProviderProxies");
            e.HasKey(x => new { x.ProviderId, x.ProxyId });
            e.HasOne(x => x.Provider)
                .WithMany(p => p.ProviderProxies)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Proxy)
                .WithMany()
                .HasForeignKey(x => x.ProxyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderAccountProxy>(e =>
        {
            e.ToTable("ProviderAccountProxies");
            e.HasKey(x => new { x.AccountId, x.ProxyId });
            e.HasOne(x => x.Account)
                .WithMany(a => a.AccountProxies)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Proxy)
                .WithMany()
                .HasForeignKey(x => x.ProxyId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
