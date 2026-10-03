# Plan: Batch 4 mục — LAN access, Sửa gán proxy, Model pin + copy, Đồng thời per-account

- **Spec (nguồn requirement duy nhất):** `docs/superpowers/specs/2026-10-03-lan-proxyfix-pin-peraccount-design.md` (commit `005a68c`, Approved) — plan này chỉ chi tiết hóa; mọi sai khác so với spec đều đánh dấu trong bảng Deviation bên dưới.
- **Baseline:** master @ `005a68c`, working tree sạch, **546 tests xanh**.
- **Kỳ vọng cuối:** **560 tests xanh** (ladder bên dưới).
- **Execution:** `subagent-driven-development` — 10 task tuần tự, mỗi task 1 commit message tiếng Anh conventional.
- **Roadmap:** batch này nằm ngoài "5 việc cần làm"; sau khi xong → roadmap #1 Search.

## Goal

Hoàn thành 4 mục độc lập của spec:

1. **LAN (Phần C):** toggle `lanAccess` (mặc định OFF) → Kestrel bind `IPAddress.Any`, Dashboard hiện URL LAN, hint bảo mật.
2. **Sửa gán proxy (Phần A):** EF diff-update (fix re-save crash), bỏ reject proxy disabled, chống race đổi scope, lỗi cụ thể `KeyNotFoundException`, reverse view "proxy gán cho ai".
3. **Model pin + copy (Phần D):** dòng `{Identifier}/{ModelId}` + nút copy trong bảng model; `IClipboardService` dùng chung.
4. **Đồng thời per-account (Phần B):** `Provider.MaxConcurrent` = giới hạn **mỗi tài khoản**, `0 = unlimited`, `ExecutionEntry` thêm `AccountId/AccountName`, chọn TK least-in-flight, truyền accountId xuống forward; xóa setting `defaultMaxConcurrent`.

## Architecture

- **Core (engine):** `ExecutionList` query `MaxConcurrent` + danh sách TK enabled mỗi lần `TryEnter` → chọn TK least-in-flight (tie-break Priority → Id) → trả `long?` (null = park, 0 = sentinel); `DispatcherLoop` truyền `accountId` qua `ServeAsync` → `ChatCompletionsHandler.ForwardAsync` dùng đúng TK đã chọn (bỏ resolve lại).
- **Core (proxies):** `ProxyService` chuyển whole-set → diff trong cùng DbContext; thêm `GetReverseAssignmentsAsync` (2 query EF projection).
- **Core (server):** `ProxyHost.ResolveBindAddress(bool)` + `LanUrlProvider` (URL IPv4 non-loopback).
- **Core (settings):** key `lanAccess`; xóa `defaultMaxConcurrent`.
- **UI (Razor):** `Proxies.razor` (race token, `KeyNotFoundException` toast, hậu tố "(tắt)", expander reverse), `SettingsPanel.razor` (toggle LAN, bỏ field maxConcurrent, copy qua service), `Dashboard.razor` (URL LAN), `Providers.razor` (min=0, pin copy, bỏ seed settings), `ClipboardService` DI.
- **i18n:** mọi key thêm/sửa đều phải có ở **CẢ 2 dict** `English` + `Vietnamese` — `TranslationParityTests` enforce.

## Tech stack

.NET 10 / EF Core 8 (SQLite) / ASP.NET Core Kestrel + TestServer / xUnit / .NET MAUI Blazor Hybrid (Razor Components) / Tailwind-classes UI.

## Global constraints (mọi task)

- **REQUIRED SUB-SKILL:** `subagent-driven-development` — controller dispatch subagent từng task: task brief (verbatim từ plan) → implement TDD → review 2-axis (Spec/Standards) → fix loop → Approved → commit.
- **Gates** (repo root, app MAUI **đã đóng** — kiểm tra `Get-Process router-balancing` trống trước gate 2):
  1. `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0 warning / 0 error.
  2. `dotnet test "router balancing test/router balancing test.csproj"` → all pass.
  3. `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0 warning / 0 error.
  - Quy tắc chạy gate: **luôn** gate 1 + gate 2 sau mỗi task; **gate 3 khi task chạm `router-balancing/`** (Razor/csproj/MauiProgram). Task 4 chạy gate giữa chừng sau khi implement (RED giai đoạn đầu là chủ đích).
- **Flake policy** (rerun + isolation, KHÔNG accept): `ProxyControlApiTests` (File.Delete IOException), `ProxyRetryIntegrationTests`, `ProxyQueueIntegrationTests.Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId`; `SingleInstanceGuardTests` fail khi app đang chạy → kill app trước khi test.
- **Không push.** 1 task = 1 commit, message tiếng Anh conventional (`fix:`/`feat:`/`refactor:`).
- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; không commit secret; không debug print.
- Comment: XML doc (`///`) cho public API + giải thích "tại sao" tiếng Việt; exception message tiếng Anh; identifier tiếng Anh.
- Unit test name mô tả hành vi: `Method_WhenX_ExpectsY`.

## Bảng deviation (spec → plan)

| # | Spec nói | Plan làm | Lý do |
|---|---|---|---|
| V1 | D-B4: 0 TK enabled → TryEnter fail (park) | **Sentinel:** trả `0`, vẫn tạo entry (`AccountId=0, AccountName=""`) → forward 503 | Park không có wake signal → request treo vô hạn khi user chưa bật TK nào; sentinel 0 ≠ id thật (DB id ≥ 1) nên forward vẫn 503 đúng contract |
| V2 | i18n reverse-rỗng reuse `proxies.assign.none` | key mới `proxies.assign.reverseNone` | `proxies.assign.none` đã tồn tại với nghĩa khác: "Chưa gán proxy (direct)." (dùng ở Providers.razor) |
| V3 | (spec không liệt kê) | thêm `proxies.assign.scopeProvider` + `proxies.assign.scopeAccount` | dòng reverse cần nhãn "Provider X (rr)" / "Tài khoản Y (fb)" |
| V4 | test tên `TryEnter_ProviderMissing_ReturnsFalse`, `TryEnter_AllAccountsFull_ReturnsFalse` | đổi `...ReturnsNull` | API mới trả `long?` — "False" sai ngữ nghĩa; đổi tên hàng loạt cho nhất quán (`TryEnter_WhenAtMax_ReturnsFalse` → `ReturnsNull`) |
| V5 | 1 test `TryEnter_TwoAccounts_SkipsSaturatedAccount_BalancesLeastInFlight` | tách 2 test: `..._SkipsSaturatedAccount` (N=1) + `..._BalancesLeastInFlight_TieBreaksById` (N=2) | N=1 chỉ cover "bỏ TK đầy"; cover tie-break least-inflight cần N=2, 3 request |
| V6 | D-B3: AccountName "dùng cho ... snapshot `/v1/requests`" | thêm field `account` vào snapshot serving (`ProxyApp.cs`) | dùng đúng mục đích spec nêu; test hiện tại không assert vắng field → không vỡ |
| V7 | D-B1 "Validator UI: 0..64" | plan thêm input `min="0" max="64"` (spec §i18n có nêu input min="0") | khớp spec; ghi rõ để không bỏ sót |

## Test ladder

| Task | Δ tests | Expected |
|---|---|---|
| Baseline | — | 546 |
| T1 ProxyService diff-update | +3 (sửa 1 test có sẵn) | 549 |
| T2 Reverse assignments | +2 | 551 |
| T3 Proxies.razor UI | 0 (gate 3) | 551 |
| T4 Engine per-account | +5 ExecutionListTests (rewrite 15 test); sửa ModelSelectorTests/HandlerTests/2 park tests | 556 |
| T5 Validator 0..64 | +1 (sửa 1) | 557 |
| T6 Xóa defaultMaxConcurrent | 0 (sửa 1 test) | 557 |
| T7 LAN | +3 (BindAddressTests ×2 + ProxyHostTests ×1) | 560 |
| T8 Clipboard service | 0 | 560 |
| T9 Model pin copy | 0 (checklist tay) | 560 |
| T10 Gates + checklist | 0 | **560** |

---

## Task 1: ProxyService — EF diff-update + bỏ check Enabled (D-A1, D-A2, D-A5)

### Files

- `src/RouterBalancing.Core/Proxies/ProxyService.cs` — sửa `ValidateProxyIdsAsync`, `AssignProviderProxiesAsync`, `AssignAccountProxiesAsync`.
- `src/RouterBalancing.Core/Proxies/IProxyService.cs` — bỏ 2 dòng `<exception InvalidOperationException>`.
- `router balancing test/Proxies/ProxyServiceTests.cs` — 3 test mới, sửa 1 test.

### Interfaces

- **Consumes:** `IDbContextFactory<RouterBalancingDbContext>`, `IProxyPool.Invalidate()`, entity `ProviderProxy`/`ProviderAccountProxy`.
- **Produces:** `IProxyService.Assign*ProxiesAsync` — contract lỗi chỉ còn `KeyNotFoundException` (proxy không tồn tại / scope không tồn tại); `ValidateProxyIdsAsync` trở thành `private static`, nhận `RouterBalancingDbContext` của caller.

### Steps

- [ ] **Step 1: RED — thêm 3 test mới + sửa test Disabled (đặt sau `AssignProviderProxies_Empty_Clears`)**

  ```csharp
  [Fact]
  public async Task AssignProviderProxies_IdempotentResave_SameSet_Succeeds()
  {
      var service = CreateService();
      var proxy = await AddProxyAsync(port: 9510);
      var providerId = await AddProviderAsync();

      await service.AssignProviderProxiesAsync(providerId, [proxy], ProxyMode.Fallback);
      // Re-save cùng tập không được đụng hàng junction cũ — idempotent (D-A1)
      await service.AssignProviderProxiesAsync(providerId, [proxy], ProxyMode.Fallback);

      using var db = _factory.CreateDbContext();
      var provider = await db.Providers
          .Include(p => p.ProviderProxies)
          .SingleAsync(p => p.Id == providerId);
      Assert.Single(provider.ProviderProxies);
      Assert.Equal(proxy, provider.ProviderProxies.Single().ProxyId);
  }

  [Fact]
  public async Task AssignProviderProxies_OverlappingResave_ReplacesDifference()
  {
      var service = CreateService();
      var proxyA = await AddProxyAsync(port: 9511);
      var proxyB = await AddProxyAsync(port: 9512);
      var providerId = await AddProviderAsync();

      await service.AssignProviderProxiesAsync(providerId, [proxyA], ProxyMode.Fallback);
      // Tập mới chồng lấp: giữ hàng A, thêm hàng B — không replace toàn bộ (D-A1)
      await service.AssignProviderProxiesAsync(providerId, [proxyA, proxyB], ProxyMode.Fallback);

      using var db = _factory.CreateDbContext();
      var provider = await db.Providers
          .Include(p => p.ProviderProxies)
          .SingleAsync(p => p.Id == providerId);
      Assert.Equal(2, provider.ProviderProxies.Count);
      Assert.Contains(provider.ProviderProxies, r => r.ProxyId == proxyA);
      Assert.Contains(provider.ProviderProxies, r => r.ProxyId == proxyB);
  }

  [Fact]
  public async Task AssignProviderProxies_SameProxySecondProvider_BothPersist()
  {
      var service = CreateService();
      var proxy = await AddProxyAsync(port: 9513);
      var providerA = await AddProviderAsync();
      var providerB = await AddProviderAsync();

      await service.AssignProviderProxiesAsync(providerA, [proxy], ProxyMode.RoundRobin);
      // Triệu chứng user báo: cùng 1 proxy gán cho 2 provider phải có 2 hàng junction
      await service.AssignProviderProxiesAsync(providerB, [proxy], ProxyMode.RoundRobin);

      using var db = _factory.CreateDbContext();
      var rows = await db.Set<ProviderProxy>().AsNoTracking()
          .Where(r => r.ProxyId == proxy)
          .ToListAsync();
      Assert.Equal(2, rows.Count);
      Assert.Contains(rows, r => r.ProviderId == providerA);
      Assert.Contains(rows, r => r.ProviderId == providerB);
  }
  ```

  Sửa test hiện có (thay toàn bộ body `AssignProviderProxies_DisabledProxy_Throws`, đổi tên):

  ```csharp
  [Fact]
  public async Task AssignProviderProxies_DisabledProxy_Allowed()
  {
      var service = CreateService();
      var created = await service.CreateAsync(Draft(port: 9502));
      var providerId = await AddProviderAsync();

      await service.SetEnabledAsync(created.Id, enabled: false);

      // Proxy tắt vẫn gán được — pool tự lọc, request tự Direct (D-A2)
      await service.AssignProviderProxiesAsync(providerId, [created.Id], ProxyMode.Fallback);

      using var db = _factory.CreateDbContext();
      var provider = await db.Providers
          .Include(p => p.ProviderProxies)
          .SingleAsync(p => p.Id == providerId);
      Assert.Contains(provider.ProviderProxies, r => r.ProxyId == created.Id);
  }
  ```

  Expected: `dotnet test --filter "FullyQualifiedName~ProxyServiceTests"` RED — `IdempotentResave`/`OverlappingResave`/`SameProxySecondProvider` fail với `InvalidOperationException` (trùng PK junction), `DisabledProxy_Allowed` fail (còn ném `InvalidOperationException`).

