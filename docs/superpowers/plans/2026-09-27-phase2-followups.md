# Phase 2 Follow-ups (Cleanup Batch) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dọn dẹp các mục deferred từ final review Phase 2 (provider-accounts) trước khi viết spec Phase 3A: sync spec 3 điểm sai lệch, bổ sung XML doc, thêm test round-trip migration + coverage thiếu, vá 4 edge-case UI.

**Architecture:** 3 task độc lập theo loại thay đổi — (1) docs/spec sync, (2) tests cho Core, (3) hardening UI trong Providers.razor. Không đổi logic service; Task 3 chỉ vá render/toast/load-fail.

**Tech Stack:** .NET 10, EF Core 10 (SQLite), xUnit, Razor Components, i18n Translations.cs.

## Global Constraints

- Comment/XML doc tiếng Việt ("why"); identifier + commit message tiếng Anh conventional (`docs:`, `test:`, `fix:`).
- XML doc `///` **bắt buộc** với mọi public/protected member mới (AGENTS.md).
- Test doubles nested private trong file test (precedent `NeverHttpFactory`/`NullLog`).
- Text UI qua `L["key"]` (không hardcode); btn variants chỉ `btn`, `btn-primary`, `btn-outline-info`, `btn-outline-secondary`, `btn-outline-danger`.
- **Đóng app trước khi chạy `dotnet test`** (mutex giữ DB/test port).
- Branch: `chore/phase2-followups` (tạo từ `master` @ `d6bca1f`).
- Gates cuối: `dotnet test` **183/0** (179 + 4 test mới), `dotnet build ... -f net10.0-windows10.0.19041.0` 0W/0E, `npm run build` exit 0, parity i18n **208/208** (207 + key mới), `git status` sạch.

```bash
dotnet test "router balancing test/router balancing test.csproj" --nologo
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
npm run build        # workdir: router-balancing/vite-project
```

---

### Task 1: Docs & Spec Sync (3 điểm spec sai lệch + 3 XML doc)

**Files:**
- Modify: `docs/superpowers/specs/2026-09-27-provider-accounts-design.md:70-71` (§2.2), `:233` (§5.2), `:239` (§5.3)
- Modify: `src/RouterBalancing.Core/Providers/IProviderService.cs:24`
- Modify: `src/RouterBalancing.Core/Providers/IProviderAccountService.cs:17`
- Modify: `src/RouterBalancing.Core/Providers/ProviderAccountDraft.cs:15`

**Interfaces:**
- Consumes: kết quả implement Phase 2 (migration thật, UI thật, guard whitespace thật).
- Produces: docs đồng bộ — không ảnh hưởng type/signature.

- [ ] **Step 1: Sửa spec §2.2 — index sai**

Thay 2 dòng index (dòng 70–71):

```markdown
- Index `IX_ProviderAccounts_ProviderId`.
- Unique index `(ProviderId, Name)` — `IX_ProviderAccounts_ProviderId_Name`.
```

bằng 1 dòng đúng với migration thật (`20260927122326_AddProviderAccounts` chỉ tạo unique composite; SQLite FK không tự tạo index):

```markdown
- Unique index `(ProviderId, Name)` — `IX_ProviderAccounts_ProviderId_Name`. Không có index `ProviderId` riêng: FK SQLite không tự index, unique composite phục vụ lookup theo ProviderId qua prefix.
```

- [ ] **Step 2: Sửa spec §5.2 — ConfirmDialog title thực tế**

Thay dòng 233:

```markdown
- **Xoá**: ConfirmDialog — title `accounts.confirm.delete` format `{0}` = Name (pattern confirm Combos: `ConfirmText` caller-i18n, không liệt kê tên trong body); toast `accounts.msg.deleted` format `{0}`.
```

bằng (đúng Providers.razor:566–576 — Title/ConfirmText tái dùng `providers.action.delete`, name nằm trong Message):

```markdown
- **Xoá**: ConfirmDialog — Title và ConfirmText tái dùng `providers.action.delete`; Message = `accounts.confirm.delete` format `{0}` = Name; CancelText `confirm.cancel`; toast `accounts.msg.deleted` format `{0}`.
```

