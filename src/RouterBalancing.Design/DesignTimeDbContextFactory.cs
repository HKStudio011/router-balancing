using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Design;

/// <summary>EF design-time factory — tools không cần chạy app chính.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RouterBalancingDbContext>
{
    public RouterBalancingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RouterBalancingDbContext>()
            .UseSqlite("Data Source=router-balancing.db")
            .Options;
        return new RouterBalancingDbContext(options);
    }
}