- [ ] **Step 2: Thay `ValidateProxyIdsAsync` (ProxyService.cs:163-178)**

  ```csharp
  /// <summary>
  /// Validate proxy tồn tại — tập rỗng = gỡ toàn bộ gán (không validate).
  /// Proxy tắt vẫn là "đã gán" trong DB; pool tự lọc enabled nên request tự Direct (D-A2).
  /// Dùng chung DbContext của caller, không tạo context riêng — tránh leak (D-A5).
  /// </summary>
  private static async Task ValidateProxyIdsAsync(RouterBalancingDbContext db,
      IReadOnlyList<long> ids, CancellationToken ct)
  {
      if (ids.Count == 0)
          return;
      var wanted = ids.ToHashSet();
      var found = await db.OutboundProxies.AsNoTracking()
          .Where(p => wanted.Contains(p.Id))
          .Select(p => p.Id)
          .ToListAsync(ct);
      var missing = wanted.Except(found).ToList();
      if (missing.Count > 0)
          throw new KeyNotFoundException($"Proxy {missing[0]} not found.");
  }
  ```

  Expected: 1 query thay vì `CreateDbContext()` không dispose; không còn throw khi `!proxy.Enabled`.

- [ ] **Step 3: Diff-update `AssignProviderProxiesAsync` (ProxyService.cs:181-198)**

  ```csharp
  /// <inheritdoc/>
  public async Task AssignProviderProxiesAsync(long providerId, IReadOnlyList<long> proxyIds,
      ProxyMode? mode, CancellationToken ct = default)
  {
      using var db = _db.CreateDbContext();
      // Include junction: cần hàng cũ để diff (không include thì EF không biết remove gì)
      var provider = await db.Providers
          .Include(p => p.ProviderProxies)
          .FirstOrDefaultAsync(p => p.Id == providerId, ct)
          ?? throw new KeyNotFoundException($"Provider {providerId} not found.");
      await ValidateProxyIdsAsync(db, proxyIds, ct);

      // Diff-update thay whole-set replacement (D-A1): hàng cũ không trong tập mới → Remove,
      // hàng mới → Add, hàng trùng → giữ nguyên. Re-save cùng tập = không thao tác → idempotent,
      // không đụng hàng đang track nên không ném InvalidOperationException trùng PK.
      var wanted = proxyIds.ToHashSet();
      foreach (var row in provider.ProviderProxies.Where(r => !wanted.Contains(r.ProxyId)).ToList())
          provider.ProviderProxies.Remove(row);
      var existing = provider.ProviderProxies.Select(r => r.ProxyId).ToHashSet();
      foreach (var id in wanted.Where(id => !existing.Contains(id)))
          provider.ProviderProxies.Add(new ProviderProxy { ProviderId = provider.Id, ProxyId = id });

      provider.ProxyMode = mode;
      provider.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync(ct);
      _pool.Invalidate();
  }
  ```

  Expected: test `PersistsAndInvalidate` + `Empty_Clears` hiện tại vẫn xanh (tập mới hoàn toàn → Add all / Remove all).

- [ ] **Step 4: Tương tự `AssignAccountProxiesAsync` (ProxyService.cs:201-218)** — y hệt Step 3 với `db.ProviderAccounts` / `Include(a => a.AccountProxies)` / `Account { accountId } not found.` / `account.AccountProxies` / `account.ProxyMode` / `account.UpdatedAt`.

  Expected: test `AssignAccountProxies_OverridesProvider` + `GetAssignments_ReturnsProviderAndAccounts` giữ nguyên xanh.

- [ ] **Step 5: Cập nhật `IProxyService` XML doc** — xóa 2 dòng sau ở cả `AssignProviderProxiesAsync` (dòng 39) và `AssignAccountProxiesAsync` (dòng 46):

  ```csharp
  /// <exception cref="InvalidOperationException">Proxy trong tập đang bị tắt.</exception>
  ```

  Expected: contract doc khớp runtime (chỉ còn `KeyNotFoundException`).

- [ ] **Step 6: Verify**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyServiceTests"   # 46+3 GREEN
  dotnet test "router balancing test/router balancing test.csproj"   # 549/549
  ```

### Commit

`fix: replace proxy assignment whole-set write with EF diff update`

---

## Task 2: `GetReverseAssignmentsAsync` + record `ProxyUsage` (D-A6)

### Files

- **Mới:** `src/RouterBalancing.Core/Proxies/ProxyUsage.cs`
- `src/RouterBalancing.Core/Proxies/IProxyService.cs` — thêm 1 method.
- `src/RouterBalancing.Core/Proxies/ProxyService.cs` — implement.
- `router balancing test/Proxies/ProxyServiceTests.cs` — 2 test mới.

### Interfaces

- **Consumes:** `Provider.ProviderProxies`, `ProviderAccount.AccountProxies` (junction EF).
- **Produces:** `IProxyService.GetReverseAssignmentsAsync(CancellationToken = default) → IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>` — key = proxy id; proxy không gán cho ai **không có key**.

### Steps

- [ ] **Step 1: File mới `ProxyUsage.cs`**

  ```csharp
  using RouterBalancing.Core.Domain;

  namespace RouterBalancing.Core.Proxies;

  /// <summary>Một scope (provider/account) đang dùng một proxy — data source cho reverse view (D-A6).</summary>
  /// <param name="ScopeId">Id của provider hoặc account.</param>
  /// <param name="ScopeName">Tên hiển thị của scope.</param>
  /// <param name="IsProvider">true = provider, false = account.</param>
  /// <param name="Mode">Chế độ dùng proxy của scope đó (null = kế thừa/unified).</param>
  public sealed record ProxyUsage(long ScopeId, string ScopeName, bool IsProvider, ProxyMode? Mode);
  ```

- [ ] **Step 2: Thêm method vào `IProxyService` (sau `GetAssignmentsAsync`)**

  ```csharp
  /// <summary>
  /// Tất cả provider/account đang gán cho mỗi proxy — đúng 2 query tổng (provider trước,
  /// account sau), không N+1 (D-A6). Proxy chưa gán cho ai không xuất hiện trong map.
  /// </summary>
  Task<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>> GetReverseAssignmentsAsync(
      CancellationToken ct = default);
  ```

- [ ] **Step 3: Implement trong `ProxyService` (sau `GetAssignmentsAsync`)**

  ```csharp
  /// <inheritdoc/>
  public async Task<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>> GetReverseAssignmentsAsync(
      CancellationToken ct = default)
  {
      using var db = _db.CreateDbContext();

      // EF projection (không Include) — đúng 2 query tổng, không N+1 (D-A6)
      var providerRows = await db.Providers.AsNoTracking()
          .Where(p => p.ProviderProxies.Count > 0)
          .Select(p => new
          {
              p.Id,
              p.Name,
              Mode = p.ProxyMode,
              ProxyIds = p.ProviderProxies.Select(x => x.ProxyId).ToList(),
          })
          .ToListAsync(ct);
      var accountRows = await db.ProviderAccounts.AsNoTracking()
          .Where(a => a.AccountProxies.Count > 0)
          .Select(a => new
          {
              a.Id,
              a.Name,
              Mode = a.ProxyMode,
              ProxyIds = a.AccountProxies.Select(x => x.ProxyId).ToList(),
          })
          .ToListAsync(ct);

      var map = new Dictionary<long, List<ProxyUsage>>();
      foreach (var p in providerRows)
          AddUsage(map, p.ProxyIds, new ProxyUsage(p.Id, p.Name, IsProvider: true, p.Mode));
      foreach (var a in accountRows)
          AddUsage(map, a.ProxyIds, new ProxyUsage(a.Id, a.Name, IsProvider: false, a.Mode));

      return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ProxyUsage>)kv.Value);
  }

  /// <summary>1 proxy có nhiều scope — cộng dồn usage thay vì ghi đè theo key proxy.</summary>
  private static void AddUsage(Dictionary<long, List<ProxyUsage>> map, List<long> proxyIds, ProxyUsage usage)
  {
      foreach (var proxyId in proxyIds)
      {
          if (!map.TryGetValue(proxyId, out var list))
              map[proxyId] = list = [];
          list.Add(usage);
      }
  }
  ```

- [ ] **Step 4: RED — 2 test mới (đặt cuối class, trước `StubEchoClient`)**

  ```csharp
  [Fact]
  public async Task GetReverseAssignments_ReturnsProvidersAndAccounts()
  {
      var service = CreateService();
      var proxy = await AddProxyAsync(port: 9515);
      var providerId = await AddProviderAsync();
      var accountId = await AddAccountAsync(providerId);

      await service.AssignProviderProxiesAsync(providerId, [proxy], ProxyMode.RoundRobin);
      await service.AssignAccountProxiesAsync(accountId, [proxy], ProxyMode.Fallback);

      var result = await service.GetReverseAssignmentsAsync();

      var usages = result[proxy];
      var providerUsage = usages.Single(u => u.IsProvider);
      Assert.Equal(providerId, providerUsage.ScopeId);
      Assert.Equal("Test", providerUsage.ScopeName);
      Assert.Equal(ProxyMode.RoundRobin, providerUsage.Mode);

      var accountUsage = usages.Single(u => !u.IsProvider);
      Assert.Equal(accountId, accountUsage.ScopeId);
      Assert.Equal("Acc", accountUsage.ScopeName);
      Assert.Equal(ProxyMode.Fallback, accountUsage.Mode);
  }

  [Fact]
  public async Task GetReverseAssignments_ProxyWithoutAssignment_NotPresent()
  {
      var service = CreateService();
      var assigned = await AddProxyAsync(port: 9515);
      var unassigned = await AddProxyAsync(port: 9516);
      var providerId = await AddProviderAsync();
      await service.AssignProviderProxiesAsync(providerId, [assigned], ProxyMode.RoundRobin);

      var result = await service.GetReverseAssignmentsAsync();

      Assert.True(result.ContainsKey(assigned));
      Assert.False(result.ContainsKey(unassigned));
  }
  ```

- [ ] **Step 5: Verify**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyServiceTests"   # GREEN
  dotnet test "router balancing test/router balancing test.csproj"   # 551/551
  ```

### Commit

`feat: add reverse proxy assignment lookup`

---

## Task 3: Proxies.razor — race token, lỗi cụ thể, "(tắt)", expander reverse (D-A2/A3/A4/A6)

### Files

- `router-balancing/Components/Pages/Proxies.razor` — state, `LoadAssignmentAsync`, `OnProviderSelected`, `SaveAssignment`, `ReloadAsync`, markup danh sách checkbox, helpers.
- `src/RouterBalancing.Core/Localization/Translations.cs` — 5 key × 2 dict.

### Interfaces

- **Consumes:** `IProxyService.GetReverseAssignmentsAsync` (Task 2), `ProxyUsage`, key i18n mới.
- **Produces:** UI không đổi API; **gate 3** (build app) vì chỉ sửa Razor + i18n.

