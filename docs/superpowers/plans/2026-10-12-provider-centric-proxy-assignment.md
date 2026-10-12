# Provider-Centric Proxy Assignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Làm lại section gán proxy trên trang Proxies theo hướng provider-centric: root cây = tất cả provider, mode rr/fb inline, thêm proxy bằng search, AC = checkbox per proxy với 3 trạng thái kế thừa/override/direct.

**Architecture:** Thêm cờ `ProxyOverrideEnabled` trên `ProviderAccounts` (migration + backfill semantics cũ), resolver đọc cờ để phân biệt kế thừa/override/direct; service thêm `SetAccountProxyOverrideAsync` + `GetAssignmentMatrixAsync`; tree builder mới `ProviderAssignmentTree`; Proxies.razor viết lại tree section, xóa form gán/panel ✎ cũ.

**Tech Stack:** .NET 10, EF Core 10 (SQLite, `Database.Migrate()`), xUnit, Blazor (MAUI Hybrid), Tailwind (`npm run build` trong `router-balancing/vite-project` nếu cần class mới).

**Spec:** `docs/superpowers/specs/2026-10-12-provider-centric-proxy-assignment-design.md`

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên (AGENTS.md).
- Build gate: `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0 --nologo -v q` (không build full slnx — lỗi NETSDK1082 iOS/MacCatalyst là môi trường, có sẵn).
- Test: `dotnet test "router balancing test/router balancing test.csproj"` (thư mục có space). `SingleInstanceGuardTests` (2) fail khi app đang chạy — môi trường; `ProxyQueueIntegrationTests.Cancel_QueuedRequest_...` flaky — chạy lại.
- Migration tool: `dotnet ef migrations add <Name> --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design`.
- Comment: chỉ "why", không "what"; XML doc cho public API; tên test mô tả hành vi (`X_WhenY_Z`).
- Giữ `GetAssignmentsAsync` + `ProxyAssignment` + `GetReverseAssignmentsAsync` — `RequestTrace.razor:909-910` đang dùng.
- Không commit trừ khi plan từng task nêu commit step (đã nêu — commit theo conventional commit, message tiếng Anh).

## Review Focus

1. **Migration backfill sai ngữ nghĩa** — junction cũ (khác rỗng) phải thành `ProxyOverrideEnabled=1`; AC không có junction = 0; AC override tham chiếu proxy ngoài tập provider phải được chèn thêm `ProviderProxies` (invariant ⊆). → Task 1 test `Up_BackfillsOldJunctions_InsertsOutOfSetProxies`.
2. **Resolver dirty data** — cờ `false` nhưng còn junction rows rác → vẫn kế thừa provider (cờ là nguồn sự thật). → Task 2 test `Resolve_AccountFlagOffWithStaleJunctions_InheritsProvider`.
3. **AC direct (override rỗng)** — cờ `true` + junction rỗng → `ProxySelection.Direct`, không rơi nhánh provider. → Task 2 test `Resolve_AccountOverrideEmptySet_ReturnsDirect`.
4. **Matrix thiếu junction** — provider không có proxy vẫn có row, `ProxyIds` rỗng (root hiển thị mọi provider). → Task 3 test `GetAssignmentMatrix_ProviderWithoutProxy_StillHasRow`.
5. **Toggle liên tiếp nhanh** — 2 tick dồn dập có thể reload đè state; UI phải có guard `_busy` (mọi handler) + handler await service → reload (spec C4). → Task 5 bước verify checklist thủ công (không bUnit).

---

### Task 1: Entity + Migration `AddAccountProxyOverrideFlag`

**Files:**
- Modify: `src/RouterBalancing.Core/Domain/Entities/ProviderAccount.cs` (thêm property sau `ProxyMode`)
- Create: `src/RouterBalancing.Core/Storage/Migrations/<timestamp>_AddAccountProxyOverrideFlag.cs` (+ Designer, snapshot — sinh bằng tool)
- Test: `router balancing test/Storage/AddAccountProxyOverrideMigrationTests.cs`

**Interfaces:**
- Produces: `ProviderAccount.ProxyOverrideEnabled` (bool, default `false`) — Task 2/3 đọc/ghi.

