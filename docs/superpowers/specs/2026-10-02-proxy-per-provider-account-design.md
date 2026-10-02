# Proxy theo Provider & Account (Outbound Proxy Assignment)

**Ngày:** 2026-10-02 — **Trạng thái:** Approved (thảo luận với user)

## 1. Bối cảnh

Hiện tại proxy là **global**: 1 `RoundRobinWebProxy` + 1 `ProxyHealthHandler` gắn trên 3 named client (`upstream`, `provider-probe`, `free-model-sync`). Mọi request của mọi provider/account đều đi qua cùng một robin pool. Không cách nào:

- Tắt proxy cho 1 provider/account cụ thể (đi direct).
- Gán nhiều proxy riêng cho một provider, chọn robin hoặc fallback.
- Để free account (không key) đi direct thay vì qua pool.

Mục tiêu: mỗi **provider** và **account** được gán một tập proxy riêng + mode (`RoundRobin` / `Fallback`); không gán → **Direct**. Health/cooldown của pool vẫn toàn cầu (1 proxy down thì down cho tất cả).

## 2. Quyết định (đã confirm với user)

| # | Quyết định |
|---|---|
| D1 | Granularity = **Provider + Account**, most-specific-wins: account có proxy → override provider. |
| D2 | Provider/account **không gán proxy → Direct** (không qua pool). Global pool không còn là mặc định. |
| D3 | **Robin** = RR + cooldown 60s (giữ `ProxyPool` hiện có, scoped theo tập). **Fallback** = thử theo thứ tự (Id tăng), lỗi → proxy kế, hết → Direct. |
| D4 | `provider-probe` + `free-model-sync` **respect proxy của provider**; test account respect proxy của account. |
| D5 | Health/cooldown **toàn cầu per-endpoint**: proxy down 60s thì down cho tất cả provider dùng nó. `ProxyPool` giữ 1 instance singleton. |
| D6 | Mode **inherit**: account chọn → `account.ProxyMode`; null → `provider.ProxyMode`; null → **Robin** (mặc định). |
| D7 | **Compat:** `ProxyHealthHandler` chạy khi không có request context proxy (context null) → giữ behavior **global pool** như cũ (giữ test cũ xanh, safety net nếu có path nào quên set context). |
| D8 | Proxy đã có **không tự gán** cho provider nào; user gán thủ công qua UI. |

## 3. Mô hình dữ liệu

### Enum

```csharp
// Domain/Enums/ProxyMode.cs
public enum ProxyMode { RoundRobin = 0, Fallback = 1 }
```

### Thuộc tính mới

- `Provider.ProxyMode?` (nullable) — null = chưa chọn mode ở provider level.
- `ProviderAccount.ProxyMode?` — tương tự.

### Bảng junction (M2M)

| Bảng | Cột | Ràng buộc |
|---|---|---|
| `ProviderProxies` | `ProviderId`, `ProxyId` | PK `(ProviderId, ProxyId)`; FK `OutboundProxies.Id` |
| `ProviderAccountProxies` | `AccountId`, `ProxyId` | PK `(AccountId, ProxyId)`; FK `ProviderAccounts.Id` |

Mỗi provider/account có thể gán **nhiều** proxy. Gán là tập ID — không có thứ tự trong bảng junction (thứ tự fallback = sort theo `ProxyId` tăng dần, deterministic).

### Migration

`AddProxyAssignments` (timestamp sinh lúc EF chạy, ngày 2026-10-02): thêm 2 cột nullable + 2 bảng junction + 2 index. Additive, không đổi dữ liệu hiện có.

## 4. Thuật toán chọn (effective selection)

```
ResolveSelection(provider, account):
    if account?.Proxies is not null and not empty:
        ids  = account.Proxies
        mode = account.ProxyMode ?? provider.ProxyMode ?? ProxyMode.RoundRobin
    else if provider.Proxies is not null and not empty:
        ids  = provider.Proxies
        mode = provider.ProxyMode ?? ProxyMode.RoundRobin
    else:
        return Direct
    // ids đã là tập proxy ENABLED (pool chỉ load enabled; junction không tự
    // "bỏ" khi proxy bị tắt — pool snapshot tự lọc, handler không thấy proxy tắt)
    return (ids, mode)
```