### Steps

- [ ] **Step 1: i18n — thêm 5 key × 2 dict**

  English — chèn **sau** dòng `["proxies.assign.badge_count"] = "{0} proxy",` (dòng 321, trước `};`):

  ```csharp
          ["proxies.assign.disabledSuffix"] = "(disabled)",
          ["proxies.assign.reverseNone"] = "Not assigned",
          ["proxies.assign.scopeProvider"] = "Provider",
          ["proxies.assign.scopeAccount"] = "Account",
          ["proxies.assign.error.scopeNotFound"] = "Proxy or scope was deleted elsewhere — refresh and try again.",
  ```

  Vietnamese — chèn **sau** dòng `["proxies.assign.badge_count"] = "{0} proxy",` (dòng 635, trước `};`):

  ```csharp
          ["proxies.assign.disabledSuffix"] = "(tắt)",
          ["proxies.assign.reverseNone"] = "Chưa gán",
          ["proxies.assign.scopeProvider"] = "Provider",
          ["proxies.assign.scopeAccount"] = "Tài khoản",
          ["proxies.assign.error.scopeNotFound"] = "Proxy hoặc đối tượng đã bị xóa ở nơi khác — tải lại và thử lại.",
  ```

  > Lưu ý: dùng key **mới** `proxies.assign.reverseNone` thay vì `proxies.assign.none` (spec D-A6) — deviation V2, vì `proxies.assign.none` đã mang nghĩa khác ("Chưa gán proxy (direct).").

- [ ] **Step 2: State mới (sau `_assignments` ở dòng 249)**

  ```csharp
      // Reverse view: proxy-id → scope đang dùng (D-A6); không có key = chưa gán ai
      private IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>> _reverseAssignments =
          new Dictionary<long, IReadOnlyList<ProxyUsage>>();
      // Race token: mỗi lần đổi scope tăng 1 — load của scope cũ về trễ bị discard (D-A3)
      private int _assignLoadToken;
      private bool _assignmentLoading;
      private long? _expandedUsageProxyId;
  ```

- [ ] **Step 3: Thay `LoadAssignmentAsync` (dòng 497-522) — nhận token + cờ loading**

  ```csharp
      /// <summary>
      /// Pre-fill form gán proxy khi đổi provider — đọc assignment hiện có (D1–D8):
      /// cache cả tập provider lẫn account rồi đổ vào form theo scope đang chọn.
      /// Token: load của scope cũ về trễ bị discard, không áp dữ liệu lên scope mới (D-A3).
      /// </summary>
      private async Task LoadAssignmentAsync(int loadToken)
      {
          _assignmentLoading = true;
          try
          {
              if (_assignProviderId is null)
              {
                  if (loadToken != _assignLoadToken) return;
                  _assignments = [];
                  ApplyScopeToForm();
                  StateHasChanged();
                  return;
              }

              List<ProxyAssignment> assignments;
              try
              {
                  // GetAssignmentsAsync trả về hàng provider + mọi account của provider —
                  // 1 query duy nhất cho cả 2 scope, đổi scope account không tốn query thêm.
                  assignments = (await ProxySvc.GetAssignmentsAsync(_assignProviderId.Value)).ToList();
              }
              catch (Exception ex)
              {
                  assignments = [];
                  Log.Error("Không tải được gán proxy cho provider.", ex);
                  Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
              }

              // Load cũ (token lệch) KHÔNG được áp dữ liệu lên scope mới (D-A3)
              if (loadToken != _assignLoadToken) return;
              _assignments = assignments;
              ApplyScopeToForm();
              StateHasChanged();
          }
          finally
          {
              // Chỉ owner của token hiện tại mới hạ cờ — load mồ côi không đụng trạng thái mới
              if (loadToken == _assignLoadToken)
                  _assignmentLoading = false;
          }
      }
  ```

- [ ] **Step 4: Sửa 2 chỗ gọi**

  `OnProviderSelected` (dòng 551) — thay `_ = LoadAssignmentAsync();` thành:

  ```csharp
          // Tăng token trước khi start: load của scope trước (nếu còn chạy) sẽ bị discard (D-A3)
          _ = LoadAssignmentAsync(++_assignLoadToken);
  ```

  `SaveAssignment` (dòng 611) — thay `await LoadAssignmentAsync();` thành:

  ```csharp
              // Ghi xong → nạp lại theo token mới để form phản ánh đúng DB (và bỏ loading cũ)
              await LoadAssignmentAsync(++_assignLoadToken);
  ```

  Guard đầu `SaveAssignment` (dòng 582):

  ```csharp
          if (_busy || _assignProviderId is null || _assignmentLoading) return;
  ```

  Button Save (dòng 131-132):

  ```razor
              <button class="btn btn-primary" @onclick="SaveAssignment"
                      disabled="@(_busy || _assignProviderId is null || _assignmentLoading)">
                  @L["proxies.assign.save"]
              </button>
  ```

  Expected: đổi scope nhanh → checkbox luôn đúng của scope đang chọn; save bị chặn trong lúc load.

- [ ] **Step 5: Catch `KeyNotFoundException` riêng trong `SaveAssignment` (thay block catch dòng 613-617)**

  ```csharp
          catch (KeyNotFoundException ex)
          {
              // Proxy/provider bị xoá ở nơi khác giữa lúc load và lúc save — toast riêng (D-A4)
              Log.Error("Không gán được proxy cho scope đang chọn.", ex);
              Toast.Show(L["proxies.assign.error.scopeNotFound"], ToastSeverity.Error);
          }
          catch (Exception ex)
          {
              Log.Error("Không gán được proxy cho scope đang chọn.", ex);
              Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
          }
  ```

- [ ] **Step 6: `ReloadAsync` — nạp reverse view (chèn sau try/catch `_providers`, trước `StateHasChanged()` dòng 304)**

  ```csharp
          // Reverse view (D-A6) — best-effort: lỗi không chặn load proxy chính
          try
          {
              _reverseAssignments = await ProxySvc.GetReverseAssignmentsAsync();
          }
          catch (Exception ex)
          {
              Log.Error("Không tải được danh sách gán đảo.", ex);
          }
  ```

- [ ] **Step 7: Markup danh sách checkbox (thay vòng `@foreach` dòng 140-148)**

  ```razor
              <div class="flex flex-col gap-1">
                  @foreach (var row in _rows)
                  {
                      <div class="flex items-center gap-2 text-sm">
                          <label class="flex min-w-0 flex-1 items-center gap-2">
                              <input type="checkbox" checked="@(_selectedProxyIds.Contains(row.Id))"
                                     disabled="@_busy"
                                     @onchange="() => ToggleProxySelection(row.Id)" />
                              <span class="font-mono">@EndpointOf(row)</span>
                              @if (!row.Enabled)
                              {
                                  @* Hậu tố trạng thái — proxy tắt vẫn tick được, pool tự lọc (D-A2) *@
                                  <span class="text-xs opacity-70">@L["proxies.assign.disabledSuffix"]</span>
                              }
                          </label>
                          @* Xem proxy đang gán cho ai (D-A6) — không thêm cột vào bảng chính *@
                          <button type="button" class="btn btn-outline-secondary px-1.5"
                                  disabled="@_busy"
                                  aria-expanded="@(_expandedUsageProxyId == row.Id)"
                                  @onclick="() => ToggleUsageExpansion(row.Id)">
                              @(_expandedUsageProxyId == row.Id ? "▾" : "▸")
                          </button>
                      </div>
                      @if (_expandedUsageProxyId == row.Id)
                      {
                          var usages = UsagesOf(row.Id);
                          <div class="pl-6 text-xs opacity-70" data-testid="proxy-usage">
                              @(usages.Count == 0
                                  ? L["proxies.assign.reverseNone"]
                                  : string.Join(" · ", usages.Select(UsageLabel)))
                          </div>
                      }
                  }
              </div>
  ```

  > `<label>` bọc checkbox được tách khỏi nút expander — click button không vô tình toggle checkbox.

- [ ] **Step 8: Helpers (đặt sau `ApplyScopeToForm`)**

  ```csharp
      /// <summary>Các scope đang dùng proxy — thiếu key = chưa gán ai (D-A6).</summary>
      private IReadOnlyList<ProxyUsage> UsagesOf(long proxyId) =>
          _reverseAssignments.TryGetValue(proxyId, out var usages) ? usages : [];

      /// <summary>Dòng mô tả 1 scope: "Provider OpenAI (rr)" / "Tài khoản acc-1 (fb)".</summary>
      private string UsageLabel(ProxyUsage usage)
      {
          var scope = L[usage.IsProvider ? "proxies.assign.scopeProvider" : "proxies.assign.scopeAccount"];
          var mode = usage.Mode switch
          {
              ProxyMode.RoundRobin => " (rr)",
              ProxyMode.Fallback => " (fb)",
              _ => string.Empty,
          };
          return $"{scope} {usage.ScopeName}{mode}";
      }

      private void ToggleUsageExpansion(long proxyId)
      {
          _expandedUsageProxyId = _expandedUsageProxyId == proxyId ? null : proxyId;
          StateHasChanged();
      }
  ```

