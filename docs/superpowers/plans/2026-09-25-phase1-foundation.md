# Phase 1 — Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Xây nền tảng app MAUI Blazor Hybrid làm local proxy — hosting Kestrel + DB auto-migration + settings/i18n/theme + dashboard shell + Log panel + Settings panel + Windows tray.

**Architecture:** Modular monolith — `RouterBalancing.Core` (class lib net10.0: Domain/Storage/Settings/Logging/Server/Security/Localization) tham chiếu từ app MAUI (UI Blazor + tray) và từ test project. Kestrel host chạy ngầm trong process MAUI, bind `127.0.0.1`. Real-time qua event in-process → component `StateHasChanged`.

**Tech Stack:** .NET 10 / MAUI Blazor Hybrid, EF Core 10.0.12 + SQLite, ASP.NET Core (Kestrel), xUnit 2.9.3, Vite 8 + Tailwind 4, TypeScript.

**Spec:** `docs/superpowers/specs/2026-09-25-router-balancing-design.md` (Phase 1 = Foundation).

## Global Constraints

- TFMs: app `net10.0-windows10.0.19041.0`, Core/test `net10.0`. Giữ `Nullable=enable`, `ImplicitUsings=enable`, `MauiXamlInflator=SourceGen`.
- Package versions (đã verify có trên NuGet): `Microsoft.EntityFrameworkCore.Sqlite` **10.0.12**, `System.Security.Cryptography.ProtectedData` **10.0.12**, `Microsoft.EntityFrameworkCore.Design` **10.0.12** (chỉ trong Design project).
- Server: bind **`127.0.0.1`**, port mặc định **8317**, range 1024–65535, `/health` không cần auth.
- Setting mặc định: `language=auto`, `theme=system`, `port=8317`, `apiKeyEnabled=false`, `closeToTray=true`, `startWithWindows=false`, `maxRetry=3`, `watchdogIntervalSec=60`, `defaultMaxConcurrent=4`, `logRetentionDays=90`, `statsErrorRateThreshold=10`.
- DB: `%AppData%\router-balancing\router-balancing.db`; DPAPI entropy purpose `router-balancing/keys`, scope `CurrentUser`.
- Comment/tài liệu: **tiếng Việt** (giải thích "why"); identifier & commit message **tiếng Anh**, conventional commits. XML doc (`///`) bắt buộc với public API. Không nuốt exception — luôn log.
- Test: xUnit 2.9.3, tên test `Method_WhenX_ExpectY`. Lệnh chuẩn:
  - Test: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
  - Build app: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
  - Frontend: `npm run build` với workdir `router-balancing/vite-project`
- i18n key phẳng: `"panel.log.title"`. JS globals: `rbTheme.applyTheme(theme)`, `rbPanel.get(id, fallback)` / `rbPanel.set(id, expanded)`; localStorage keys `rb.theme`, `rb.panel.{id}`.
- Mọi task kết thúc bằng build/test xanh + commit (message tiếng Anh, conventional).

---

### Task 1: Dọn Services hỏng + tạo RouterBalancing.Core

**Files:**
- Delete: `router-balancing/Services/` (11 file — copy từ project DMFT, tham chiếu `DMFT.*`/Playwright không tồn tại, đang làm build FAIL; là file untracked nên chỉ cần xóa trên đĩa)
- Create: `src/RouterBalancing.Core/RouterBalancing.Core.csproj`
- Modify: `router-balancing.slnx`, `router-balancing/router-balancing.csproj`, `router balancing test/router balancing test.csproj`

**Interfaces:**
- Consumes: không có (task đầu tiên)
- Produces: project `RouterBalancing.Core` (net10.0, RootNamespace `RouterBalancing.Core`) — mọi task sau tạo file trong project này; app + test tham chiếu bằng `<ProjectReference>`

- [ ] **Step 1: Xóa folder Services hỏng**

```powershell
Remove-Item -Recurse -Force "router-balancing/Services"
```

- [ ] **Step 2: Tạo Core csproj**

Tạo `src/RouterBalancing.Core/RouterBalancing.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>RouterBalancing.Core</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!-- Kestrel/middleware trong Server/ — dùng chung shared framework, không cần NuGet -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.12" />
  </ItemGroup>

  <ItemGroup>
    <!-- Test project dùng lại helper nội bộ (TestDb, FakeSettings…) -->
    <InternalsVisibleTo Include="router balancing test" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Nạp vào solution + tham chiếu**

`router-balancing.slnx` — thêm 1 dòng (giữ 2 project hiện có):

```xml
  <Project Path="src/RouterBalancing.Core/RouterBalancing.Core.csproj" />
```

`router-balancing/router-balancing.csproj` — thêm trước `</Project>` (ItemGroup riêng):

```xml
    <ItemGroup>
        <ProjectReference Include="..\src\RouterBalancing.Core\RouterBalancing.Core.csproj" />
    </ItemGroup>
```

`router balancing test/router balancing test.csproj` — thêm ItemGroup tương tự:

```xml
  <ItemGroup>
    <ProjectReference Include="..\src\RouterBalancing.Core\RouterBalancing.Core.csproj" />
  </ItemGroup>
```

- [ ] **Step 4: Build + test xanh**

```powershell
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: build `0 Error(s)` (lỗi DMFT đã mất); test `Passed! - Total: 1`.

- [ ] **Step 5: Commit**

```powershell
git add src/RouterBalancing.Core router-balancing.slnx router-balancing/router-balancing.csproj "router balancing test/router balancing test.csproj"
git commit -m "chore: add RouterBalancing.Core and remove broken DMFT services"
```

---

### Task 2: Domain entities + DbContext + migration tự động (TDD)

**Files:**
- Create: `src/RouterBalancing.Core/Domain/Enums/ProviderType.cs`, `ComboMode.cs`, `LogSeverity.cs`, `LogCategory.cs`
- Create: `src/RouterBalancing.Core/Domain/Entities/Provider.cs`, `Model.cs`, `Combo.cs`, `ComboItem.cs`, `LogEntry.cs`, `AppSetting.cs`
- Create: `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs`, `DbInitializer.cs`, `StoragePathProvider.cs`
- Create: `src/RouterBalancing.Design/RouterBalancing.Design.csproj`, `Program.cs`, `DesignTimeDbContextFactory.cs`
- Create: `.config/dotnet-tools.json` (dotnet-ef local tool), `src/RouterBalancing.Core/Storage/Migrations/` (sinh bằng `dotnet ef`)
- Modify: `router-balancing.slnx` (thêm Design project)
- Test: `router balancing test/TestDb.cs`, `router balancing test/Storage/DbInitializerTests.cs`

**Interfaces:**
- Consumes: Task 1 (project Core)
- Produces:
  - `RouterBalancingDbContext` với `DbSet<Provider> Providers`, `DbSet<Model> Models`, `DbSet<Combo> Combos`, `DbSet<ComboItem> ComboItems`, `DbSet<LogEntry> LogEntries`, `DbSet<AppSetting> AppSettings`
  - `DbInitializer.Initialize(IDbContextFactory<RouterBalancingDbContext> factory)` — static void
  - `StoragePathProvider.GetDataDirectory(): string`, `StoragePathProvider.GetDatabasePath(): string`
  - Entities `Provider`, `Model`, `Combo`, `ComboItem`, `LogEntry`, `AppSetting`; enums `ProviderType {OpenAI, Anthropic}`, `ComboMode {RoundRobin, Fallback}`, `LogSeverity {Info, Warning, Error}`, `LogCategory {App, Request}` — namespace `RouterBalancing.Core.Domain`

- [ ] **Step 1: Tạo TestDb helper + viết failing test**

Tạo `router balancing test/TestDb.cs`:

```csharp
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
        var options = new DbContextOptionsBuilder<RouterBalancingDbContext>()
            .UseSqlite($"Data Source={DbPath}")
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
```

Tạo `router balancing test/Storage/DbInitializerTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

public class DbInitializerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Initialize_OnEmptyDatabase_CreatesAllTables()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        var tables = db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table'")
            .ToList();
        Assert.Contains("Providers", tables);
        Assert.Contains("Models", tables);
        Assert.Contains("Combos", tables);
        Assert.Contains("ComboItems", tables);
        Assert.Contains("LogEntries", tables);
        Assert.Contains("AppSettings", tables);
    }

    [Fact]
    public void Initialize_RunTwice_IsIdempotent()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);
        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        var migrations = db.Database
            .SqlQueryRaw<string>("SELECT MigrationId FROM __EFMigrationsHistory")
            .ToList();
        Assert.Single(migrations);
    }
}
```

- [ ] **Step 2: Chạy test — FAIL (chưa có code)**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** — build lỗi vì `DbInitializer`/`RouterBalancingDbContext` chưa tồn tại.

- [ ] **Step 3: Tạo 4 enums**

`Domain/Enums/ProviderType.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Chuẩn API của provider — quyết định lớp dịch thuật.</summary>
public enum ProviderType
{
    /// <summary>OpenAI-compatible: passthrough /v1/chat/completions.</summary>
    OpenAI = 0,

    /// <summary>Anthropic Messages API — cần dịch 2 chiều.</summary>
    Anthropic = 1,
}
```

`Domain/Enums/ComboMode.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Chiến lược cân bằng tải của một combo.</summary>
public enum ComboMode
{
    /// <summary>Chia đều request cho các model trong combo; không cross-model failover.</summary>
    RoundRobin = 0,

    /// <summary>Đi theo thứ tự combo; lỗi retryable → chuyển model kế tiếp.</summary>
    Fallback = 1,
}
```

`Domain/Enums/LogSeverity.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Mức log — cho filter Log panel và ngưỡng cảnh báo stats.</summary>
public enum LogSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}
```

`Domain/Enums/LogCategory.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Nhóm log: sự kiện app hay nhật ký request tới provider.</summary>
public enum LogCategory
{
    App = 0,
    Request = 1,
}
```

- [ ] **Step 4: Tạo 6 entities**

`Domain/Entities/Provider.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Nhà cung cấp LLM mà proxy chuyển tiếp request tới.</summary>
public class Provider
{
    public long Id { get; set; }

    /// <summary>Tên hiển thị do người dùng đặt.</summary>
    public string Name { get; set; } = string.Empty;

    public ProviderType Type { get; set; }

    /// <summary>Đảo gốc API, ví dụ <c>https://api.openai.com</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key đã mã hóa DPAPI — không bao giờ lưu plaintext.</summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Số request đồng thời tối đa — kích thước Execution List.</summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Kết quả test connection gần nhất; null = chưa test.</summary>
    public bool? LastTestSuccess { get; set; }

    public DateTimeOffset? LastTestAt { get; set; }

    public string? LastTestMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Model> Models { get; set; } = [];
}
```

`Domain/Entities/Model.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Một model thuộc provider — đơn vị được chọn khi cân bằng tải.</summary>
public class Model
{
    public long Id { get; set; }

    public long ProviderId { get; set; }

    public Provider? Provider { get; set; }

    /// <summary>ID model phía provider, ví dụ <c>gpt-4o-mini</c>.</summary>
    public string ModelId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Active/deactive — model tắt không tham gia selection.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True khi người dùng thêm tay thay vì auto-fetch.</summary>
    public bool IsManual { get; set; }

    public int? ContextWindow { get; set; }

    public bool SupportsVision { get; set; }

    public bool SupportsThink { get; set; }

    /// <summary>JSON array mức think effort, ví dụ <c>["low","high"]</c>; null = không rõ.</summary>
    public string? ThinkEfforts { get; set; }

    /// <summary>JSON array modalities đầu vào, ví dụ <c>["text","image"]</c>.</summary>
    public string? InputModalities { get; set; }

    /// <summary>JSON array modalities đầu ra, ví dụ <c>["text"]</c>.</summary>
    public string? OutputModalities { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`Domain/Entities/Combo.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Combo router — client chọn qua trường <c>model</c> của request.</summary>
public class Combo
{
    public long Id { get; set; }

    /// <summary>Tên combo = giá trị trường <c>model</c> client gửi lên.</summary>
    public string Name { get; set; } = string.Empty;

    public ComboMode Mode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ComboItem> Items { get; set; } = [];
}
```

`Domain/Entities/ComboItem.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Dòng trong combo — trỏ tới model hoặc combo con (đúng 1 trong 2).</summary>
public class ComboItem
{
    public long Id { get; set; }

    public long ComboId { get; set; }

    /// <summary>Thứ tự — có ý nghĩa với mode Fallback.</summary>
    public int Position { get; set; }

    public long? TargetModelId { get; set; }

    /// <summary>Combo lồng nhau — phải validate cycle khi lưu.</summary>
    public long? TargetComboId { get; set; }
}
```

`Domain/Entities/LogEntry.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>
/// Một dòng nhật ký — dùng chung cho app log (Category=App) và
/// request journal/stats (Category=Request) để chỉ cần một bảng.
/// </summary>
public class LogEntry
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public LogSeverity Severity { get; set; }

    public LogCategory Category { get; set; }

    public string Message { get; set; } = string.Empty;

    public long? ProviderId { get; set; }

    public long? ModelId { get; set; }

    /// <summary>Tương quan các dòng của cùng một request.</summary>
    public Guid? RequestId { get; set; }

    /// <summary>Thời gian xử lý (ms) — chỉ Category=Request.</summary>
    public int? DurationMs { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>JSON chi tiết (error body, stack…) — không chứa secret.</summary>
    public string? Details { get; set; }
}
```

`Domain/Entities/AppSetting.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Key-value setting dạng JSON — dễ thêm key khi upgrade app.</summary>
public class AppSetting
{
    /// <summary>Khóa chính là key, ví dụ <c>port</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string ValueJson { get; set; } = string.Empty;
}
```

- [ ] **Step 5: Tạo DbContext + DbInitializer + StoragePathProvider**

`Storage/RouterBalancingDbContext.cs`:

```csharp
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

    public DbSet<LogEntry> LogEntries => Set<LogEntry>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Provider>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.BaseUrl).IsRequired().HasMaxLength(2000);
            e.Property(x => x.ApiKeyEncrypted).IsRequired();
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

        modelBuilder.Entity<Combo>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ComboItem>(e =>
        {
            e.HasOne<Combo>().WithMany().HasForeignKey(x => x.ComboId)
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
```

`Storage/StoragePathProvider.cs`:

```csharp
namespace RouterBalancing.Core.Storage;

/// <summary>Định vị thư mục dữ liệu app — file DB nằm trong AppData theo spec.</summary>
public static class StoragePathProvider
{
    /// <summary>Thư mục dữ liệu: %AppData%/router-balancing.</summary>
    public static string GetDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "router-balancing");

    /// <summary>Đường dẫn file SQLite: %AppData%/router-balancing/router-balancing.db.</summary>
    public static string GetDatabasePath() =>
        Path.Combine(GetDataDirectory(), "router-balancing.db");
}
```

`Storage/DbInitializer.cs`:

```csharp
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
```

- [ ] **Step 6: Design project + dotnet-ef + sinh migration**

Tạo `src/RouterBalancing.Design/RouterBalancing.Design.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>RouterBalancing.Design</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\RouterBalancing.Core\RouterBalancing.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- EF tools tìm IDesignTimeDbContextFactory trong startup project — bắt buộc ở đây -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

Tạo `src/RouterBalancing.Design/Program.cs`:

```csharp
// Host design-time — chỉ tồn tại để dotnet-ef có assembly để khởi tạo context.
return 0;
```

Tạo `src/RouterBalancing.Design/DesignTimeDbContextFactory.cs`:

```csharp
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
```

Thêm vào `router-balancing.slnx`:

```xml
  <Project Path="src/RouterBalancing.Design/RouterBalancing.Design.csproj" />
```

Cài dotnet-ef local tool + sinh migration (chạy từ gốc repo):

```powershell
dotnet new tool-manifest
dotnet tool install dotnet-ef
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --nologo
dotnet ef migrations add InitialCreate --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --output-dir Storage/Migrations
```

Expected: thư mục `src/RouterBalancing.Core/Storage/Migrations/` có file `*_InitialCreate.cs` + `RouterBalancingDbContextModelSnapshot.cs`.

- [ ] **Step 7: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 3` (1 test template + 2 DbInitializer).

- [ ] **Step 8: Commit**

```powershell
git add src/RouterBalancing.Core src/RouterBalancing.Design .config router-balancing.slnx "router balancing test"
git commit -m "feat: add domain model, SQLite schema and auto-migration"
```

---

### Task 3: ISecretProtector (DPAPI) — TDD

**Files:**
- Create: `src/RouterBalancing.Core/Security/ISecretProtector.cs`, `Security/DpapiSecretProtector.cs`
- Test: `router balancing test/Security/DpapiSecretProtectorTests.cs`

**Interfaces:**
- Consumes: Task 1
- Produces:
  - `interface ISecretProtector { string Protect(string plaintext); string Unprotect(string protectedValue); }` (namespace `RouterBalancing.Core.Security`)
  - `sealed class DpapiSecretProtector : ISecretProtector` — DPAPI `CurrentUser`, entropy `Encoding.UTF8.GetBytes("router-balancing/keys")`; non-Windows ném `PlatformNotSupportedException`

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Security/DpapiSecretProtectorTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Security;

public class DpapiSecretProtectorTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void Protect_ThenUnprotect_ReturnsOriginal()
    {
        const string secret = "sk-test-123";

        var protectedValue = _protector.Protect(secret);
        var restored = _protector.Unprotect(protectedValue);

        Assert.Equal(secret, restored);
        Assert.NotEqual(secret, protectedValue);
    }

    [Fact]
    public void Unprotect_WhenEntropyDiffers_ThrowsCryptographicException()
    {
        // Mã hóa với entropy khác (mô phỏng file DB bị đổi purpose) phải fail, không trả bừa
        var wrongEntropy = Encoding.UTF8.GetBytes("other-purpose");
        var foreign = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes("x"), wrongEntropy, DataProtectionScope.CurrentUser));

        Assert.Throws<CryptographicException>(() => _protector.Unprotect(foreign));
    }

    [Fact]
    public void Protect_OnNonWindows_ThrowsPlatformNotSupported()
    {
        if (OperatingSystem.IsWindows()) return; // chỉ có ý nghĩa trên CI phi Windows

        Assert.Throws<PlatformNotSupportedException>(() => _protector.Protect("x"));
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — namespace `RouterBalancing.Core.Security` chưa tồn tại.

- [ ] **Step 3: Viết implementation**

`src/RouterBalancing.Core/Security/ISecretProtector.cs`:

```csharp
namespace RouterBalancing.Core.Security;

/// <summary>Mã hóa secret (API key) trước khi lưu DB — chống đọc trực tiếp file SQLite.</summary>
public interface ISecretProtector
{
    /// <summary>Mã hóa plaintext thành chuỗi an toàn lưu DB.</summary>
    string Protect(string plaintext);

    /// <summary>Giải mã chuỗi đã Protect.</summary>
    /// <exception cref="CryptographicException">Khi chuỗi bị đổi hoặc purpose sai.</exception>
    string Unprotect(string protectedValue);
}
```

`src/RouterBalancing.Core/Security/DpapiSecretProtector.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace RouterBalancing.Core.Security;

/// <summary>
/// DPAPI CurrentUser — secret chỉ giải được bởi cùng user trên cùng máy,
/// không cần cơ chế key management riêng của app.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Entropy purpose: gắn mã hóa vào app này — copy file DB sang app khác không tự giải được
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("router-balancing/keys");

    public string Protect(string plaintext)
    {
        EnsureWindows();
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Purpose, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedValue)
    {
        EnsureWindows();
        var bytes = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedValue), Purpose, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI chỉ hỗ trợ Windows.");
    }
}
```

- [ ] **Step 4: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 6` (3 cũ + 3 mới).