- [ ] **Step 3: Sửa spec §5.3 — ghi nhận dual-writer LastTest\***

Thay dòng 239:

```markdown
- `Provider.LastTest*` do service ghi (AND) — badge test level provider ở card list giữ nguyên hiển thị.
```

bằng:

```markdown
- `Provider.LastTest*` là **dual-writer**: `ProviderAccountService.TestAllAsync` ghi AND các account enabled (Test tất cả / test từ card), `ProviderService.TestConnectionAsync` ghi theo kết quả 1 key (nút Test trong modal, provider đã lưu — spec phase2a §4.2). Hai ngữ nghĩa chưa chuẩn hoá — thống nhất ở Phase 3 Engine.
```

- [ ] **Step 4: Sửa XML doc `IProviderService.DeleteAsync` (dòng 24)**

```csharp
    /// <summary>Xóa provider — models con và accounts cascade theo cấu hình FK.</summary>
```

- [ ] **Step 5: Sửa XML doc `IProviderAccountService.UpdateAsync` (dòng 17)**

```csharp
    /// <summary>Cập nhật; <c>draft.ApiKey</c> rỗng hoặc toàn khoảng trắng = giữ nguyên key cũ.</summary>
```

- [ ] **Step 6: Sửa XML doc `ProviderAccountDraft.ApiKey` (dòng 15)**

```csharp
    /// <summary>Plaintext lúc nhập; rỗng hoặc toàn khoảng trắng khi update = giữ key đã lưu. Không bao giờ log/persist trực tiếp.</summary>
```

- [ ] **Step 7: Verify build + test**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `0 Error(s)` — docs không đổi logic.

- [ ] **Step 8: Commit**

```bash
git add docs/superpowers/specs/2026-09-27-provider-accounts-design.md src/RouterBalancing.Core/Providers/IProviderService.cs src/RouterBalancing.Core/Providers/IProviderAccountService.cs src/RouterBalancing.Core/Providers/ProviderAccountDraft.cs
git commit -m "docs: sync provider-accounts spec and xml docs with implementation"
```

---

### Task 2: Tests — migration round-trip + coverage thiếu

**Files:**
- Create: `router balancing test/Storage/AddProviderAccountsMigrationTests.cs`
- Modify: `router balancing test/Providers/ProviderServiceTests.cs` (sau `ListAsync_IncludesAccounts`, ~dòng 130)
- Modify: `router balancing test/Providers/ProviderTestConnectionTests.cs:196-197`
- Modify: `router balancing test/Providers/ProviderAccountServiceTests.cs` (thêm 1 test + XML doc toàn class)

**Interfaces:**
- Consumes: `TestDb` (`router balancing test/TestDb.cs` — `DbPath` public, `CreateFactory()`), migration `20260925172150_InitialCreate` + `20260927122326_AddProviderAccounts`, `IMigrator.Migrate(target)` (`Microsoft.EntityFrameworkCore.Infrastructure` + `.Migrations`).
- Produces: +4 tests → tổng **183**; assert mới trong 2 test có sẵn (không đổi count).

- [ ] **Step 1: Viết test `GetAsync` Include (ProviderServiceTests)**

Chèn sau method `ListAsync_IncludesAccounts` (kết thúc dòng 130):

```csharp
    [Fact]
    public async Task GetAsync_WhenExists_IncludesModelsAndAccounts()
    {
        var provider = await _service.CreateAsync(Draft());
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = provider.Id, ModelId = "gpt-4o" });
            await db.SaveChangesAsync();
        }

        var loaded = await _service.GetAsync(provider.Id);

        Assert.NotNull(loaded);
        Assert.Equal("gpt-4o", Assert.Single(loaded.Models).ModelId);
        Assert.Equal("Default", Assert.Single(loaded.Accounts).Name);
    }
```

- [ ] **Step 2: Chạy test mới — verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "FullyQualifiedName~GetAsync_WhenExists"`
Expected: 1 passed.

- [ ] **Step 3: Siết assert `ProviderTestConnectionTests` (dòng 196–197)**

Thay:

```csharp
        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.True(auth is null || !auth.ToString().Contains("sk-saved", StringComparison.Ordinal));