- [ ] **Step 9: Verify (gate 1 + 2 + 3)**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj"   # 551/551 (TranslationParity + parity 5 key)
  dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
  ```

### Commit

`feat: harden proxy assignment UI and add reverse usage view`

---

## Task 4: Engine per-account — TryEnter trả accountId, 0 = unlimited, sentinel (D-B1…D-B8)

> **Lưu ý RED:** sửa test TRƯỚC khi sửa src — build test project sẽ fail compile chủ đích (CS1501/CS1503/CS0117) rồi mới implement. DispatcherLoopTests/ComboResolverTests **không sửa** (đã seed 1 account enabled + dùng ExecutionList thật — đã verify).

### Files

- `src/RouterBalancing.Core/Engine/ExecutionEntry.cs` — thêm 2 field.
- `src/RouterBalancing.Core/Engine/IExecutionList.cs` — `TryEnterAsync` trả `Task<long?>`; `CanEnterAsync` giữ `Task<bool>`; XML doc cập nhật.
- `src/RouterBalancing.Core/Engine/ExecutionList.cs` — capacity per-account + chọn TK least-in-flight.
- `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` — nhận accountId, truyền qua `ServeAsync`, retry-walk.
- `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` — `ForwardAsync` thêm param `accountId`.
- `src/RouterBalancing.Core/Server/ProxyApp.cs` — snapshot serving thêm field `account` (V6).
- Tests: `ExecutionListTests.cs` (rewrite), `ModelSelectorTests.cs`, `ChatCompletionsHandlerTests.cs` (13 call site), `ProxyControlApiTests.cs` (park test), `ProxyQueueIntegrationTests.cs` (park test).

### Interfaces

- **Consumes:** `Provider.MaxConcurrent` (0 = unlimited), `ProviderAccount.{Id,Name,Enabled,Priority}`, `IDbContextFactory`.
- **Produces:**
  - `IExecutionList.TryEnterAsync(...) → Task<long?>` — null = park/mất provider; **0 = sentinel** (provider OK, 0 TK enabled → entry AccountId=0 → forward 503 — V1).
  - `IExecutionList.CanEnterAsync` — provider missing → false; 0 TK enabled → **true** (để selector chọn, TryEnter tạo sentinel); ≥1 TK còn capacity → true.
  - `ChatCompletionsHandler.ForwardAsync(HttpContext, Provider, Model, byte[], long accountId, CancellationToken)` — param mới `accountId`.
  - `ExecutionEntry(long AccountId, string AccountName)` — thêm cuối record.

### Steps

- [ ] **Step 1: Rewrite `router balancing test/Engine/ExecutionListTests.cs` (toàn file — 15 test)**

  ```csharp
  using Microsoft.EntityFrameworkCore;
  using RouterBalancing.Core.Domain;
  using RouterBalancing.Core.Engine;
  using RouterBalancing.Core.Storage;

  namespace router_balancing_test.Engine;

  public class ExecutionListTests : IDisposable
  {
      private readonly TestDb _db = new();

      public ExecutionListTests()
      {
          DbInitializer.Initialize(_db.CreateFactory());
      }

      public void Dispose() => _db.Dispose();

      /// <summary>Seed provider với N tài khoản enabled — accountCount = 0 để test sentinel (D-B4+).</summary>
      private long SeedProvider(string name, int maxConcurrent = 4, int accountCount = 1)
      {
          using var db = _db.CreateFactory().CreateDbContext();
          var provider = new Provider
          {
              Name = name,
              BaseUrl = "https://api.openai.com",
              MaxConcurrent = maxConcurrent,
          };
          for (var i = 1; i <= accountCount; i++)
          {
              provider.Accounts.Add(new ProviderAccount
              {
                  Name = $"{name}-acc{i}",
                  Enabled = true,
              });
          }
          db.Providers.Add(provider);
          db.SaveChanges();
          return provider.Id;
      }

      private ExecutionList CreateSut() => new(_db.CreateFactory());

      // static readonly (không phải property) — giá trị phải cố định giữa lúc Enter
      // và lúc assert, nếu re-evaluate UtcNow thì Assert.Equal(Enq, ...) luôn lệch.
      private static readonly DateTimeOffset Enq = DateTimeOffset.UtcNow.AddMinutes(-1);

      [Fact]
      public async Task TryEnter_WhenBelowMax_ReturnsAccountIdAndTracksEntryWithAllFields()
      {
          var pid = SeedProvider("p1", maxConcurrent: 2);
          var sut = CreateSut();
          var accountId = EnabledAccountIds(pid).Single();

          var ok = await sut.TryEnterAsync(pid, "req00001", "p1", "gpt-4o-mini",
              RequestPriority.High, Enq, default);

          Assert.NotNull(ok);
          Assert.Equal(accountId, ok); // trả đúng TK được chọn, không chỉ bool (D-B6)
          Assert.True(sut.Contains("req00001"));
          var entry = Assert.Single(sut.Snapshot());
          Assert.Equal("req00001", entry.RequestId);
          Assert.Equal(pid, entry.ProviderId);
          Assert.Equal("p1", entry.ProviderName);
          Assert.Equal("gpt-4o-mini", entry.Model);
          Assert.Equal(RequestPriority.High, entry.Priority);
          Assert.Equal(Enq, entry.EnqueuedAt);
          Assert.True(entry.StartedAt >= entry.EnqueuedAt);
          Assert.Equal(accountId, entry.AccountId); // chiều TK cho đếm/log/snapshot (D-B3)
          Assert.Equal("p1-acc1", entry.AccountName);
      }

      [Fact]
      public async Task TryEnter_WhenAtMax_ReturnsNull()
      {
          var pid = SeedProvider("p1", maxConcurrent: 1);
          var sut = CreateSut();
          Assert.NotNull(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));

          Assert.Null(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));
          Assert.False(sut.Contains("req00002"));
      }

      [Fact]
      public async Task TryEnter_WhenProviderMissing_ReturnsNull()
      {
          var sut = CreateSut();

          // Provider không tồn tại → không enter — 0 KHÔNG được làm sentinel vì 0 = unlimited (D-B7)
          Assert.Null(await sut.TryEnterAsync(999, "req00001", "ghost", "m", RequestPriority.Normal, Enq, default));
          Assert.False(sut.Contains("req00001"));
      }

      [Fact]
      public async Task TryEnter_ReflectsLatestMaxConcurrentFromDb()
      {
          var pid = SeedProvider("p1", maxConcurrent: 1);
          var sut = CreateSut();
          Assert.NotNull(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));
          Assert.Null(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));

          using (var db = _db.CreateFactory().CreateDbContext())
          {
              var p = await db.Providers.FirstAsync(x => x.Id == pid);
              p.MaxConcurrent = 2;
              await db.SaveChangesAsync();
          }

          // Không cache — giá trị mới nhất từ DB tại mỗi lần Enter (spec §2.1)
          Assert.NotNull(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));
      }

      [Fact]
      public async Task TryEnter_TwoAccounts_SkipsSaturatedAccount()
      {
          var pid = SeedProvider("p1", maxConcurrent: 1, accountCount: 2);
          var sut = CreateSut();

          var first = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);
          var second = await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default);

          // TK1 đầy (N=1) → request 2 nhảy sang TK2, không bị chặn (D-B4.2)
          Assert.NotNull(first);
          Assert.NotNull(second);
          Assert.NotEqual(first, second);
          Assert.Equal(2, sut.Snapshot().Select(e => e.AccountId).Distinct().Count());
      }

      [Fact]
      public async Task TryEnter_TwoAccounts_BalancesLeastInFlight_TieBreaksById()
      {
          var pid = SeedProvider("p1", maxConcurrent: 2, accountCount: 2);
          var sut = CreateSut();

          var first = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);
          var second = await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default);
          var third = await sut.TryEnterAsync(pid, "req00003", "p1", "m", RequestPriority.Normal, Enq, default);

          // Least-in-flight: req2 thấy TK1 (1) > TK2 (0) → TK2;
          // req3 tie 1-1 → Id tăng dần → TK1 (D-B4.3)
          Assert.NotNull(first);
          Assert.NotNull(second);
          Assert.NotNull(third);
          Assert.NotEqual(first, second);
          Assert.Equal(first, third);
      }

      [Fact]
      public async Task TryEnter_AllAccountsFull_ReturnsNull()
      {
          var pid = SeedProvider("p1", maxConcurrent: 1, accountCount: 2);
          var sut = CreateSut();
          Assert.NotNull(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));
          Assert.NotNull(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));

          // Mọi TK enabled đều đầy → park (null), không tạo entry (D-B4.4)
          Assert.Null(await sut.TryEnterAsync(pid, "req00003", "p1", "m", RequestPriority.Normal, Enq, default));
          Assert.False(sut.Contains("req00003"));
      }

      [Fact]
      public async Task TryEnter_ZeroMax_ConcurrentUnlimited()
      {
          var pid = SeedProvider("p1", maxConcurrent: 0);
          var sut = CreateSut();

          for (var i = 0; i < 5; i++)
          {
              Assert.NotNull(await sut.TryEnterAsync(pid, $"req{i}", "p1", "m",
                  RequestPriority.Normal, Enq, default));
          }

          Assert.Equal(5, sut.GetInFlight(pid)); // 0 = không giới hạn (D-B1)
      }

      [Fact]
      public async Task TryEnter_WhenNoEnabledAccounts_ReturnsSentinelZeroAndCreatesEntry()
      {
          var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 0);
          var sut = CreateSut();

          var sentinel = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);

          // Sentinel 0: provider OK nhưng 0 TK enabled → entry vẫn tạo để forward trả 503,
          // không park vô hạn khi user chưa bật TK nào (deviation V1 / D-B4+)
          Assert.Equal(0, sentinel);
          var entry = Assert.Single(sut.Snapshot());
          Assert.Equal(0, entry.AccountId);
          Assert.Equal(string.Empty, entry.AccountName);
      }

      [Fact]
      public async Task TryEnter_SkipsDisabledAccount_UsesEnabledOne()
      {
          var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 2);
          using (var db = _db.CreateFactory().CreateDbContext())
          {
              var first = await db.ProviderAccounts
                  .Where(a => a.ProviderId == pid)
                  .OrderBy(a => a.Id)
                  .FirstAsync();
              first.Enabled = false;
              await db.SaveChangesAsync();
          }
          var enabledId = EnabledAccountIds(pid).Single();
          var sut = CreateSut();

          var ok = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);

          Assert.Equal(enabledId, ok); // TK tắt không bao giờ được chọn (D-B4.1)
      }

      [Fact]
      public async Task Exit_RemovesEntryAndFiresExitedOnce()
      {
          var pid = SeedProvider("p1");
          var sut = CreateSut();
          await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);
          var fired = 0;
          sut.Exited += () => fired++;

          sut.Exit("req00001");

          Assert.Equal(1, fired);
          Assert.False(sut.Contains("req00001"));
          Assert.Equal(0, sut.GetInFlight(pid));
      }

      [Fact]
      public async Task Exit_UnknownId_DoesNotFireExited()
      {
          var pid = SeedProvider("p1");
          var sut = CreateSut();
          await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);
          var fired = 0;
          sut.Exited += () => fired++;

          sut.Exit("ghost");

          Assert.Equal(0, fired);
          Assert.True(sut.Contains("req00001"));
      }

      [Fact]
      public async Task CanEnter_ChecksWithoutMutating()
      {
          var pid = SeedProvider("p1", maxConcurrent: 1);
          var sut = CreateSut();

          Assert.True(await sut.CanEnterAsync(pid, default));
          Assert.Equal(0, sut.GetInFlight(pid)); // check không mutate — selector dùng được nhiều lần

          Assert.NotNull(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));
          Assert.False(await sut.CanEnterAsync(pid, default));
      }

      [Fact]
      public async Task CanEnter_WhenNoEnabledAccounts_ReturnsTrue()
      {
          var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 0);
          var sut = CreateSut();

          // Sentinel (V1): CanEnter true để selector chọn → TryEnter tạo entry → forward 503
          Assert.True(await sut.CanEnterAsync(pid, default));
      }

      [Fact]
      public async Task Snapshot_ReturnsAllLiveEntriesAcrossProviders()
      {
          var p1 = SeedProvider("p1");
          var p2 = SeedProvider("p2");
          var sut = CreateSut();
          await sut.TryEnterAsync(p1, "req00001", "p1", "m1", RequestPriority.Normal, Enq, default);
          await sut.TryEnterAsync(p2, "req00002", "p2", "m2", RequestPriority.Highest, Enq, default);

          var snapshot = sut.Snapshot();

          Assert.Equal(2, snapshot.Count);
          Assert.Contains(snapshot, e => e.RequestId == "req00001" && e.ProviderId == p1);
          Assert.Contains(snapshot, e => e.RequestId == "req00002" && e.Priority == RequestPriority.Highest);
          Assert.Equal(1, sut.GetInFlight(p1));
          Assert.Equal(1, sut.GetInFlight(p2));
      }

      private List<long> EnabledAccountIds(long providerId)
      {
          using var db = _db.CreateFactory().CreateDbContext();
          return db.ProviderAccounts.AsNoTracking()
              .Where(a => a.ProviderId == providerId && a.Enabled)
              .OrderBy(a => a.Id)
              .Select(a => a.Id)
              .ToList();
      }
  }
  ```

- [ ] **Step 2: `ModelSelectorTests.cs` — seed account + đổi assert**

  Trong `SeedProvider` (sau `provider.Models.Add(...)` dòng 30):

  ```csharp
          provider.Accounts.Add(new ProviderAccount
          {
              Name = $"{name}-acc",
              Enabled = true,
          });
  ```

  Trong `OccupyAsync` (dòng 49) — `Assert.True(ok);` →

  ```csharp
              Assert.NotNull(ok); // TryEnter trả long? (accountId) — D-B6
  ```

- [ ] **Step 3: `ChatCompletionsHandlerTests.cs` — helper + 13 call site**

  Thêm helper sau `ModelOf` (dòng 38):

  ```csharp
      // Entity in-memory (chưa SaveChanges) → Id = 0 khớp account duy nhất của test;
      // withKey: false → không có account → -1 → handler không match → 503 như cũ.
      private static long AccountIdOf(Provider provider) => provider.Accounts.FirstOrDefault()?.Id ?? -1;
  ```

  Replace toàn bộ (đúng 13 chỗ — verify `rg -c "AccountIdOf\(provider\)"` = 13):

  - `Body(ValidJson), default);` → `Body(ValidJson), AccountIdOf(provider), default);` (11 chỗ: dòng 180, 200, 214, 235, 258, 279, 301, 316, 333, 400, 434)
  - `Body(StreamJson), default);` → `Body(StreamJson), AccountIdOf(provider), default);` (2 chỗ: dòng 378, 418)

- [ ] **Step 4: Sửa 2 park test (max=0 → saturate bằng max=1 + holder)**

  `ProxyControlApiTests.Cancel_WhenRequestIsQueued_Returns200AndOriginGets400` (dòng 243-265) — thay toàn bộ body:

  ```csharp
      [Fact]
      public async Task Cancel_WhenRequestIsQueued_Returns200AndOriginGets400()
      {
          // max=0 giờ nghĩa là unlimited (D-B1) — park bằng cách bão hòa 1 tài khoản với N=1
          SeedProvider(maxConcurrent: 1, "m1");
          var upstream = new GatedUpstream();
          var client = await StartAsync(upstream);

          var holding = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // holder chiếm trọn slot
          var pending = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          var queuedId = await WaitForQueuedIdAsync("m1");

          var response = await CancelAsync(queuedId);

          Assert.Equal(HttpStatusCode.OK, response.StatusCode);
          var json = await ReadJson(response);
          Assert.True(json.GetProperty("cancelled").GetBoolean());

          // Endpoint gốc đang await TCS → nhận Cancelled → ghi 400 request_cancelled (spec §3.4)
          var origin = await pending.WaitAsync(TimeSpan.FromSeconds(5));
          Assert.Equal(HttpStatusCode.BadRequest, origin.StatusCode);
          var error = (await ReadJson(origin)).GetProperty("error");
          Assert.Equal("request_cancelled", error.GetProperty("code").GetString());
          Assert.Equal(queuedId, origin.Headers.GetValues("X-Request-Id").Single());

          // Dọn có kiểm chứng: mở gate cho holder — không treo Dispose
          upstream.Release();
          Assert.Equal(HttpStatusCode.OK, (await holding).StatusCode);
      }
  ```

  > `WaitForQueuedIdAsync("m1")` vẫn đúng: holder đã ra queue (đang serve), chỉ request bị park còn trong queue.

  `ProxyQueueIntegrationTests.Snapshot_ReflectsPriorityHeaderAndTiming` (dòng 221-272) — sửa phần seed/holder và cleanup:

  - Dòng 224-226 thay:

  ```csharp
          // max=0 giờ là unlimited — giữ request trong queue bằng holder chiếm slot (N=1)
          SeedProvider(maxConcurrent: 1, "m1");
          var upstream = new GatedUpstream();
          var client = await StartAsync(upstream);
          var holding = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
  ```

  (các assert + nhánh `abort.Cancel()` giữ nguyên 100%).

  - Cuối test — sau block `try/finally` (sau dòng 271) thêm:

  ```csharp
          // Mở gate cho holder để Dispose không treo; assert có kiểm chứng
          upstream.Release();
          Assert.Equal(HttpStatusCode.OK, (await holding).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t => t.Result).Result);
  ```

  > Viết gọn lại thành 2 dòng chuẩn (dùng `await`):

  ```csharp
          upstream.Release();
          var ok = await holding.WaitAsync(TimeSpan.FromSeconds(5));
          Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
  ```

  (đặt sau `finally` — nếu assert trong `try` fail thì `finally` vẫn unsubcribe log; holder được release trong mọi nhánh: thêm `upstream.Release();` vào `finally` ngay sau `_log.LogAdded -= OnLog;` để chắc chắn không treo, và 2 dòng assert phía trên chỉ chạy trên nhánh thành công.)

  → Cấu trúc cuối của `finally`:

  ```csharp
          finally
          {
              _log.LogAdded -= OnLog;
              upstream.Release(); // holder luôn được giải phóng — Dispose không treo
          }
  ```

  và **không** cần assert 200 của holder ở test này (đã có cleanup pattern trong `ProxyControlApiTests` ở trên; giữ assert tại đó).

- [ ] **Step 5: RED — build test project, ghi nhận lỗi chủ đích**

  ```bash
  dotnet build "router balancing test/router balancing test.csproj"
  ```

  Expected RED (một phần): `CS1501` (ForwardAsync chưa nhận 6 tham số — ChatCompletionsHandlerTests), `CS1503` (Assert.True/False nhận `long?` — ExecutionListTests/ModelSelectorTests), `CS0117` (`ExecutionEntry` chưa có `AccountId`).

- [ ] **Step 6: Implement Core — `ExecutionEntry.cs` (thêm 2 param cuối)**

  ```csharp
  /// <summary>1 request đang được phục vụ — data source cho snapshot và cancel-409 (spec §2.1).</summary>
  /// <param name="RequestId">Id request (8 ký tự).</param>
  /// <param name="ProviderId">Provider đang giữ request.</param>
  /// <param name="ProviderName">Tên provider (snapshot không cần join DB).</param>
  /// <param name="Model">ModelId đang serve.</param>
  /// <param name="Priority">Priority tại thời điểm enqueue.</param>
  /// <param name="EnqueuedAt">Thời điểm vào queue.</param>
  /// <param name="StartedAt">Thời điểm bắt đầu serve (lúc TryEnter thành công).</param>
  /// <param name="AccountId">TK giữ slot — 0 = sentinel khi provider không có TK enabled nào (D-B3, V1).</param>
  /// <param name="AccountName">Tên TK; chuỗi rỗng ở sentinel.</param>
  public sealed record ExecutionEntry(
      string RequestId,
      long ProviderId,
      string ProviderName,
      string Model,
      RequestPriority Priority,
      DateTimeOffset EnqueuedAt,
      DateTimeOffset StartedAt,
      long AccountId,
      string AccountName);
  ```

- [ ] **Step 7: `IExecutionList.cs` — đổi `TryEnterAsync` + XML doc**

  ```csharp
  namespace RouterBalancing.Core.Engine;

  /// <summary>Track request đang phục vụ + enforce <c>Provider.MaxConcurrent</c> mỗi tài khoản (spec §2.1, D-B1).</summary>
  public interface IExecutionList
  {
      /// <summary>Bắn sau mỗi Exit thành công — dispatcher dùng làm wake signal để thử request bị park.</summary>
      event Action? Exited;

      /// <summary>
      /// Hỏi xem còn slot cho ≥1 TK enabled của provider — KHÔNG mutate (selector dùng khi chọn).
      /// Provider không tồn tại hoặc mọi TK enabled đầy → <see langword="false"/>;
      /// 0 TK enabled → <see langword="true"/> (TryEnter trả sentinel → forward 503, không park — V1).
      /// </summary>
      Task<bool> CanEnterAsync(long providerId, CancellationToken ct);

      /// <summary>
      /// Reserve 1 slot cho TK ít in-flight nhất nếu còn chỗ (query <c>MaxConcurrent</c> mới nhất từ DB).
      /// Trả Id TK đã chọn; <see langword="null"/> = hết slot hoặc provider không tồn tại (park — D-B4/D-B7);
      /// trả <c>0</c> = sentinel khi provider không có TK enabled nào (entry vẫn tạo để forward 503 — V1).
      /// </summary>
      Task<long?> TryEnterAsync(long providerId, string requestId, string providerName, string modelId,
          RequestPriority priority, DateTimeOffset enqueuedAt, CancellationToken ct);

      /// <summary>Trả slot + gỡ entry + bắn <see cref="Exited"/>. Idempotent: id không có thì không bắn event.</summary>
      void Exit(string requestId);

      /// <summary>Id có đang phục vụ không (cancel endpoint phân biệt 409 vs 404).</summary>
      bool Contains(string requestId);

      /// <summary>Số request in-flight của provider.</summary>
      int GetInFlight(long providerId);

      /// <summary>Snapshot toàn bộ entry đang phục vụ — data source cho <c>GET /v1/requests</c>.</summary>
      IReadOnlyList<ExecutionEntry> Snapshot();
  }
  ```

- [ ] **Step 8: `ExecutionList.cs` — capacity per-account + chọn TK**

  ```csharp
  using Microsoft.EntityFrameworkCore;
  using RouterBalancing.Core.Storage;

  namespace RouterBalancing.Core.Engine;

  /// <summary>
  /// Dictionary id → entry dưới 1 lock (check-then-add atomic với Exit) —
  /// local app, contention thấp nên lock đơn giản hơn ConcurrentDictionary + Interlocked.
  /// </summary>
  public sealed class ExecutionList(IDbContextFactory<RouterBalancingDbContext> db) : IExecutionList
  {
      private readonly object _lock = new();
      private readonly Dictionary<string, ExecutionEntry> _entries = new();

      /// <inheritdoc/>
      public event Action? Exited;

      /// <inheritdoc/>
      public async Task<bool> CanEnterAsync(long providerId, CancellationToken ct)
      {
          var capacity = await LoadCapacityAsync(providerId, ct);
          if (capacity is null)
              return false; // provider không tồn tại — không bao giờ enter (D-B7)
          if (capacity.Value.Accounts.Count == 0)
          {
              // Sentinel (V1): 0 TK enabled → TryEnter vẫn tạo entry AccountId=0 để forward 503;
              // park ở đây sẽ treo vĩnh viễn vì không có wake signal nào khi user bật lại TK
              return true;
          }
          lock (_lock)
              return capacity.Value.Accounts.Any(a =>
                  HasCapacity(capacity.Value.Max, providerId, a.Id));
      }

      /// <inheritdoc/>
      public async Task<long?> TryEnterAsync(long providerId, string requestId, string providerName,
          string modelId, RequestPriority priority, DateTimeOffset enqueuedAt, CancellationToken ct)
      {
          // Query tại mỗi lần Enter — chỉnh MaxConcurrent trong UI có hiệu lực ngay (spec §2.1)
          var capacity = await LoadCapacityAsync(providerId, ct);
          if (capacity is null)
              return null; // provider không tồn tại — không serve (D-B7)

          lock (_lock)
          {
              AccountSlot chosen;
              if (capacity.Value.Accounts.Count == 0)
              {
                  // Sentinel (V1) — xem chú thích trong CanEnterAsync
                  chosen = new AccountSlot(0, string.Empty, 0);
              }
              else
              {
                  // Chọn TK least-in-flight; tie-break Priority tăng dần → Id tăng dần (D-B4.3)
                  var candidates = capacity.Value.Accounts
                      .Where(a => HasCapacity(capacity.Value.Max, providerId, a.Id))
                      .ToList();
                  if (candidates.Count == 0)
                      return null; // mọi TK enabled đầy → park, chờ Exited (D-B4.4)
                  chosen = candidates
                      .OrderBy(a => CountInFlight(providerId, a.Id))
                      .ThenBy(a => a.Priority)
                      .ThenBy(a => a.Id)
                      .First();
              }

              _entries[requestId] = new ExecutionEntry(
                  requestId, providerId, providerName, modelId, priority, enqueuedAt,
                  DateTimeOffset.UtcNow, chosen.Id, chosen.Name);
              return chosen.Id;
          }
      }

      /// <inheritdoc/>
      public void Exit(string requestId)
      {
          lock (_lock)
          {
              if (!_entries.Remove(requestId))
                  return;
          }
          Exited?.Invoke();
      }

      /// <inheritdoc/>
      public bool Contains(string requestId)
      {
          lock (_lock)
              return _entries.ContainsKey(requestId);
      }

      /// <inheritdoc/>
      public int GetInFlight(long providerId)
      {
          lock (_lock)
              return CountInFlight(providerId);
      }

      /// <inheritdoc/>
      public IReadOnlyList<ExecutionEntry> Snapshot()
      {
          lock (_lock)
              return _entries.Values.ToList();
      }

      private int CountInFlight(long providerId) =>
          _entries.Values.Count(e => e.ProviderId == providerId);

      private int CountInFlight(long providerId, long accountId) =>
          _entries.Values.Count(e => e.ProviderId == providerId && e.AccountId == accountId);

      /// <summary>Max ≤ 0 = không giới hạn (D-B1) — validator UI chặn 0..64 nên âm không tới được từ UI.</summary>
      private bool HasCapacity(int max, long providerId, long accountId) =>
          max <= 0 || CountInFlight(providerId, accountId) < max;

      /// <summary>
      /// Query MaxConcurrent + TK enabled (Id, Name, Priority) mới nhất từ DB;
      /// provider không tồn tại → null (D-B7 — không dùng FirstOrDefault = 0 vì 0 giờ là "không giới hạn").
      /// </summary>
      private async Task<(int Max, List<AccountSlot> Accounts)?> LoadCapacityAsync(
          long providerId, CancellationToken ct)
      {
          using var context = await db.CreateDbContextAsync(ct);
          var provider = await context.Providers.AsNoTracking()
              .Where(p => p.Id == providerId)
              .Select(p => new
              {
                  p.MaxConcurrent,
                  Accounts = p.Accounts
                      .Where(a => a.Enabled)
                      .Select(a => new { a.Id, a.Name, a.Priority })
                      .ToList(),
              })
              .FirstOrDefaultAsync(ct);
          return provider is null
              ? null
              : (provider.MaxConcurrent, provider.Accounts
                  .Select(a => new AccountSlot(a.Id, a.Name, a.Priority)).ToList());
      }

      /// <summary>TK enabled trong capacity query — Id/Name cho entry, Priority cho tie-break (D-B4).</summary>
      private sealed record AccountSlot(long Id, string Name, int Priority);
  }
  ```

- [ ] **Step 9: `DispatcherLoop.cs` —3 điểm sửa**

  (a) `TryDispatchOnceAsync` (dòng 128-140) — thay khối TryEnter/Take/serve:

  ```csharp
          var accountId = await executions.TryEnterAsync(candidate.Provider.Id, request.Id,
              candidate.Provider.Name, candidate.Model.ModelId, request.Priority,
              request.EnqueuedAt, ct);
          if (accountId is null)
              return false; // capacity vừa hết (mọi TK đầy) — park, Exited sẽ đánh thức

          if (!queue.Take(request.Id, out var taken))
          {
              // Take fail = item vừa bị huỷ/abort giữa TryEnter và Take — trả slot ngay (spec §3.3)
              executions.Exit(request.Id);
              return true;
          }

          _ = ServeAsync(taken, candidate, remaining, success.Mode, accountId.Value);
          return true;
  ```

  (b) `ServeAsync` — thêm param + truyền vào forward (dòng 149-150, 158-159):

  ```csharp
      private async Task ServeAsync(ProxyRequest request, ModelCandidate candidate,
          IReadOnlyList<ModelCandidate> remaining, ComboMode mode, long accountId)
      {
  ```

  ```csharp
                  outcome = await handler.ForwardAsync(request.Context, candidate.Provider,
                      candidate.Model, request.Body, accountId, request.Context.RequestAborted);
  ```

  (c) Retry-walk (dòng 202-244) — thay khối `nextCandidate`:

  ```csharp
                  ModelCandidate? nextCandidate;
                  long? nextAccountId = null;
                  try
                  {
                      nextCandidate = await selector.TrySelectAsync(
                          new SelectionSuccess(next, mode), request.Context.RequestAborted);
                      nextAccountId = nextCandidate is null
                          ? null
                          : await executions.TryEnterAsync(nextCandidate.Provider.Id, request.Id,
                              nextCandidate.Provider.Name, nextCandidate.Model.ModelId,
                              request.Priority, request.EnqueuedAt, request.Context.RequestAborted);
                      if (nextCandidate is not null && nextAccountId is null)
                          nextCandidate = null; // capacity corner — park lại, TK đã trả qua Exit
                  }
  ```

  (các `catch` giữ nguyên; cuối vòng lặp, trước `continue`):

  ```csharp
                  candidate = nextCandidate;
                  remaining = next;
                  accountId = nextAccountId!.Value; // nextCandidate != null ⇒ đã enter thành công (TK mới mỗi vòng — D-B6)
                  continue;
  ```

- [ ] **Step 10: `ChatCompletionsHandler.cs` — param `accountId` + bỏ resolve**

  XML doc — thêm sau dòng 82 (`<param name="body">`):

  ```csharp
      /// <param name="accountId">TK đã chọn lúc TryEnter — dùng đúng id này, không resolve lại (D-B6).</param>
  ```

  Signature (dòng 84-85) + dòng 87:

  ```csharp
      public async Task<DispatchOutcome> ForwardAsync(HttpContext ctx, Provider provider, Model model,
          byte[] body, long accountId, CancellationToken ct)
      {
          // Đúng TK đã chọn bởi TryEnter — resolve "first enabled" tại đây sẽ lệch đếm (D-B6);
          // id không khớp (TK bị xóa giữa chừng) → 503 cùng contract với nhánh không có key
          var account = provider.Accounts.FirstOrDefault(a => a.Id == accountId);
  ```

  (phần còn lại của hàm **giữ nguyên** — nhánh `account is null` vẫn 503 với message `No enabled API key for provider '{name}'`.)

  Xóa `using RouterBalancing.Core.Providers;` (dòng 7) — verify bằng `rg "ProviderKeyResolver|ProviderRequestFactory" src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` → 0 kết quả trước khi xóa (chỉ `ProviderKeyResolver` dùng namespace này).

- [ ] **Step 11: `ProxyApp.cs` — snapshot serving thêm `account` (V6 / D-B3)**

  Trong object `serving` (sau dòng 265 `provider = (string?)e.ProviderName,`):

  ```csharp
                  account = string.IsNullOrEmpty(e.AccountName) ? null : e.AccountName,
  ```

- [ ] **Step 12: Verify**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ExecutionListTests|FullyQualifiedName~ModelSelectorTests|FullyQualifiedName~ChatCompletionsHandlerTests|FullyQualifiedName~DispatcherLoopTests"   # GREEN
  dotnet test "router balancing test/router balancing test.csproj"   # 556/556 (flake policy nếu retry)
  ```

  Nếu `ProxyRetryIntegrationTests`/`ProxyControlApiTests` vỡ → đọc lại khả năng seed không có account enabled; KHÔNG accept flake.

### Commit

`feat: enforce max concurrency per provider account`

---

## Task 5: Validator 0..64 cho `Provider.MaxConcurrent` (D-B1, V7)

### Files

- `src/RouterBalancing.Core/Providers/ProviderValidator.cs` — rule `< 0 or > 64`.
- `router-balancing/Components/Pages/Providers.razor` — input `min="0"`.
- `src/RouterBalancing.Core/Localization/Translations.cs` — sửa 2 key × 2 dict.
- `router balancing test/Providers/ProviderValidatorTests.cs` — sửa 1 + thêm 1.

### Interfaces

- **Consumes:** `ProviderDraft.MaxConcurrent`.
- **Produces:** contract 0..64 (0 hợp lệ = unlimited).

### Steps

- [ ] **Step 1: RED — sửa/Thêm test (dòng 46-54)**

  ```csharp
  [Fact]
  public void Validate_WhenMaxConcurrentOutOfRange_ReturnsMaxConcurrentError()
  {
      var low = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = -1 });
      var high = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = 65 });

      Assert.Equal("providers.error.maxConcurrent", low[nameof(ProviderDraft.MaxConcurrent)]);
      Assert.Equal("providers.error.maxConcurrent", high[nameof(ProviderDraft.MaxConcurrent)]);
  }

  [Fact]
  public void Validate_WhenMaxConcurrentZero_ReturnsNoError()
  {
      // 0 = không giới hạn đồng thời (D-B1) — hợp lệ
      var errors = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = 0 });

      Assert.False(errors.ContainsKey(nameof(ProviderDraft.MaxConcurrent)));
  }
  ```

  Expected: `Validate_WhenMaxConcurrentZero_ReturnsNoError` RED (validator cũ chặn `< 1`).

- [ ] **Step 2: Sửa validator (ProviderValidator.cs:33)**

  ```csharp
          if (draft.MaxConcurrent is < 0 or > 64)
  ```

  (comment không cần thêm — code tự diễn tả; XML doc hàm giữ nguyên.)

- [ ] **Step 3: Input Providers.razor (dòng 346)** — `min="1" max="64"` →

  ```razor
              <input type="number" min="0" max="64"
  ```

- [ ] **Step 4: i18n sửa text × 2 dict**

  English:

  ```csharp
          ["providers.field.maxConcurrent"] = "Max concurrent per account (0 = unlimited)",
          ["providers.error.maxConcurrent"] = "Max concurrent per account must be between 0 and 64.",
  ```

  Vietnamese:

  ```csharp
          ["providers.field.maxConcurrent"] = "Đồng thời tối đa mỗi tài khoản (0 = không giới hạn)",
          ["providers.error.maxConcurrent"] = "Đồng thời tối đa mỗi tài khoản phải từ 0 đến 64.",
  ```

- [ ] **Step 5: Verify (gate 1 + 2 + 3)**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderValidatorTests"   # GREEN (+1)
  dotnet test "router balancing test/router balancing test.csproj"   # 557/557
  dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
  ```

### Commit

`feat: allow zero max concurrent per account`

---

## Task 6: Xóa setting `defaultMaxConcurrent` (D-B2)

### Files

- `src/RouterBalancing.Core/Settings/SettingsKeys.cs` — xóa dòng 23 (+ blank).
- `src/RouterBalancing.Core/Settings/SettingsDraft.cs` — xóa dòng 24 (+ blank).
- `src/RouterBalancing.Core/Settings/IAppSettingsService.cs` — xóa dòng 24 (+ blank).
- `src/RouterBalancing.Core/Settings/AppSettingsService.cs` — xóa dòng 49 (+ blank).
- `src/RouterBalancing.Core/Settings/SettingsValidator.cs` — xóa dòng 30-31.
- `router-balancing/Components/Pages/SettingsPanel.razor` — field, error, `EngineFields`, `LoadDraft`, `SaveEngine`, grid cols.
- `router-balancing/Components/Pages/Providers.razor` — `OpenAdd` (dòng 1139), xóa `@using RouterBalancing.Core.Settings` (dòng 5) + `@inject IAppSettingsService Settings` (dòng 15).
- `src/RouterBalancing.Core/Localization/Translations.cs` — xóa 2 key × 2 dict.
- `router balancing test/Settings/SettingsValidatorTests.cs` — sửa.

### Interfaces

- **Consumes:** —
- **Produces:** setting `defaultMaxConcurrent` biến mất hoàn toàn; seed form thêm provider = default của `ProviderDraft` (= 4).

### Steps

- [ ] **Step 1: Sửa test (SettingsValidatorTests)**

  - Dòng 14: xóa `DefaultMaxConcurrent = 4,`
  - `Validate_WhenEngineValuesOutOfRange_ReturnsEachFieldError`: xóa `DefaultMaxConcurrent = 65,` (dòng 60); `Assert.Equal(5, errors.Count)` → `Assert.Equal(4, errors.Count)`; xóa assert dòng 68.

- [ ] **Step 2: Xóa toàn bộ code C#**

  Xóa chính xác các dòng đã liệt kê trong Files (mỗi dòng kể cả dòng trắng lân cận để file không sót double-blank). SettingsValidator rule:

  ```csharp
          if (draft.DefaultMaxConcurrent is < 1 or > 64)
              errors[nameof(SettingsDraft.DefaultMaxConcurrent)] = "settings.error.maxConcurrent";
  ```

- [ ] **Step 3: `SettingsPanel.razor`**

  - Xóa field (dòng 171-175) và đổi grid `sm:grid-cols-3` (dòng 160) → `sm:grid-cols-2` (2 field còn lại: MaxRetry + Watchdog).
  - Xóa error block (dòng 186-189).
  - `EngineFields` (dòng 281-286): xóa entry `nameof(SettingsDraft.DefaultMaxConcurrent),`.
  - `LoadDraft` (dòng 323): xóa `DefaultMaxConcurrent = Settings.DefaultMaxConcurrent,`.
  - `SaveEngine` (dòng 417): xóa `Settings.Set(SettingsKeys.DefaultMaxConcurrent, _draft.DefaultMaxConcurrent);`.

- [ ] **Step 4: `Providers.razor`**

  - `OpenAdd` (dòng 1139): `_draft = new ProviderDraft { MaxConcurrent = Settings.DefaultMaxConcurrent };` →

    ```csharp
            _draft = new ProviderDraft(); // MaxConcurrent = 4 là default của draft — không còn setting seed (D-B2)
    ```

  - Xóa `@using RouterBalancing.Core.Settings` (dòng 5) + `@inject IAppSettingsService Settings` (dòng 15).
  - **Verify trước khi xóa:** `rg "Settings\.|SettingsDraft|SettingsKeys|IAppSettingsService" router-balancing/Components/Pages/Providers.razor` → chỉ còn dòng 1139 (sau khi sửa → 0) → xóa dùng/inkject. Nếu có chỗ khác → GIỮ using/inject và báo lại (đừng xóa mù).

- [ ] **Step 5: i18n xóa 2 key × 2 dict**

  - EN dòng 50: `["settings.field.maxConcurrent"] = "Default max concurrent",`
  - EN dòng 63: `["settings.error.maxConcurrent"] = "Max concurrent must be between 1 and 64.",`
  - VI dòng 364: `["settings.field.maxConcurrent"] = "Số request đồng thời tối đa",`
  - VI dòng 377: `["settings.error.maxConcurrent"] = "Số request đồng thời phải từ 1–64.",`

  > `providers.field.maxConcurrent`/`providers.error.maxConcurrent` GIỮ NGUYÊN (đã sửa text ở Task 5).

- [ ] **Step 6: Verify (gate 1 + 2 + 3)**

  ```bash
  rg -n "DefaultMaxConcurrent|defaultMaxConcurrent" --glob '!docs/**' --glob '!*.md'   # 0 kết quả
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj"   # 557/557 (parity 2 dict)
  dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
  ```

  > Không migration: row `defaultMaxConcurrent` (nếu user từng lưu) nằm thừa trong DB vô hại, không đọc lại.

### Commit

`refactor: remove unused default max concurrent setting`

---

## Task 7: LAN access toggle (D-C1…D-C4)

### Files

- `src/RouterBalancing.Core/Settings/SettingsKeys.cs` + `SettingsDraft.cs` + `IAppSettingsService.cs` + `AppSettingsService.cs` — key/property `LanAccess`.
- `src/RouterBalancing.Core/Server/ProxyHost.cs` — `ResolveBindAddress` + StartAsync + log.
- **Mới:** `src/RouterBalancing.Core/Server/LanUrlProvider.cs`.
- `router-balancing/Components/Pages/SettingsPanel.razor` — checkbox + hint + `SaveServerAsync`.
- `router-balancing/Components/Pages/Dashboard.razor` — URL LAN.
- `src/RouterBalancing.Core/Localization/Translations.cs` — 2 key × 2 dict.
- **Mới:** `router balancing test/Server/BindAddressTests.cs`.
- `router balancing test/Server/ProxyHostTests.cs` — 1 test integration.

### Interfaces

- **Consumes:** `IAppSettingsService.Get/Set`, `IProxyHost.RestartAsync`, `NetworkInterface`.
- **Produces:**
  - `SettingsKeys.LanAccess = "lanAccess"` (bool, default `false`), `SettingsDraft.LanAccess`, `IAppSettingsService.LanAccess`.
  - `ProxyHost.ResolveBindAddress(bool lanAccess) → IPAddress` (**public static** — unit test không cần socket).
  - `LanUrlProvider.GetUrls(int port) → IReadOnlyList<string>` (rỗng nếu port ≤ 0 hoặc không có NIC non-loopback).

### Steps

- [ ] **Step 1: Settings —4 file Core**

  `SettingsKeys.cs` (sau `Port` dòng 12):

  ```csharp
      /// <summary>Bật bind Kestrel mọi interface (LAN) — mặc định false, chỉ loopback (D-C1).</summary>
      public const string LanAccess = "lanAccess";
  ```

  `SettingsDraft.cs` (sau `Port` dòng 14):

  ```csharp
      public bool LanAccess { get; set; }
  ```

  `IAppSettingsService.cs` (sau `Port` dòng 14):

  ```csharp
      /// <summary>Bật endpoint truy cập từ LAN — ProxyHost đọc mỗi lần Start (D-C1).</summary>
      bool LanAccess { get; }
  ```

  `AppSettingsService.cs` (sau `Port` dòng 39):

  ```csharp
      public bool LanAccess => Get(SettingsKeys.LanAccess, false);
  ```

- [ ] **Step 2: `ProxyHost.cs` — bind address + log**

  Sau ctor, thêm public static (dòng ~46):

  ```csharp
      /// <summary>
      /// Bind Kestrel: LAN tắt → loopback (chỉ máy này); LAN bật → mọi interface (D-C2).
      /// Tách static để unit test 2 nhánh không cần mở socket.
      /// </summary>
      public static IPAddress ResolveBindAddress(bool lanAccess) =>
          lanAccess ? IPAddress.Any : IPAddress.Loopback;
  ```

  Dòng 67 — thay:

  ```csharp
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(ResolveBindAddress(_settings.LanAccess), port));
  ```

  Dòng 88 — thay (log liệt kê URL LAN khi bật — D-C2):

  ```csharp
            var lanUrls = _settings.LanAccess ? LanUrlProvider.GetUrls(port) : [];
            _log.Info(lanUrls.Count == 0
                ? $"Proxy server đang chạy tại http://127.0.0.1:{port}/"
                : $"Proxy server đang chạy tại http://127.0.0.1:{port}/ — LAN: {string.Join(", ", lanUrls)}");
  ```

  > `PortSelector` giữ nguyên probe loopback (bind Any vẫn bắt được — D-C2).

- [ ] **Step 3: File mới `LanUrlProvider.cs`**

  ```csharp
  using System.Net;
  using System.Net.NetworkInformation;
  using System.Net.Sockets;

  namespace RouterBalancing.Core.Server;

  /// <summary>Danh sách URL IPv4 non-loopback của máy — hiển thị khi bật LAN (D-C3).</summary>
  public static class LanUrlProvider
  {
      /// <summary>
      /// Mỗi NIC đang Up (không loopback) một URL <c>http://ip:port</c>; port chưa xác định
      /// (≤ 0) hoặc không có NIC phù hợp → danh sách rỗng — caller tự render/tự bỏ qua.
      /// </summary>
      public static IReadOnlyList<string> GetUrls(int port)
      {
          if (port <= 0)
              return [];
          return NetworkInterface.GetAllNetworkInterfaces()
              .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                  && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
              .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
              .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                  && !IPAddress.IsLoopback(a.Address))
              .Select(a => $"http://{a.Address}:{port}")
              .Distinct()
              .ToList();
      }
  }
  ```

- [ ] **Step 4: i18n — 2 key × 2 dict**

  EN (chèn sau `["settings.field.port"] = "Port",`):

  ```csharp
          ["settings.field.lanAccess"] = "Allow access from LAN",
          ["settings.hint.lanAccess"] = "LAN endpoint is reachable from other devices on your network — consider enabling a client key.",
  ```

  VI (chèn sau `["settings.field.port"] = "Cổng",`):

  ```csharp
          ["settings.field.lanAccess"] = "Cho phép truy cập từ LAN",
          ["settings.hint.lanAccess"] = "Endpoint LAN truy cập được từ thiết bị khác trong mạng — nên bật client key.",
  ```

- [ ] **Step 5: `SettingsPanel.razor` — UI + save**

  UI (chèn sau error block của Port, trước `<button ... SaveServerAsync>`):

  ```razor
      <label class="mt-3 flex items-center gap-2 text-sm">
          <input type="checkbox" @bind="_draft.LanAccess" />
          @L["settings.field.lanAccess"]
      </label>
      @* Hint LUÔN hiện (kể cả trước khi bật) — user tự đánh giá rủi ro trước khi mở endpoint (D-C4) *@
      <div class="mt-1 text-xs opacity-70">@L["settings.hint.lanAccess"]</div>
  ```

  `LoadDraft` (sau `Port = Settings.Port,` dòng 318):

  ```csharp
          LanAccess = Settings.LanAccess,
  ```

  `SaveServerAsync` (dòng 384-409) — thay toàn bộ:

  ```csharp
      private async Task SaveServerAsync()
      {
          var portChanged = _draft.Port != Settings.Port;
          var lanChanged = _draft.LanAccess != Settings.LanAccess;
          if (!ValidateFor(ServerFields)) return;

          Settings.Set(SettingsKeys.Port, _draft.Port);
          Settings.Set(SettingsKeys.LanAccess, _draft.LanAccess);

          if ((portChanged || lanChanged) && Proxy.IsRunning)
          {
              try
              {
                  // Port/LAN chỉ có hiệu lực khi Kestrel bind lại — restart để áp dụng ngay (D-C1)
                  await Proxy.RestartAsync();
                  Toast.Show(L[portChanged ? "settings.msg.portRestart" : "settings.msg.saved"],
                      ToastSeverity.Success);
              }
              catch (Exception ex)
              {
                  Log.Error("Khởi động lại proxy sau khi đổi cài đặt máy chủ thất bại.", ex);
                  Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
              }
          }
          else
          {
              Toast.Show(L["settings.msg.saved"], ToastSeverity.Success);
          }
      }
  ```

  > `ServerFields` giữ nguyên `[nameof(SettingsDraft.Port)]` — `LanAccess` không có rule validate số nên không cần liệt kê trong `ValidateFor`.

- [ ] **Step 6: `Dashboard.razor` — URL LAN (D-C3)**

  Header:

  ```razor
  @page "/"
  @using RouterBalancing.Core.Settings
  @implements IDisposable
  @inject IProxyHost Proxy
  @inject ILogService Log
  @inject ToastService Toast
  @inject LocalizationService L
  @inject IAppSettingsService Settings
  ```

  Markup (thay block `@if (Proxy.IsRunning)` dòng 17-20):

  ```razor
              @if (Proxy.IsRunning)
              {
                  <div class="mt-1 font-mono text-sm">http://127.0.0.1:@Proxy.Port</div>
                  @if (Settings.LanAccess)
                  {
                      @* Render lúc truy vấn (không cache) — phản ánh ngay sau restart; loopback đã có dòng trên (D-C3) *@
                      @foreach (var url in LanUrls)
                      {
                          <div class="mt-1 font-mono text-sm">@url</div>
                      }
                  }
              }
  ```

  Code-behind (thêm property):

  ```csharp
      /// <summary>URL LAN hiện tại — tính lúc render để không cần cache/refresh tay.</summary>
      private IReadOnlyList<string> LanUrls =>
          Proxy.Port is { } port ? LanUrlProvider.GetUrls(port) : [];
  ```

  > Khi bật/tắt LAN while proxy chạy → `SaveServerAsync` đã restart → `Proxy.StateChanged` → Dashboard re-render; khi proxy stopped thì block LAN ẩn → không cần subscribe `SettingsChanged`.

- [ ] **Step 7: RED — 2 test unit (`router balancing test/Server/BindAddressTests.cs`, file mới)**

  ```csharp
  using System.Net;
  using RouterBalancing.Core.Server;

  namespace router_balancing_test.Server;

  public class BindAddressTests
  {
      [Fact]
      public void ResolveBindAddress_WhenLanAccessDisabled_ReturnsLoopback() =>
          Assert.Equal(IPAddress.Loopback, ProxyHost.ResolveBindAddress(lanAccess: false));

      [Fact]
      public void ResolveBindAddress_WhenLanAccessEnabled_ReturnsAny() =>
          Assert.Equal(IPAddress.Any, ProxyHost.ResolveBindAddress(lanAccess: true));
  }
  ```

- [ ] **Step 8: Integration test (thêm vào `ProxyHostTests.cs`, sau test `StartAsync_WhenPreferredPortBusy...`)**

  ```csharp
  [Fact]
  public async Task StartAsync_WhenLanAccessEnabled_RespondsOnNonLoopbackAddress()
  {
      _settings.Set(SettingsKeys.LanAccess, true);

      await using var host = await StartHostAsync();

      var lanUrls = LanUrlProvider.GetUrls(host.Port!.Value);
      Assert.NotEmpty(lanUrls); // máy dev luôn có ≥1 NIC non-loopback (vEthernet/ Wi-Fi/ LAN)
      using var lanClient = new HttpClient { BaseAddress = new Uri(lanUrls[0]) };
      var response = await lanClient.GetAsync("/health");
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }
  ```

  > Test instance mới mỗi test → `LanAccess` mặc định false ở các test khác; `StartAsync` đọc `_settings` lúc start.

- [ ] **Step 9: Verify (gate 1 + 2 + 3)**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~BindAddressTests|FullyQualifiedName~ProxyHostTests"   # GREEN (+3)
  dotnet test "router balancing test/router balancing test.csproj"   # 560/560
  dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
  ```

  > Windows Firewall có thể hiện prompt khi Kestrel lần đầu bind non-loopback — đây là hành vi expected, ghi vào checklist tay (Task 10), KHÔNG commit code dọn prompt.

