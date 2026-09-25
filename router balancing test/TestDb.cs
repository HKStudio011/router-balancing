using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Storage;

namespace router_balancing_test;

/// <summary>Tạo DbContext trên file tạm trong thư mục temp — mỗi test một file riêng.</summary>
internal sealed class TestDb : IDisposable
{
    public string DbPath { get; } =
        Path.Combine(Path.GetTempPath(), $"rb-test-{Guid.NewGuid():N}.db");

    public IDbContextFactory<RouterBalancingDbContext> CreateFactory()
    {
        // Pooling=False: connection pool giữ handle file sau Dispose → File.Delete
        // trong Dispose() ném IOException trên Windows.
        var options = new DbContextOptionsBuilder<RouterBalancingDbContext>()
            .UseSqlite($"Data Source={DbPath};Pooling=False")
            .Options;
        return new SingleOptionsFactory(options);
    }

    public void Dispose()
    {
        if (File.Exists(DbPath)) File.Delete(DbPath);
    }

    private sealed class SingleOptionsFactory(
        DbContextOptions<RouterBalancingDbContext> options) : IDbContextFactory<RouterBalancingDbContext>
    {
        public RouterBalancingDbContext CreateDbContext() => new(options);
    }
}