```

bằng (probe đã xác minh `AuthenticationHeaderValue("Bearer", "").ToString()` == `"Bearer"` — pin đúng nhánh empty-key, không còn OR mơ hồ):

```csharp
        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.ToString()); // key rỗng → chỉ còn scheme, không kèm key nào
```

- [ ] **Step 4: Viết test >50 patterns (ProviderAccountServiceTests)**

Chèn trước method `Update_EmptyApiKey_KeepsExistingKey` (dòng 199):

```csharp
    [Fact]
    public async Task Create_TooManyPatterns_ThrowsArgument()
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId);
        draft.ModelPatterns = Enumerable.Range(0, 51).Select(i => $"m{i}").ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(draft));
    }
```

- [ ] **Step 5: Bổ sung assert composition `LastTestMessage` (test có sẵn)**

Trong `TestAllAsync_MixedResults_WritesEachAndProviderAnd`, sau dòng `Assert.NotNull(provider.LastTestAt);` (dòng 342):

```csharp
        Assert.Equal("bad: HTTP 401 Unauthorized", provider.LastTestMessage); // composition "Name: message", chỉ account fail
```

- [ ] **Step 6: Chạy 2 test vừa sửa — verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "FullyQualifiedName~Create_TooManyPatterns|FullyQualifiedName~TestAllAsync_MixedResults"`
Expected: 2 passed.

- [ ] **Step 7: Viết file test migration round-trip**

Create `router balancing test/Storage/AddProviderAccountsMigrationTests.cs`:

```csharp
// router balancing test/Storage/AddProviderAccountsMigrationTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip data migration AddProviderAccounts: key cũ trên Providers → account "Default" (Up)
/// và ngược lại (Down). Phải migrate tới đúng migration rồi mới seed — Migrate() mặc định trên DB
/// trống để data SQL chạy trên empty set, không chứng minh gì.
/// </summary>
public class AddProviderAccountsMigrationTests : IDisposable
{
    private const string PreviousId = "20260925172150_InitialCreate";
    private const string TargetId = "20260927122326_AddProviderAccounts";
    private const string Timestamp = "'2026-09-27T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_MigratesProviderKey_ToDefaultAccount()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO Providers (Name, Type, BaseUrl, ApiKeyEncrypted, Enabled, MaxConcurrent, CreatedAt, UpdatedAt)
            VALUES ('P', 0, 'https://api.example.com', 'enc-old', 1, 4, {Timestamp}, {Timestamp});
            """);

        db.GetService<IMigrator>().Migrate(TargetId);

        var account = db.ProviderAccounts.Single(a => a.ProviderId == 1);
        Assert.Equal("Default", account.Name);
        Assert.Equal("enc-old", account.ApiKeyEncrypted);
        // Cột cũ đã drop — SQLite không cho EF modelche, phải kiểm tra schema thật
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Providers') WHERE name = 'ApiKeyEncrypted'")));
    }

    [Fact]
    public void Down_RestoresDefaultAccountKey_ToProviderColumn()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);
        // Đúng schema tại TargetId: Providers không còn ApiKeyEncrypted, ProviderAccounts NOT NULL các cột dùng default
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO Providers (Name, Type, BaseUrl, Enabled, MaxConcurrent, CreatedAt, UpdatedAt)
            VALUES ('P', 0, 'https://api.example.com', 1, 4, {Timestamp}, {Timestamp});
            """);
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO ProviderAccounts (ProviderId, Name, ApiKeyEncrypted, Enabled, Weight, Priority,
                                          TokensUsed, RequestsUsed, CreatedAt, UpdatedAt)
            VALUES (1, 'Default', 'enc-back', 1, 100, 0, 0, 0, {Timestamp}, {Timestamp});
            """);

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal("enc-back", Scalar("SELECT ApiKeyEncrypted FROM Providers WHERE Id = 1"));
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts')")));
    }

    /// <summary>Scalar qua ADO thuần — tránh alias "Value" của EF SqlQuery và trần thuật ngữ EF.</summary>
    private object? Scalar(string sql)
    {
        // Pooling=False như TestDb: pool giữ handle file → Dispose không xóa được (IOException trên Windows)
        using var conn = new SqliteConnection($"Data Source={_testDb.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
```