### Commit

`feat: add LAN access toggle for proxy endpoint`

---

## Task 8: `IClipboardService` dùng chung (D-D2)

### Files

- **Mới:** `router-balancing/Services/IClipboardService.cs`.
- **Mới:** `router-balancing/Services/ClipboardService.cs`.
- `router-balancing/MauiProgram.cs` — đăng ký DI.
- `router-balancing/Components/Pages/SettingsPanel.razor` — `CopyNewKeyAsync` dùng service, bỏ `@inject IJSRuntime JS`.
- `src/RouterBalancing.Core/Localization/Translations.cs` — key `common.copied` × 2 dict.

### Interfaces

- **Consumes:** `IJSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text)`.
- **Produces:** `IClipboardService.CopyAsync(string) → Task<bool>` (false khi JSException/NotAllowedError); key i18n `common.copied`.

### Steps

- [ ] **Step 1: File mới `router-balancing/Services/IClipboardService.cs`**

  ```csharp
  namespace router_balancing.Services;

  /// <summary>Sao chép text vào clipboard — caller tự toast theo kết quả (D-D2).</summary>
  public interface IClipboardService
  {
      /// <summary>Trả <see langword="false"/> khi trình duyệt từ chối (document không focus, thiếu quyền).</summary>
      Task<bool> CopyAsync(string text);
  }
  ```