- [ ] **Step 1: Viết test migration (fail — chưa có cột)**

Pattern `AddStatsJournalColumnsMigrationTests` (`PreviousId = "20261010150737_AddStatsJournalColumns"`; `TargetId` lấy tên file migration sau khi sinh — tạm placeholder, sửa lại sau Step 3):

```
Up_AddsFlagColumn_BackfillsOldSemantics:
  Migrate(PreviousId)
  seed qua raw SQL: 2 provider (P1,P2), 3 account (A1@P1 có junction→proxyX, A2@P1 không junction, A3@P2 có junction→proxyY nhưng KHÔNG có hàng ProviderProxies(P2,proxyY))
  Migrate(TargetId)
  assert pragma_table_info: cột ProxyOverrideEnabled tồn tại, "notnull"=1
  assert A1=1, A2=0, A3=1  (Scalar qua SqliteConnection pattern)
  assert tồn tại ProviderProxies(P2,proxyY)  (invariant ⊆)

Down_DropsFlagColumn:
  Migrate(TargetId) → Down về PreviousId → cột biến mất
```

- [ ] **Step 2: Chạy test — fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~AddAccountProxyOverrideMigrationTests"`
Expected: FAIL (cột chưa tồn tại / migration chưa có).

- [ ] **Step 3: Thêm property + sinh migration + edit Up**

1. `ProviderAccount`: thêm sau `ProxyMode`:
```csharp
/// <summary>true = AC dùng tập proxy riêng của mình (rỗng = direct); false = kế thừa provider.</summary>
public bool ProxyOverrideEnabled { get; set; }
```
2. Sinh migration: `dotnet ef migrations add AddAccountProxyOverrideFlag --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design`
3. Sửa `Up()` — sau `AddColumn<bool>` (EF sinh `INTEGER NOT NULL DEFAULT 0`), thêm:
```csharp
migrationBuilder.Sql("""
    UPDATE "ProviderAccounts" SET "ProxyOverrideEnabled" = 1
    WHERE "Id" IN (SELECT DISTINCT "AccountId" FROM "ProviderAccountProxies");

    INSERT INTO "ProviderProxies" ("ProviderId", "ProxyId")
    SELECT DISTINCT pa."ProviderId", ap."ProxyId"
    FROM "ProviderAccountProxies" ap
    JOIN "ProviderAccounts" pa ON pa."Id" = ap."AccountId"
    WHERE NOT EXISTS (
        SELECT 1 FROM "ProviderProxies" pp
        WHERE pp."ProviderId" = pa."ProviderId" AND pp."ProxyId" = ap."ProxyId");
    """);
```
4. Sửa `TargetId` trong test cho khớp tên migration thật; `Down()` do EF sinh (DropColumn) — giữ.

- [ ] **Step 4: Chạy test — pass**

Run: cùng lệnh Step 2. Expected: PASS 2/2.

- [ ] **Step 5: Chạy lại toàn bộ test Storage**

Run: `dotnet test ... --filter "FullyQualifiedName~Storage"`. Expected: PASS (không phá migration khác).

- [ ] **Step 6: Commit**

```bash
git add "src/RouterBalancing.Core/Domain/Entities/ProviderAccount.cs" "src/RouterBalancing.Core/Storage/Migrations/" "router balancing test/Storage/AddAccountProxyOverrideMigrationTests.cs"
git commit -m "feat: add ProxyOverrideEnabled flag with backfill migration"
```

---

### Task 2: Resolver semantics theo cờ

**Files:**
- Modify: `src/RouterBalancing.Core/Proxies/ProxySelectionResolver.cs`
- Test: `router balancing test/Proxies/ProxySelectionResolverTests.cs` (test mới + sửa test cũ đang seed theo semantics junction-rỗng-đồng-nghĩa-override)

**Interfaces:**
- Consumes: `ProviderAccount.ProxyOverrideEnabled` (Task 1).
- Produces: `ProxySelectionResolver.Resolve(Provider, ProviderAccount?)` — semantics mới (doc comment cập nhật).

- [ ] **Step 1: Viết test mới + sửa test cũ (fail)**

Test mới (thêm vào file hiện có):