Lưu ý namespace `router_balancing_test.Storage` (same assembly → `TestDb internal` dùng được).

- [ ] **Step 8: Chạy 2 test migration — verify PASS**

Run: `dotnet test "router balancing test/router-balancing test.csproj" --nologo --filter "FullyQualifiedName~AddProviderAccountsMigrationTests"`
Expected: 2 passed. Nếu FAIL ở `IMigrator`/INSERT schema → đối chiếu migration file thật rồi sửa test (không sửa migration).

- [ ] **Step 9: XML doc toàn bộ `ProviderAccountServiceTests`**

Thêm `/// <summary>` tiếng Việt mô tả hành vi cho class + mọi method thiếu (giữ nguyên summary đã có của helper `NeverHttpFactory`/`KeyedHandler`):

```csharp
    /// <summary>CRUD/test ProviderAccountService trên DB file tạm — không network, DPAPI thật.</summary>
    public class ProviderAccountServiceTests : IDisposable
    // Dispose(): giải phóng TestDb (xóa file tạm).
    // ServiceWith(handler): service với HTTP stub — test TestAll không đụng mạng.
    // StubFactory: IHttpClientFactory trả HttpClient gắn handler test.
    // FixedHandler: trả đúng status cố định cho mọi request.
    // SeedProviderAsync: seed provider + các account (tên/khối/ưu tiên) — trả ProviderId.
    // Draft: bản nháp account hợp lệ cho test (ProviderId tùy chỗ gọi).
    // Create_WithKey_SavesDpapiEncrypted: key lưu xuống DB phải là ciphertext DPAPI, không plaintext.
    // Create_DuplicateNameInSameProvider_ThrowsInvalidOperation: trùng Name trong cùng provider → InvalidOperationException.
    // Create_SameNameDifferentProviders_Ok: cùng Name giữa 2 provider là hợp lệ.
    // Create_WhenProviderMissing_ThrowsKeyNotFound: provider không tồn tại → KeyNotFoundException.
    // Create_WithPatterns_SerializesJsonArray: ModelPatterns → JSON array trong cột TEXT.
    // Create_EmptyPattern_ThrowsArgument: pattern rỗng/trắng → ArgumentException.
    // Create_NameTooLong_ThrowsArgument: Name > 100 ký tự → ArgumentException.
    // Create_InvalidDraft_ThrowsArgument: lần lượt các rule Weight/Priority/limit vi phạm → ArgumentException.
    // Create_TooManyPatterns_ThrowsArgument: quá 50 pattern → ArgumentException.
    // Update_EmptyApiKey_KeepsExistingKey: key rỗng khi sửa = giữ key đã lưu.
    // Update_WhitespaceApiKey_KeepsExistingKey: key toàn khoảng trắng cũng = giữ (chống persist key rỗng).
    // Update_NewKey_ReplacesEncrypted: có key mới → ciphertext đổi.
    // Update_DuplicateNameExcludingSelf_Ok: trùng tên chính nó không phải lỗi.
    // Update_WhenAccountMissing_ThrowsKeyNotFound: account không tồn tại → KeyNotFoundException.
    // Delete_LastAccount_ThrowsInvalidOperation: xoá account cuối → InvalidOperationException.
    // Delete_NotLast_Succeeds: còn >1 account → xoá được.
    // ListAsync_OrdersByPriorityThenName: thứ tự Priority tăng dần rồi Name.
    // DeleteProvider_CascadesToAccounts: xoá provider cascade accounts (FK OnDelete).
    // TestAllAsync_MixedResults_WritesEachAndProviderAnd: ghi LastTest* từng account + Provider = AND + composition message.
    // TestAllAsync_NoEnabledAccounts_SetsProviderTestNull: không account enabled → Provider.LastTest* = null.
    // TestAllAsync_WhenProviderMissing_ThrowsKeyNotFound: provider không tồn tại → KeyNotFoundException.
```