- [ ] **Step 2: File mới `router-balancing/Services/ClipboardService.cs`**

  ```csharp
  using Microsoft.JSInterop;

  namespace router_balancing.Services;

  /// <inheritdoc cref="IClipboardService"/>
  public sealed class ClipboardService(IJSRuntime js) : IClipboardService
  {
      public async Task<bool> CopyAsync(string text)
      {
          try
          {
              await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
              return true;
          }
          catch (JSException)
          {
              // NotAllowedError khi document không focus — trả false thay vì văng exception (D-D2)
              return false;
          }
      }
  }
  ```

- [ ] **Step 3: DI — `MauiProgram.cs` (sau dòng 64 `AddScoped<ToastService>();`)**

  ```csharp
            // Scoped như ThemeService: ClipboardService phụ thuộc IJSRuntime scoped
            builder.Services.AddScoped<IClipboardService, ClipboardService>();
  ```

- [ ] **Step 4: i18n — `common.copied` × 2 dict**

  EN (chèn **sau** `["dashboard.msg.failed"] = ...` dòng 30):

  ```csharp
          ["common.copied"] = "Copied",
  ```

  VI (chèn **sau** `["dashboard.msg.failed"] = ...` dòng 344):

  ```csharp
          ["common.copied"] = "Đã sao chép",
  ```

