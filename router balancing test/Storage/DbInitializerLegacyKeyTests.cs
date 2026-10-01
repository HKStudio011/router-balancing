using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

public class DbInitializerLegacyKeyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DpapiSecretProtector _protector = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Migrate schema trước rồi mới ghi settings — bảng AppSetting chưa có lúc file trống.</summary>
    private IDbContextFactory<RouterBalancingDbContext> SeedLegacySettings(string plain, bool enabled)
    {
        var factory = _db.CreateFactory();
        using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.AppSettings.Add(new AppSetting { Key = "apiKey", ValueJson = _protector.Protect(plain) });
            db.AppSettings.Add(new AppSetting { Key = "apiKeyEnabled", ValueJson = enabled ? "true" : "false" });
            db.SaveChanges();
        }
        return factory;
    }

    [Fact]
    public void Initialize_WithLegacyKeyEnabled_CreatesRowAndClearsSettings()
    {
        var factory = SeedLegacySettings("old-secret", enabled: true);

        DbInitializer.Initialize(factory, _protector);

        using var db = factory.CreateDbContext();
        var key = Assert.Single(db.ClientKeys.AsNoTracking().ToList());
        Assert.Equal("Legacy key", key.Name);
        Assert.Equal(ClientKeyHasher.Hash("old-secret"), key.KeyHash);
        Assert.True(key.Enabled);
        Assert.False(db.AppSettings.AsNoTracking()
            .Any(a => a.Key == "apiKey" || a.Key == "apiKeyEnabled"));
    }

    [Fact]
    public void Initialize_WithLegacyKeyDisabled_CreatesDisabledRow()
    {
        var factory = SeedLegacySettings("old-secret", enabled: false);

        DbInitializer.Initialize(factory, _protector);

        using var db = factory.CreateDbContext();
        Assert.False(Assert.Single(db.ClientKeys.AsNoTracking().ToList()).Enabled);
    }

    [Fact]
    public void Initialize_RunTwice_DoesNotDuplicate()
    {
        var factory = SeedLegacySettings("old-secret", enabled: true);

        DbInitializer.Initialize(factory, _protector);
        DbInitializer.Initialize(factory, _protector);

        using var db = factory.CreateDbContext();
        Assert.Single(db.ClientKeys.AsNoTracking().ToList());
    }

    [Fact]
    public void Initialize_WithoutLegacyKey_CreatesNoRows()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        Assert.Empty(db.ClientKeys.AsNoTracking().ToList());
    }

    [Fact]
    public void Initialize_WhenLegacyKeyButNoProtector_Throws()
    {
        var factory = SeedLegacySettings("old-secret", enabled: true);

        // Mất protector = không giải mã được key cũ → fail loud thay vì âm thầm bỏ auth (spec §3)
        Assert.Throws<InvalidOperationException>(() => DbInitializer.Initialize(factory));
    }
}