(Mỗi dòng trên = một `/// <summary>...</summary>` ngay trên member tương ứng; helper private cũng có summary.)

- [ ] **Step 10: Chạy toàn bộ suite — verify 183/0**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed! - Failed: 0, Passed: 183, ...` (179 + 4 mới).

- [ ] **Step 11: Commit**

```bash
git add "router balancing test/Storage/AddProviderAccountsMigrationTests.cs" "router balancing test/Providers/ProviderServiceTests.cs" "router balancing test/Providers/ProviderTestConnectionTests.cs" "router balancing test/Providers/ProviderAccountServiceTests.cs"
git commit -m "test: add provider accounts migration round-trip and coverage pins"
```

---

### Task 3: UI hardening — Providers.razor (4 edge-case) + 1 i18n key

**Files:**
- Modify: `router-balancing/Components/Pages/Providers.razor` (field ~616, render ~370, `TestAsync` ~725, `DecodePatterns` ~1166, `LoadAccountsAsync` ~1135, `TestAllAccountsAsync` ~1275, `PatternSummary` ~1294)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (EN sau dòng 226, VI sau dòng 448)

**Interfaces:**
- Consumes: `ToastService.Show(string, ToastSeverity)` (`ToastSeverity` chỉ có `Info/Success/Error` — **không Warning**), `EmptyState`, key `dashboard.msg.failed` + `accounts.msg.testDone` (đã có).
- Produces: key mới `accounts.msg.testNoAccounts` (parity 207 → **208/208**); helper `ShowTestResultsToast(IReadOnlyList<ProviderAccountTestResult>)`; field `_accountsLoadFailed`.

- [ ] **Step 1: Thêm i18n key `accounts.msg.testNoAccounts`**

Sau dòng `["accounts.msg.testDone"]` trong **dict EN** (~226):

```csharp
        ["accounts.msg.testNoAccounts"] = "No enabled accounts to test.",
```

Sau `["accounts.msg.testDone"]` trong **dict VI** (~448):

```csharp
        ["accounts.msg.testNoAccounts"] = "Khong co tai khoan dang bat nao de test.",
```

(Chuỗi VI không dấu theo precedent các key hiện có trong file — copy style từ dòng 448.)

- [ ] **Step 2: Thêm field `_accountsLoadFailed`**

Sau dòng 616 `private List<ProviderAccount> _accounts = [];`:

```csharp
    private bool _accountsLoadFailed;
```

- [ ] **Step 3: Render phân biệt load-fail vs empty (dòng 370–373)**

Thay:

```razor
            @if (_accounts.Count == 0)
            {
                <EmptyState Message="@L["accounts.empty"]" />
            }
            else
```

bằng:

```razor
            @if (_accountsLoadFailed)
            {
                @* Load fail ≠ không có account — không được hiện EmptyState mời thêm account *@
                <div class="text-sm text-danger">@L["dashboard.msg.failed"]</div>
            }
            else if (_accounts.Count == 0)
            {
                <EmptyState Message="@L["accounts.empty"]" />
            }
            else