- [ ] **Step 5: Commit**

```powershell
git add src/RouterBalancing.Core/Security "router balancing test/Security"
git commit -m "feat: add DPAPI secret protector for API keys"
```

---

### Task 4: AppSettingsService — TDD

**Files:**
- Create: `src/RouterBalancing.Core/Settings/SettingsKeys.cs`, `Settings/IAppSettingsService.cs`, `Settings/AppSettingsService.cs`
- Test: `router balancing test/Settings/AppSettingsServiceTests.cs`

**Interfaces:**
- Consumes: Task 2 (`RouterBalancingDbContext`), Task 3 (`ISecretProtector`)
- Produces (namespace `RouterBalancing.Core.Settings`):
  - `static class SettingsKeys` — hằng `Language, Theme, Port, ApiKeyEnabled, ApiKey, CloseToTray, StartWithWindows, MaxRetry, WatchdogIntervalSec, DefaultMaxConcurrent, LogRetentionDays, StatsErrorRateThreshold`
  - `interface IAppSettingsService`:
    - `event Action? SettingsChanged;`
    - Props get: `string Language`, `string Theme`, `int Port`, `bool ApiKeyEnabled`, `bool CloseToTray`, `bool StartWithWindows`, `int MaxRetry`, `int WatchdogIntervalSec`, `int DefaultMaxConcurrent`, `int LogRetentionDays`, `int StatsErrorRateThreshold`
    - `T Get<T>(string key, T defaultValue)` / `void Set<T>(string key, T value)` — ném `InvalidOperationException` nếu key == `SettingsKeys.ApiKey`
    - `string GetApiKey()`, `void SetApiKey(string plain)`
  - `sealed class AppSettingsService : IAppSettingsService, IDisposable` — ctor `(IDbContextFactory<RouterBalancingDbContext>, ISecretProtector)`

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Settings/AppSettingsServiceTests.cs`:

```csharp
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;

namespace router_balancing_test.Settings;

public class AppSettingsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public void Dispose() => _db.Dispose();

    private AppSettingsService Create() => new(_db.CreateFactory(), _protector);

    [Fact]
    public void Get_MissingKey_ReturnsDefault()
    {
        using var service = Create();

        Assert.Equal(8317, service.Get(SettingsKeys.Port, 8317));
        Assert.Equal("auto", service.Language);
        Assert.Equal(3, service.MaxRetry);
        Assert.Equal(90, service.LogRetentionDays);
        Assert.True(service.CloseToTray);
    }

    [Fact]
    public void Set_ThenGet_ReturnsValue()
    {
        using var service = Create();

        service.Set(SettingsKeys.MaxRetry, 5);

        Assert.Equal(5, service.MaxRetry);
    }

    [Fact]
    public void Set_PersistsToDatabase_NewServiceInstanceReadsSameValue()
    {
        using (var service = Create())
        {
            service.Set(SettingsKeys.Port, 9000);
        }

        using var reopened = Create();

        Assert.Equal(9000, reopened.Port);
    }

    [Fact]
    public void Set_FiresSettingsChanged()
    {
        using var service = Create();
        var raised = 0;
        service.SettingsChanged += () => raised++;

        service.Set(SettingsKeys.Theme, "dark");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void SetApiKey_StoresProtectedValue_Roundtrips()
    {
        using var service = Create();
        service.SetApiKey("sk-plaintext");

        // Plaintext không bao giờ nằm trong DB
        using var db = _db.CreateFactory().CreateDbContext();
        var stored = db.AppSettings.First(x => x.Key == "apiKey").ValueJson;
        Assert.DoesNotContain("sk-plaintext", stored);
        Assert.Equal("sk-plaintext", service.GetApiKey());
    }

    [Fact]
    public void Get_WhenKeyIsApiKey_ThrowsInvalidOperation()
    {
        using var service = Create();

        // Chặn đường generic truy cập plaintext key — phải đi qua GetApiKey/SetApiKey
        Assert.Throws<InvalidOperationException>(() => service.Get<string>(SettingsKeys.ApiKey, ""));
        Assert.Throws<InvalidOperationException>(() => service.Set(SettingsKeys.ApiKey, "x"));
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — namespace `RouterBalancing.Core.Settings` chưa tồn tại.

- [ ] **Step 3: Viết implementation**

`src/RouterBalancing.Core/Settings/SettingsKeys.cs`:

```csharp
namespace RouterBalancing.Core.Settings;

/// <summary>Tên key trong bảng AppSettings — tập trung để không gõ sai.</summary>
public static class SettingsKeys
{
    /// <summary>"auto" | "en" | "vi" — auto theo ngôn ngữ hệ thống.</summary>
    public const string Language = "language";

    /// <summary>"light" | "dark" | "system".</summary>
    public const string Theme = "theme";

    public const string Port = "port";

    public const string ApiKeyEnabled = "apiKeyEnabled";

    /// <summary>Lưu DPAPI-protected — đọc/ghi chỉ qua GetApiKey/SetApiKey.</summary>
    public const string ApiKey = "apiKey";

    public const string CloseToTray = "closeToTray";

    public const string StartWithWindows = "startWithWindows";

    /// <summary>Dùng chung cho: số lần retry watchdog VÀ ngưỡng lỗi liên tiếp (spec Quyết định #11).</summary>
    public const string MaxRetry = "maxRetry";

    public const string WatchdogIntervalSec = "watchdogIntervalSec";

    public const string DefaultMaxConcurrent = "defaultMaxConcurrent";

    public const string LogRetentionDays = "logRetentionDays";

    public const string StatsErrorRateThreshold = "statsErrorRateThreshold";
}
```

`src/RouterBalancing.Core/Settings/IAppSettingsService.cs`:

```csharp
namespace RouterBalancing.Core.Settings;

/// <summary>Setting đọc/ghi qua cache in-memory, đồng bộ xuống SQLite.</summary>
public interface IAppSettingsService
{
    /// <summary>Phát sau mỗi lần Set/SetApiKey — consumer re-read ngay.</summary>
    event Action? SettingsChanged;

    string Language { get; }

    string Theme { get; }

    /// <summary>Port ưa thích — ProxyHost dùng làm mốc tìm port trống.</summary>
    int Port { get; }

    bool ApiKeyEnabled { get; }

    bool CloseToTray { get; }

    bool StartWithWindows { get; }

    int MaxRetry { get; }

    int WatchdogIntervalSec { get; }

    int DefaultMaxConcurrent { get; }

    int LogRetentionDays { get; }

    int StatsErrorRateThreshold { get; }

    /// <summary>Đọc setting bất kỳ với mặc định khi chưa có trong DB.</summary>
    /// <exception cref="InvalidOperationException">Khi key là <see cref="SettingsKeys.ApiKey"/> — dùng <see cref="GetApiKey"/>.</exception>
    T Get<T>(string key, T defaultValue);

    /// <summary>Ghi cache + DB (đồng bộ) + phát <see cref="SettingsChanged"/>.</summary>
    /// <exception cref="InvalidOperationException">Khi key là <see cref="SettingsKeys.ApiKey"/> — dùng <see cref="SetApiKey"/>.</exception>
    void Set<T>(string key, T value);

    /// <summary>API key plaintext (đã giải mã); chuỗi rỗng nếu chưa đặt.</summary>
    string GetApiKey();

    /// <summary>Mã hóa DPAPI rồi lưu API key.</summary>
    void SetApiKey(string plain);
}
```

`src/RouterBalancing.Core/Settings/AppSettingsService.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Settings;

/// <inheritdoc cref="IAppSettingsService"/>
public sealed class AppSettingsService : IAppSettingsService, IDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly Dictionary<string, string> _cache = new();
    private bool _disposed;

    public event Action? SettingsChanged;

    public AppSettingsService(IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
        using var context = _db.CreateDbContext();
        foreach (var row in context.AppSettings.AsNoTracking())
        {
            _cache[row.Key] = row.ValueJson;
        }
    }

    public string Language => Get(SettingsKeys.Language, "auto");

    public string Theme => Get(SettingsKeys.Theme, "system");

    public int Port => Get(SettingsKeys.Port, 8317);

    public bool ApiKeyEnabled => Get(SettingsKeys.ApiKeyEnabled, false);

    public bool CloseToTray => Get(SettingsKeys.CloseToTray, true);

    public bool StartWithWindows => Get(SettingsKeys.StartWithWindows, false);

    public int MaxRetry => Get(SettingsKeys.MaxRetry, 3);

    public int WatchdogIntervalSec => Get(SettingsKeys.WatchdogIntervalSec, 60);

    public int DefaultMaxConcurrent => Get(SettingsKeys.DefaultMaxConcurrent, 4);

    public int LogRetentionDays => Get(SettingsKeys.LogRetentionDays, 90);

    public int StatsErrorRateThreshold => Get(SettingsKeys.StatsErrorRateThreshold, 10);

    public T Get<T>(string key, T defaultValue = default!)
    {
        GuardApiKey(key);
        return _cache.TryGetValue(key, out var json)
            ? JsonSerializer.Deserialize<T>(json)!
            : defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        GuardApiKey(key);
        var json = JsonSerializer.Serialize(value);
        _cache[key] = json;
        // Ghi đồng bộ: SQLite local rất nhanh, và lỗi phải nổi lên cho UI toast thay vì nuốt
        Persist(key, json);
        SettingsChanged?.Invoke();
    }

    public string GetApiKey()
    {
        var stored = _cache.TryGetValue(SettingsKeys.ApiKey, out var json) ? json : null;
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        return _protector.Unprotect(stored);
    }

    public void SetApiKey(string plain)
    {
        var value = plain.Length == 0 ? string.Empty : _protector.Protect(plain);
        _cache[SettingsKeys.ApiKey] = value;
        Persist(SettingsKeys.ApiKey, value);
        SettingsChanged?.Invoke();
    }

    private static void GuardApiKey(string key)
    {
        if (key == SettingsKeys.ApiKey)
            throw new InvalidOperationException(
                "Không đọc/ghi API key qua Get/Set — dùng GetApiKey/SetApiKey để tránh để lộ plaintext.");
    }

    private void Persist(string key, string json)
    {
        using var db = _db.CreateDbContext();
        var entity = db.AppSettings.Find(key);
        if (entity is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, ValueJson = json });
        }
        else
        {
            entity.ValueJson = json;
        }
        db.SaveChanges();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 12` (6 cũ + 6 mới).

- [ ] **Step 5: Commit**

```powershell
git add src/RouterBalancing.Core/Settings "router balancing test/Settings"
git commit -m "feat: add app settings service with defaults and change event"
```

### Task 5: ILogService / LogService — TDD

**Files:**
- Create: `src/RouterBalancing.Core/Logging/LogQuery.cs`, `Logging/ILogService.cs`, `Logging/LogService.cs`
- Test: `router balancing test/Logging/LogServiceTests.cs`

**Interfaces:**
- Consumes: Task 2 (`LogEntry`, `LogSeverity`, `LogCategory`, `RouterBalancingDbContext`)
- Produces (namespace `RouterBalancing.Core.Logging`):
  - `sealed record LogQuery(LogSeverity? MinSeverity = null, LogCategory? Category = null, string? Search = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Page = 1, int PageSize = 100)`
  - `interface ILogService`:
    - `event Action<LogEntry>? LogAdded;`
    - `void Write(LogEntry entry);`
    - `void Info(string message, LogCategory category = LogCategory.App);`
    - `void Warn(string message, LogCategory category = LogCategory.App);`
    - `void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App);`
    - `IReadOnlyList<LogEntry> Query(LogQuery query);`
    - `int Count(LogQuery query);`
  - `sealed class LogService : ILogService, IDisposable` — ctor `(IDbContextFactory<RouterBalancingDbContext>)`

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Logging/LogServiceTests.cs`:

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;

namespace router_balancing_test.Logging;

public class LogServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private LogService Create() => new(_db.CreateFactory());

    [Fact]
    public void Write_ThenQuery_ReturnsEntry()
    {
        using var service = Create();

        service.Info("app started");

        var result = service.Query(new LogQuery());
        var entry = Assert.Single(result);
        Assert.Equal("app started", entry.Message);
        Assert.Equal(LogSeverity.Info, entry.Severity);
        Assert.Equal(LogCategory.App, entry.Category);
    }

    [Fact]
    public void Write_FiresLogAdded()
    {
        using var service = Create();
        LogEntry? notified = null;
        service.LogAdded += e => notified = e;

        service.Warn("port busy");

        Assert.NotNull(notified);
        Assert.Equal("port busy", notified.Message);
    }

    [Fact]
    public void Query_WhenMinSeverityWarning_ExcludesInfo()
    {
        using var service = Create();
        service.Info("info line");
        service.Warn("warn line");
        service.Error("error line");

        var result = service.Query(new LogQuery(MinSeverity: LogSeverity.Warning));

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.Severity == LogSeverity.Info);
    }

    [Fact]
    public void Query_WhenSearchMatches_ReturnsOnlyMatch()
    {
        using var service = Create();
        service.Info("startup complete");
        service.Info("provider timeout");

        var result = service.Query(new LogQuery(Search: "timeout"));

        var entry = Assert.Single(result);
        Assert.Equal("provider timeout", entry.Message);
    }

    [Fact]
    public void Query_WhenCategoryFilterApplied_ReturnsOnlyThatCategory()
    {
        using var service = Create();
        service.Info("app line");
        service.Write(new LogEntry { Message = "request line", Category = LogCategory.Request });

        var result = service.Query(new LogQuery(Category: LogCategory.Request));

        var entry = Assert.Single(result);
        Assert.Equal("request line", entry.Message);
    }

    [Fact]
    public void Query_PaginatesByPageAndPageSize()
    {
        using var service = Create();
        for (var i = 0; i < 5; i++) service.Info($"line {i}");

        var page2 = service.Query(new LogQuery(Page: 2, PageSize: 2));

        Assert.Equal(2, page2.Count);
        Assert.Equal(5, service.Count(new LogQuery()));
    }

    [Fact]
    public void Query_EntriesPersistAcrossServiceInstances()
    {
        using (var service = Create())
        {
            service.Error("boom", new InvalidOperationException("detail"));
        }

        using var reopened = Create();
        var entry = Assert.Single(reopened.Query(new LogQuery()));
        Assert.Equal(LogSeverity.Error, entry.Severity);
        Assert.Contains("detail", entry.Details);
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — namespace `RouterBalancing.Core.Logging` chưa tồn tại.

- [ ] **Step 3: Viết implementation**

`src/RouterBalancing.Core/Logging/LogQuery.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Logging;

/// <summary>Bộ lọc truy vấn Log panel — Page bắt đầu từ 1.</summary>
public sealed record LogQuery(
    LogSeverity? MinSeverity = null,
    LogCategory? Category = null,
    string? Search = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 100);
```

`src/RouterBalancing.Core/Logging/ILogService.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Logging;

/// <summary>Ghi/truy vấn nhật ký app + request; phát sự kiện khi có dòng mới.</summary>
public interface ILogService
{
    /// <summary>Phát sau mỗi lần ghi thành công — Log panel re-render.</summary>
    event Action<LogEntry>? LogAdded;

    /// <summary>Ghi một dòng (Timestamp được set nếu đang là default).</summary>
    void Write(LogEntry entry);

    void Info(string message, LogCategory category = LogCategory.App);

    void Warn(string message, LogCategory category = LogCategory.App);

    /// <summary>Ghi lỗi — stack trace của <paramref name="exception"/> đưa vào Details.</summary>
    void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App);

    IReadOnlyList<LogEntry> Query(LogQuery query);

    /// <summary>Tổng dòng khớp bộ lọc — dùng cho pagination.</summary>
    int Count(LogQuery query);
}
```

`src/RouterBalancing.Core/Logging/LogService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Logging;

/// <inheritdoc cref="ILogService"/>
public sealed class LogService : ILogService, IDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private bool _disposed;

    public event Action<LogEntry>? LogAdded;

    public LogService(IDbContextFactory<RouterBalancingDbContext> db) => _db = db;

    public void Write(LogEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry.Timestamp == default) entry.Timestamp = DateTimeOffset.UtcNow;
        using var db = _db.CreateDbContext();
        db.LogEntries.Add(entry);
        db.SaveChanges();
        LogAdded?.Invoke(entry);
    }

    public void Info(string message, LogCategory category = LogCategory.App) =>
        Write(new LogEntry { Severity = LogSeverity.Info, Category = category, Message = message });

    public void Warn(string message, LogCategory category = LogCategory.App) =>
        Write(new LogEntry { Severity = LogSeverity.Warning, Category = category, Message = message });

    public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
        Write(new LogEntry
        {
            Severity = LogSeverity.Error,
            Category = category,
            Message = message,
            // Giữ stack trace để chẩn đoán từ Log panel — không nuốt exception
            Details = exception?.ToString(),
            ErrorCode = exception?.GetType().Name,
        });

    public IReadOnlyList<LogEntry> Query(LogQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var db = _db.CreateDbContext();
        return ApplyFilter(db.LogEntries.AsNoTracking(), query)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Skip((Math.Max(query.Page, 1) - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();
    }

    public int Count(LogQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var db = _db.CreateDbContext();
        return ApplyFilter(db.LogEntries.AsNoTracking(), query).Count();
    }

    private static IQueryable<LogEntry> ApplyFilter(IQueryable<LogEntry> source, LogQuery query)
    {
        if (query.MinSeverity is { } min)
            source = source.Where(e => e.Severity >= min);
        if (query.Category is { } category)
            source = source.Where(e => e.Category == category);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            source = source.Where(e => e.Message.Contains(term));
        }
        if (query.From is { } from)
            source = source.Where(e => e.Timestamp >= from);
        if (query.To is { } to)
            source = source.Where(e => e.Timestamp <= to);
        return source;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 19` (12 cũ + 7 mới).

- [ ] **Step 5: Commit**

```powershell
git add src/RouterBalancing.Core/Logging "router balancing test/Logging"
git commit -m "feat: add log service with filtered queries and change event"
```

---

### Task 6: PortSelector + ApiKeyMiddleware — TDD

**Files:**
- Create: `src/RouterBalancing.Core/Server/PortSelector.cs`, `Server/ApiKeyMiddleware.cs`
- Test: `router balancing test/Server/PortSelectorTests.cs`, `Server/ApiKeyMiddlewareTests.cs`

**Interfaces:**
- Consumes: Task 4 (`IAppSettingsService`)
- Produces (namespace `RouterBalancing.Core.Server`):
  - `static class PortSelector` — `int FindAvailable(int preferred = 8317, int maxAttempts = 50)` (range 1024–65535, ném `IOException` khi hết), `bool IsPortAvailable(int port)`
  - `sealed class ApiKeyMiddleware(RequestDelegate next, IAppSettingsService settings)` — `Task InvokeAsync(HttpContext context)`: bỏ qua path `/health`; khi `ApiKeyEnabled && GetApiKey() != ""` yêu cầu `Authorization: Bearer <key>` hoặc `X-API-Key`, so sánh `CryptographicOperations.FixedTimeEquals`; sai → 401 JSON OpenAI-shape

- [ ] **Step 1: Viết failing tests**

Tạo `router balancing test/Server/PortSelectorTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

public class PortSelectorTests
{
    [Fact]
    public void FindAvailable_WhenPreferredFree_ReturnsPreferred()
    {
        var free = PortSelector.FindAvailable(20000);

        Assert.Equal(20000, free);
    }

    [Fact]
    public void FindAvailable_WhenPreferredOccupied_ReturnsOtherPort()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var occupied = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var result = PortSelector.FindAvailable(occupied);

        Assert.NotEqual(occupied, result);
        Assert.InRange(result, 1024, 65535);
    }

    [Fact]
    public void FindAvailable_WhenAllAttemptsFail_ThrowsIOException()
    {
        // Khóa kín một dải port liên tục rồi yêu cầu tìm trong đúng dải đó
        var listeners = new List<TcpListener>();
        var start = PortSelector.FindAvailable(21000);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var l = new TcpListener(IPAddress.Loopback, start + i);
                l.Start();
                listeners.Add(l);
            }

            Assert.Throws<IOException>(() => PortSelector.FindAvailable(start, maxAttempts: 5));
        }
        finally
        {
            foreach (var l in listeners) l.Stop();
        }
    }

    [Fact]
    public void IsPortAvailable_OnFreePort_ReturnsTrue()
    {
        var port = PortSelector.FindAvailable(22000);

        Assert.True(PortSelector.IsPortAvailable(port));
    }
}
```

Tạo `router balancing test/Server/ApiKeyMiddlewareTests.cs`:

```csharp
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;

