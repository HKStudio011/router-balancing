# Spec: Tree gán proxy theo hướng Provider — checkbox AC per proxy, direct mode

- **Ngày:** 2026-10-12
- **Trạng thái:** Approved (brainstorming, chờ review file)
- **Scope:** Thiết kế lại toàn bộ section gán proxy trên trang Proxies: root cây = provider, thao tác gán = checkbox trực tiếp; semantics AC "direct" (không qua proxy nào).

## Bối cảnh

UI gán proxy hiện tại (tree root = proxy + form "+Gán" + panel ✎ theo scope + mode per-AC) quá phức tạp so với nhu cầu thực: user muốn thao tác theo chiều **provider → proxy → AC**, mỗi AC chỉ cần biết "đi qua proxy này hay không" (checkbox bật/tắt). Ba quyết định đã chốt với user:

1. **Mode RR/FB nằm ở provider** — hợp lý vì nhìn vào provider là thấy tập proxy; AC dùng mode provider khi đi qua nhiều proxy.
2. **AC bỏ tick hết proxy = direct** — không đi qua proxy nào (hiện hệ thống không phân biệt được "chưa override" vs "override tập rỗng").
3. **Root cây = tất cả provider** kể cả chưa gán proxy (node chỉ có [+ Thêm proxy]).

## Non-goals

- Không đổi bảng CRUD proxy phía trên trang (create/edit/delete/toggle/test giữ nguyên).
- Không đổi `GetReverseAssignmentsAsync` — `RequestTrace.razor` đang dùng (live-trace markers).
- Không thêm per-AC mode — mode override cột `ProviderAccounts.ProxyMode` giữ nguyên schema,(UI không ghi nữa; resolver vẫn đọc cho back-compat).
- Không đổi runtime lọc proxy tắt (D-A2: pool tự lọc enabled; proxy tắt → effectively direct, bật lại là dùng lại — không cần "thiết lập lại").
- Không cho phép AC chọn proxy ngoài tập của provider (UI enforced; data cũ được migration chuẩn hóa — xem A2).

---

## Phần A — Semantics & DB

### A1 — Ba trạng thái gán của AC

| Trạng thái DB của AC | AC đi qua tập nào | Mode |
|---|---|---|
| `ProxyOverrideEnabled = false` | **Toàn bộ** tập + mode provider (kế thừa) | `provider.Mode ?? RoundRobin` |
| `= true`, junction khác rỗng | **Đúng** tập junction | `acc.ProxyMode ?? provider.Mode ?? RoundRobin` |
| `= true`, junction rỗng | **Direct** — không proxy nào | — |

Invariant mới: tập override của AC **luôn là tập con** của tập provider (UI chỉ hiện checkbox trong tập provider; migration chuẩn hóa data cũ — A2).

### A2 — Migration `AddAccountProxyOverrideFlag`

- Thêm cột `ProviderAccounts.ProxyOverrideEnabled` — `bool` NOT NULL, default `0` (SQLite `INTEGER NOT NULL DEFAULT 0`).
- **Backfill semantics cũ** (junction rỗng trước đây = kế thừa; khác rỗng = override):

  ```sql
  UPDATE "ProviderAccounts"
  SET "ProxyOverrideEnabled" = 1
  WHERE "Id" IN (SELECT DISTINCT "AccountId" FROM "ProviderAccountProxies");
  ```

- **Chuẩn hóa invariant ⊆**: mọi (provider, proxy) mà AC override của provider đó đang tham chiếu nhưng chưa có hàng `ProviderProxies` → chèn thêm hàng (AC override tham chiếu proxy ngoài tập provider = data cũ từ UI assign-theo-AC; nếu không chuẩn hóa thì tree mới hiển thị "mất" proxy đó, AC vẫn đi qua — rối).
- Migration test theo pattern `AddStatsJournalColumnsMigrationTests` (Migrate(PreviousId) → seed → Migrate(TargetId) → assert cột + backfill + chèn junction; Down drop cột).