```
Resolve_AccountFlagOffWithStaleJunctions_InheritsProvider:
  provider có junction [p1]; account flag=false nhưng junction [p2]
  → ProxySelection(m.mode, [p1]) — tập provider, không phải [p2]

Resolve_AccountOverrideEmptySet_ReturnsDirect:
  provider có junction [p1]; account flag=true, junction rỗng
  → ProxySelection.Direct

Resolve_AccountOverrideSubset_UsesAccountSetWithProviderMode:
  provider mode=Fallback, junction [p1,p2]; account flag=true, junction [p2], ProxyMode=null
  → ProxySelection(Fallback, [p2])
```

Sửa test cũ dùng "junction khác rỗng = override" mà không set cờ (vd. test override hiện có) → set `ProxyOverrideEnabled = true` khi seed override; case "junction rỗng = kế thừa" giữ (flag=false).

- [ ] **Step 2: Chạy — fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxySelectionResolverTests"`
Expected: FAIL test mới / test cũ sửa đổi.

- [ ] **Step 3: Sửa resolver**

Nhánh mới trong `Resolve`:

```csharp
if (account is { ProxyOverrideEnabled: true })
{
    var ids = account.AccountProxies.Select(x => x.ProxyId).ToList();
    if (ids.Count == 0) return ProxySelection.Direct;
    return new ProxySelection(
        account.ProxyMode ?? provider.ProxyMode ?? ProxyMode.RoundRobin, ids);
}
```

Giữ nhánh provider + Direct cuối. Cập nhật XML doc đầu class (3 trạng thái, cờ là nguồn sự thật).

- [ ] **Step 4: Chạy — pass**

Run: cùng Step 2. Expected: PASS tất cả.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Proxies/ProxySelectionResolver.cs "router balancing test/Proxies/ProxySelectionResolverTests.cs"
git commit -m "feat: resolve proxy by OverrideEnabled flag (inherit/override/direct)"
```

---

### Task 3: Service — `SetAccountProxyOverrideAsync` + `GetAssignmentMatrixAsync`

**Files:**
- Modify: `src/RouterBalancing.Core/Proxies/IProxyService.cs`, `src/RouterBalancing.Core/Proxies/ProxyService.cs`
- Create: `src/RouterBalancing.Core/Proxies/ProxyAssignmentMatrix.cs` (records)
- Test: `router balancing test/Proxies/ProxyServiceTests.cs`

**Interfaces:**
- Consumes: `ProviderAccount.ProxyOverrideEnabled` (Task 1), pattern diff-update D-A1 (`AssignProviderProxiesAsync` giữ nguyên làm mẫu).
- Produces (Task 4/5 dùng):
```csharp
Task SetAccountProxyOverrideAsync(long accountId, IReadOnlyList<long> proxyIds,
    bool overrideEnabled, CancellationToken ct = default);
Task<ProxyAssignmentMatrix> GetAssignmentMatrixAsync(CancellationToken ct = default);

public sealed record ProxyAssignmentMatrix(
    IReadOnlyList<ProviderAssignmentRow> Providers,
    IReadOnlyList<AccountAssignmentRow> Accounts);
public sealed record ProviderAssignmentRow(
    long Id, string Name, ProxyMode? Mode, IReadOnlyList<long> ProxyIds);
public sealed record AccountAssignmentRow(
    long Id, long ProviderId, bool OverrideEnabled, IReadOnlyList<long> ProxyIds);
```
- GIỮ nguyên `AssignProviderProxiesAsync`, `AssignAccountProxiesAsync`, `GetAccountOverridesAsync`, `ResetAccountOverridesAsync` + test hiện có — **Task 3 chỉ thuần thêm**; toàn bộ việc xóa (methods + test cũ) dồn về Task 5 khi UI ngừng dùng, để build luôn xanh.

- [ ] **Step 1: Viết tests (fail)**

```
SetAccountProxyOverride_Enable_NonEmpty_ReplacesJunctionsAndSetsFlag:
  seed provider+p1,p2; account; gọi SetAccountProxyOverrideAsync(acc, [p2], true)
  → junction đúng [p2]; flag=true; ProxyMode=null

SetAccountProxyOverride_Disable_ClearsJunctionsAndFlag:
  seed override [p1] flag=true; gọi (acc, [], false)
  → junction rỗng; flag=false