- [ ] **Step 5: `SettingsPanel.razor` — dùng service**

  - Thay dòng 17 `@inject IJSRuntime JS` →

    ```razor
    @inject IClipboardService Clipboard
    ```

    > **Verify trước:** `rg "\bJS\." router-balancing/Components/Pages/SettingsPanel.razor` → chỉ dòng 537 → an toàn bỏ. Nếu còn chỗ khác → GIỮ `IJSRuntime` và chỉ thêm `IClipboardService`.

  - Thay `CopyNewKeyAsync` (dòng 531-546):

    ```csharp
        private async Task CopyNewKeyAsync()
        {
            if (_newKeyPlaintext is null) return;
            if (await Clipboard.CopyAsync(_newKeyPlaintext))
            {
                Toast.Show(L["common.copied"], ToastSeverity.Success);
            }
            else
            {
                // Clipboard reject khi document không focus (NotAllowedError) — hiện toast thay vì văng (D-D2)
                Log.Error("Sao chép plaintext client key thất bại.", null);
                Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
            }
        }
    ```

  > Key `clientKeys.msg.copied` giữ nguyên trong dict (không dùng ở UI nữa) — không test nào phụ thuộc việc xóa.

- [ ] **Step 6: Verify (gate 1 + 2 + 3)**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj"   # 560/560 (parity)
  dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
  ```

### Commit

`refactor: centralize clipboard copy into service`

---

## Task 9: Model pin `{Identifier}/{ModelId}` + copy (D-D1)

### Files

- `router-balancing/Components/Pages/Providers.razor` — ô model cell, inject `IClipboardService`, handler `CopyModelPinAsync`.

### Interfaces

- **Consumes:** `IClipboardService` (Task 8), key i18n `common.copied` + `clientKeys.action.copy` (title nút — reuse, không key mới), `p.Identifier`, `m.ModelId`.
- **Produces:** UI-only — **không unit test** (spec: clipboard = checklist tay); gate 3.

### Steps

- [ ] **Step 1: Inject (sau `@inject IAppSettingsService Settings` dòng 15 — hoặc sau khi Task 6 đã xóa dòng đó, thêm cuối block inject)**

  ```razor
  @inject IClipboardService Clipboard
  ```

- [ ] **Step 2: Model cell (thay td dòng 221-223)**

  ```razor
                                                          <td class="px-2 py-1.5 font-mono">
                                                              <div>@(m.DisplayName is null ? m.ModelId : $"{m.DisplayName} ({m.ModelId})")</div>
                                                              @* Pin {Identifier}/{ModelId} + copy — gửi request nhắm đúng provider (D-D1) *@
                                                              @if (!string.IsNullOrEmpty(p.Identifier))
                                                              {
                                                                  <div class="flex items-center gap-1 text-[11px] opacity-70">
                                                                      <span>@p.Identifier/@m.ModelId</span>
                                                                      <button type="button" class="btn btn-outline-secondary px-1"
                                                                              disabled="@_busy" title="@L["clientKeys.action.copy"]"
                                                                              @onclick="() => CopyModelPinAsync(p, m)">
                                                                          &#128203;
                                                                      </button>
                                                                  </div>
                                                              }
                                                          </td>
  ```

  > Ngắt dòng đúng ngữ cảnh indentation thật (ô cell trong bảng model lồng trong provider row `p`). Preset không có Identifier → không render dòng pin (guard), không nút copy rỗng.

- [ ] **Step 3: Handler (đặt sau `ToggleModelAsync` hoặc nhóm handler copy gần nhất)**

  ```csharp
      /// <summary>Copy {Identifier}/{ModelId} — pin provider chính xác trong trường model (D-D1).</summary>
      private async Task CopyModelPinAsync(Provider provider, Model model)
      {
          if (string.IsNullOrEmpty(provider.Identifier))
              return; // nút không render khi thiếu Identifier — guard dư phòng
          if (await Clipboard.CopyAsync($"{provider.Identifier}/{model.ModelId}"))
          {
              Toast.Show(L["common.copied"], ToastSeverity.Success);
          }
          else
          {
              Log.Error("Sao chép model pin thất bại.", null);
              Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
          }
      }
  ```

- [ ] **Step 4: Verify (gate 1 + 2 + 3)**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
  dotnet test "router balancing test/router balancing test.csproj"   # 560/560
  dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
  ```