namespace router_balancing_test.Server;

public class ApiKeyMiddlewareTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;

    public ApiKeyMiddlewareTests()
    {
        _settings = new AppSettingsService(_db.CreateFactory(), new DpapiSecretProtector());
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
    }

    private static async Task<(int StatusCode, bool NextCalled, string Body)> InvokeAsync(
        IAppSettingsService settings, string path, string? authorization = null, string? apiKeyHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        if (apiKeyHeader is not null) context.Request.Headers["X-API-Key"] = apiKeyHeader;
        context.Response.Body = new MemoryStream();

        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            settings);
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (context.Response.StatusCode, nextCalled, body);
    }

    [Fact]
    public async Task Invoke_WhenApiKeyDisabled_PassesThrough()
    {
        var (status, nextCalled, _) = await InvokeAsync(_settings, "/v1/chat/completions");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_OnHealthPath_SkipsAuth()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, _) = await InvokeAsync(_settings, "/health");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenEnabledButNoKeyConfigured_PassesThrough()
    {
        _settings.SetApiKeyEnabled(true);

        var (status, nextCalled, _) = await InvokeAsync(_settings, "/v1/models");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenKeyMissing_Returns401WithErrorBody()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, body) = await InvokeAsync(_settings, "/v1/models");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
        Assert.Contains("invalid_api_key", body);
    }

    [Fact]
    public async Task Invoke_WhenKeyWrong_Returns401()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, _) = await InvokeAsync(
            _settings, "/v1/models", authorization: "Bearer wrong-key");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenBearerKeyCorrect_PassesThrough()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, _) = await InvokeAsync(
            _settings, "/v1/models", authorization: "Bearer secret-key");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenXApiKeyHeaderCorrect_PassesThrough()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");

        var (status, nextCalled, _) = await InvokeAsync(
            _settings, "/v1/models", apiKeyHeader: "secret-key");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }
}
```

Lưu ý: test dùng `_settings.SetApiKeyEnabled(true)` — cần mở rộng interface (Step 3).

- [ ] **Step 2: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — `PortSelector`/`ApiKeyMiddleware` và `SetApiKeyEnabled` chưa tồn tại.

- [ ] **Step 3: Mở rộng IAppSettingsService + viết implementation**

Thêm vào `src/RouterBalancing.Core/Settings/IAppSettingsService.cs` (props + interface):

```csharp
    void SetApiKeyEnabled(bool enabled);
```

Thêm vào `AppSettingsService.cs`:

```csharp
    public void SetApiKeyEnabled(bool enabled) => Set(SettingsKeys.ApiKeyEnabled, enabled);
```

`src/RouterBalancing.Core/Server/PortSelector.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace RouterBalancing.Core.Server;

/// <summary>Tìm port trống trên loopback — tránh crash khi port mặc định đã bị chiếm.</summary>
public static class PortSelector
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    /// <summary>Tìm port khả dụng bắt đầu từ <paramref name="preferred"/>, tăng dần tối đa <paramref name="maxAttempts"/> lần.</summary>
    /// <exception cref="IOException">Khi không còn port trống trong dải đã thử.</exception>
    public static int FindAvailable(int preferred = 8317, int maxAttempts = 50)
    {
        var start = Math.Clamp(preferred, MinPort, MaxPort);
        for (var port = start; port <= Math.Min(start + maxAttempts - 1, MaxPort); port++)
        {
            if (IsPortAvailable(port)) return port;
        }
        throw new IOException($"Không tìm thấy port trống sau {maxAttempts} lần thử (bắt đầu từ {start}).");
    }

    /// <summary>Thử bind thật — cổng đã lắng nghe ở bất kỳ địa chỉ nào trả về false.</summary>
    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
```

`src/RouterBalancing.Core/Server/ApiKeyMiddleware.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Server;

/// <summary>
/// Xác thực API key cho mọi endpoint của proxy (trừ /health).
/// Đọc setting mỗi request — bật/tắt key có hiệu lực ngay, không cần restart server.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IAppSettingsService settings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // /health luôn mở để watchdog/monitor không cần secret (spec: port + health)
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var expected = settings.ApiKeyEnabled ? settings.GetApiKey() : string.Empty;
        if (string.IsNullOrEmpty(expected))
        {
            await next(context);
            return;
        }

        var provided = ExtractKey(context.Request);
        if (provided is not null && FixedTimeEquals(provided, expected))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = "Invalid or missing API key",
                type = "invalid_request_error",
                code = "invalid_api_key",
            },
        });
        await context.Response.WriteAsync(payload, context.RequestAborted);
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return authorization[bearer.Length..].Trim();

        var headerKey = request.Headers["X-API-Key"].ToString();
        return string.IsNullOrEmpty(headerKey) ? null : headerKey;
    }

    private static bool FixedTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
```

- [ ] **Step 4: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 30` (19 cũ + 4 PortSelector + 7 middleware).

- [ ] **Step 5: Commit**

```powershell
git add src/RouterBalancing.Core/Server src/RouterBalancing.Core/Settings "router balancing test/Server"
git commit -m "feat: add port selector and API key auth middleware"
```

### Task 7: ProxyHost (Kestrel) — TDD

**Files:**
- Create: `src/RouterBalancing.Core/Server/IProxyHost.cs`, `Server/ProxyHost.cs`
- Test: `router balancing test/Server/ProxyHostTests.cs`

