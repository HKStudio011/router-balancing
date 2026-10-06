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
        if (!File.Exists(DbPath)) return;
        // Connection của request vừa xong có thể còn đóng dở đúng lúc test kết thúc —
        // race đã biết của suite (xem comment DispatcherLoop về "test xoá file DB
        // trong lúc log còn ghi"). Thử lại có giới hạn: hết 2s vẫn khóa = leak thật,
        // để IOException nổ chứ không nuốt.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(DbPath);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class SingleOptionsFactory(
        DbContextOptions<RouterBalancingDbContext> options) : IDbContextFactory<RouterBalancingDbContext>
    {
        public RouterBalancingDbContext CreateDbContext() => new(options);
    }
}