### A3 — Giữ nguyên schema khác

`ProviderAccounts.ProxyMode`, `ProviderAccountProxies`, `ProviderProxies` không đổi cấu trúc. UI mới không ghi `ProxyMode` của AC (luôn `null` → resolver lấy mode provider).

---

## Phần B — Resolver & Service

### B1 — `ProxySelectionResolver`

Đọc thêm `acc.ProxyOverrideEnabled`:

```
if (acc.ProxyOverrideEnabled)
    → tập acc (rỗng = direct); mode = acc.ProxyMode ?? provider.Mode ?? RoundRobin
else
    → tập provider; mode = provider.Mode ?? RoundRobin
```

Test: 4 nhóm — kế thừa / override tập con / override rỗng = direct / mode fallback chuỗi (`acc → provider → RR`). Cập nhật test resolver hiện có theo semantics cờ.

### B2 — `SetAccountProxyOverrideAsync` (thay `AssignAccountProxiesAsync`)

```csharp
Task SetAccountProxyOverrideAsync(long accountId, IReadOnlyList<long> proxyIds,
    bool overrideEnabled, CancellationToken ct = default);
```

- `overrideEnabled = false` → xóa toàn bộ junction + `ProxyMode = null` + cờ `false` (quay kế thừa).
- `= true` → junction = `proxyIds` (toàn bộ diff-update theo pattern D-A1; **rỗng = direct**) + cờ `true` + `ProxyMode = null`.
- Validate `proxyIds` tồn tại (giữ `ValidateProxyIdsAsync`); `KeyNotFoundException` nếu account không có.
- Invalidate pool khi có thay đổi.
- Xóa `AssignAccountProxiesAsync` (UI là consumer duy nhất; integration test chuyển sang method mới).

### B3 — `AssignProviderProxiesAsync` giữ nguyên

Dùng cho: thêm proxy vào provider (cộng), bỏ proxy (trừ), đổi mode rr/fb (giữ tập, đổi mode). Diff-update D-A1 giữ nguyên.

### B4 — `GetAssignmentMatrixAsync` (thay `GetAssignmentsAsync` + `GetAccountOverridesAsync` + `ResetAccountOverridesAsync`)

```csharp
Task<ProxyAssignmentMatrix> GetAssignmentMatrixAsync(CancellationToken ct = default);

public sealed record ProxyAssignmentMatrix(
    IReadOnlyList<ProviderAssignmentRow> Providers,
    IReadOnlyList<AccountAssignmentRow> Accounts);
public sealed record ProviderAssignmentRow(
    long Id, string Name, ProxyMode? Mode, IReadOnlyList<long> ProxyIds);
public sealed record AccountAssignmentRow(
    long Id, long ProviderId, bool OverrideEnabled, IReadOnlyList<long> ProxyIds);
```

- 3 query EF projection tổng (providers+proxyIds, accounts+flags, account junction) — không N+1 (D-A6).
- Xóa: `GetAssignmentsAsync`, `GetAccountOverridesAsync`, `ResetAccountOverridesAsync` (vừa thêm — semantics bị thay bằng cờ; nút ↺ bị thay bằng untick-all).

### B5 — Tree builder mới: `ProviderAssignmentTree`

Thay `ProxyAssignmentTree` (chỉ Proxies.razor + test cũ dùng):

```csharp
public static class ProviderAssignmentTree
{
    // providers kèm Accounts; proxies toàn bộ (endpoint + Enabled);
    // matrix từ B4 — builder tự tính tick
    public static IReadOnlyList<ProviderTreeNode> Build(
        IReadOnlyList<Provider> providers,
        IReadOnlyList<OutboundProxy> proxies,
        ProxyAssignmentMatrix matrix);
}

public sealed record ProviderTreeNode(
    Provider Provider,
    ProxyMode EffectiveMode,                 // Mode ?? RoundRobin
    IReadOnlyList<ProxyNode> Proxies);       // đúng tập ProviderProxies
public sealed record ProxyNode(
    OutboundProxy Proxy,                     // có Enabled/endpoint
    IReadOnlyList<AccountTickNode> Accounts); // mọi AC của provider
public sealed record AccountTickNode(
    ProviderAccount Account,
    bool Tick,                               // OverrideEnabled ? accSet.Contains : providerSet.Contains
    bool IsOverride);                        // OverrideEnabled (badge "override")
```