```

- [ ] **Step 4: `LoadAccountsAsync` set/clear flag (dòng 1135–1146)**

Thay method:

```csharp
    private async Task LoadAccountsAsync(long providerId)
    {
        try
        {
            _accounts = (await AccountSvc.ListAsync(providerId)).ToList();
            _accountsLoadFailed = false;
        }
        catch (Exception ex)
        {
            Log.Error("Không tải được danh sách tài khoản.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
            _accountsLoadFailed = true;
        }
    }
```

- [ ] **Step 5: Extract helper `ShowTestResultsToast` + nhánh 0 account**

Chèn ngay trước `PatternSummary` (~1293):

```csharp
    /// <summary>
    /// Toast kết quả test account: 0 account enabled → Info "không có gì để test"
    /// (không nói dối bằng success 0/0); có kết quả → OK/lỗi như cũ.
    /// </summary>
    private void ShowTestResultsToast(IReadOnlyList<ProviderAccountTestResult> results)
    {
        if (results.Count == 0)
        {
            Toast.Show(L["accounts.msg.testNoAccounts"], ToastSeverity.Info);
            return;
        }
        var failed = results.Count(r => !r.Success);
        Toast.Show(
            string.Format(L["accounts.msg.testDone"], results.Count - failed, failed),
            failed > 0 ? ToastSeverity.Error : ToastSeverity.Success);
    }
```

- [ ] **Step 6: Dùng helper ở 2 call site**

Trong `TestAsync` (dòng 724–727) thay:

```csharp
            var failed = results.Count(r => !r.Success);
            Toast.Show(
                string.Format(L["accounts.msg.testDone"], results.Count - failed, failed),
                failed > 0 ? ToastSeverity.Error : ToastSeverity.Success);
```

bằng:

```csharp
            ShowTestResultsToast(results);
```

Trong `TestAllAccountsAsync` (dòng 1274–1277) thay block toast tương tự (cùng code) bằng:

```csharp
            ShowTestResultsToast(results);
```

- [ ] **Step 7: Guard JSON trong `DecodePatterns` + `PatternSummary`**

Thay `DecodePatterns` (dòng 1165–1169):

```csharp
    /// <summary>JSON array → textarea 1 pattern/dòng; null/empty hoặc JSON hỏng → chuỗi rỗng.</summary>
    private static string DecodePatterns(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return string.Empty;
        }
        try
        {
            return string.Join('\n', JsonSerializer.Deserialize<string[]>(json) ?? []);
        }
        catch (JsonException)
        {
            // Dữ liệu hỏng không được chặn mở form sửa — mất pattern còn sửa lại được
            return string.Empty;
        }
    }
```

Thay `PatternSummary` (dòng 1293–1302):

```csharp
    /// <summary>Pattern summary: rỗng = mọi model; có = số pattern; JSON hỏng = "?" (không crash render).</summary>
    private string PatternSummary(ProviderAccount account)
    {
        if (string.IsNullOrEmpty(account.ModelPatterns))
        {
            return L["accounts.patterns.all"];
        }
        try
        {
            var patterns = JsonSerializer.Deserialize<string[]>(account.ModelPatterns) ?? [];
            return string.Format(L["accounts.patterns.count"], patterns.Length);
        }
        catch (JsonException)
        {
            // Render path — 1 row hỏng không được sập cả modal; "?" = unknown count
            return "?";
        }
    }
```

- [ ] **Step 8: Build + parity + test**

```bash
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo   # 0 Error(s)
npm run build        # exit 0 (workdir router-balancing/vite-project)
dotnet test "router balancing test/router balancing test.csproj" --nologo                        # 183/0
```

Parity check (đếm `["` mỗi dict — EN start dòng 10, VI start dòng 232):

```powershell
$t = Get-Content "src\RouterBalancing.Core\Localization\Translations.cs" -Encoding UTF8
"EN=" + (($t[9..231] | Select-String -Pattern '\["').Count) + " VI=" + (($t[232..($t.Count-1)] | Select-String -Pattern '\["').Count)
```

Expected: `EN=208 VI=208`.

- [ ] **Step 9: Commit**

```bash
git add router-balancing/Components/Pages/Providers.razor src/RouterBalancing.Core/Localization/Translations.cs
git commit -m "fix: harden provider accounts ui edge cases"
```

---

## Final Gates (controller)

- [ ] `dotnet test` → 183/0; build → 0W/0E; `npm run build` → exit 0; parity 208/208; `git status` sạch.
- [ ] Controller CDP smoke (drive42, optional nhưng khuyến nghị): mở edit modal provider ≥2 account → tắt toàn bộ account → bấm "Test tất cả" → toast Info `Không có tài khoản đang bật nào để test.` (không phải success "0/0"); Hủy modal → mở lại bình thường.
- [ ] Final review (subagent) → merge `chore/phase2-followups` vào `master` (local, **không push**) → ghi ledger MERGED.
