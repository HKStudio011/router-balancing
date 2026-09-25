using Microsoft.EntityFrameworkCore;

namespace RouterBalancing.Core.Storage;

/// <summary>Auto-migration khi khởi động — end-user không migrate thủ công được nên phải chạy ở đây.</summary>
public static class DbInitializer
{
    public static void Initialize(IDbContextFactory<RouterBalancingDbContext> factory)
    {
        Directory.CreateDirectory(StoragePathProvider.GetDataDirectory());
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
    }
}
