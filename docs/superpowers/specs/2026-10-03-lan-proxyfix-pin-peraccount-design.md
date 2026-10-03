# Spec: Batch 4 mục — LAN access, Sửa gán proxy, Model pin + copy, Đồng thời per-account

- **Ngày:** 2026-10-03
- **Trạng thái:** Approved (brainstorming, chờ review file)
- **Scope:** 4 mục độc lập sửa/làm trước khi sang item roadmap tiếp theo (#1 Search).

## Bối cảnh

| Mục | Vấn đề hiện tại |
|---|---|
| #1 LAN | Kestrel proxy bind cứng `IPAddress.Loopback` (`ProxyHost.cs:67`) — thiết bị khác trong LAN không dùng được endpoint |
| #2 Model pin | Bảng model chỉ hiện `DisplayName (ModelId)` — không có pin `{Identifier}/{ModelId}` + nút copy để gửi request nhắm đúng provider |
| #3 Gán proxy (nghiêm trọng) | UI gán proxy có bugs: re-save crash âm thầm (EF junction hazard), proxy disabled bị reject nhưng vẫn tick được (lỗi nuốt thành toast chung), race khi đổi scope, không xem được proxy đang gán cho ai |
| #4 Đồng thời | `Provider.MaxConcurrent` đang là tổng cả provider; setting `defaultMaxConcurrent` trùng lặp (chỉ dùng seed form); 0 đang nghĩa là "chặn vĩnh viễn" |

## Non-goals

- Không migration schema (junction đã M2M; `ExecutionEntry` là runtime-only).
- Không host UI Blazor qua LAN (UI vẫn WebView local).
- Không tự cấu hình firewall Windows (user chấp nhận prompt khi bật LAN).
- Không đổi cơ chế ưu tiên hàng đợi (`RequestPriority`), không đổi thứ tự Take.
- Không enforce `ProviderAccount.ModelPatterns` khi chọn TK (hành vi hiện tại giữ nguyên).
- Không thiết kế lại flow gán proxy (user chọn giữ flow: chọn scope → tick proxy → Lưu).

---

## Phần A — Sửa gán proxy + reverse view

### D-A1 — EF diff-update thay whole-set replacement

`AssignProviderProxiesAsync` / `AssignAccountProxiesAsync` hiện gán `provider.ProviderProxies = proxyIds.Select(... new ProviderProxy ...)` — trùng PK với hàng đang track → `InvalidOperationException` khi re-save (set trùng/lệch) → user thấy toast "thất bại" không rõ nguyên nhân.

**Quyết định:** chuyển sang diff trong cùng DbContext:
- Hàng có trong DB nhưng không có trong tập mới → `db.Remove(...)`.
- Hàng mới (chưa có) → `Add`.
- Hàng có sẵn giữ nguyên (không đụng).
- `ProxyMode` + `UpdatedAt` giữ như hiện tại. Tập rỗng = gỡ toàn bộ (giữ hành vi).

Re-save cùng tập = không thao tác gì → idempotent.

### D-A2 — Bỏ check `Enabled` trong validate

Code hiện throw `InvalidOperationException($"Proxy {id} is disabled.")` nhưng doc comment của chính hàm nói *"Proxy tắt vẫn là 'đã gán' trong DB; pool tự lọc enabled"* — mâu thuẫn. UI danh sách tick không lọc disabled.

**Quyết định:**
- Validate chỉ check **tồn tại** → không có → `KeyNotFoundException` (giữ).
- Bỏ throw khi disabled — khớp spec §8: pool tự lọc, request tự Direct.
- UI: dòng proxy disabled trong danh sách tick hiển thị hậu tố **"(tắt)"** (i18n `proxies.assign.disabledSuffix`), vẫn tick/untick được.
- Cập nhật test `AssignProviderProxies_DisabledProxy_Throws` → `..._Allowed`.

### D-A3 — Chống race đổi scope

`OnProviderSelected` fire-and-forget `_ = LoadAssignmentAsync()` → load cũ về trễ áp lên scope mới → checkbox sai, cảm giác "không gán thêm cho provider khác".

**Quyết định:** load token (số nguyên tăng dần):
- Mỗi lần đổi scope: `++_assignLoadToken`, capture local.
- `LoadAssignmentAsync` kết thúc: nếu token != hiện tại → **discard** (không setState).
- Thêm cờ `_assignmentLoading`; `SaveAssignment` disabled khi đang load scope hiện tại (`_busy || _assignProviderId is null || _assignmentLoading`).

### D-A4 — Lỗi cụ thể thay toast chung

`SaveAssignment` catch `Exception` chung → `dashboard.msg.failed`.

**Quyết định:** bắt riêng `KeyNotFoundException` → toast `proxies.assign.error.scopeNotFound` (i18n); giữ catch chung khác + `Log.Error`. (Sau D-A2, lỗi thường gặp duy nhất còn lại là race proxy bị xoá giữa chừng.)

### D-A5 — Fix DbContext leak

`ValidateProxyIdsAsync` tạo context (`db.CreateDbContext()`) không bao giờ dispose.

**Quyết định:** truyền DbContext của hàm caller vào validate (dùng chung, scope `using` của caller) — tránh query 2 lần + leak.

### D-A6 — Reverse view: proxy này gán cho ai

**Requirement:** xem tất cả provider/account đang dùng một proxy (hiện rất khó xem).

- Service: `IProxyService.GetReverseAssignmentsAsync()` → `IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>`; record `ProxyUsage { long ScopeId; string ScopeName; bool IsProvider; ProxyMode? Mode; }`.
- Query: join `ProviderProxies` (tên Provider) + `ProviderAccountProxies` (tên Account), không N+1 (2 query tổng).
- UI: mỗi dòng proxy có nút expand (pattern expander Providers table với models) → hàng chi tiết: `Provider A (rr) · Tài khoản B (fb) · ...`; rỗng → `proxies.assign.none` ("Chưa gán").
- Refresh: cùng `ReloadAsync()` sau Create/Update/Delete proxy và sau `SaveAssignment`.

---

## Phần B — Đồng thời per-account + bỏ setting

### D-B1 — Ngữ nghĩa `Provider.MaxConcurrent`

- **Trước:** tổng request đồng thời của cả provider.
- **Sau:** đồng thời tối đa **mỗi tài khoản** của provider đó (mọi TK cùng 1 giá trị N — không thêm trường per-account).
- **0 = không giới hạn.**
- Validator UI: `0..64` (trước `1..64`); input `min="0"`; text lỗi + label đổi (xem Phần E).
- Không còn giới hạn tổng provider.

### D-B2 — Xóa setting `defaultMaxConcurrent`

Setting chỉ được dùng làm seed form "Thêm provider" — engine không đọc.

**Quyết định:** xóa hoàn toàn: `SettingsKeys.DefaultMaxConcurrent`, `SettingsDraft.DefaultMaxConcurrent`, `IAppSettingsService.DefaultMaxConcurrent`, `AppSettingsService` property, `SettingsValidator` rule, field + error trong `SettingsPanel.razor` (Engine group), comment `SettingsPanel:418`, test `SettingsValidatorTests` liên quan. Seed form thêm provider = `ProviderDraft.MaxConcurrent = 4` (đã là default của draft).

### D-B3 — `ExecutionEntry` thêm chiều TK

- Thêm `AccountId` (long) + `AccountName` (string) vào record — dùng cho đếm, log, snapshot `/v1/requests`.
- `ProviderId`, `Priority`, các field hiện có giữ nguyên.

### D-B4 — Chọn TK least-in-flight trong `TryEnterAsync`

Cùng một lock với hành vi hiện tại (không thêm semaphore):

1. Query provider + danh sách TK enabled (Id, Name, Priority) + `MaxConcurrent` (int? — null = provider không tồn tại → chặn, thấy D-B7).
2. Với mỗi TK enabled: capacity OK nếu `MaxConcurrent == 0` **hoặc** `CountInFlight(providerId, accountId) < MaxConcurrent`.
3. Chọn TK có in-flight thấp nhất; tie-break: `Priority` tăng dần → `Id` tăng dần.
4. Có TK → tạo entry với `AccountId/AccountName`; không có → trả fail (park).

`CanEnterAsync` = tồn tại ≥1 TK enabled còn capacity.

**Ưu tiên hàng đợi giữ nguyên:** chọn TK không đổi thứ tự Take; `RequestPriority` trong entry giữ nguyên.

### D-B5 — Đếm per `(providerId, accountId)`

`CountInFlight(providerId)` → `CountInFlight(providerId, accountId)` (entry đã ghi `AccountId`). `Exit`/release theo `RequestId` như cũ.

### D-B6 — Truyền TK đã chọn tới forward

- `IExecutionList.TryEnterAsync` trả kết quả kèm `AccountId` (null khi fail) thay vì bool.
- `DispatcherLoop`: lưu TK từ TryEnter → truyền vào `ServeAsync` → `ChatCompletionsHandler.ForwardAsync(..., account)` dùng **đúng TK đã chọn** — bỏ `ProviderKeyResolver.ResolveFirstEnabledAccount` tại forward (tránh lệch đếm). `ProxyTarget(provider, account)` nhận TK này.
- Retry-walk (capacity corner) cũng qua TryEnter → TK mới mỗi lần.
- `ModelSelector` chỉ cần `CanEnterAsync` (chưa chọn TK) — giữ nguyên interface gọi.

### D-B7 — Missing provider = chặn (sentinel)

Trước: `FirstOrDefault = 0` + so `>= max` → chặn vĩnh viễn (đúng ý). Giờ 0 = unlimited → **không được dùng 0 làm sentinel**.

**Quyết định:** query trả `int?`; provider không tồn tại → `null` → không cho enter (giữ hành vi "provider đã xoá không serve").

### D-B8 — Watchdog/probe không đổi

Probe không qua ExecutionList — vẫn `ResolveFirstEnabledKey` (first enabled). Không slot.

---

## Phần C — LAN toggle

### D-C1 — Setting

- Key `lanAccess` (bool, **mặc định `false`**), `SettingsDraft.LanAccess`, group **Server**, không rule validate số.
- `SaveServerAsync`: giá trị đổi → persist + `Proxy.RestartAsync()` (đúng pattern restart khi đổi port).

### D-C2 — Bind address

`ProxyHost.StartAsync`: `settings.LanAccess ? IPAddress.Any : IPAddress.Loopback`.

- Log khi bật: liệt kê URL IPv4 non-loopback của máy + loopback.
- `PortSelector` giữ nguyên probe loopback (bind Any vẫn bắt được).

### D-C3 — Dashboard

Dòng endpoint: khi LAN bật hiện thêm `http://<IP-LAN>:<port>` bên cạnh `127.0.0.1`.

### D-C4 — Cảnh báo bảo mật

Hint luôn hiển thị dưới toggle (trước khi bật, để user quyết định có nên bật): *"Endpoint LAN truy cập được từ thiết bị khác trong mạng — nên bật client key."* (i18n `settings.hint.lanAccess`).*

---

## Phần D — Model pin + copy

### D-D1 — Hiển thị pin

Trong bảng model của provider (dưới tên model, ô đầu tiên):

```
DisplayName (ModelId)
{Identifier}/{ModelId}  [📋]
```

Dòng mono nhỏ + nút copy. Chỉ trang Providers (không mở rộng Combos — YAGNI).

### D-D2 — `IClipboardService` dùng chung

- Service DI: `CopyAsync(string) → bool` — wrap `navigator.clipboard.writeText` qua `IJSRuntime`; `false` khi JS exception (NotAllowedError khi document không focus).
- Refactor `SettingsPanel.CopyNewKeyAsync` dùng lại (hiện inline pattern).
- Caller tự toast: thành công → `common.copied`; thất bại → toast lỗi hiện có + `Log.Error`.

---

## Phần E — i18n, test, gates

### i18n (BẮT BUỘC đủ 2 dict `English` + `Vietnamese`)

| Key | Hành động |
|---|---|
| `settings.field.lanAccess` | thêm |
| `settings.hint.lanAccess` | thêm |
| `proxies.assign.disabledSuffix` | thêm ("(tắt)" / "(disabled)") |
| `proxies.assign.none` | thêm ("Chưa gán" / "Not assigned") |
| `proxies.assign.error.scopeNotFound` | thêm |
| `common.copied` | thêm ("Đã sao chép" / "Copied") |
| `providers.field.maxConcurrent` | **sửa text** → "... mỗi tài khoản của provider (0 = không giới hạn)" |
| `providers.error.maxConcurrent` | **sửa text** → "0–64" |
| `settings.field.maxConcurrent`, `settings.error.maxConcurrent` | **xóa** |

Key `proxies.assign.disabledSuffix` chỉ dùng cho hậu tố "(tắt)" trong danh sách tick proxy (D-A2) — reverse view không hiển thị trạng thái enabled của proxy (đã có cột Enabled trong bảng).

### Unit test mới / sửa

**Proxy (A):**
- `AssignProviderProxies_IdempotentResave_SameSet_Succeeds`
- `AssignProviderProxies_OverlappingResave_ReplacesDifference`
- `AssignProviderProxies_SameProxySecondProvider_BothPersist` (triệu chứng user báo)
- Sửa: `AssignProviderProxies_DisabledProxy_Throws` → `..._Allowed`
- `GetReverseAssignments_ReturnsProvidersAndAccounts` + proxy không gán → rỗng

**Engine (B):**
- `TryEnter_TwoAccounts_SkipsSaturatedAccount_BalancesLeastInFlight` (2 TK, N=1 → req2 vào TK2)
- `TryEnter_AllAccountsFull_ReturnsFalse`
- `TryEnter_ZeroMax_ConcurrentUnlimited` (0 = unlimited)
- `TryEnter_ProviderMissing_ReturnsFalse` (sentinel — cập nhật test cũ)
- Sửa test dùng `maxConcurrent: 0` để park → cơ chế saturate khác (max=1 + chiếm slot):
  - `ProxyQueueIntegrationTests` (SeedProvider max=0)
  - `ProxyControlApiTests` (SeedProvider max=0)
- `ProviderValidatorTests`: 0 hợp lệ, 65 lỗi
- Settings: xóa test `DefaultMaxConcurrent` rule

**LAN (C):**
- Unit test chọn bind address 2 nhánh (LAN on/off)
- Integration test 1 case: host với `lanAccess=true` respond trên non-loopback

**Không unit test:** copy clipboard (JS) → checklist tay.

### Gates (repo root, app đóng)

1. `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0 warning / 0 error
2. `dotnet test "router balancing test/router balancing test.csproj"` → all pass
3. `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0 warning / 0 error

Flake policy cũ: `ProxyControlApiTests`, `ProxyRetryIntegrationTests`, `ProxyQueueIntegrationTests.Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId` — rerun + isolation, không accept.

### Checklist tay

1. LAN: bật toggle → Kestrel restart → Dashboard hiện URL LAN → request từ thiết bị khác trong LAN (nếu có) / tối thiểu curl LAN-IP từ chính máy; tắt → quay loopback.
2. Gán proxy: gán P cho Provider A → chuyển nhanh sang B (checkbox phải đúng của B) → gán P cho B (cùng proxy 2 provider) → re-save không lỗi → proxy disabled vẫn gán được + hiện "(tắt)" → expand dòng proxy hiện đúng danh sách Provider/Account.
3. Pin copy: copy `{Identifier}/{ModelId}` → paste đúng; toast hiện.
4. Per-account: provider 2 TK, N=1 → 2 request đồng thời phân bổ 2 TK; cả 2 full → request park; N=0 → không giới hạn.
5. Settings: field "Số request đồng thời tối đa" biến mất; form thêm provider mặc định 4.

## Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| Phần B đụng core engine (ExecutionList/DispatcherLoop/ModelSelector/Handler) | TDD từng quyết định; checklist tay parking/balancing; giữ nguyên park/wake + priority |
| Test park-by-0 vỡ | Chuyển cơ chế saturate (D-B1 test list) |
| EF diff-update sai → mất gán | Test idempotent + overlapping + rỗng |
| LAN mở暴露 endpoint | Mặc định OFF + hint client key; user tự bật |
| Firewall prompt gây hiểu nhầm "hỏng" | Checklist ghi rõ prompt Windows expected |