- Thứ tự: providers theo `Name` (UI), proxies theo Id, AC theo Id — ổn định.
- Xóa `ProxyAssignmentTree.cs` + `ProxyAssignmentTreeTests.cs` (viết lại theo builder mới).

---

## Phần C — UI tree (Proxies.razor)

### C1 — Cấu trúc

```
▸ NVIDIA NIM Free   [rr ▾]   2 proxy          ← dropdown mode inline, đổi = save ngay
├── [+ Thêm proxy  🔍____]                    ← search-add, ẩn proxy ĐÃ gán cho provider này
├── socks5://x:11000 ●
│    ├── nvidia 1 [x]      ●override           ← tick = AC đi qua proxy này
│    └── nvidia 2 [ ]
└── http://y:8080 (tắt)  ✕                     ← ✕ = bỏ gán khỏi provider (vẫn gỡ được)
     ├── nvidia 1 [x disabled]                 ← proxy tắt: checkbox AC disabled, giữ tick
     └── nvidia 2 [  disabled]
```

- Root = **tất cả** provider (`ProviderSvc.ListAsync`), kể cả chưa gán proxy — node rỗng chỉ có [+ Thêm proxy] + empty-state.
- Node provider collapse/expand — chỉ 1 provider expand tại một thời điểm (giống Providers table).

### C2 — Thêm proxy (search-add)

- Pattern search client-side như model panel ở Providers.razor (`_modelSearch` + filter không phân biệt hoa thường).
- Option pool = **toàn bộ proxy − proxy đã gán cho provider này** (bao gồm proxy đang tắt — hiển thị, đánh dấu "(tắt)"; thêm được, pool tự lọc khi chạy — nhất quán D-A2).
- Chọn option → `AssignProviderProxiesAsync(provider, hiện tại + proxy, mode hiện tại)` → reload.
- Không còn option → empty-state "Tất cả proxy đã được gán".

### C3 — Gán/bỏ gán + mode

- ✕ trên proxy row → `AssignProviderProxiesAsync(provider, hiện tại − proxy, mode)` → reload.
- Dropdown `[rr ▾]` trên node provider → `AssignProviderProxiesAsync(provider, giữ nguyên tập, mode mới)` → reload.
- Không toast thành công (thay đổi thấy ngay qua UI); lỗi → toast cụ thể (`KeyNotFoundException` → scopeNotFound; còn lại `dashboard.msg.failed`) — giữ pattern D-A4.

### C4 — Checkbox AC per proxy (save ngay)

- Mỗi thay đổi tick: tính tập mới của AC = tập tick hiện tại ± dòng bấm → `SetAccountProxyOverrideAsync(acc, tập, overrideEnabled)`.
  - Tập mới **== tập provider** → `overrideEnabled: false` (tự quay kế thừa — thay nút ↺ cũ).
  - Tập mới **rỗng** → `overrideEnabled: true` + junction rỗng (**direct**).
  - Còn lại → `overrideEnabled: true`.
- Mỗi toggle: **await service → reload cây** (matrix ~3 query, provider ~10 — đủ nhanh; nếu về sau chậm thì chuyển optimistic). Lỗi → toast + reload (không để state lệch).
- Badge `override` trên dòng AC khi `IsOverride = true` (đã ghi đè — tập khác tập provider, vì tập bằng đã tự tắt cờ).
- **Proxy tắt**: checkbox AC dưới nó `disabled` (hiển thị tick hiện tại); ✕ vẫn hoạt động. Không có guard tập rỗng — direct được phép (bỏ `needOneProxy`).