### Commit

`feat: add model pin copy on providers page`

---

## Task 10: Gates toàn cục + checklist tay + ledger

### Files

- Không file code. Ledger: `.superpowers/sdd/progress.md` (gitignored — không commit).

### Steps

- [ ] **Step 1: Đảm bảo app MAUI đóng** — `Get-Process router-balancing` rỗng (nếu đang chạy: kill, vì `SingleInstanceGuardTests` giữ mutex).

- [ ] **Step 2: Gates đầy đủ (repo root)**

  ```bash
  dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental    # 0 warning / 0 error
  dotnet test "router balancing test/router balancing test.csproj"                       # 560/560 (flake policy nếu retry)
  dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0 warning / 0 error
  ```

- [ ] **Step 3: Checklist tay** (user/controller chạy app — ghi kết quả vào ledger):

  1. **LAN:** Settings → bật "Cho phép truy cập từ LAN" → Kestrel restart (toast) → Dashboard hiện `http://<IP-LAN>:<port>` → `curl http://<IP-LAN>:<port>/health` từ chính máy (kỳ vọng 200; nếu có thêm thiết bị trong LAN thì test từ đó) → **Windows Firewall prompt là expected, không phải bug** → tắt toggle → restart → chỉ còn `127.0.0.1`, URL LAN biến mất.
  2. **Gán proxy:** gán P cho Provider A → chuyển nhanh sang B (checkbox phải đúng của B — race token) → gán P cho B (cùng proxy 2 provider — `SameProxySecondProvider`) → re-save cùng tập không lỗi (idempotent) → disable proxy → vẫn tick được + hiện "(tắt)" → expand nút `▸` dòng proxy hiện đúng danh sách "Provider … / Tài khoản …" → proxy chưa gán hiện "Chưa gán".
  3. **Pin copy:** Providers → mở row model → thấy `{Identifier}/{ModelId}` + nút 📋 → copy → paste đúng; toast "Đã sao chép"; Settings → copy client key plaintext → toast dùng chung `common.copied`.
  4. **Per-account:** provider 2 TK, N=1 → 2 request đồng thời phân bổ 2 TK (Dashboard `/v1/requests` → field `account` khác nhau) → request 3 park → đóng 1 request → wake; N=0 → không giới hạn (5 request cùng vào); 0 TK enabled → request trả 503 ngay (không treo).
  5. **Settings:** field "Số request đồng thời tối đa" biến mất khỏi nhóm Engine (còn 2 field, grid 2 cột) → form thêm provider mặc định 4 → nhập 0 hợp lệ, nhập -1/65 báo lỗi "0–64".

- [ ] **Step 4: Cập nhật ledger `.superpowers/sdd/progress.md`** — append section batch "4 mục" (LAN/gán proxy/pin/per-account) theo format batch trước: 10 task, commit hashes, gates `560/560`, checklist tay kết quả, các deviation V1–V7, ghi chú flake (nếu có). Roadmap: "5 việc" — #2 đã DONE (batch trước), batch này là work ngoài roadmap; bước tiếp theo **#1 Search**.

- [ ] **Step 5: Dừng — không push.** Báo cáo user: tổng kết + chờ review.

### Commit

Không commit (chỉ ledger gitignored; nếu user yêu cầu ghi chú docs thì commit riêng `docs:`).

---

## Self-review (trước khi dispatch)

- [ ] Mọi D-ID trong spec (D-A1…D-A6, D-B1…D-B8, D-C1…D-C4, D-D1, D-D2) đều có ít nhất 1 step tương ứng.
- [ ] Test list spec: 4 test Proxy A ✅, 2 reverse ✅, Engine (2TK/AllFull/ZeroMax/ProviderMissing/park×2/validator/settings) ✅, LAN (2 unit + 1 integration) ✅, "không unit test copy" → checklist ✅.
- [ ] i18n list spec: 9 mục — 5 key thêm (Task 3), 2 key LAN (Task 7), 1 `common.copied` (Task 8), 2 sửa (Task 5), 2 xóa (Task 6) ✅ — tất cả thao tác trên **2 dict**.
- [ ] Gates/flake policy lặp lại ở Global constraints + Task 10 ✅.
- [ ] Deviation V1–V7 đánh dấu trong code comment bằng tên deviation tương ứng ✅.
- [ ] Non-goals: không migration, không đổi queue priority/Take, không host UI qua LAN, không enforce ModelPatterns, không đổi flow gán proxy — không task nào đụng ✅.