SetAccountProxyOverride_Enable_EmptySet_KeepsFlagTrue:
  gọi (acc, [], true) → junction rỗng, flag=true (direct)

SetAccountProxyOverride_UnknownAccount_Throws: → KeyNotFoundException

GetAssignmentMatrix_ReturnsAllProvidersAndOverrides:
  seed P1 có junction [p1] + account A1 override [p1] flag=true, A2 không override; P2 không proxy
  → matrix.Providers chứa cả P1 (ProxyIds=[p1]) và P2 (ProxyIds rỗng)
  → matrix.Accounts: A1 (OverrideEnabled=true, ProxyIds=[p1]), A2 (false, rỗng)
```

- [ ] **Step 2: Chạy — fail**

Run: `dotnet test ... --filter "FullyQualifiedName~ProxyServiceTests"`. Expected: FAIL (method chưa có).

- [ ] **Step 3: Implement (thêm mới, không xóa gì)**

1. `ProxyAssignmentMatrix.cs`: 3 records như trên (XML doc mỗi record).
2. `SetAccountProxyOverrideAsync`: load account `Include(AccountProxies)` (throw `KeyNotFoundException` nếu thiếu); `ValidateProxyIdsAsync`; nếu `!overrideEnabled` → `Clear()` junction; else diff-update D-A1 = `proxyIds`; `ProxyMode = null`; `ProxyOverrideEnabled = overrideEnabled`; `UpdatedAt`; SaveChanges; `_pool.Invalidate()`.
3. `GetAssignmentMatrixAsync`: 3 query EF projection (providers + `ProxyIds`, accounts + flag + `ProxyIds`) — không Include, không N+1.

- [ ] **Step 4: Chạy — pass + build app**

Run: test filter Step 2 → PASS; rồi `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0` → 0 err (chưa xóa gì nên UI cũ vẫn compile).

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Proxies/ "router balancing test/Proxies/ProxyServiceTests.cs"
git commit -m "feat: add SetAccountProxyOverride and assignment matrix queries"
```

---

### Task 4: Tree builder `ProviderAssignmentTree`

**Files:**
- Create: `src/RouterBalancing.Core/Proxies/ProviderAssignmentTree.cs`
- Test: `router balancing test/Proxies/ProviderAssignmentTreeTests.cs`

**Interfaces:**
- Consumes: `ProxyAssignmentMatrix` + records (Task 3), `OutboundProxy`, `Provider`/`ProviderAccount` entities.
- Produces (Task 5 dùng):
```csharp
public static class ProviderAssignmentTree
{
    public static IReadOnlyList<ProviderTreeNode> Build(
        IReadOnlyList<Provider> providers,       // cần Accounts (Id, Name) mỗi provider
        IReadOnlyList<OutboundProxy> proxies,    // toàn bộ — cho Enabled + endpoint
        ProxyAssignmentMatrix matrix);
}
public sealed record ProviderTreeNode(
    Provider Provider, ProxyMode EffectiveMode, IReadOnlyList<ProxyNode> Proxies);
public sealed record ProxyNode(
    OutboundProxy Proxy, IReadOnlyList<AccountTickNode> Accounts);
public sealed record AccountTickNode(
    ProviderAccount Account, bool Tick, bool IsOverride);
```

- [ ] **Step 1: Viết tests (fail)**

```
Build_ProviderWithoutProxy_ReturnsNodeWithEmptyProxies
Build_TickInheritsProviderSet_AndOverrideShowsOwnSet:
  P1 set [p1,p2]; A1 flag=false; A2 flag=true [p2]
  → node P1: 2 ProxyNode; dưới p1: A1.Tick=true, A2.Tick=false, A2.IsOverride=true;
    dưới p2: cả hai Tick=true; EffectiveMode = provider.Mode ?? RoundRobin
Build_DisabledProxy_StillIncluded: proxy Enabled=false vẫn là ProxyNode
Build_ProxyIdNotInProxyList_Skipped: matrix tham chiếu proxy không có trong `proxies` → bỏ qua (race xóa)
Build_Orders_Stable: providers theo Name (OrdinalIgnoreCase) rồi Id; proxies theo Id; accounts theo Id
```