- **Direct**: không proxy, không `ReportSuccess`/`ReportFailure`, không cooldown.
- Proxy trong tập bị **tắt** không nằm trong pool snapshot (pool chỉ load enabled) → không xuất hiện trong attempt; **down** (cooldown) bị `GetNext`/`GetLivingInOrder` skip. Toàn bộ tập không còn proxy sống → Direct (Robin) / Direct (fallback hết hàng).

## 5. Flow runtime

### 5.1 Request path (upstream)

1. `ChatCompletionsHandler.ForwardAsync(provider, model, ...)`:
   - Chọn account: refactor `ProviderKeyResolver.ResolveFirstEnabledKey` → `ResolveFirstEnabledAccount(provider, protector)` trả `ProviderAccount?` (enabled, key không rỗng, `Priority` rồi `Id` tăng dần). Key plaintext = `Unprotect(account.ApiKeyEncrypted)`. *(Account free không key vẫn bị skip ở bước này — là item riêng, không thay đổi ở đây.)*
   - `ProxyTarget.Current = new ProxyTarget(provider, account)` (AsyncLocal mới, đặt trong method, `finally` reset).
   - `upstream.PostChatCompletionAsync(...)` không đổi signature.
2. `ProxyHealthHandler.SendAsync`:
   - `var target = ProxyTarget.Current;`
   - **`target is null`** → behavior global pool **không đổi** (D7).
   - **`target` có** → `_resolver.Resolve(target.Provider, target.Account)`:
     - **Direct** → `ProxyContext.Current = null` → `base.SendAsync(request, ct)`; không report health.
     - **Robin** → budget = số proxy sống trong tập; vòng `pool.GetNext(allowedIds)` (overload mới); pick → `ProxyContext.Current = pick` → `base.SendAsync` → lỗi connect/407 → `ReportFailure` → pick kế; hết → Direct.
     - **Fallback** → `pool.GetLivingInOrder(allowedIds)` (mới, sort `ProxyId`); lần lượt: lỗi connect/407 → `ReportFailure` + bước kế; hết hàng → Direct. Mỗi proxy thử tối đa 1 lần/request.
   - `RoundRobinWebProxy` / `ProxyContext` / `DynamicProxyCredentials` **không đổi** — vẫn chỉ echo `ProxyContext.Current`.
3. `finally` reset `ProxyTarget.Current`.

### 5.2 Pool — 2 method mới trên `IProxyPool`

| Method | Ý nghĩa |
|---|---|
| `ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds)` | `null` = behavior hiện tại (tất cả). Có tập → chỉ RR trong tập đó, skip down. Cursor toàn cầu giữ nguyên. |
| `IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids)` | Proxy trong `ids`, **không down**, sort `ProxyId` tăng — dùng cho Fallback. |

### 5.3 Probe & sync (D4)

- `FreeModelSyncService` (đi theo provider): trước khi `SendAsync`, `ProxyTarget.Current = new(provider, null)`; `finally` reset.
- Provider connection test (`ProviderService.TestConnectionAsync(provider, ...)`, client `provider-probe`): set `(provider, null)`.
- Account test (`ProviderAccountService`): set `(provider, account)`.
- Cả 3 chạy trên client `provider-probe`/`free-model-sync` đã có `RoundRobinWebProxy` + `ProxyHealthHandler` → tự động respect assignment.

### 5.4 Không cache

`ComboResolver` query DB **mỗi request** (`Include Provider + Accounts`). Assignment đọc từ entity đã load → luôn tươi, **không cần cache/invalidation**. Chỉ pool (cooldown + list enabled) là in-memory, invalidate theo CRUD proxy — behavior hiện có.

## 6. Service & API

### `IProxySelectionResolver` (mới, singleton, in-memory)

```csharp
public sealed record ProxySelection(ProxyMode? Mode, IReadOnlyList<long>? ProxyIds);
// ProxySelection.Direct = (null, null)

public interface IProxySelectionResolver
{
    /// <summary>Gán theo Provider + Account (most-specific-wins, D1). Không đọc DB —
    /// entity Provider/Account phải đã load (gồm junction tables).</summary>
    ProxySelection Resolve(Provider provider, ProviderAccount? account);
}
```

### `IProxyService` (mới, CRUD assignment)