### C5 — components bị xóa

- `AssignProxyToScope.razor`, `ScopeProxyEditor.razor`, ConfirmDialog reset override (nút ↺), panel `_editKey`/`_addProxyId`/`OpenEditScope`… — toàn bộ logic gán nằm inline trong tree của Proxies.razor (handler nhỏ: `AddProxyAsync`, `RemoveProxyAsync`, `SetModeAsync`, `ToggleAccountAsync`).

---

## Phần D — i18n

- **Thêm:** `proxies.tree.addPlaceholder`, `proxies.tree.addEmpty` (tất cả proxy đã gán), `proxies.tree.empty` (provider chưa có proxy), `proxies.tree.remove` (title nút ✕); giữ `proxies.assign.disabledSuffix`, `mode_rr`, `mode_fallback`, `error.scopeNotFound`, `proxies.assign.override` (badge).
- **Xóa** (không còn Razor dùng): `add`, `edit`, `message`, `message_account`, `needOneProxy`, `mode_inherit`, `resetOverrides*`, `scopeProvider`, `scopeAccount`, `scopeCount`, `accountCount`, `accountOnly`, `inherit`, `noneForProxy`, `reverseNone`, `select_provider`, `select_account`(đã xóa trước đó)… — rà toàn bộ bằng grep trước khi xóa, giữ `TranslationParityTests` xanh EN/VI.
- Badge mode provider dùng chung nhãn `mode_rr`/`mode_fallback`.

---

## Phần E — Tests

| Lớp | Nội dung |
|---|---|
| Migration | Pattern `AddXxxMigrationTests`: cột mới, backfill cờ từ junction cũ, chèn junction thiếu (invariant ⊆), Down |
| Resolver | Kế thừa / override tập con / override rỗng = direct / fallback mode chuỗi |
| Service | `SetAccountProxyOverrideAsync`: bật-tắt cờ, tập rỗng=direct, diff-update idempotent, unknown account → throw; `GetAssignmentMatrixAsync`: đúng 3 query-ish, provider không có proxy vẫn có row; `AssignProviderProxiesAsync` giữ hành vi |
| Tree builder | Provider chưa gán → node rỗng; tick inherit vs override; proxy tắt vẫn hiện; thứ tự ổn định |
| UI-level | Không có bUnit — phủ qua builder + service; các thao tác UI là handler mỏng gọi service |

Xóa: `ProxyAssignmentTreeTests`, test dùng `AssignAccountProxiesAsync`/`GetAssignmentsAsync`/`GetAccountOverridesAsync`/`ResetAccountOverridesAsync` (chuyển hoặc xóa theo consumer).

---

## Phần F — File thay đổi (dự kiến)

| Hành động | File |
|---|---|
| Sửa | `Domain/ProviderAccount.cs` (+cờ), `Migrations/*` (mới), `Proxies/ProxySelectionResolver.cs`, `Proxies/IProxyService.cs`, `Proxies/ProxyService.cs`, `Components/Pages/Proxies.razor` (tree section) |
| Mới | `Proxies/ProviderAssignmentTree.cs`, migration + designer, migration test |
| Xóa | `Proxies/ProxyAssignmentTree.cs`, `Proxies/ProxyAssignment.cs` (chỉ consumer cũ đều chết), `Components/Shared/AssignProxyToScope.razor`, `Components/Shared/ScopeProxyEditor.razor` |
| Test | `Proxies/*Tests.cs` (xem E), `Storage/*MigrationTests.cs` |

## Rủi ro / edge

- **Data cũ có AC override tham chiếu proxy ngoài tập provider** → migration chèn junction (A2); nếu user thật sự có data như vậy sau migration, cây hiển thị đầy đủ.
- **Reload sau mỗi toggle** — matrix ~3 query, provider ~10 → chấp nhận được; nếu chậm có thể optimistic-làm-tròn sau.
- **ProxyPool.Invalidate** gọi sau mọi thao tác gán — giữ nguyên (D-A1 pattern).