- [ ] **Step 2: Chạy — fail**

Run: `dotnet test ... --filter "FullyQualifiedName~ProviderAssignmentTreeTests"`. Expected: FAIL (chưa có class).

- [ ] **Step 3: Implement builder**

Build: match matrix theo Id (provider thiếu row → `ProxyIds` rỗng); tick = `row.OverrideEnabled ? row.ProxyIds.Contains(proxyId) : providerSet.Contains(proxyId)`; `IsOverride = row.OverrideEnabled`.

- [ ] **Step 4: Chạy — pass**

Run: cùng Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Proxies/ProviderAssignmentTree.cs "router balancing test/Proxies/ProviderAssignmentTreeTests.cs"
git commit -m "feat: add ProviderAssignmentTree builder for provider-centric UI"
```

---

### Task 5: Viết lại tree section Proxies.razor + dọn components/i18n cũ

**Files:**
- Modify: `router-balancing/Components/Pages/Proxies.razor` (thay toàn bộ section gán — từ `<!-- Section gán proxy -->`/tree markup + @code liên quan), `src/RouterBalancing.Core/Localization/Translations.cs`
- Delete: `router-balancing/Components/Shared/AssignProxyToScope.razor`, `router-balancing/Components/Shared/ScopeProxyEditor.razor`, `src/RouterBalancing.Core/Proxies/ProxyAssignmentTree.cs`, `router balancing test/Proxies/ProxyAssignmentTreeTests.cs`, method `GetAccountOverridesAsync`/`ResetAccountOverridesAsync`/`AssignAccountProxiesAsync` (interface+impl, nếu Task 3 để lại), 3 test `ResetAccountOverrides_*` + test dùng `AssignAccountProxiesAsync` (chuyển sang `SetAccountProxyOverrideAsync` hoặc xóa nếu redundancy)
- Test: `router balancing test/TranslationParityTests` (parity — giữ xanh), các test service liên quan

**Interfaces:**
- Consumes: Task 2 resolver (runtime), Task 3 service, Task 4 builder.
- Proxies.razor state mới: `_matrix`, `_tree` (`IReadOnlyList<ProviderTreeNode>` tính lại từ `_providers`(Accounts) + `_rows` + matrix), `_expandedProviderId`, `_addSearch`, `_busy`; handler: `ToggleExpandAsync(long)`, `AddProxyAsync(long providerId, long proxyId)`, `RemoveProxyAsync(long, long)`, `SetModeAsync(long, ProxyMode)`, `ToggleAccountAsync(ProviderTreeNode, ProxyNode, AccountTickNode, bool checking)`.

- [ ] **Step 1: i18n — thêm keys mới (EN block ~sau `proxies.assign.needOneProxy`, VI tương ứng)**

```
proxies.tree.addPlaceholder = "Search proxy to add…" / "Tìm proxy để thêm…"
proxies.tree.addEmpty = "All proxies are already assigned." / "Tất cả proxy đã được gán."
proxies.tree.empty = "No proxy assigned yet." / "Chưa gán proxy nào."
proxies.tree.remove = "Unassign from provider" / "Bỏ gán khỏi provider"
```

- [ ] **Step 2: Viết lại markup tree + @code trong Proxies.razor**

Theo spec C1-C4:
- Root: `@foreach (var p in Tree)` — `<details @key>` provider node: summary = tên + `<select>` mode (rr/fb, `_busy` guard) + badge "N proxy"; body:
  - row "Thêm proxy": `<input placeholder=addPlaceholder @bind=_addSearch @bind:event=oninput>` + list option `AllProxies.Where(x => !providerSet.Contains(x.Id) && matchSearch)` → click `AddProxyAsync`.
  - empty-state nếu chưa có proxy: text `proxies.tree.empty`.
  - `@foreach proxyNode`: row endpoint + badge `(tắt)` nếu `!Enabled` + nút ✕ `RemoveProxyAsync` (title `proxies.tree.remove` — ✕ vẫn enabled khi proxy tắt); dưới là AC rows: `<label><input type=checkbox checked=Tick disabled=!Proxy.Enabled @onchange → ToggleAccountAsync> Tên AC [+ badge override nếu IsOverride]</label>`.
- `ToggleAccountAsync`: tính tập AC hiệu lực hiện tại (OverrideEnabled ? acc set : provider set) từ `_matrix` (không từ tick DOM); newSet = ± proxyId; gọi `SetAccountProxyOverrideAsync(accId, newSet, overrideEnabled: !SequenceEqual(ordered newSet, ordered providerSet))` — newSet rỗng → overrideEnabled=true; await → `ReloadAsync()`; mọi handler early-return khi `_busy`; set `_busy` trong try/finally.
- `ReloadAsync` nạp thêm `GetAssignmentMatrixAsync()` best-effort (song song với reverse như hiện có) — `_accountOverrides`/`_reverseAssignments` giữ nguyên chỗ còn dùng; `_tree` tính lại.
- Xóa markup/code: tree cũ (proxy-root), `_editKey`, `_addProxyId`, `OpenEditScope`, `ClosePanel` panel logic, ConfirmDialog reset override, `InheritLabel`/`OverrideLabel`/`ModeShort` nếu chết, references `ScopeProxyEditor`/`AssignProxyToScope`.
- Badge mode provider: dùng `mode_rr`/`mode_fallback` trực tiếp (bỏ `ModeShort` nếu không còn nơi dùng).
- Nếu dùng Tailwind class mới → chạy `npm run build` trong `router-balancing/vite-project`; ưu tiên class đã có (`pt-panel`, `pt-node`, `pt-summary`, `pt-leaf`, `pt-badge`, `pt-edit`, `btn`…).

- [ ] **Step 3: Xóa files/methods chết + sửa test service**

- Xóa 2 component `.razor`, `ProxyAssignmentTree.cs` + `ProxyAssignmentTreeTests.cs`.
- Xóa `GetAccountOverridesAsync`, `ResetAccountOverridesAsync`, `AssignAccountProxiesAsync` (interface + impl); sửa mọi test còn gọi `AssignAccountProxiesAsync` → `SetAccountProxyOverrideAsync(acc, ids, true)` (hoặc xóa test redundancy với test mới Task 3).
- `Translations.cs`: xóa keys chết — grep từng key trước khi xóa (`proxies.assign.add|edit|message|message_account|needOneProxy|mode_inherit|resetOverrides|scopeProvider|scopeAccount|scopeCount|accountCount|accountOnly|inherit|noneForProxy|reverseNone|select_provider|select_account` — chỉ xóa khi 0 hit ngoài Translations.cs + docs).

- [ ] **Step 4: Build + toàn bộ test**

Run: `dotnet build ... net10.0-windows10.0.19041.0` → 0 err; `dotnet test` toàn bộ → xanh (trừ 2 env + flaky đã biết).

- [ ] **Step 5: Checklist thủ công (user restart app)**

① Root thấy mọi provider kể cả chưa gán; ② search-add: proxy đã gán ẩn; thêm được proxy (kể cả tắt); ③ ✕ gỡ được proxy đang tắt; ④ checkbox AC: untick 1 → badge override; untick hết → các proxy còn lại untick hết (direct); tick lại đủ → badge biến mất (quay kế thừa); ⑤ proxy tắt → checkbox disabled giữ tick; ⑥ đổi mode rr/fb trên node provider.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: provider-centric proxy assignment tree with AC checkboxes"
```

---

### Task 6: Sweep cuối

**Files:**
- Modify: nếu còn sót.

- [ ] **Step 1: Grep dead symbols**

`AssignAccountProxiesAsync|GetAccountOverridesAsync|ResetAccountOverridesAsync|ProxyAssignmentTree|ScopeProxyEditor|AssignProxyToScope|needOneProxy|resetOverrides|InheritLabel|OverrideLabel|EditProviderKey|EditAccountKey` — mong đợi: 0 hit ngoài `docs/` (spec/plan cũ).

- [ ] **Step 2: Build + full test + parity**

Run: build Windows TFM; `dotnet test` toàn bộ; sửa mọi thứ còn sót.

- [ ] **Step 3: Commit (nếu có thay đổi)**

```bash
git add -A && git commit -m "chore: remove dead proxy assignment code"
```