| Method | Ý nghĩa |
|---|---|
| `Task<IReadOnlyList<ProxyAssignment>> GetAssignmentsAsync(long providerId)` | Gán của provider + tất cả account của nó (cho UI). |
| `Task AssignProviderProxiesAsync(long providerId, IReadOnlyList<long> proxyIds, ProxyMode? mode)` | Set (thay thế tập cũ). |
| `Task AssignAccountProxiesAsync(long accountId, IReadOnlyList<long> proxyIds, ProxyMode? mode)` | Tương tự cho account. |

Gán proxy đã **xóa/tắt** → validation reject (kiểm tra tồn tại + enabled). `proxyIds` rỗng = gỡ toàn bộ gán.

### `ProxyTarget` (mới, `AsyncLocal<ProxyTarget?>`)

```csharp
public sealed record ProxyTarget(Provider Provider, ProviderAccount? Account);
public static class ProxyTargetContext { public static AsyncLocal<ProxyTarget?> Current { get; } }
```

## 7. UI (mở rộng Proxies page)

- **Section "Gán proxy"** (dưới danh sách proxy):
  - Chọn Provider → hiển thị proxy đang gán + mode; chọn thêm/xóa proxy (multi-select), chọn mode (Robin / Fallback / Không gán).
  - Chọn Account (thực theo provider) → cùng UI, scope account.
  - Summary badge trên Providers page: "3 proxy · Robin".
- **i18n**: key mới (EN + VI, chèn cuối dict), reuse `providers.action.edit/delete`, `confirm.cancel`, `dashboard.msg.failed`, `settings.action.save` khi có thể.
- Existing "Test" button (provider/account) không đổi UX — tự động đi qua proxy của nó (D4).

## 8. Edge cases

| Case | Hành vi |
|---|---|
| Account có proxy, provider có proxy khác | Account thắng (tập + mode của account). |
| Tập gán nhưng toàn bộ proxy tắt/down | Direct. |
| Fallback: proxy #1 sống nhưng lỗi | Mark down + thử #2; #1 trong cooldown 60s. |
| Provider gán proxy, provider tắt (Enabled=false) | Model không vào candidate → không request tới provider đó. |
| Gán proxy rồi tắt proxy | Request tự Direct (pool không có attempt đó). |
| Request không có `ProxyTarget` (path nội bộ/test) | Global pool (D7). |
| 2 provider cùng gán 1 proxy | Cooldown/chia load chung 1 endpoint (D5) — ý thức được, chấp nhận. |

## 9. Testing

- **`ProxySelectionResolverTests`** (mới, ~10): direct; provider-only; account-only; account override; mode inheritance (account null → provider mode; cả hai null → Robin); tập rỗng.
- **`ProxyPoolTests`** (bổ sung ~5): `GetNext(allowed)` RR trong tập, skip down, cursor không bị ảnh hưởng ngoài tập; `GetLivingInOrder` sort + skip down.
- **`ProxyHealthHandlerTests`** (bổ sung ~5): Direct (không dùng proxy, không report); Robin scoped (chỉ proxy trong tập, tôn trọng cooldown); Fallback (thứ tự Id, báo failure từng cái, hết → direct, không re-try); context null → global pool (test hiện có vẫn xanh).
- **Integration** (mới, 1): 2 proxy thật qua `LocalHttpProxyStub` (1 chết), provider gán Fallback [p1, p2] → request → p1 fail, p2 serve; `ReportFailure(p1)` được gọi.
- **`ProxyServiceTests`** (bổ sung): CRUD assignment (gán, gỡ, reject proxy tắt, reject proxy không tồn tại).
- Test hiện có không đổi hành vi (context null → global).

## 10. Rủi ro & ghi chú

- **Thay đổi hành vi mặc định:** provider chưa gán proxy trước đây đi pool → sau thay đổi đi **Direct**. Đây là quyết định có chủ đích (D2). Người dùng muốn giữ pool cho provider cũ phải gán proxy trong UI.
- `ResolveFirstEnabledAccount` thay thế `ResolveFirstEnabledKey` — kiểm tra mọi caller trước khi refactor (chỉ `ChatCompletionsHandler`).
- Free account (không key) hiện bị skip khi chọn key → đến item "free account không cần key" mới dùng được; design này không chặn (assignment gắn vào account, không phụ thuộc key).
- Anthropic provider (POST `Messages API`) dùng cùng `upstream` client → tự động hưởng assignment (không cần code riêng).