**Interfaces:**
- Consumes: Task 4 (`IAppSettingsService`), Task 5 (`ILogService`), Task 6 (`PortSelector`, `ApiKeyMiddleware`), Task 2 (DbContext — endpoint `/v1/models` đọc model từ DB)
- Produces (namespace `RouterBalancing.Core.Server`):
  - `interface IProxyHost : IAsyncDisposable` — `int? Port { get; }`, `bool IsRunning { get; }`, `event Action? StateChanged;`, `Task StartAsync(CancellationToken ct = default)`, `Task StopAsync(CancellationToken ct = default)`, `Task RestartAsync(CancellationToken ct = default)`
  - `sealed class ProxyHost : IProxyHost` — ctor `(IAppSettingsService settings, ILogService log, IDbContextFactory<RouterBalancingDbContext> db)`
  - Endpoint Phase 1: `GET /health` → `{"status":"ok"}` (không auth); `GET /v1/models` → OpenAI list-shape từ DB (có auth middleware)

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Server/ProxyHostTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ProxyHostTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly HttpClient _client = new();

    public ProxyHostTests()
    {
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
        // Port 0-style: lấy port trống để test không đụng port thật của app đang chạy
        _settings.Set(SettingsKeys.Port, PortSelector.FindAvailable(24000));
    }

    public void Dispose()
    {
        _client.Dispose();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private async Task<ProxyHost> StartHostAsync()
    {
        var host = new ProxyHost(_settings, _log, _db.CreateFactory());
        await host.StartAsync();
        _client.BaseAddress = new Uri($"http://127.0.0.1:{host.Port}");
        return host;
    }

    [Fact]
    public async Task StartAsync_WhenPortFree_RunsOnPreferredPort()
    {
        await using var host = await StartHostAsync();

        Assert.True(host.IsRunning);
        Assert.Equal(_settings.Port, host.Port);
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StartAsync_WhenPreferredPortBusy_FallsBackToAvailablePort()
    {
        // Chiếm port ưa thích rồi host phải tự chọn port khác
        var busyPort = _settings.Port;
        using var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, busyPort);
        blocker.Start();

        await using var host = await StartHostAsync();

        Assert.NotEqual(busyPort, host.Port);
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_WhenApiKeyEnabled_StillOpen()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");
        await using var _ = await StartHostAsync();

        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Models_WhenApiKeyEnabled_RequiresKey()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");
        await using var _ = await StartHostAsync();

        var withoutKey = await _client.GetAsync("/v1/models");
        using var withKey = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        withKey.Headers.Authorization = new("Bearer", "secret-key");
        var withKeyResponse = await _client.SendAsync(withKey);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withKeyResponse.StatusCode);
    }

    [Fact]
    public async Task Models_ReturnsOpenAiListShape_WithEnabledModels()
    {
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var provider = new Provider { Name = "OpenAI", BaseUrl = "https://api.openai.com" };
            provider.Models.Add(new Model { ModelId = "gpt-4o-mini", Enabled = true });
            provider.Models.Add(new Model { ModelId = "gpt-4o", Enabled = false });
            db.Providers.Add(provider);
            db.SaveChanges();
        }
        await using var host = await StartHostAsync();

        var response = await _client.GetFromJsonAsync<JsonElement>("/v1/models");

        Assert.Equal("list", response.GetProperty("object").GetString());
        var data = response.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(data);
        Assert.Equal("gpt-4o-mini", data[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task StopAsync_ThenRequest_Fails()
    {
        var host = await StartHostAsync();

        await host.StopAsync();

        Assert.False(host.IsRunning);
        await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetAsync("/health"));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task RestartAsync_PicksUpNewPortSetting()
    {
        var host = await StartHostAsync();
        var oldPort = host.Port;
        _settings.Set(SettingsKeys.Port, PortSelector.FindAvailable(25000));

        await host.RestartAsync();

        Assert.True(host.IsRunning);
        Assert.NotEqual(oldPort, host.Port);
        _client.BaseAddress = new Uri($"http://127.0.0.1:{host.Port}");
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.DisposeAsync();
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — `IProxyHost`/`ProxyHost` chưa tồn tại.

- [ ] **Step 3: Viết implementation**

`src/RouterBalancing.Core/Server/IProxyHost.cs`:

```csharp
namespace RouterBalancing.Core.Server;

/// <summary>Kestrel host của proxy — Start/Stop/Restart từ UI Settings.</summary>
public interface IProxyHost : IAsyncDisposable
{
    /// <summary>Port đang lắng nghe thực tế; null khi chưa chạy.</summary>
    int? Port { get; }

    bool IsRunning { get; }

    /// <summary>Phát khi IsRunning/Port đổi — UI cập nhật badge trạng thái.</summary>
    event Action? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Dừng rồi chạy lại — dùng sau khi đổi port setting.</summary>
    Task RestartAsync(CancellationToken cancellationToken = default);
}
```

`src/RouterBalancing.Core/Server/ProxyHost.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IProxyHost"/>
public sealed class ProxyHost : IProxyHost, IAsyncDisposable
{
    private readonly IAppSettingsService _settings;
    private readonly ILogService _log;
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private bool _disposed;

    public int? Port { get; private set; }

    public bool IsRunning => _app is not null;

    public event Action? StateChanged;

    public ProxyHost(IAppSettingsService settings, ILogService log, IDbContextFactory<RouterBalancingDbContext> db)
    {
        _settings = settings;
        _log = log;
        _db = db;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null) return;

            var preferred = _settings.Port;
            var port = PortSelector.FindAvailable(preferred);
            if (port != preferred)
            {
                _log.Warn($"Port {preferred} đang bị chiếm — chuyển sang port {port}.");
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(_settings);
            builder.Services.AddSingleton(_log);
            builder.Services.AddSingleton(_db);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

            var app = builder.Build();
            app.UseMiddleware<ApiKeyMiddleware>();
            MapEndpoints(app);

            await app.StartAsync(cancellationToken);
            _app = app;
            Port = port;
            _log.Info($"Proxy server đang chạy tại http://127.0.0.1:{port}/");
            StateChanged?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is null) return;
            var app = _app;
            _app = null;
            await app.StopAsync(cancellationToken);
            await app.DisposeAsync();
            Port = null;
            _log.Info("Proxy server đã dừng.");
            StateChanged?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    private void MapEndpoints(WebApplication app)
    {
        // /health mở luôn (middleware bỏ qua path này) — watchdog của Phase 2 dùng để ping
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        // Danh sách model đã bật, đúng shape OpenAI /v1/models để client không cần phân biệt
        app.MapGet("/v1/models", async (HttpContext http) =>
        {
            using var db = _db.CreateDbContext();
            var models = await db.Models.AsNoTracking()
                .Where(m => m.Enabled)
                .OrderBy(m => m.Id)
                .Select(m => new
                {
                    id = m.ModelId,
                    object = "model",
                    created = m.CreatedAt.ToUnixTimeSeconds(),
                    owned_by = m.Provider != null ? m.Provider.Name : "unknown",
                })
                .ToListAsync(http.RequestAborted);

            return Results.Json(new { object = "list", data = models });
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 37` (30 cũ + 7 mới).

- [ ] **Step 5: Commit**

```powershell
git add src/RouterBalancing.Core/Server "router balancing test/Server"
git commit -m "feat: add Kestrel proxy host with health and models endpoints"
```

---

### Task 8: Wire DI + auto-migration + single instance

**Files:**
- Create: `router-balancing/Services/SingleInstanceGuard.cs`
- Modify: `router-balancing/MauiProgram.cs`, `router-balancing/App.xaml.cs`
- Test: `router balancing test/Services/SingleInstanceGuardTests.cs`

**Interfaces:**
- Consumes: Task 2 (`DbInitializer`, `StoragePathProvider`), Task 3–7 (mọi service đăng ký DI)
- Produces:
  - `sealed class SingleInstanceGuard : IDisposable` — `static bool TryAcquire()` (named mutex `router-balancing-single-instance`, giữ reference tĩnh để GC không giải mutex), `static void Release()` (chỉ test)
  - DI registrations trong `MauiProgram`: `IDbContextFactory<RouterBalancingDbContext>` (Sqlite file trong AppData), `ISecretProtector`→`DpapiSecretProtector`, `IAppSettingsService`, `ILogService`, `IProxyHost` (singleton)
  - `App` ctor gọi `DbInitializer.Initialize`; `App.Started` khởi động ProxyHost nền

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Services/SingleInstanceGuardTests.cs`:

```csharp
using router_balancing.Services;

namespace router_balancing_test.Services;

public class SingleInstanceGuardTests : IDisposable
{
    public SingleInstanceGuardTests() => SingleInstanceGuard.Release();

    public void Dispose() => SingleInstanceGuard.Release();

    [Fact]
    public void TryAcquire_WhenCalledTwice_FirstWinsSecondFails()
    {
        Assert.True(SingleInstanceGuard.TryAcquire());
        Assert.False(SingleInstanceGuard.TryAcquire());
    }

    [Fact]
    public void TryAcquire_AfterRelease_Succeeds()
    {
        Assert.True(SingleInstanceGuard.TryAcquire());
        SingleInstanceGuard.Release();

        Assert.True(SingleInstanceGuard.TryAcquire());
    }
}
```

Lưu ý: test project hiện **chưa** tham chiếu app project (MAUI) — để test được `SingleInstanceGuard`, Step 3 đặt guard vào Core thay vì app, HOẶC project test thêm `ProjectReference` app MAUI. Chọn cách **đặt `SingleInstanceGuard` trong Core** (`src/RouterBalancing.Core/Security/SingleInstanceGuard.cs`) — app project không cần bị test tham chiếu (tránh kéo cả MAUI vào test). Sửa namespace test thành `RouterBalancing.Core.Security`.

`router balancing test/Services/SingleInstanceGuardTests.cs` (phiên bản cuối):

```csharp
using RouterBalancing.Core.Security;

namespace router_balancing_test.Services;

public class SingleInstanceGuardTests : IDisposable
{
    public SingleInstanceGuardTests() => SingleInstanceGuard.Release();

    public void Dispose() => SingleInstanceGuard.Release();

    [Fact]
    public void TryAcquire_WhenCalledTwice_FirstWinsSecondFails()
    {
        Assert.True(SingleInstanceGuard.TryAcquire());
        Assert.False(SingleInstanceGuard.TryAcquire());
    }

    [Fact]
    public void TryAcquire_AfterRelease_Succeeds()
    {
        Assert.True(SingleInstanceGuard.TryAcquire());
        SingleInstanceGuard.Release();

        Assert.True(SingleInstanceGuard.TryAcquire());
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — `SingleInstanceGuard` chưa tồn tại.

- [ ] **Step 3: Viết SingleInstanceGuard trong Core**

`src/RouterBalancing.Core/Security/SingleInstanceGuard.cs`:

```csharp
namespace RouterBalancing.Core.Security;

/// <summary>
/// Chặn mở app lần 2: named mutex giữ nguyên qua vòng đời process;
/// instance thứ 2 nhận false và thoát — tránh 2 Kestrel giành port/tray trùng.
/// </summary>
public static class SingleInstanceGuard
{
    private const string MutexName = "router-balancing-single-instance";
    private static Mutex? _mutex;
    private static readonly object Sync = new();

    /// <summary>True nếu đây là instance đầu tiên; false nếu mutex đã tồn tại (instance khác).</summary>
    public static bool TryAcquire()
    {
        lock (Sync)
        {
            if (_mutex is not null) return false;

            // initiallyOwned=false: chỉ giữ quyền sở hữu object mutex, không cần lock tức thì
            var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            // Giữ reference tĩnh — nếu để GC thu, mutex bị giải và instance khác vào được
            _mutex = mutex;
            return true;
        }
    }

    /// <summary>Giải mutex — chỉ dùng trong test.</summary>
    public static void Release()
    {
        lock (Sync)
        {
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}
```

- [ ] **Step 4: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 39`.

- [ ] **Step 5: Wire MauiProgram + App**

`router-balancing/MauiProgram.cs` (thay toàn bộ):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            // Instance thứ 2 thoát luôn — trước khi tạo bất kỳ service nào.
            // Environment.Exit an toàn ở đây vì CreateMauiApp chạy trước khi window được tạo.
            if (!SingleInstanceGuard.TryAcquire())
            {
                Environment.Exit(0);
            }

            // Migration PHẢI chạy trước Build(): App ctor inject IProxyHost →
            // IAppSettingsService đọc bảng AppSettings khi còn chưa được migrate.
            DbInitializer.Initialize();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            // DB file trong AppData — không theo thư mục cài app (Program Files ghi chỉ đọc)
            builder.Services.AddDbContextFactory<RouterBalancingDbContext>(options =>
                options.UseSqlite($"Data Source={StoragePathProvider.GetDatabasePath()}"));

            builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
            builder.Services.AddSingleton<IAppSettingsService, AppSettingsService>();
            builder.Services.AddSingleton<ILogService, LogService>();
            builder.Services.AddSingleton<IProxyHost, ProxyHost>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
```

`router-balancing/App.xaml.cs` (thay toàn bộ):

```csharp
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Server;

namespace router_balancing
{
    public partial class App : Application
    {
        private readonly ILogService _log;
        private readonly IProxyHost _proxyHost;

        public App(ILogService log, IProxyHost proxyHost)
        {
            InitializeComponent();
            _log = log;
            _proxyHost = proxyHost;

            _log.Info("router-balancing khởi động.");
            // Start nền để UI render không bị Kestrel block; lỗi chỉ log, không crash app
            _ = Task.Run(async () =>
            {
                try
                {
                    await _proxyHost.StartAsync();
                }
                catch (Exception ex)
                {
                    _log.Error("Không khởi động được proxy server.", ex);
                }
            });
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new MainPage()) { Title = "router-balancing" };
        }
    }
}
```

Điều chỉnh API `DbInitializer` cho khớp cách dùng trên: đổi signature từ `Initialize(IDbContextFactory<RouterBalancingDbContext>)` sang overload không tham số tự tạo factory:

`src/RouterBalancing.Core/Storage/DbInitializer.cs` (thay toàn bộ):

```csharp
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
    }

    private sealed class SimpleFactory(DbContextOptions<RouterBalancingDbContext> options)
        : IDbContextFactory<RouterBalancingDbContext>
    {
        public RouterBalancingDbContext CreateDbContext() => new(options);
    }
}
```

Test `DbInitializerTests` giữ nguyên (dùng overload có tham số).

- [ ] **Step 6: Build + test toàn cục**

```powershell
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: build `0 Error(s)`; test `Passed! - Failed: 0, Total: 39`.

Chạy thử app (`dotnet run` hoặc F5 trong IDE): file `%AppData%\router-balancing\router-balancing.db` được tạo; mở app lần 2 → instance thứ 2 thoát.

- [ ] **Step 7: Commit**

```powershell
git add router-balancing/MauiProgram.cs router-balancing/App.xaml.cs src/RouterBalancing.Core/Security src/RouterBalancing.Core/Storage "router balancing test/Services"
git commit -m "feat: wire core services, auto-migration and single instance guard"
```

### Task 9: Frontend build pipeline — Vite + theme + index.html

**Files:**
- Modify: `router-balancing/vite-project/src/ts/main.ts`, `router-balancing/vite-project/src/css/theme.css`, `router-balancing/vite-project/src/css/style.css`, `router-balancing/vite-project/package.json`
- Modify: `router-balancing/wwwroot/index.html`
- Delete: `router-balancing/wwwroot/lib/` (bootstrap), `router-balancing/wwwroot/app.css` (template Blazor default — không dùng nữa)
- Generate (commit): `router-balancing/wwwroot/build/assets/main.js`, `styles.css`

**Interfaces:**
- Consumes: quyết định Global Constraints (JS globals `rbTheme`/`rbPanel`, key localStorage `rb.theme`, `rb.panel.{id}`)
- Produces:
  - JS global `window.rbTheme = { applyTheme(theme: string): void }` — set `data-theme` trên `<html>`, ghi localStorage `rb.theme`
  - JS global `window.rbPanel = { get(id: string, fallback: boolean): boolean, set(id: string, expanded: boolean): void }`
  - CSS tokens: `--background`, `--surface`, `--text`, `--border` cho light/dark; Tailwind custom variant `dark:` theo `[data-theme="dark"]`
  - `index.html` load `build/assets/styles.css` + `build/assets/main.js`, inline script đọc `localStorage rb.theme` trước khi render (tránh flash sai theme)

- [ ] **Step 1: Sửa `main.ts` (thay toàn bộ)**

```ts
import '../css/style.css';

type PanelFallback = boolean;

function applyTheme(theme: string): void {
    const resolved = theme === 'system'
        ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
        : theme;
    document.documentElement.setAttribute('data-theme', resolved);
    // Persist để lần mở app sau (trước khi Blazor init) đọc lại được — tránh flash sai theme
    localStorage.setItem('rb.theme', theme);
}

function getPanel(id: string, fallback: PanelFallback): boolean {
    const raw = localStorage.getItem(`rb.panel.${id}`);
    return raw === null ? fallback : raw === '1';
}

function setPanel(id: string, expanded: boolean): void {
    localStorage.setItem(`rb.panel.${id}`, expanded ? '1' : '0');
}

const rbTheme = { applyTheme };
const rbPanel = { get, set };

declare global {
    interface Window {
        rbTheme: typeof rbTheme;
        rbPanel: typeof rbPanel;
    }
}

window.rbTheme = rbTheme;
window.rbPanel = rbPanel;

applyTheme(localStorage.getItem('rb.theme') ?? 'system');

window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if ((localStorage.getItem('rb.theme') ?? 'system') === 'system') {
        applyTheme('system');
    }
});

export { rbTheme, rbPanel };
```

Lưu ý `tsconfig` có `noUnusedLocals`/`strict` — không để biến chết; bước build `tsc` sẽ bắt lỗi.

- [ ] **Step 2: Sửa `theme.css` (thay toàn bộ)**

```css
@theme inline {
    --color-background: var(--background);
    --color-surface: var(--surface);
    --color-text: var(--text);
    --color-border: var(--border);
}

@theme {
    --color-primary: #007bff;
    --color-secondary: #6c757d;
    --color-success: #28a745;
    --color-danger: #dc3545;
    --color-warning: #ffda6a;
    --color-info: #17a2b8;
    --color-light: #f8f9fa;
    --color-dark: #343a40;
}

/* LIGHT THEME (default) — surface phải SÁNG hơn background (sửa mapping cũ bị đảo) */
:root, [data-theme="light"] {
    --background: #f5f6f8;
    --surface: #ffffff;
    --text: #1a1d21;
    --border: #d9dde3;
}

/* DARK THEME */
[data-theme="dark"] {
    --background: #141414;
    --surface: #1f2024;
    --text: #e8eaed;
    --border: #33363c;
}
```

- [ ] **Step 3: Thêm dark variant vào `style.css`**

Sau 2 dòng `@import` đầu, thêm custom variant để class `dark:*` của Tailwind 4 theo `data-theme` (không theo `prefers-color-scheme` — app cho người dùng chọn theme chủ động):

```css
@custom-variant dark (&:where([data-theme="dark"], [data-theme="dark"] *));
```

Kết quả `style.css` (thay toàn bộ):

```css
@config "../../tailwind.config.js";
@import "tailwindcss";
@custom-variant dark (&:where([data-theme="dark"], [data-theme="dark"] *));

/* ==========================================================================
   CSS Priority Order (Lowest → Highest)

   1. Tailwind CSS (imported first - base styles only)
   2. CSS Variables & Theme
   3. Utility Classes (@utility - reusable patterns)
   4. Layout & Utilities
   5. Component Styles
   6. Page Styles
   7. <style> in .razor (component-specific)
   8. Components/*.razor.css (scoped - keyframes only)
   ========================================================================== */

/* 1. Base Tailwind - imported first for lowest priority */
@import "./theme.css";
@import "./utility.css";

@layer base {
  * {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
  }
}
```

- [ ] **Step 4: Gỡ jquery khỏi `package.json`**

Xóa `"dependencies"` (jquery) và `"@types/jquery"`:

```json
{
  "name": "vite-project",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc && vite build",
    "preview": "vite preview",
    "watch": "chokidar \"../**/*.razor\" \"src/**/*.{ts,js}\" \"src/**/*.css\" -c \"tsc && vite build\""
  },
  "devDependencies": {
    "@tailwindcss/vite": "^4.3.3",
    "chokidar-cli": "^3.0.0",
    "tailwindcss": "^4.3.3",
    "typescript": "~7.0.2",
    "vite": "^8.1.5"
  }
}
```

- [ ] **Step 5: Thay `wwwroot/index.html` (thay toàn bộ)**

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover" />
    <title>router-balancing</title>
    <base href="/" />
    <link rel="stylesheet" href="build/assets/styles.css" />
    <link rel="stylesheet" href="router-balancing.styles.css" />
    <link rel="icon" href="data:,">
    <script>
        // Theme trước lần render đầu — main.js là module (defer) nên đọc trực tiếp ở đây
        (function () {
            var t = localStorage.getItem('rb.theme') || 'system';
            var resolved = t === 'system'
                ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
                : t;
            document.documentElement.setAttribute('data-theme', resolved);
        })();
    </script>
</head>

<body>

    <div id="app">Loading...</div>

    <div id="blazor-error-ui" data-nosnippet>
        An unhandled error has occurred.
        <a href="." class="reload">Reload</a>
        <span class="dismiss">🗙</span>
    </div>

    <script src="_framework/blazor.webview.js" autostart="false"></script>
    <script type="module" src="build/assets/main.js"></script>

</body>

</html>
```

- [ ] **Step 6: Xóa template leftovers**

```powershell
Remove-Item -Recurse -Force "router-balancing/wwwroot/lib"
Remove-Item -Force "router-balancing/wwwroot/app.css"
```

Lưu ý: style cho `#blazor-error-ui` nằm trong `app.css` bị xóa — chuyển vào `theme.css` cuối file (append):

```css
#blazor-error-ui {
    background: var(--surface);
    color: var(--text);
    bottom: 0;
    box-shadow: 0 -1px 2px rgba(0, 0, 0, 0.2);
    display: none;
    left: 0;
    padding: 0.6rem 1.25rem;
    position: fixed;
    right: 0;
    z-index: 1000;
}

#blazor-error-ui .reload {
    color: var(--color-primary);
    margin-left: 0.5rem;
}

#blazor-error-ui .dismiss {
    cursor: pointer;
    position: absolute;
    right: 0.75rem;
    top: 0.5rem;
}
```

- [ ] **Step 7: Cài deps + build frontend**

```powershell
npm install
npm run build
```

(workdir `router-balancing/vite-project`)

Expected: không lỗi `tsc`; tồn tại `router-balancing/wwwroot/build/assets/main.js` và `styles.css`.

- [ ] **Step 8: Build app + commit (kèm output build)**

```powershell
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
```

Commit cả `wwwroot/build/` — end-user/CI không cần Node để chạy app:

```powershell
git add router-balancing/vite-project router-balancing/wwwroot
git commit -m "feat: set up vite build pipeline with theme tokens and panel storage"
```

---

### Task 10: LocalizationService (i18n) — TDD

**Files:**
- Create: `src/RouterBalancing.Core/Localization/LocalizationService.cs`, `Localization/Translations.cs`
- Test: `router balancing test/Localization/LocalizationServiceTests.cs`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký DI)

**Interfaces:**
- Consumes: Task 4 (`IAppSettingsService` — key `language`)
- Produces (namespace `RouterBalancing.Core.Localization`):
  - `sealed class LocalizationService : IDisposable` — ctor `(IAppSettingsService settings)`
    - `string Language { get; }` — ngôn ngữ hiệu lực (`"en"` | `"vi"`), đã resolve `"auto"` theo `CultureInfo.CurrentUICulture`
    - `string this[string key] { get; }` — tra cứu, fallback trả key khi thiếu
    - `event Action? LanguageChanged;`
    - `void SetLanguage(string language)` — ghi setting + phát event
  - `Translations` — dictionary phẳng key→text cho en/vi (keys theo quy ước `panel.log.title`)

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Localization/LocalizationServiceTests.cs`:

```csharp
using System.Globalization;
using RouterBalancing.Core.Localization;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;

namespace router_balancing_test.Localization;

public class LocalizationServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;

    public LocalizationServiceTests()
    {
        _settings = new AppSettingsService(_db.CreateFactory(), new DpapiSecretProtector());
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Language_WhenSettingVi_ReturnsVi()
    {
        _settings.Set(SettingsKeys.Language, "vi");
        using var service = new LocalizationService(_settings);

        Assert.Equal("vi", service.Language);
    }

    [Fact]
    public void Language_WhenSettingAuto_ResolvesSystemCulture()
    {
        _settings.Set(SettingsKeys.Language, "auto");
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            using var service = new LocalizationService(_settings);
            Assert.Equal("en", service.Language);

            CultureInfo.CurrentUICulture = new CultureInfo("vi-VN");
            using var serviceVi = new LocalizationService(_settings);
            Assert.Equal("vi", serviceVi.Language);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Indexer_WhenKeyExistsInLanguage_ReturnsTranslation()
    {
        _settings.Set(SettingsKeys.Language, "vi");
        using var service = new LocalizationService(_settings);

        Assert.Equal("Bảng điều khiển", service["nav.dashboard"]);
    }

    [Fact]
    public void Indexer_WhenKeyMissing_ReturnsKeyItself()
    {
        using var service = new LocalizationService(_settings);

        Assert.Equal("some.unknown.key", service["some.unknown.key"]);
    }

    [Fact]
    public void SetLanguage_FiresLanguageChanged_AndPersists()
    {
        using var service = new LocalizationService(_settings);
        var raised = 0;
        service.LanguageChanged += () => raised++;

        service.SetLanguage("vi");

        Assert.Equal(1, raised);
        Assert.Equal("vi", service.Language);
        Assert.Equal("vi", _settings.Language);
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — namespace `RouterBalancing.Core.Localization` chưa tồn tại.

- [ ] **Step 3: Viết Translations + LocalizationService**

`src/RouterBalancing.Core/Localization/Translations.cs` (bắt đầu với keys của nav — các task UI sau bổ sung keys vào đây):

```csharp
namespace RouterBalancing.Core.Localization;

/// <summary>
/// Từ điển i18n phẳng: key dạng "nhóm.ten" → text. Thêm key mới tại đây
/// cho cả 2 ngôn ngữ — test LocalizationService_Fallback an toàn khi thiếu.
/// </summary>
public static class Translations
{
    /// <summary>Từ điển ngôn ngữ hệ thống → bảng ngôn ngữ nội bộ; thiếu → "en".</summary>
    public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["app.title"] = "router-balancing",
        ["nav.dashboard"] = "Dashboard",
        ["nav.logs"] = "Logs",
        ["nav.settings"] = "Settings",
        ["panel.log.title"] = "Logs",
        ["panel.settings.title"] = "Settings",
        ["dashboard.title"] = "Dashboard",
    };

    public static readonly IReadOnlyDictionary<string, string> Vietnamese = new Dictionary<string, string>
    {
        ["app.title"] = "router-balancing",
        ["nav.dashboard"] = "Bảng điều khiển",
        ["nav.logs"] = "Nhật ký",
        ["nav.settings"] = "Cài đặt",
        ["panel.log.title"] = "Nhật ký",
        ["panel.settings.title"] = "Cài đặt",
        ["dashboard.title"] = "Bảng điều khiển",
    };

    /// <summary>Chọn bảng theo ngôn ngữ hiệu lực — không có bảng → English (fallback mọi keys).</summary>
    public static IReadOnlyDictionary<string, string> For(string language) =>
        language == "vi" ? Vietnamese : English;
}
```

`src/RouterBalancing.Core/Localization/LocalizationService.cs`:

```csharp
using System.Globalization;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Localization;

/// <summary>Dịch key i18n sang text theo ngôn ngữ cài đặt (xem <see cref="Translations"/>).</summary>
public sealed class LocalizationService : IDisposable
{
    private readonly IAppSettingsService _settings;
    private IReadOnlyDictionary<string, string> _strings;
    private bool _disposed;

    /// <summary>Ngôn ngữ hiệu lực: "auto" đã resolve theo UI culture của hệ thống.</summary>
    public string Language { get; private set; }

    public event Action? LanguageChanged;

    public LocalizationService(IAppSettingsService settings)
    {
        _settings = settings;
        Language = Resolve(settings.Language);
        _strings = Translations.For(Language);
    }

    /// <summary>Tra cứu text; thiếu key trả chính key — UI hiển thị key thay vì crash.</summary>
    public string this[string key] =>
        _strings.TryGetValue(key, out var text) ? text : key;

    public void SetLanguage(string language)
    {
        _settings.Set(SettingsKeys.Language, language);
        Language = Resolve(language);
        _strings = Translations.For(Language);
        LanguageChanged?.Invoke();
    }

    /// <summary>Re-resolve khi setting language đổi từ nguồn khác (Settings panel).</summary>
    public void Reload() => SetLanguage(_settings.Language);

    private static string Resolve(string setting) => setting switch
    {
        "vi" => "vi",
        "en" => "en",
        // "auto" và mọi giá trị lạ → theo hệ thống; không phải "vi" thì dùng English
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? "vi" : "en",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 44` (39 cũ + 5 mới).

- [ ] **Step 5: Đăng ký DI**

Thêm `using RouterBalancing.Core.Localization;` vào đầu `router-balancing/MauiProgram.cs`, và đăng ký sau dòng `builder.Services.AddSingleton<ILogService, LogService>();`:

```csharp
            builder.Services.AddSingleton<LocalizationService>();
```

- [ ] **Step 6: Build + Commit**

```powershell
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
git add src/RouterBalancing.Core/Localization "router balancing test/Localization" router-balancing/MauiProgram.cs
git commit -m "feat: add localization service with flat translation keys"
```

### Task 11: Dashboard shell — MainLayout + NavMenu + Dashboard

**Files:**
- Create: `router-balancing/Services/ThemeService.cs`, `router-balancing/Services/ToastService.cs`, `router-balancing/Components/Pages/Dashboard.razor`
- Modify: `router-balancing/Components/Layout/MainLayout.razor`, `router-balancing/Components/Layout/NavMenu.razor`, `router-balancing/Components/_Imports.razor`, `router-balancing/MauiProgram.cs`, `src/RouterBalancing.Core/Localization/Translations.cs`
- Delete: `router-balancing/Components/Pages/Home.razor`, `Counter.razor`, `Weather.razor`, `MainLayout.razor.css`, `NavMenu.razor.css` (chuyển sang Tailwind utility — không còn style scoped template)

**Interfaces:**
- Consumes: `IProxyHost` (Task 7), `ILogService` (Task 5), `LocalizationService` (Task 10), JS global `rbTheme` (Task 9), utility `btn btn-*` (Task 9)
- Produces:
  - `sealed class ThemeService` (namespace `router_balancing.Services`) — `Task ApplyAsync()` invoke `rbTheme.applyTheme(settings.Theme)`
  - `sealed record ToastItem(Guid Id, string Message, ToastSeverity Severity)`; `enum ToastSeverity { Info, Success, Error }`; `sealed class ToastService` — `event Action<ToastItem>? ToastAdded; void Show(string message, ToastSeverity severity = ToastSeverity.Info)`
  - `MainLayout` — sidebar + header (title + proxy status badge + toast stack), tự re-render khi `ProxyHost.StateChanged`
  - `NavMenu` — link Dashboard (Logs/Settings sẽ thêm ở Task 13/14 để mọi commit đều navigate được)
  - `Dashboard.razor` (`@page "/"`) — card trạng thái proxy + endpoint + nút Start/Stop/Restart
  - DI: `ThemeService`/`ToastService` **Scoped** (ThemeService phụ thuộc `IJSRuntime` — scoped không được inject vào singleton, nên để scoped cho Blazor resolve)

- [ ] **Step 1: Bổ sung i18n keys**

Thêm vào `Translations.English`:

```csharp
        ["dashboard.status.label"] = "Status",
        ["dashboard.status.running"] = "Running",
        ["dashboard.status.stopped"] = "Stopped",
        ["dashboard.action.start"] = "Start",
        ["dashboard.action.stop"] = "Stop",
        ["dashboard.action.restart"] = "Restart",
        ["dashboard.msg.started"] = "Proxy started.",
        ["dashboard.msg.stopped"] = "Proxy stopped.",
        ["dashboard.msg.restarted"] = "Proxy restarted.",
        ["dashboard.msg.failed"] = "Action failed. See Logs for details.",
```

Thêm vào `Translations.Vietnamese`:

```csharp
        ["dashboard.status.label"] = "Trạng thái",
        ["dashboard.status.running"] = "Đang chạy",
        ["dashboard.status.stopped"] = "Đã dừng",
        ["dashboard.action.start"] = "Khởi động",
        ["dashboard.action.stop"] = "Dừng",
        ["dashboard.action.restart"] = "Khởi động lại",
        ["dashboard.msg.started"] = "Đã khởi động proxy.",
        ["dashboard.msg.stopped"] = "Đã dừng proxy.",
        ["dashboard.msg.restarted"] = "Đã khởi động lại proxy.",
        ["dashboard.msg.failed"] = "Thao tác thất bại. Xem Nhật ký để biết chi tiết.",
```

- [ ] **Step 2: Tạo ThemeService + ToastService**

`router-balancing/Services/ThemeService.cs`:

```csharp
using RouterBalancing.Core.Settings;

namespace router_balancing.Services;

/// <summary>Áp theme từ setting lên DOM qua JS global rbTheme (xem vite-project/src/ts/main.ts).</summary>
public sealed class ThemeService(IAppSettingsService settings, IJSRuntime js)
{
    public async Task ApplyAsync() =>
        await js.InvokeVoidAsync("rbTheme.applyTheme", settings.Theme);
}
```

`router-balancing/Services/ToastService.cs`:

```csharp
namespace router_balancing.Services;

public enum ToastSeverity
{
    Info,
    Success,
    Error,
}

public sealed record ToastItem(Guid Id, string Message, ToastSeverity Severity);

/// <summary>Phát toast cho UI — Show từ bất kỳ đâu, MainLayout render và tự hủy sau vài giây.</summary>
public sealed class ToastService
{
    public event Action<ToastItem>? ToastAdded;

    public void Show(string message, ToastSeverity severity = ToastSeverity.Info) =>
        ToastAdded?.Invoke(new ToastItem(Guid.NewGuid(), message, severity));
}
```

- [ ] **Step 3: Đăng ký DI (Scoped — ThemeService cần IJSRuntime)**

Thêm vào `MauiProgram.cs` sau `AddSingleton<LocalizationService>()`:

```csharp
            builder.Services.AddScoped<ThemeService>();
            builder.Services.AddScoped<ToastService>();
```

và thêm usings:

```csharp
using router_balancing.Services;
```

- [ ] **Step 4: Cập nhật `_Imports.razor`**

Thêm 4 dòng:

```razor
@using RouterBalancing.Core.Server
@using RouterBalancing.Core.Localization
@using RouterBalancing.Core.Logging
@using router_balancing.Services
```

- [ ] **Step 5: Thay `NavMenu.razor` (thay toàn bộ)**

```razor
@inject LocalizationService L

<nav class="flex flex-col gap-1 p-3">
    <div class="px-3 pb-2 text-base font-semibold">@L["app.title"]</div>

    <NavLink class="rounded px-3 py-2 text-sm no-underline hover:bg-background"
             href="" Match="NavLinkMatch.All">
        @L["nav.dashboard"]
    </NavLink>
</nav>
```

Xóa `NavMenu.razor.css`.

- [ ] **Step 6: Thay `MainLayout.razor` (thay toàn bộ)**

```razor
@inherits LayoutComponentBase
@implements IDisposable
@inject IProxyHost ProxyHost
@inject ThemeService Theme
@inject LocalizationService L
@inject ToastService Toast

<div class="flex h-screen overflow-hidden bg-background text-text">
    <aside class="w-56 shrink-0 border-r border-border bg-surface">
        <NavMenu />
    </aside>

    <div class="flex min-w-0 flex-1 flex-col">
        <header class="flex items-center justify-between border-b border-border bg-surface px-4 py-2">
            <span class="text-sm font-semibold">@L["app.title"]</span>
            <span class="@_badgeClass">@_badgeText</span>
        </header>

        <main class="flex-1 overflow-auto p-4">
            @Body
        </main>
    </div>

    @* Toast stack: fixed góc dưới phải, z-index cao để nổi trên nội dung *@
    <div class="fixed bottom-4 right-4 z-50 flex flex-col gap-2">
        @foreach (var toast in _toasts)
        {
            <div class="@ToastClass(toast.Severity) rounded px-4 py-2 shadow" @key="toast.Id">
                @toast.Message
            </div>
        }
    </div>
</div>

@code {
    private string _badgeText = string.Empty;
    private string _badgeClass = string.Empty;
    private readonly List<ToastItem> _toasts = [];

    protected override async Task OnInitializedAsync()
    {
        ProxyHost.StateChanged += OnProxyStateChanged;
        Toast.ToastAdded += OnToastAdded;
        UpdateBadge();
        await Theme.ApplyAsync();
    }

    private void OnProxyStateChanged() => _ = InvokeAsync(() =>
    {
        UpdateBadge();
        StateHasChanged();
    });

    private void OnToastAdded(ToastItem item) => _ = InvokeAsync(async () =>
    {
        _toasts.Add(item);
        StateHasChanged();
        await Task.Delay(TimeSpan.FromSeconds(4));
        _toasts.Remove(item);
        StateHasChanged();
    });

    private void UpdateBadge()
    {
        // Badge dùng màu Tailwind literal — class động không được purge vì nằm sẵn trong string constant
        _badgeText = ProxyHost.IsRunning
            ? $"● {L["dashboard.status.running"]} : {ProxyHost.Port}"
            : $"○ {L["dashboard.status.stopped"]}";
        _badgeClass = ProxyHost.IsRunning
            ? "rounded-full bg-green-600 px-3 py-1 text-xs text-white"
            : "rounded-full bg-neutral-500 px-3 py-1 text-xs text-white";
    }

    private static string ToastClass(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => "bg-green-700 text-white",
        ToastSeverity.Error => "bg-red-700 text-white",
        _ => "bg-neutral-700 text-white",
    };

    public void Dispose()
    {
        ProxyHost.StateChanged -= OnProxyStateChanged;
        Toast.ToastAdded -= OnToastAdded;
    }
}
```

Xóa `MainLayout.razor.css`.

Lưu ý Tailwind purge: các class viết trong string C# (`bg-green-600`…) KHÔNG được scanner của Tailwind 4 nhìn thấy (scanner chỉ quét `.razor`/`.ts`). Step 8 bổ sung chúng vào `tailwind.config.js` safelist.

- [ ] **Step 7: Tạo `Dashboard.razor` (thay `Home.razor`)**

Xóa `Home.razor`, `Counter.razor`, `Weather.razor`. Tạo `Components/Pages/Dashboard.razor`:

```razor
@page "/"
@implements IDisposable
@inject IProxyHost Proxy
@inject ILogService Log
@inject ToastService Toast
@inject LocalizationService L

<h1 class="mb-4 text-xl font-semibold">@L["dashboard.title"]</h1>

<div class="mb-4 rounded border border-border bg-surface p-4">
    <div class="flex items-center justify-between">
        <div>
            <div class="text-sm opacity-70">@L["dashboard.status.label"]</div>
            <div class="mt-1 text-lg font-medium">
                @(Proxy.IsRunning ? L["dashboard.status.running"] : L["dashboard.status.stopped"])
            </div>
            @if (Proxy.IsRunning)
            {
                <div class="mt-1 font-mono text-sm">http://127.0.0.1:@Proxy.Port</div>
            }
        </div>

        <div class="flex gap-2">
            @if (!Proxy.IsRunning)
            {
                <button class="btn btn-primary" @onclick="StartAsync">@L["dashboard.action.start"]</button>
            }
            else
            {
                <button class="btn btn-danger" @onclick="StopAsync">@L["dashboard.action.stop"]</button>
                <button class="btn btn-secondary" @onclick="RestartAsync">@L["dashboard.action.restart"]</button>
            }
        </div>
    </div>
</div>

@code {
    protected override void OnInitialized() => Proxy.StateChanged += OnStateChanged;

    private void OnStateChanged() => _ = InvokeAsync(StateHasChanged);

    private async Task StartAsync()
    {
        try
        {
            await Proxy.StartAsync();
            Log.Info("Proxy được khởi động từ Dashboard.");
            Toast.Show(L["dashboard.msg.started"], ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Khởi động proxy thất bại từ Dashboard.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await Proxy.StopAsync();
            Log.Info("Proxy được dừng từ Dashboard.");
            Toast.Show(L["dashboard.msg.stopped"], ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Dừng proxy thất bại từ Dashboard.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
    }

    private async Task RestartAsync()
    {
        try
        {
            await Proxy.RestartAsync();
            Log.Info("Proxy được khởi động lại từ Dashboard.");
            Toast.Show(L["dashboard.msg.restarted"], ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Khởi động lại proxy thất bại từ Dashboard.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
    }

    public void Dispose() => Proxy.StateChanged -= OnStateChanged;
}
```

- [ ] **Step 8: Safelist class động vào `tailwind.config.js`**

```js
  safelist: [
    'bg-green-600', 'bg-neutral-500',
    'bg-green-700', 'bg-red-700', 'bg-neutral-700',
    'text-white',
  ],
```

- [ ] **Step 9: Build frontend + app (verify)**

```powershell
npm run build
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
```

(Task này là UI thuần — không unit test; logic nghiệp vụ đã nằm ở service có test. Verify = build xanh + checklist chạy tay ở cuối plan.)

- [ ] **Step 10: Commit**

```powershell
git add router-balancing/Components router-balancing/Services router-balancing/MauiProgram.cs router-balancing/vite-project/tailwind.config.js src/RouterBalancing.Core/Localization
git commit -m "feat: add dashboard shell with status badge, toasts and theme application"
```

### Task 12: Tray hệ thống + close-to-tray + khởi động cùng Windows (Windows)

**Files:**
- Create: `src/RouterBalancing.Core/Platform/ITrayService.cs`, `IStartupRegistration.cs`, `NullTrayService.cs`, `NullStartupRegistration.cs`
- Create: `router-balancing/Platforms/Windows/TrayService.cs`, `router-balancing/Platforms/Windows/StartupRegistration.cs`
- Modify: `router-balancing/router-balancing.csproj` (FrameworkReference WinForms), `router-balancing/MauiProgram.cs` (DI `#if WINDOWS`), `router-balancing/App.xaml.cs` (4 service + tray + close-to-tray + exit flow), `src/RouterBalancing.Core/Localization/Translations.cs`
- Test: không có unit test mới — phần này là platform interop (WinForms NotifyIcon, WinUI `AppWindow.Closing`, registry HKCU) không chạy được trong test process; verify = build xanh + checklist chạy tay (Step 6). Logic nghiệp vụ tiêu dùng (`CloseToTray`, `StartWithWindows`) đã được test ở Task 4.

**Interfaces:**
- Consumes: `IAppSettingsService.CloseToTray/StartWithWindows` (Task 4), `IProxyHost.StopAsync` (Task 7), `LocalizationService` (Task 10), `ITrayService`/`IStartupRegistration` sẽ được `SettingsPanel` (Task 13) inject
- Produces:
  - `ITrayService : IDisposable` (ns `RouterBalancing.Core.Platform`) — `event Action? OpenRequested`, `event Action? ExitRequested`, `void Initialize()`; impl Windows `TrayService` (WinForms `NotifyIcon`), impl mặc định `NullTrayService` (no-op, event accessor rỗng)
  - `IStartupRegistration` — `bool IsEnabled { get; }`, `void SetEnabled(bool enabled)`; impl Windows `StartupRegistration` (HKCU Run), mặc định `NullStartupRegistration`
  - `App`: window close → ẩn về khay khi `closeToTray=true`; tray Exit → dừng proxy → quit; registry Run đồng bộ theo setting khi khởi động

- [ ] **Step 1: Core interfaces + null impls**

`src/RouterBalancing.Core/Platform/ITrayService.cs`:

```csharp
namespace RouterBalancing.Core.Platform;

/// <summary>Biểu tượng khay hệ thống — bấm đúp/mở → hiện cửa sổ, Exit → thoát app.</summary>
public interface ITrayService : IDisposable
{
    /// <summary>Yêu cầu hiện lại cửa sổ chính.</summary>
    event Action? OpenRequested;

    /// <summary>Yêu cầu thoát app hoàn toàn (khác với đóng cửa sổ về khay).</summary>
    event Action? ExitRequested;

    /// <summary>Tạo icon trên khay — gọi sau khi DI sẵn sàng; idempotent.</summary>
    void Initialize();
}
```

`src/RouterBalancing.Core/Platform/IStartupRegistration.cs`:

```csharp
namespace RouterBalancing.Core.Platform;

/// <summary>Đăng ký app tự khởi động cùng hệ điều hành khi máy đăng nhập.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
```

`src/RouterBalancing.Core/Platform/NullTrayService.cs`:

```csharp
namespace RouterBalancing.Core.Platform;

/// <summary>Không khay hệ thống trên nền tảng không phải Windows — no-op để DI luôn resolve.</summary>
public sealed class NullTrayService : ITrayService
{
    // Accessor rỗng tường minh: không phát event, không tạo warning CS0067
    public event Action? OpenRequested { add { } remove { } }

    public event Action? ExitRequested { add { } remove { } }

    public void Initialize()
    {
    }

    public void Dispose()
    {
    }
}
```

`src/RouterBalancing.Core/Platform/NullStartupRegistration.cs`:

```csharp
namespace RouterBalancing.Core.Platform;

/// <summary>Nền tảng không phải Windows không có mục Run — no-op (Phase 1 Windows-first).</summary>
public sealed class NullStartupRegistration : IStartupRegistration
{
    public bool IsEnabled => false;

    public void SetEnabled(bool enabled)
    {
    }
}
```

- [ ] **Step 2: WinForms reference trong csproj**

Thêm ItemGroup sau nhóm PackageReference hiện có trong `router-balancing/router-balancing.csproj`:

```xml
    <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'">
        <!-- NotifyIcon (khay hệ thống) cần WinForms; chỉ trên TFM windows -->
        <FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />
    </ItemGroup>
```

- [ ] **Step 3: Windows impls**

`router-balancing/Platforms/Windows/TrayService.cs`:

```csharp
using System.Drawing;
using System.Windows.Forms;
using RouterBalancing.Core.Localization;
using RouterBalancing.Core.Platform;

namespace router_balancing.Platforms.Windows;

/// <summary>
/// Icon khay bằng WinForms NotifyIcon — chạy trên UI thread WinUI
/// (đã có message pump nên không cần Application.Run).
/// </summary>
internal sealed class TrayService : ITrayService
{
    private readonly LocalizationService _localization;
    private NotifyIcon? _icon;
    private ToolStripMenuItem _openItem = null!;
    private ToolStripMenuItem _exitItem = null!;

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public TrayService(LocalizationService localization) => _localization = localization;

    public void Initialize()
    {
        if (_icon is not null) return;

        _openItem = new ToolStripMenuItem(_localization["tray.open"], null, (_, _) => OpenRequested?.Invoke());
        _exitItem = new ToolStripMenuItem(_localization["tray.exit"], null, (_, _) => ExitRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _icon = new NotifyIcon
        {
            // Icon hệ thống — Phase 1 chưa đóng gói .ico riêng
            Icon = SystemIcons.Application,
            Text = _localization["app.title"],
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        _localization.LanguageChanged += UpdateMenuTexts;
    }

    private void UpdateMenuTexts()
    {
        _openItem.Text = _localization["tray.open"];
        _exitItem.Text = _localization["tray.exit"];
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _localization.LanguageChanged -= UpdateMenuTexts;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
```

`router-balancing/Platforms/Windows/StartupRegistration.cs`:

```csharp
using Microsoft.Win32;
using RouterBalancing.Core.Platform;

namespace router_balancing.Platforms.Windows;

/// <summary>HKCU Run — không cần quyền admin; giá trị là đường dẫn exe bọc trong nháy kép.</summary>
internal sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "router-balancing";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Không xác định được đường dẫn exe để đăng ký khởi động.");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
```

- [ ] **Step 4: i18n keys cho tray**

Thêm vào `Translations.English`:

```csharp
        ["tray.open"] = "Open router-balancing",
        ["tray.exit"] = "Exit",
```

Thêm vào `Translations.Vietnamese`:

```csharp
        ["tray.open"] = "Mở router-balancing",
        ["tray.exit"] = "Thoát",
```

- [ ] **Step 5: DI + App lifecycle**

`MauiProgram.cs` — thêm usings:

```csharp
using RouterBalancing.Core.Platform;
#if WINDOWS
using router_balancing.Platforms.Windows;
#endif
```

Thêm registrations sau `AddScoped<ToastService>()`:

```csharp
            // Tray + tự khởi động cùng Windows: bản Windows thật, nền tảng khác là no-op
#if WINDOWS
            builder.Services.AddSingleton<ITrayService, TrayService>();
            builder.Services.AddSingleton<IStartupRegistration, StartupRegistration>();
#else
            builder.Services.AddSingleton<ITrayService, NullTrayService>();
            builder.Services.AddSingleton<IStartupRegistration, NullStartupRegistration>();
#endif
```

`router-balancing/App.xaml.cs` (thay toàn bộ — tích hợp Task 8 + Task 12):

```csharp
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Platform;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
#if WINDOWS
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
#endif

namespace router_balancing
{
    public partial class App : Application
    {
        private readonly ILogService _log;
        private readonly IProxyHost _proxyHost;
        private readonly IAppSettingsService _settings;
        private readonly IStartupRegistration _startup;
        private readonly ITrayService _tray;
        private Window? _mainWindow;
        private bool _reallyExiting;

        public App(
            ILogService log,
            IProxyHost proxyHost,
            IAppSettingsService settings,
            IStartupRegistration startup,
            ITrayService tray)
        {
            InitializeComponent();
            _log = log;
            _proxyHost = proxyHost;
            _settings = settings;
            _startup = startup;
            _tray = tray;

            _log.Info("router-balancing khởi động.");

            _tray.OpenRequested += ShowMainWindow;
            _tray.ExitRequested += ExitFromTray;
            _tray.Initialize();

            SyncStartupRegistration();
            // Ngắt kết nối socket Kestrel khi process thoát — kể cả khi người dùng tắt từ Task Manager
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopProxyOnExit();

            // Start nền để UI render không bị Kestrel block; lỗi chỉ log, không crash app
            _ = Task.Run(async () =>
            {
                try
                {
                    await _proxyHost.StartAsync();
                }
                catch (Exception ex)
                {
                    _log.Error("Không khởi động được proxy server.", ex);
                }
            });
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new MainPage()) { Title = "router-balancing" };
            _mainWindow = window;
#if WINDOWS
            AttachCloseToTray(window);
#endif
            return window;
        }

        /// <summary>Setting là nguồn sự thật — đồng bộ registry khi app khởi động (user có thể sửa tay ngoài app).</summary>
        private void SyncStartupRegistration()
        {
            try
            {
                if (_startup.IsEnabled != _settings.StartWithWindows)
                {
                    _startup.SetEnabled(_settings.StartWithWindows);
                }
            }
            catch (Exception ex)
            {
                // Không chặn app khởi động chỉ vì registry bị chính sách máy chặn ghi
                _log.Error("Không đồng bộ được mục khởi động cùng Windows.", ex);
            }
        }

        private void ShowMainWindow()
        {
            if (_mainWindow is null) return;
#if WINDOWS
            if (GetAppWindow(_mainWindow) is { } appWindow)
            {
                appWindow.Show();
            }
#endif
            _mainWindow.Activate();
        }

        /// <summary>Thoát hẳn từ menu tray: dừng proxy trước, giải phóng icon, rồi quit.</summary>
        private async void ExitFromTray()
        {
            _reallyExiting = true;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _proxyHost.StopAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _log.Error("Dừng proxy khi thoát app thất bại.", ex);
            }
            _tray.Dispose();
            Current.Quit();
        }

        private void StopProxyOnExit()
        {
            try
            {
                // ProcessExit không chờ async — blocking với timeout để không treo process
                _proxyHost.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log.Error("Dừng proxy khi process thoát thất bại.", ex);
            }
        }

#if WINDOWS
        private void AttachCloseToTray(Window window)
        {
            window.HandlerChanged += (_, _) =>
            {
                if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.Closing += (_, args) =>
                {
                    // Thoát từ tray thì cho phép đóng; ngược lại closeToTray=true → hủy đóng, ẩn cửa sổ
                    if (_reallyExiting || !_settings.CloseToTray) return;
                    args.Cancel = true;
                    appWindow.Hide();
                    _log.Info("Đóng cửa sổ → thu về khay hệ thống.");
                };
            };
        }

        private static AppWindow? GetAppWindow(Window window)
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return null;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(windowId);
        }
#endif
    }
}
```

- [ ] **Step 6: Build + checklist chạy tay**

```powershell
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: build `0 Error(s)`; test `Passed! - Failed: 0, Total: 44`.

Checklist chạy tay (F5 hoặc `dotnet run`):
1. Icon khay xuất hiện; bấm đúp → cửa sổ hiện ra.
2. Đóng cửa sổ (X) với `closeToTray=true` (mặc định) → app vẫn sống, icon khay còn; bấm đúp mở lại.
3. Menu tray → Exit → process biến mất (Task Manager), port 8317 giải phóng.
4. Đặt `closeToTray=false` (Task 13 sẽ có UI; tạm sửa DB hoặc để Task 13 verify) → X = thoát app.
5. Bật `startWithWindows` → `regedit` thấy `HKCU\...\Run\router-balancing`; tắt → giá trị biến mất.

- [ ] **Step 7: Commit**

```powershell
git add src/RouterBalancing.Core/Platform src/RouterBalancing.Core/Localization router-balancing/Platforms/Windows router-balancing/router-balancing.csproj router-balancing/MauiProgram.cs router-balancing/App.xaml.cs
git commit -m "feat: add windows tray, close-to-tray and startup registration"
```

### Task 13: SettingsValidator (TDD) + SettingsPanel

**Files:**
- Create: `src/RouterBalancing.Core/Settings/SettingsDraft.cs`, `Settings/SettingsValidator.cs`
- Test: `router balancing test/Settings/SettingsValidatorTests.cs`
- Create: `router-balancing/Components/Pages/SettingsPanel.razor`
- Modify: `router-balancing/Components/Layout/NavMenu.razor` (link Settings + re-render khi đổi ngôn ngữ), `router-balancing/Components/Layout/MainLayout.razor` (LanguageChanged), `src/RouterBalancing.Core/Localization/Translations.cs` (keys `settings.*`)

**Interfaces:**
- Consumes: Task 4 (`IAppSettingsService`, `SettingsKeys`), Task 6 (`SetApiKeyEnabled`), Task 7 (`IProxyHost.RestartAsync`), Task 10 (`LocalizationService.Reload`), Task 11 (`ThemeService`, `ToastService`), Task 12 (`IStartupRegistration`), Task 2 (`StoragePathProvider`)
- Produces (namespace `RouterBalancing.Core.Settings`):
  - `sealed record SettingsDraft` — toàn bộ field của form Settings, property **mutable** (`get; set;`). Lý do: `@bind` của Blazor cần setter, record positional (`init`-only) không bind được. Kể cả `CloseToTray`/`StartWithWindows` (bool không cần validate) để cả form state nằm một chỗ.
  - `static class SettingsValidator` — `IReadOnlyDictionary<string, string> Validate(SettingsDraft draft)`; key = `nameof(SettingsDraft.X)`, value = **i18n error key** (`settings.error.*`). Validator không chứa text EN/VI — UI dịch key theo ngôn ngữ hiện tại.
  - `SettingsPanel.razor` (`@page "/settings"`) — 4 nhóm General/Server/Engine/Data (spec §8), Save từng nhóm, lỗi hiện inline đúng nhóm.

**Bảng rule validate:**

| Field | Range / Enum | Error key |
|---|---|---|
| `Language` | `auto` / `en` / `vi` | `settings.error.language` |
| `Theme` | `light` / `dark` / `system` | `settings.error.theme` |
| `Port` | 1024–65535 | `settings.error.port` |
| `MaxRetry` | 1–10 | `settings.error.maxRetry` |
| `WatchdogIntervalSec` | 10–86400 | `settings.error.watchdog` |
| `DefaultMaxConcurrent` | 1–64 | `settings.error.maxConcurrent` |
| `LogRetentionDays` | 1–3650 | `settings.error.retention` |
| `StatsErrorRateThreshold` | 1–100 | `settings.error.threshold` |
| `ApiKey` | bắt buộc khi `ApiKeyEnabled=true` | `settings.error.apiKey` |

- [ ] **Step 1: Bổ sung i18n keys `settings.*`**

Thêm vào `Translations.English`:

```csharp
        // Settings panel (Task 13)
        ["settings.group.general"] = "General",
        ["settings.group.server"] = "Server",
        ["settings.group.engine"] = "Engine",
        ["settings.group.data"] = "Data",
        ["settings.field.language"] = "Language",
        ["settings.lang.auto"] = "Auto (system)",
        ["settings.lang.en"] = "English",
        ["settings.lang.vi"] = "Vietnamese",
        ["settings.field.theme"] = "Theme",
        ["settings.theme.light"] = "Light",
        ["settings.theme.dark"] = "Dark",
        ["settings.theme.system"] = "System",
        ["settings.field.closeToTray"] = "Close window to tray",
        ["settings.field.startWithWindows"] = "Start with Windows",
        ["settings.field.port"] = "Port",
        ["settings.field.apiKeyEnabled"] = "Require API key",
        ["settings.field.apiKey"] = "API key",
        ["settings.field.maxRetry"] = "Max retries",
        ["settings.field.watchdog"] = "Watchdog interval (s)",
        ["settings.field.maxConcurrent"] = "Default max concurrent",
        ["settings.field.retention"] = "Log retention (days)",
        ["settings.field.threshold"] = "Error rate threshold (%)",
        ["settings.field.dbPath"] = "Database file",
        ["settings.action.save"] = "Save",
        ["settings.action.regenerate"] = "Regenerate",
        ["settings.msg.saved"] = "Saved.",
        ["settings.msg.portRestart"] = "Port changed — proxy restarted.",
        ["settings.msg.startupFailed"] = "Cannot update start with Windows — see Logs.",
        ["settings.error.port"] = "Port must be between 1024 and 65535.",
        ["settings.error.language"] = "Language must be Auto, English or Vietnamese.",
        ["settings.error.theme"] = "Theme must be Light, Dark or System.",
        ["settings.error.maxRetry"] = "Max retries must be between 1 and 10.",
        ["settings.error.watchdog"] = "Watchdog interval must be between 10 and 86400 seconds.",
        ["settings.error.maxConcurrent"] = "Max concurrent must be between 1 and 64.",
        ["settings.error.retention"] = "Log retention must be between 1 and 3650 days.",
        ["settings.error.threshold"] = "Error rate threshold must be between 1 and 100.",
        ["settings.error.apiKey"] = "API key is required when enabled.",
```

Thêm vào `Translations.Vietnamese`:

```csharp
        // Settings panel (Task 13)
        ["settings.group.general"] = "Chung",
        ["settings.group.server"] = "Máy chủ",
        ["settings.group.engine"] = "Engine",
        ["settings.group.data"] = "Dữ liệu",
        ["settings.field.language"] = "Ngôn ngữ",
        ["settings.lang.auto"] = "Tự động (hệ thống)",
        ["settings.lang.en"] = "English",
        ["settings.lang.vi"] = "Tiếng Việt",
        ["settings.field.theme"] = "Giao diện",
        ["settings.theme.light"] = "Sáng",
        ["settings.theme.dark"] = "Tối",
        ["settings.theme.system"] = "Theo hệ thống",
        ["settings.field.closeToTray"] = "Đóng cửa sổ về khay",
        ["settings.field.startWithWindows"] = "Khởi động cùng Windows",
        ["settings.field.port"] = "Cổng",
        ["settings.field.apiKeyEnabled"] = "Yêu cầu API key",
        ["settings.field.apiKey"] = "API key",
        ["settings.field.maxRetry"] = "Số lần thử lại tối đa",
        ["settings.field.watchdog"] = "Chu kỳ watchdog (giây)",
        ["settings.field.maxConcurrent"] = "Số request đồng thời tối đa",
        ["settings.field.retention"] = "Giữ nhật ký (ngày)",
        ["settings.field.threshold"] = "Ngưỡng tỷ lệ lỗi (%)",
        ["settings.field.dbPath"] = "File cơ sở dữ liệu",
        ["settings.action.save"] = "Lưu",
        ["settings.action.regenerate"] = "Tạo lại",
        ["settings.msg.saved"] = "Đã lưu.",
        ["settings.msg.portRestart"] = "Đã đổi cổng — proxy khởi động lại.",
        ["settings.msg.startupFailed"] = "Không cập nhật được khởi động cùng Windows — xem Nhật ký.",
        ["settings.error.port"] = "Cổng phải nằm trong khoảng 1024–65535.",
        ["settings.error.language"] = "Ngôn ngữ phải là Tự động, English hoặc Tiếng Việt.",
        ["settings.error.theme"] = "Giao diện phải là Sáng, Tối hoặc Theo hệ thống.",
        ["settings.error.maxRetry"] = "Số lần thử lại phải từ 1–10.",
        ["settings.error.watchdog"] = "Chu kỳ watchdog phải từ 10–86400 giây.",
        ["settings.error.maxConcurrent"] = "Số request đồng thời phải từ 1–64.",
        ["settings.error.retention"] = "Giữ nhật ký phải từ 1–3650 ngày.",
        ["settings.error.threshold"] = "Ngưỡng tỷ lệ lỗi phải từ 1–100.",
        ["settings.error.apiKey"] = "Bắt buộc nhập API key khi bật yêu cầu.",
```

- [ ] **Step 2: Viết failing test**

Tạo `router balancing test/Settings/SettingsValidatorTests.cs`:

```csharp
using RouterBalancing.Core.Settings;

namespace router_balancing_test.Settings;

public class SettingsValidatorTests
{
    private static SettingsDraft ValidDraft() => new()
    {
        Language = "auto",
        Theme = "system",
        Port = 8317,
        MaxRetry = 3,
        WatchdogIntervalSec = 60,
        DefaultMaxConcurrent = 4,
        LogRetentionDays = 90,
        StatsErrorRateThreshold = 10,
    };

    [Fact]
    public void Validate_WhenDraftValid_ReturnsEmpty()
    {
        var errors = SettingsValidator.Validate(ValidDraft());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenPortOutOfRange_ReturnsPortError()
    {
        var low = SettingsValidator.Validate(ValidDraft() with { Port = 80 });
        var high = SettingsValidator.Validate(ValidDraft() with { Port = 70000 });

        Assert.Equal("settings.error.port", low[nameof(SettingsDraft.Port)]);
        Assert.Equal("settings.error.port", high[nameof(SettingsDraft.Port)]);
    }

    [Fact]
    public void Validate_WhenLanguageInvalid_ReturnsLanguageError()
    {
        var errors = SettingsValidator.Validate(ValidDraft() with { Language = "fr" });

        Assert.Equal("settings.error.language", errors[nameof(SettingsDraft.Language)]);
    }

    [Fact]
    public void Validate_WhenThemeInvalid_ReturnsThemeError()
    {
        var errors = SettingsValidator.Validate(ValidDraft() with { Theme = "blue" });

        Assert.Equal("settings.error.theme", errors[nameof(SettingsDraft.Theme)]);
    }

    [Fact]
    public void Validate_WhenEngineValuesOutOfRange_ReturnsEachFieldError()
    {
        var errors = SettingsValidator.Validate(ValidDraft() with
        {
            MaxRetry = 0,
            WatchdogIntervalSec = 5,
            DefaultMaxConcurrent = 65,
            LogRetentionDays = 0,
            StatsErrorRateThreshold = 101,
        });

        Assert.Equal(5, errors.Count);
        Assert.Equal("settings.error.maxRetry", errors[nameof(SettingsDraft.MaxRetry)]);
        Assert.Equal("settings.error.watchdog", errors[nameof(SettingsDraft.WatchdogIntervalSec)]);
        Assert.Equal("settings.error.maxConcurrent", errors[nameof(SettingsDraft.DefaultMaxConcurrent)]);
        Assert.Equal("settings.error.retention", errors[nameof(SettingsDraft.LogRetentionDays)]);
        Assert.Equal("settings.error.threshold", errors[nameof(SettingsDraft.StatsErrorRateThreshold)]);
    }

    [Fact]
    public void Validate_WhenApiKeyEnabledWithoutKey_ReturnsApiKeyError()
    {
        var errors = SettingsValidator.Validate(
            ValidDraft() with { ApiKeyEnabled = true, ApiKey = " " });

        Assert.Equal("settings.error.apiKey", errors[nameof(SettingsDraft.ApiKey)]);
    }
}
```

- [ ] **Step 3: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — `SettingsDraft`/`SettingsValidator` chưa tồn tại.

- [ ] **Step 4: Viết implementation**

`src/RouterBalancing.Core/Settings/SettingsDraft.cs`:

```csharp
namespace RouterBalancing.Core.Settings;

/// <summary>
/// Bản nháp toàn bộ form Settings — nạp từ <see cref="IAppSettingsService"/> khi mở trang,
/// validate rồi ghi lại theo từng nhóm. Property mutable để Blazor <c>@bind</c> nối trực tiếp
/// (record positional là init-only nên không bind được).
/// </summary>
public sealed record SettingsDraft
{
    public string Language { get; set; } = "auto";

    public string Theme { get; set; } = "system";

    public int Port { get; set; } = 8317;

    public bool ApiKeyEnabled { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    public bool CloseToTray { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public int MaxRetry { get; set; } = 3;

    public int WatchdogIntervalSec { get; set; } = 60;

    public int DefaultMaxConcurrent { get; set; } = 4;

    public int LogRetentionDays { get; set; } = 90;

    public int StatsErrorRateThreshold { get; set; } = 10;
}
```

`src/RouterBalancing.Core/Settings/SettingsValidator.cs`:

```csharp
namespace RouterBalancing.Core.Settings;

/// <summary>
/// Validate SettingsDraft trước khi ghi setting — trả map tên field → key lỗi i18n.
/// Validator không chứa text hiển thị: UI dịch key sang ngôn ngữ hiện tại nên
/// thêm ngôn ngữ mới không phải đụng vào đây.
/// </summary>
public static class SettingsValidator
{
    /// <summary>Kiểm tra toàn bộ rule của bảng Settings; trả rỗng khi hợp lệ.</summary>
    public static IReadOnlyDictionary<string, string> Validate(SettingsDraft draft)
    {
        var errors = new Dictionary<string, string>();

        if (draft.Language is not ("auto" or "en" or "vi"))
            errors[nameof(SettingsDraft.Language)] = "settings.error.language";

        if (draft.Theme is not ("light" or "dark" or "system"))
            errors[nameof(SettingsDraft.Theme)] = "settings.error.theme";

        if (draft.Port is < 1024 or > 65535)
            errors[nameof(SettingsDraft.Port)] = "settings.error.port";

        if (draft.MaxRetry is < 1 or > 10)
            errors[nameof(SettingsDraft.MaxRetry)] = "settings.error.maxRetry";

        if (draft.WatchdogIntervalSec is < 10 or > 86400)
            errors[nameof(SettingsDraft.WatchdogIntervalSec)] = "settings.error.watchdog";

        if (draft.DefaultMaxConcurrent is < 1 or > 64)
            errors[nameof(SettingsDraft.DefaultMaxConcurrent)] = "settings.error.maxConcurrent";

        if (draft.LogRetentionDays is < 1 or > 3650)
            errors[nameof(SettingsDraft.LogRetentionDays)] = "settings.error.retention";

        if (draft.StatsErrorRateThreshold is < 1 or > 100)
            errors[nameof(SettingsDraft.StatsErrorRateThreshold)] = "settings.error.threshold";

        if (draft.ApiKeyEnabled && string.IsNullOrWhiteSpace(draft.ApiKey))
            errors[nameof(SettingsDraft.ApiKey)] = "settings.error.apiKey";

        return errors;
    }
}
```

- [ ] **Step 5: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 50` (44 cũ + 6 mới).

- [ ] **Step 6: Tạo `SettingsPanel.razor`**

Tạo `router-balancing/Components/Pages/SettingsPanel.razor`:

```razor
@page "/settings"
@using System.Security.Cryptography
@using RouterBalancing.Core.Platform
@using RouterBalancing.Core.Settings
@using RouterBalancing.Core.Storage
@implements IDisposable
@inject IAppSettingsService Settings
@inject LocalizationService L
@inject ThemeService Theme
@inject ToastService Toast
@inject IProxyHost Proxy
@inject IStartupRegistration Startup
@inject ILogService Log

<h1 class="mb-4 text-xl font-semibold">@L["panel.settings.title"]</h1>

@* Spec §8: 4 nhóm form, Save từng nhóm, validate inline *@
<section class="mb-4 rounded border border-border bg-surface p-4">
    <h2 class="mb-3 text-base font-semibold">@L["settings.group.general"]</h2>

    <div class="grid gap-3 sm:grid-cols-2">
        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.language"]
            <select class="rounded border border-border bg-surface px-2 py-1.5" @bind="_draft.Language">
                <option value="auto">@L["settings.lang.auto"]</option>
                <option value="en">@L["settings.lang.en"]</option>
                <option value="vi">@L["settings.lang.vi"]</option>
            </select>
        </label>

        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.theme"]
            <select class="rounded border border-border bg-surface px-2 py-1.5" @bind="_draft.Theme">
                <option value="light">@L["settings.theme.light"]</option>
                <option value="dark">@L["settings.theme.dark"]</option>
                <option value="system">@L["settings.theme.system"]</option>
            </select>
        </label>
    </div>

    <label class="mt-3 flex items-center gap-2 text-sm">
        <input type="checkbox" @bind="_draft.CloseToTray" />
        @L["settings.field.closeToTray"]
    </label>
    <label class="flex items-center gap-2 text-sm">
        <input type="checkbox" @bind="_draft.StartWithWindows" />
        @L["settings.field.startWithWindows"]
    </label>

    @if (Error(nameof(SettingsDraft.Language)) is { } languageError)
    {
        <div class="mt-1 text-sm text-danger">@L[languageError]</div>
    }
    @if (Error(nameof(SettingsDraft.Theme)) is { } themeError)
    {
        <div class="mt-1 text-sm text-danger">@L[themeError]</div>
    }

    <button class="btn btn-primary mt-3" @onclick="SaveGeneralAsync">@L["settings.action.save"]</button>
</section>

<section class="mb-4 rounded border border-border bg-surface p-4">
    <h2 class="mb-3 text-base font-semibold">@L["settings.group.server"]</h2>

    <div class="grid gap-3 sm:grid-cols-2">
        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.port"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   type="number" @bind="_draft.Port" />
        </label>

        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.apiKey"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   type="password" @bind="_draft.ApiKey" autocomplete="off" spellcheck="false" />
        </label>
    </div>

    @if (Error(nameof(SettingsDraft.Port)) is { } portError)
    {
        <div class="mt-1 text-sm text-danger">@L[portError]</div>
    }
    @if (Error(nameof(SettingsDraft.ApiKey)) is { } apiKeyError)
    {
        <div class="mt-1 text-sm text-danger">@L[apiKeyError]</div>
    }

    <label class="mt-3 flex items-center gap-2 text-sm">
        <input type="checkbox" @bind="_draft.ApiKeyEnabled" />
        @L["settings.field.apiKeyEnabled"]
    </label>

    <div class="mt-3 flex gap-2">
        <button class="btn btn-primary" @onclick="SaveServerAsync">@L["settings.action.save"]</button>
        <button class="btn btn-outline-secondary" @onclick="RegenerateApiKey">@L["settings.action.regenerate"]</button>
    </div>
</section>

<section class="mb-4 rounded border border-border bg-surface p-4">
    <h2 class="mb-3 text-base font-semibold">@L["settings.group.engine"]</h2>

    <div class="grid gap-3 sm:grid-cols-3">
        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.maxRetry"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   type="number" @bind="_draft.MaxRetry" />
        </label>
        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.watchdog"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   type="number" @bind="_draft.WatchdogIntervalSec" />
        </label>
        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.maxConcurrent"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   type="number" @bind="_draft.DefaultMaxConcurrent" />
        </label>
    </div>

    @if (Error(nameof(SettingsDraft.MaxRetry)) is { } maxRetryError)
    {
        <div class="mt-1 text-sm text-danger">@L[maxRetryError]</div>
    }
    @if (Error(nameof(SettingsDraft.WatchdogIntervalSec)) is { } watchdogError)
    {
        <div class="mt-1 text-sm text-danger">@L[watchdogError]</div>
    }
    @if (Error(nameof(SettingsDraft.DefaultMaxConcurrent)) is { } concurrentError)
    {
        <div class="mt-1 text-sm text-danger">@L[concurrentError]</div>
    }

    <button class="btn btn-primary mt-3" @onclick="SaveEngine">@L["settings.action.save"]</button>
</section>

<section class="mb-4 rounded border border-border bg-surface p-4">
    <h2 class="mb-3 text-base font-semibold">@L["settings.group.data"]</h2>

    <div class="grid gap-3 sm:grid-cols-2">
        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.retention"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   type="number" @bind="_draft.LogRetentionDays" />
        </label>
        <label class="flex flex-col gap-1 text-sm">
            @L["settings.field.threshold"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   type="number" @bind="_draft.StatsErrorRateThreshold" />
        </label>
        <label class="flex flex-col gap-1 text-sm sm:col-span-2">
            @L["settings.field.dbPath"]
            @* Read-only: người dùng không sửa đường dẫn tay — app quản lý file DB *@
            <input class="rounded border border-border bg-background px-2 py-1.5 opacity-70"
                   value="@StoragePathProvider.GetDatabasePath()" readonly />
        </label>
    </div>

    @if (Error(nameof(SettingsDraft.LogRetentionDays)) is { } retentionError)
    {
        <div class="mt-1 text-sm text-danger">@L[retentionError]</div>
    }
    @if (Error(nameof(SettingsDraft.StatsErrorRateThreshold)) is { } thresholdError)
    {
        <div class="mt-1 text-sm text-danger">@L[thresholdError]</div>
    }

    <button class="btn btn-primary mt-3" @onclick="SaveData">@L["settings.action.save"]</button>
</section>

@code {
    /// <summary>Field validator từng nhóm — Save nhóm nào chỉ hiện lỗi của nhóm đó.</summary>
    private static readonly string[] GeneralFields =
    [
        nameof(SettingsDraft.Language),
        nameof(SettingsDraft.Theme),
    ];
    private static readonly string[] ServerFields =
    [
        nameof(SettingsDraft.Port),
        nameof(SettingsDraft.ApiKey),
    ];
    private static readonly string[] EngineFields =
    [
        nameof(SettingsDraft.MaxRetry),
        nameof(SettingsDraft.WatchdogIntervalSec),
        nameof(SettingsDraft.DefaultMaxConcurrent),
    ];
    private static readonly string[] DataFields =
    [
        nameof(SettingsDraft.LogRetentionDays),
        nameof(SettingsDraft.StatsErrorRateThreshold),
    ];

    private SettingsDraft _draft = new();
    private Dictionary<string, string> _errors = [];

    protected override void OnInitialized()
    {
        _draft = LoadDraft();
        L.LanguageChanged += OnLanguageChanged;
    }

    private SettingsDraft LoadDraft() => new()
    {
        Language = Settings.Language,
        Theme = Settings.Theme,
        Port = Settings.Port,
        ApiKeyEnabled = Settings.ApiKeyEnabled,
        ApiKey = Settings.GetApiKey(),
        CloseToTray = Settings.CloseToTray,
        StartWithWindows = Settings.StartWithWindows,
        MaxRetry = Settings.MaxRetry,
        WatchdogIntervalSec = Settings.WatchdogIntervalSec,
        DefaultMaxConcurrent = Settings.DefaultMaxConcurrent,
        LogRetentionDays = Settings.LogRetentionDays,
        StatsErrorRateThreshold = Settings.StatsErrorRateThreshold,
    };

    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>Lấy key lỗi i18n của field; null nếu field không có lỗi.</summary>
    private string? Error(string field) =>
        _errors.TryGetValue(field, out var key) ? key : null;

    /// <summary>Validate bản nháp, chỉ giữ lỗi của nhóm được lưu; false = có lỗi, chặn ghi.</summary>
    private bool ValidateFor(string[] fields)
    {
        var all = SettingsValidator.Validate(_draft);
        _errors = all
            .Where(kv => fields.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return _errors.Count == 0;
    }

    private async Task SaveGeneralAsync()
    {
        if (!ValidateFor(GeneralFields)) return;

        Settings.Set(SettingsKeys.Language, _draft.Language);
        Settings.Set(SettingsKeys.Theme, _draft.Theme);
        Settings.Set(SettingsKeys.CloseToTray, _draft.CloseToTray);

        // Registry có thể bị chính sách máy chặn ghi — lỗi không được chặn cả lượt lưu chung
        try
        {
            if (Startup.IsEnabled != _draft.StartWithWindows)
            {
                Startup.SetEnabled(_draft.StartWithWindows);
            }
            Settings.Set(SettingsKeys.StartWithWindows, _draft.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Error("Không cập nhật được khởi động cùng Windows.", ex);
            Toast.Show(L["settings.msg.startupFailed"], ToastSeverity.Error);
        }

        L.Reload();
        await Theme.ApplyAsync();
        Toast.Show(L["settings.msg.saved"], ToastSeverity.Success);
    }

    private async Task SaveServerAsync()
    {
        var portChanged = _draft.Port != Settings.Port;
        if (!ValidateFor(ServerFields)) return;

        Settings.Set(SettingsKeys.Port, _draft.Port);
        Settings.SetApiKeyEnabled(_draft.ApiKeyEnabled);
        if (!string.Equals(_draft.ApiKey, Settings.GetApiKey(), StringComparison.Ordinal))
        {
            Settings.SetApiKey(_draft.ApiKey);
        }

        if (portChanged && Proxy.IsRunning)
        {
            try
            {
                // Port chỉ có hiệu lực khi Kestrel bind lại — restart để áp dụng ngay
                await Proxy.RestartAsync();
                Toast.Show(L["settings.msg.portRestart"], ToastSeverity.Success);
            }
            catch (Exception ex)
            {
                Log.Error("Khởi động lại proxy sau khi đổi cổng thất bại.", ex);
                Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
            }
        }
        else
        {
            Toast.Show(L["settings.msg.saved"], ToastSeverity.Success);
        }
    }

    private void SaveEngine()
    {
        if (!ValidateFor(EngineFields)) return;

        Settings.Set(SettingsKeys.MaxRetry, _draft.MaxRetry);
        Settings.Set(SettingsKeys.WatchdogIntervalSec, _draft.WatchdogIntervalSec);
        Settings.Set(SettingsKeys.DefaultMaxConcurrent, _draft.DefaultMaxConcurrent);
        // Engine đọc setting mỗi request qua SettingsChanged — giá trị mới áp dụng ngay
        Toast.Show(L["settings.msg.saved"], ToastSeverity.Success);
    }

    private void SaveData()
    {
        if (!ValidateFor(DataFields)) return;

        Settings.Set(SettingsKeys.LogRetentionDays, _draft.LogRetentionDays);
        Settings.Set(SettingsKeys.StatsErrorRateThreshold, _draft.StatsErrorRateThreshold);
        Toast.Show(L["settings.msg.saved"], ToastSeverity.Success);
    }

    private void RegenerateApiKey()
    {
        // 256-bit từ CSPRNG; chỉ đi vào field — persists khi bấm Save của nhóm Server
        _draft.ApiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public void Dispose() => L.LanguageChanged -= OnLanguageChanged;
}
```

- [ ] **Step 7: NavMenu thêm link Settings + LanguageChanged ở NavMenu/MainLayout**

Thay toàn bộ `router-balancing/Components/Layout/NavMenu.razor` (Task 14 sẽ thêm link Logs):

```razor
@inject LocalizationService L
@implements IDisposable

<nav class="flex flex-col gap-1 p-3">
    <div class="px-3 pb-2 text-base font-semibold">@L["app.title"]</div>

    <NavLink class="rounded px-3 py-2 text-sm no-underline hover:bg-background"
             href="" Match="NavLinkMatch.All">
        @L["nav.dashboard"]
    </NavLink>

    <NavLink class="rounded px-3 py-2 text-sm no-underline hover:bg-background"
             href="settings">
        @L["nav.settings"]
    </NavLink>
</nav>

@code {
    protected override void OnInitialized() => L.LanguageChanged += OnLanguageChanged;

    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose() => L.LanguageChanged -= OnLanguageChanged;
}
```

Sửa `MainLayout.razor` — text trong header do `L` sinh nên phải re-render khi đổi ngôn ngữ:

1. Trong `OnInitializedAsync`, sau `Toast.ToastAdded += OnToastAdded;` thêm:

```csharp
        L.LanguageChanged += OnLanguageChanged;
```

2. Thêm handler:

```csharp
    private void OnLanguageChanged() => _ = InvokeAsync(() =>
    {
        // Badge hiển thị text của L — phải cập nhật cả text lẫn StateHasChanged
        UpdateBadge();
        StateHasChanged();
    });
```

3. Trong `Dispose()`, thêm `L.LanguageChanged -= OnLanguageChanged;`.

- [ ] **Step 8: Build frontend + app (verify)**

```powershell
npm run build
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
```

(workdir `router-balancing/vite-project` cho lệnh npm)

- [ ] **Step 9: Commit**

```powershell
git add src/RouterBalancing.Core/Settings src/RouterBalancing.Core/Localization "router balancing test/Settings" router-balancing/Components
git commit -m "feat: add settings validation and settings panel with per-group save"
```

---

### Task 14: LogPanel — filter + live mode + pagination

**Files:**
- Create: `router-balancing/Components/Pages/LogPanel.razor`
- Modify: `router-balancing/Components/Layout/NavMenu.razor` (link Logs), `src/RouterBalancing.Core/Localization/Translations.cs` (keys `log.*`)
- Test: không có unit test mới — query/filter/pagination đã có test ở Task 5 (`LogServiceTests`); UI thuần. Verify = build xanh + checklist chạy tay (Task 15). Tổng test giữ nguyên **50**.

**Interfaces:**
- Consumes: Task 5 (`ILogService`, `LogQuery`, event `LogAdded`), Task 2 (`LogEntry`, `LogSeverity`, `LogCategory`), Task 9 (JS global `rbPanel`), Task 10 (`LocalizationService`)
- Produces:
  - `LogPanel.razor` (`@page "/logs"`) — spec §6: filter bar (level/category/search, collapse được qua `rbPanel` id `log.filters`), live mode (auto-prepend + Pause/Resume), pagination PageSize=100, cap ~500 dòng trên UI, click row → chi tiết `Details`.

- [ ] **Step 1: Bổ sung i18n keys `log.*`**

Thêm vào `Translations.English`:

```csharp
        // Log panel (Task 14)
        ["log.filter.title"] = "Filters",
        ["log.filter.severity"] = "Level",
        ["log.filter.category"] = "Category",
        ["log.filter.search"] = "Search",
        ["log.filter.all"] = "All",
        ["log.action.pause"] = "Pause",
        ["log.action.resume"] = "Resume",
        ["log.live.on"] = "Live",
        ["log.live.off"] = "Paused",
        ["log.severity.info"] = "Info",
        ["log.severity.warning"] = "Warning",
        ["log.severity.error"] = "Error",
        ["log.category.app"] = "App",
        ["log.category.request"] = "Request",
        ["log.col.time"] = "Time",
        ["log.col.message"] = "Message",
        ["log.page"] = "Page {0} of {1}",
        ["log.count"] = "{0} entries",
        ["log.empty"] = "No log entries.",
        ["log.detail"] = "Details",
```

Thêm vào `Translations.Vietnamese`:

```csharp
        // Log panel (Task 14)
        ["log.filter.title"] = "Bộ lọc",
        ["log.filter.severity"] = "Mức",
        ["log.filter.category"] = "Phân loại",
        ["log.filter.search"] = "Tìm kiếm",
        ["log.filter.all"] = "Tất cả",
        ["log.action.pause"] = "Tạm dừng",
        ["log.action.resume"] = "Tiếp tục",
        ["log.live.on"] = "Theo thời gian thực",
        ["log.live.off"] = "Đã tạm dừng",
        ["log.severity.info"] = "Thông tin",
        ["log.severity.warning"] = "Cảnh báo",
        ["log.severity.error"] = "Lỗi",
        ["log.category.app"] = "Ứng dụng",
        ["log.category.request"] = "Yêu cầu",
        ["log.col.time"] = "Thời gian",
        ["log.col.message"] = "Nội dung",
        ["log.page"] = "Trang {0} / {1}",
        ["log.count"] = "{0} dòng",
        ["log.empty"] = "Không có nhật ký nào.",
        ["log.detail"] = "Chi tiết",
```

- [ ] **Step 2: Tạo `LogPanel.razor`**

Tạo `router-balancing/Components/Pages/LogPanel.razor`:

```razor
@page "/logs"
@using RouterBalancing.Core.Domain
@implements IDisposable
@inject ILogService Log
@inject LocalizationService L
@inject IJSRuntime JS

<div class="mb-3 flex items-center justify-between">
    <h1 class="text-xl font-semibold">@L["panel.log.title"]</h1>

    <div class="flex items-center gap-3">
        @if (_live)
        {
            <span class="text-sm text-success">@L["log.live.on"]</span>
        }
        else
        {
            <span class="text-sm opacity-70">@L["log.live.off"]</span>
        }
        <button class="btn btn-outline-secondary" @onclick="() => _live = !_live">
            @(_live ? L["log.action.pause"] : L["log.action.resume"])
        </button>
        <button class="btn btn-outline-secondary" @onclick="ToggleFiltersAsync">
            @(_filtersExpanded ? "−" : "+") @L["log.filter.title"]
        </button>
    </div>
</div>

@if (_filtersExpanded)
{
    <div class="mb-3 flex flex-wrap items-end gap-3 rounded border border-border bg-surface p-3">
        <label class="flex flex-col gap-1 text-sm">
            @L["log.filter.severity"]
            <select class="rounded border border-border bg-surface px-2 py-1.5"
                    @bind="_severity" @bind:after="ApplyFilters">
                <option value="">@L["log.filter.all"]</option>
                <option value="Info">@L["log.severity.info"]</option>
                <option value="Warning">@L["log.severity.warning"]</option>
                <option value="Error">@L["log.severity.error"]</option>
            </select>
        </label>

        <label class="flex flex-col gap-1 text-sm">
            @L["log.filter.category"]
            <select class="rounded border border-border bg-surface px-2 py-1.5"
                    @bind="_category" @bind:after="ApplyFilters">
                <option value="">@L["log.filter.all"]</option>
                <option value="App">@L["log.category.app"]</option>
                <option value="Request">@L["log.category.request"]</option>
            </select>
        </label>

        <label class="flex flex-col gap-1 text-sm">
            @L["log.filter.search"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   @bind="_search" @bind:event="onchange" />
        </label>
    </div>
}

<div class="overflow-x-auto rounded border border-border bg-surface">
    <table class="w-full text-sm">
        <thead>
            <tr class="border-b border-border text-left opacity-70">
                <th class="px-3 py-2 font-medium">@L["log.col.time"]</th>
                <th class="px-3 py-2 font-medium">@L["log.filter.severity"]</th>
                <th class="px-3 py-2 font-medium">@L["log.filter.category"]</th>
                <th class="px-3 py-2 font-medium">@L["log.col.message"]</th>
            </tr>
        </thead>
        <tbody>
            @if (_entries.Count == 0)
            {
                <tr>
                    <td class="px-3 py-4 opacity-70" colspan="4">@L["log.empty"]</td>
                </tr>
            }
            @foreach (var entry in _entries)
            {
                <tr class="cursor-pointer border-b border-border hover:bg-background"
                    @onclick="() => ToggleDetails(entry.Id)">
                    <td class="whitespace-nowrap px-3 py-1.5 font-mono text-xs opacity-70">
                        @entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    </td>
                    <td class="px-3 py-1.5">
                        @if (entry.Severity == LogSeverity.Error)
                        {
                            <span class="font-semibold text-danger">@L["log.severity.error"]</span>
                        }
                        else if (entry.Severity == LogSeverity.Warning)
                        {
                            <span class="font-semibold">@L["log.severity.warning"]</span>
                        }
                        else
                        {
                            <span class="opacity-70">@L["log.severity.info"]</span>
                        }
                    </td>
                    <td class="px-3 py-1.5">@CategoryLabel(entry.Category)</td>
                    <td class="px-3 py-1.5">@entry.Message</td>
                </tr>
                @if (_expandedId == entry.Id && !string.IsNullOrEmpty(entry.Details))
                {
                    <tr class="border-b border-border bg-background">
                        <td class="px-3 py-2" colspan="4">
                            <div class="mb-1 text-xs font-semibold opacity-70">@L["log.detail"]</div>
                            <pre class="whitespace-pre-wrap text-xs">@entry.Details</pre>
                        </td>
                    </tr>
                }
            }
        </tbody>
    </table>
</div>

<div class="mt-3 flex items-center justify-between text-sm">
    <span class="opacity-70">@string.Format(L["log.count"], _total)</span>
    <div class="flex items-center gap-2">
        <button class="btn btn-outline-secondary" disabled="@(_page <= 1)" @onclick="PrevPage">‹</button>
        <span>@string.Format(L["log.page"], _page, TotalPages)</span>
        <button class="btn btn-outline-secondary" disabled="@(_page >= TotalPages)" @onclick="NextPage">›</button>
    </div>
</div>

@code {
    /// <summary>100 dòng/trang — khớp mặc định của <c>LogQuery.PageSize</c>.</summary>
    private const int PageSize = 100;

    /// <summary>Spec §6: UI giữ tối đa ~500 dòng, dòng cũ nhất bị drop.</summary>
    private const int MaxRows = 500;

    private readonly List<LogEntry> _entries = [];
    private int _total;
    private int _page = 1;
    private string _severity = string.Empty;
    private string _category = string.Empty;
    private string _search = string.Empty;
    private bool _live = true;
    private bool _filtersExpanded = true;
    private long? _expandedId;

    private LogSeverity? MinSeverity =>
        Enum.TryParse<LogSeverity>(_severity, out var value) ? value : null;

    private LogCategory? Category =>
        Enum.TryParse<LogCategory>(_category, out var value) ? value : null;

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));

    protected override void OnInitialized()
    {
        Load();
        Log.LogAdded += OnLogAdded;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        // rbPanel: trạng thái collapse filter bar nhớ qua localStorage rb.panel.log.filters
        _filtersExpanded = await JS.InvokeAsync<bool>("rbPanel.get", "log.filters", true);
        StateHasChanged();
    }

    private void Load()
    {
        var query = BuildQuery();
        _entries.Clear();
        _entries.AddRange(Log.Query(query));
        _total = Log.Count(query);
        _expandedId = null;
    }

    private LogQuery BuildQuery() => new(
        MinSeverity: MinSeverity,
        Category: Category,
        Search: string.IsNullOrWhiteSpace(_search) ? null : _search.Trim(),
        Page: _page,
        PageSize: PageSize);

    private void ApplyFilters()
    {
        _page = 1;
        Load();
    }

    private void PrevPage()
    {
        if (_page <= 1) return;
        _page--;
        Load();
    }

    private void NextPage()
    {
        if (_page >= TotalPages) return;
        _page++;
        Load();
    }

    /// <summary>Dòng mới: prepend chỉ khi đang live + trang đầu + khớp filter hiện tại.</summary>
    private void OnLogAdded(LogEntry entry) => _ = InvokeAsync(() =>
    {
        if (!_live || _page != 1 || !Matches(entry)) return;
        _entries.Insert(0, entry);
        _total++;
        while (_entries.Count > MaxRows)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }
        StateHasChanged();
    });

    /// <summary>Filter live phải khớp đúng ngữ nghĩa của LogService.ApplyFilter.</summary>
    private bool Matches(LogEntry entry)
    {
        if (MinSeverity is { } min && entry.Severity < min) return false;
        if (Category is { } category && entry.Category != category) return false;
        if (!string.IsNullOrWhiteSpace(_search) && !entry.Message.Contains(_search.Trim())) return false;
        return true;
    }

    private void ToggleDetails(long id) => _expandedId = _expandedId == id ? null : id;

    private string CategoryLabel(LogCategory category) => category switch
    {
        LogCategory.Request => L["log.category.request"],
        _ => L["log.category.app"],
    };

    private async Task ToggleFiltersAsync()
    {
        _filtersExpanded = !_filtersExpanded;
        await JS.InvokeVoidAsync("rbPanel.set", "log.filters", _filtersExpanded);
    }

    public void Dispose() => Log.LogAdded -= OnLogAdded;
}
```

Lưu ý: các class động (severity badge) viết dưới dạng nhánh `@if` trong markup để Tailwind scanner nhìn thấy — không gom thành chuỗi C#.

- [ ] **Step 3: NavMenu thêm link Logs**

Sửa `NavMenu.razor` — chèn NavLink Logs giữa Dashboard và Settings:

```razor
    <NavLink class="rounded px-3 py-2 text-sm no-underline hover:bg-background"
             href="logs">
        @L["nav.logs"]
    </NavLink>
```

- [ ] **Step 4: Build frontend + app (verify)**

```powershell
npm run build
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
```

(workdir `router-balancing/vite-project` cho lệnh npm) — Task UI thuần, không thêm test; tổng test vẫn **50**.

- [ ] **Step 5: Commit**

```powershell
git add router-balancing/Components src/RouterBalancing.Core/Localization
git commit -m "feat: add log panel with filters, live tail and pagination"
```

---

### Task 15: LogRetentionWorker (TDD) + nút purge + README + verify cuối

**Files:**
- Create: `src/RouterBalancing.Core/Logging/LogRetentionWorker.cs`
- Test: `router balancing test/Logging/LogRetentionWorkerTests.cs`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký singleton), `router-balancing/App.xaml.cs` (Start + dispose), `router-balancing/Components/Pages/SettingsPanel.razor` (nút "Dọn log cũ ngay"), `src/RouterBalancing.Core/Localization/Translations.cs` (keys `settings.purge*`)
- Create: `README.md` (thay toàn bộ)

**Interfaces:**
- Consumes: Task 2 (`LogEntries`, `StoragePathProvider`), Task 4 (`IAppSettingsService.LogRetentionDays`), Task 5 (`ILogService` — chỉ ghi lỗi, không tự ghi dòng "đã dọn" để không lẫn vào chính bảng đang purge)
- Produces (namespace `RouterBalancing.Core.Logging`):
  - `sealed class LogRetentionWorker : IAsyncDisposable` — ctor `(IDbContextFactory<RouterBalancingDbContext>, IAppSettingsService, ILogService)`; `Task<int> PurgeAsync(CancellationToken ct = default)` (xóa `Timestamp < UtcNow - retentionDays`, trả số dòng); `void Start()` (purge ngay 1 lần rồi lặp `PeriodicTimer` 24h, idempotent); `ValueTask DisposeAsync()` (dừng vòng lặp, chờ kết thúc)
  - DI: singleton trong `MauiProgram`; `App` ctor inject + `Start()`, dispose trong `StopProxyOnExit`
  - `SettingsPanel` Data group: nút purge → toast `settings.purge.done` với số dòng

- [ ] **Step 1: Bổ sung i18n keys purge**

Thêm vào `Translations.English`:

```csharp
        ["settings.purge"] = "Delete old logs now",
        ["settings.purge.done"] = "Removed {0} log entries.",
```

Thêm vào `Translations.Vietnamese`:

```csharp
        ["settings.purge"] = "Dọn log cũ ngay",
        ["settings.purge.done"] = "Đã xóa {0} dòng nhật ký.",
```

- [ ] **Step 2: Viết failing test**

Tạo `router balancing test/Logging/LogRetentionWorkerTests.cs`:

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;

namespace router_balancing_test.Logging;

public class LogRetentionWorkerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;

    public LogRetentionWorkerTests()
    {
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
    }

    public void Dispose()
    {
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private LogRetentionWorker CreateWorker() => new(_db.CreateFactory(), _settings, _log);

    private void WriteEntryAt(int daysAgo) =>
        _log.Write(new LogEntry
        {
            Message = $"entry {daysAgo}d",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        });

    [Fact]
    public async Task PurgeAsync_WhenEntryPastRetention_DeletesOnlyExpired()
    {
        WriteEntryAt(100);
        WriteEntryAt(1);
        var worker = CreateWorker();

        var removed = await worker.PurgeAsync();

        Assert.Equal(1, removed);
        var remaining = Assert.Single(_log.Query(new LogQuery()));
        Assert.Equal("entry 1d", remaining.Message);
    }

    [Fact]
    public async Task PurgeAsync_WhenAllEntriesWithinRetention_ReturnsZero()
    {
        WriteEntryAt(1);
        var worker = CreateWorker();

        var removed = await worker.PurgeAsync();

        Assert.Equal(0, removed);
        Assert.Single(_log.Query(new LogQuery()));
    }

    [Fact]
    public async Task PurgeAsync_WhenRetentionDaysChanged_UsesNewValue()
    {
        _settings.Set(SettingsKeys.LogRetentionDays, 7);
        WriteEntryAt(10);
        WriteEntryAt(2);
        var worker = CreateWorker();

        var removed = await worker.PurgeAsync();

        Assert.Equal(1, removed);
        Assert.Equal("entry 2d", Assert.Single(_log.Query(new LogQuery())).Message);
    }

    [Fact]
    public async Task Start_ThenDispose_PurgesOnStartAndStopsCleanly()
    {
        WriteEntryAt(100);
        var worker = CreateWorker();

        worker.Start();
        // Purge đầu chạy ngay khi Start — poll tối đa 5s thay vì sleep cố định
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_log.Count(new LogQuery()) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        await worker.DisposeAsync();

        Assert.Equal(0, _log.Count(new LogQuery()));
    }
}
```

- [ ] **Step 3: Chạy test — FAIL**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **FAIL** build — `LogRetentionWorker` chưa tồn tại.

- [ ] **Step 4: Viết implementation**

`src/RouterBalancing.Core/Logging/LogRetentionWorker.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Logging;

/// <summary>
/// Dọn nhật ký quá hạn: purge ngay 1 lần khi khởi động rồi lặp mỗi 24 giờ.
/// End-user không mở SQLite tay được nên retention phải tự chạy; nút
/// "Dọn log cũ ngay" trong Settings gọi thẳng <see cref="PurgeAsync"/>.
/// </summary>
public sealed class LogRetentionWorker : IAsyncDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly IAppSettingsService _settings;
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LogRetentionWorker(
        IDbContextFactory<RouterBalancingDbContext> db,
        IAppSettingsService settings,
        ILogService log)
    {
        _db = db;
        _settings = settings;
        _log = log;
    }

    /// <summary>Xóa mọi dòng log cũ hơn setting <c>logRetentionDays</c>.</summary>
    /// <returns>Số dòng đã xóa.</returns>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.LogRetentionDays);
        using var db = _db.CreateDbContext();
        // Không ghi log dòng "đã dọn" — đó là dữ liệu trong chính bảng đang purge
        return await db.LogEntries
            .Where(entry => entry.Timestamp < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Chạy nền: purge ngay rồi lặp 24h — gọi lần 2 chỉ là no-op.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        // Capture CTS local: DisposeAsync có thể đặt _cts = null trước khi Task kịp chạy
        var cts = _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(cts.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Lỗi một chu kỳ không được giết vòng lặp — vẫn thử ở chu kỳ sau
                _log.Error("Dọn nhật ký định kỳ thất bại.", ex);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Dừng vòng lặp nền — chờ nó kết thúc để không cắt giữa chừng khi app thoát.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Chu kỳ đang chạy bị hủy khi dispose — đã xử lý xong
            }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
```

- [ ] **Step 5: Chạy test — PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: `Passed! - Failed: 0, Total: 54` (50 cũ + 4 mới).

- [ ] **Step 6: DI + App lifecycle**

`router-balancing/MauiProgram.cs` — thêm sau `builder.Services.AddSingleton<IProxyHost, ProxyHost>();`:

```csharp
            builder.Services.AddSingleton<LogRetentionWorker>();
```

(Using `RouterBalancing.Core.Logging` đã có từ Task 8.)

`router-balancing/App.xaml.cs` — 4 điểm sửa:

1. Thêm field: `private readonly LogRetentionWorker _retention;`
2. Thêm parameter `LogRetentionWorker retention` vào ctor và gán `_retention = retention;`.
3. Sau dòng `_log.Info("router-balancing khởi động.");` thêm:

```csharp
            // Dọn log quá hạn ngay khi mở app rồi lặp 24h — retention không cần user bấm
            _retention.Start();
```

4. Trong `StopProxyOnExit()`, sau khối dừng `_proxyHost` thêm:

```csharp
            try
            {
                // Dừng vòng lặp dọn log trước khi process chết
                _retention.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log.Error("Dừng LogRetentionWorker khi process thoát thất bại.", ex);
            }
```

- [ ] **Step 7: Nút purge trong SettingsPanel**

Sửa `router-balancing/Components/Pages/SettingsPanel.razor`:

1. Thêm inject: `@inject LogRetentionWorker Retention`
2. Trong nhóm Data, sau nút Save (trước `</section>`) thêm:

```razor
    <div class="mt-3">
        <button class="btn btn-outline-danger" disabled="@_purging" @onclick="PurgeLogsAsync">
            @L["settings.purge"]
        </button>
    </div>
```

3. Trong `@code`, thêm field `private bool _purging;` và method:

```csharp
    private async Task PurgeLogsAsync()
    {
        if (_purging) return;
        _purging = true;
        try
        {
            var removed = await Retention.PurgeAsync();
            Toast.Show(string.Format(L["settings.purge.done"], removed), ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Dọn log cũ thất bại từ Settings.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _purging = false;
        }
    }
```

- [ ] **Step 8: Viết `README.md` (thay toàn bộ)**

````markdown
# router-balancing

**router-balancing** là ứng dụng desktop (.NET MAUI Blazor Hybrid, Windows-first) đóng vai **local proxy server** cân bằng tải request LLM: expose API chuẩn OpenAI tại `http://127.0.0.1:<port>` (mặc định **8317**), tự động chọn nhà cung cấp, kèm UI quản lý nhật ký và cài đặt.

## Yêu cầu

- .NET 10 SDK + MAUI workload (`dotnet workload install maui`)
- Node.js 20+ — **chỉ cần khi sửa frontend** (`router-balancing/vite-project`); bản build đã commit sẵn trong `router-balancing/wwwroot/build/`

## Build & chạy

```powershell
# build app (Windows)
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo

# chạy
dotnet run --project router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

## Test

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

## Frontend (chỉ khi sửa UI)

```powershell
# workdir: router-balancing/vite-project
npm install
npm run build      # hoặc npm run watch khi dev
```

## Cấu hình & dữ liệu

| Hạng mục | Giá trị |
|---|---|
| Port mặc định | `8317` (range 1024–65535, đổi trong Settings) |
| Health check | `GET /health` — không cần API key |
| API key | Tùy chọn; mã hóa DPAPI (CurrentUser), so sánh constant-time |
| DB | `%AppData%\router-balancing\router-balancing.db` (SQLite, auto-migration khi khởi động) |
| Nhật ký | Giữ 90 ngày mặc định; dọn tự động 24h/lần + nút "Dọn log cũ ngay" trong Settings |
| Ngôn ngữ / theme | `auto` / `system` theo hệ thống, đổi được trong Settings |

## Tài liệu

- Spec: [`docs/superpowers/specs/2026-09-25-router-balancing-design.md`](docs/superpowers/specs/2026-09-25-router-balancing-design.md)
- Plan Phase 1: [`docs/superpowers/plans/2026-09-25-phase1-foundation.md`](docs/superpowers/plans/2026-09-25-phase1-foundation.md)
````

- [ ] **Step 9: Verify cuối toàn phase**

```powershell
# 1. Frontend (workdir router-balancing/vite-project)
npm run build

# 2. App — mong đợi 0 Error(s)
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo

# 3. Tests — mong đợi Passed! - Failed: 0, Total: 54
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Checklist chạy tay (F5 hoặc `dotnet run`):

1. App mở lên → badge header hiện `● Đang chạy : 8317` (hoặc `Running : 8317`).
2. `curl http://127.0.0.1:8317/health` → `{"status":"ok"}`; `curl http://127.0.0.1:8317/v1/models` → JSON list (chưa bật API key).
3. Settings → đổi port sang 8320 → Save nhóm Server → toast "Đã đổi cổng — proxy khởi động lại" → `curl http://127.0.0.1:8320/health` OK.
4. Settings → Language = Tiếng Việt → Save nhóm General → UI đổi ngôn ngữ ngay (nav, badge, chính form Settings).
5. Settings → Theme = Tối → Save → giao diện tối; đóng/mở lại app vẫn tối (localStorage `rb.theme`).
6. Settings → bật "Yêu cầu API key" nhưng để trống key → Save nhóm Server → hiện lỗi inline `settings.error.apiKey`; Regenerate → Save → `curl /v1/models` không key = 401, có key = 200.
7. Logs → có dòng khởi động; lọc Level=Error → còn 0 dòng; gõ search → Enter lọc; Pause → dòng mới (đổi setting) không tự thêm; Resume → thêm lại.
8. Bật "Khởi động cùng Windows" → Save → `regedit` thấy `HKCU\...\Run\router-balancing`; tắt → giá trị biến mất.
9. Bấm "Dọn log cũ ngay" → toast "Đã xóa N dòng nhật ký" (N=0 khi chưa có log quá hạn).
10. Đóng cửa sổ → app về khay; Exit từ tray → process biến mất, port giải phóng.

- [ ] **Step 10: Commit**

```powershell
git add src/RouterBalancing.Core/Logging src/RouterBalancing.Core/Localization "router balancing test/Logging" router-balancing/MauiProgram.cs router-balancing/App.xaml.cs router-balancing/Components/Pages/SettingsPanel.razor README.md
git commit -m "feat: add log retention worker with purge button and readme"
```

---

## Self-Review

### Spec coverage — Phase 1 (Foundation)

| Spec section | Nội dung | Task |
|---|---|---|
| §2 Process model / Startup flow | Kestrel in-process, bind `127.0.0.1`, single instance, auto-migration trước khi resolve settings | 7, 8 |
| §3 Data model | 6 entities + `AppSettings` + `LogEntries`, EF Migration | 2 |
| §4 (một phần) | `/v1/models` đọc model đã bật từ DB, shape OpenAI | 7 |
| §6 Log panel | Filter (level/category/search), live + Pause/Resume, pagination, cap ~500 dòng, click xem Details | 14 |
| §8 Settings panel | 4 nhóm General/Server/Engine/Data, Save từng nhóm + validate inline, port → restart Kestrel, API key Regenerate, nút dọn log cũ | 13, 15 |
| §9 UI System | Theme tokens + dark variant, toast stack, i18n EN/VI, Vite/Tailwind pipeline, tray/close-to-tray/startup cùng Windows | 9, 10, 11, 12 |
| §11 Testing strategy | TDD cho mọi logic nghiệp vụ (settings, log, port, middleware, host, retention) | 2–8, 10, 13, 15 |

### Quét placeholder

Chạy tìm `TODO` / `TBD` / `FIXME` / `placeholder` trong plan — dòng duy nhất được match phải là chính lệnh grep này:

```powershell
Select-String -Path "docs/superpowers/plans/2026-09-25-phase1-foundation.md" -Pattern "TODO|TBD|FIXME"
```

### Kiểm tra loại (type consistency)

- [ ] Lệnh test duy nhất & đúng tên file trong toàn plan: `dotnet test "router balancing test/router-balancing test.csproj" --nologo` (đã sửa 13 chỗ viết sai `router-balancing test.csproj` + dòng Global Constraints).
- [ ] Chuỗi test totals nhất quán: 1 → 3 → 6 → 12 → 19 → 30 → 37 → 39 → 44 → **50** (T13) → 50 (T14, UI thuần) → **54** (T15).
- [ ] Mọi i18n key dùng trong component đều có trong `Translations` (en + vi): `app/nav/panel.*` (T10), `dashboard.*` (T11), `tray.*` (T12), `settings.*` (T13/T15), `log.*` (T14).
- [ ] Mọi namespace inject trong `.razor` đều được `@using` (file-level hoặc `_Imports` — T11 đã thêm `Core.Logging` cho `ILogService`).
- [ ] Class Tailwind dùng trong chuỗi C# (badge/toast) đều nằm trong safelist `tailwind.config.js` (T11 Step 8, kể cả `text-white`).

### Out of scope (Phase 2+)

Runtime panel (4 block), Statistics + Chart.js, Provider/Model/Router panels, engine pipeline (priority queue, failover/retry/watchdog, streaming, dịch thuật) — xem spec §10.

---

**Hết plan.** Thực thi theo `superpowers:subagent-driven-development` (khuyến nghị) hoặc `superpowers:executing-plans`, mỗi task một commit, đánh dấu checkbox khi xong.

