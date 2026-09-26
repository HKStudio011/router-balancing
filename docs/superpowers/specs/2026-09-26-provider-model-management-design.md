# Spec: Phase 2A — Provider/Model Management

- **Ngày:** 2026-09-26
- **Trạng thái:** Approved (brainstorming session 2026-09-26)
- **Nguồn:** Design gốc `docs/superpowers/specs/2026-09-25-router-balancing-design.md` §4 + §10 (Phase 2)
- **Branch:** `feat/phase2-provider-mgmt` (từ master @ `080276f` — chứa Phase 1 đầy đủ)

## 1. Mục tiêu

Thêm màn hình quản lý Provider + Model vào app:

- CRUD provider (thêm/sửa/xóa/bật-tắt) với test connection trước khi lưu.
- Quản lý model trong từng provider: thêm tự động (fetch từ provider), thêm tay/bulk, bật-tắt, xóa, hiển thị metadata.
- UI tái dùng được cho Bloc B sau này (Router/Combo panel).

## 2. Scope

### Trong scope

- Backend service layer trong `RouterBalancing.Core`: `IProviderService`, `IModelService`, metadata chain `IModelMetadataProvider`.
- Page Blazor `/providers` + shared components (`Modal`, `ConfirmDialog`, `Pager`, `Badge`, `EmptyState`).
- NavMenu link "Providers".
- i18n `providers.*` / `models.*` / `modal.*` / `pager.*` (EN + VI).
- Unit test TDD cho mọi logic backend.

### Ngoài scope (các phase sau)

- Engine (queue, balancer, retry, translation, streaming) — Phase 3.
- Runtime panel, Statistics — Phase 4.
- Router/Combo CRUD (Bloc B) — sub-project B, spec riêng.
- Proxy endpoint `/v1/models` của chính app (Phase 3/5).
- Auto-refresh/health-check định kỳ provider (watchdog — Phase 3).

### Quyết định đã chốt (brainstorming session)

1. **Approach:** Service layer trong Core (nhất quán `AppSettingsService`/`LogService`) — không EF trực tiếp trong component.
2. **Placement:** Page riêng `@page "/providers"` + NavLink trong NavMenu (kế tục pattern Phase 1, không gộp dashboard 1 trang).
3. **Model list:** Hiển thị inline khi **expand row** provider (không modal tab, không trang con).
4. **Pager:** Client-side (list all, paging trong bộ nhớ component) — hợp lý với quy mô desktop vài chục provider.
5. **Không migration mới:** entity `Provider`/`Model`/`ProviderType` đã có từ Phase 1 Task 2, đủ field (kể cả `LastTestSuccess/At/Message`, metadata, `IsManual`).

## 3. Kiến trúc backend

### 3.1 `IProviderService` / `ProviderService`

Singleton trong DI. Dependencies: `IDbContextFactory<RouterBalancingDbContext>`, `ISecretProtector`, `IHttpClientFactory`, `ILogService`.

```csharp
public interface IProviderService
{
    Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default);
    Task<Provider?> GetAsync(long id, CancellationToken ct = default);
    Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default);
    Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);
    Task<ProviderTestResult> TestConnectionAsync(Provider provider, string? apiKeyOverride, CancellationToken ct = default);
}
```

- `ProviderDraft` — record mutable các field form: `Name`, `Type`, `BaseUrl`, `ApiKey` (plaintext, chỉ tồn tại trong lúc edit), `MaxConcurrent`.
- **Bảo mật key:** key luôn `ISecretProtector.Protect` trước khi lưu DB; service **không bao giờ** trả plaintext ra ngoài (`GetAsync` trả entity với `ApiKeyEncrypted` — UI không hiển thị giá trị, chỉ placeholder "đã lưu").
- **Update với key để trống** = giữ nguyên key cũ (không ghi đè bằng chuỗi rỗng).
- `DeleteAsync` cascade xóa models (EF đã cấu hình `OnDelete(Cascade)` từ Phase 1 — verify khi implement, nếu không thì xóa tường minh trong transaction).
- `TestConnectionAsync`:
  - URL: `{BaseUrl.TrimEnd('/')}/v1/models` — BaseUrl phải hợp lệ `http://` hoặc `https://`.
  - Header theo `ProviderType`:
    - `OpenAI` → `Authorization: Bearer {key}`
    - `Anthropic` → `x-api-key: {key}` + `anthropic-version: 2023-06-01`
  - `apiKeyOverride` (key gõ trên form, chưa lưu) ưu tiên hơn key đã lưu (decrypt từ DB).
  - Timeout 10s (named HttpClient `provider-probe`); HTTP 2xx = success; exception/timeout/4xx/5xx = fail kèm message ngắn gọn (không lộ key).
  - Provider đã lưu → persist `LastTestSuccess`/`LastTestAt` (UTC) /`LastTestMessage` vào DB.
  - Trả về `ProviderTestResult(bool Success, string? Message, DateTimeOffset At)`.
- **Validation** (pattern `SettingsValidator` — static class, trả `IReadOnlyDictionary<string, string>` key → i18n error key, không text literal):

| Rule | Điều kiện | Error key |
|---|---|---|
| Name | bắt buộc, không rỗng sau trim | `providers.error.name` |
| BaseUrl | phải bắt đầu `http://` hoặc `https://` | `providers.error.baseUrl` |
| MaxConcurrent | 1–64 | `providers.error.maxConcurrent` |

### 3.2 `IModelService` / `ModelService`

Singleton. Cùng dependencies (+ `IProviderService` không cần — dùng DbContext trực tiếp).

```csharp
public interface IModelService
{
    Task<(int Added, int Skipped)> FetchFromProviderAsync(long providerId, CancellationToken ct = default);
    Task<Model> AddManualAsync(long providerId, string modelId, CancellationToken ct = default);
    Task<(int Added, int Skipped)> AddBulkAsync(long providerId, IReadOnlyList<string> modelIds, CancellationToken ct = default);
    Task RemoveAsync(long modelId, CancellationToken ct = default);
    Task<int> RemoveAllAsync(long providerId, CancellationToken ct = default);
    Task SetEnabledAsync(long modelId, bool enabled, CancellationToken ct = default);
    Task SetAllEnabledAsync(long providerId, bool enabled, CancellationToken ct = default);
}
```

- `FetchFromProviderAsync` — GET `/v1/models` (cùng endpoint/header như test connection, key đã lưu), normalize 2 shape:
  - OpenAI: `data[].id`
  - Anthropic: `data[].id` (cùng key `id`; khác phần wrapper — parse tolerance)
  - Thêm models chưa có (so `ModelId`, case-sensitive theo provider), `IsManual=false`; bỏ qua đã tồn tại (dedupe).
- `AddManualAsync` / `AddBulkAsync` — `IsManual=true`; bulk: mỗi dòng 1 id, trim, bỏ dòng rỗng, dedupe trong payload + với models hiện có.
- Duplicate `ModelId` trong cùng provider bị chặn (unique index `(ProviderId, ModelId)` đã có từ Phase 1) — service trả lỗi/ skip thay vì throw raw.
- Mọi thao tác thêm model → gọi `IModelMetadataService.TryFillAsync(model)` (xem 3.3) để điền metadata nếu lấy được.

### 3.3 Metadata chain (spec gốc §4)

```csharp
public interface IModelMetadataProvider
{
    /// Trả null nếu provider này không biết metadata cho model.
    Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default);
}
```

- `ProviderEndpointMetadataProvider` — thử `GET {base}/v1/models/{modelId}` (header theo Type), parse field nếu có (context window, modalities…). Bỏ qua lỗi im lặng → nhường bước sau.
- `StaticCatalogMetadataProvider` — dictionary nhúng trong Core: prefix → metadata (`gpt-*`, `o1/o3*`, `claude-*`, `deepseek-*`…); covers phần lớn model phổ biến.
- **Fallback cuối:** `null` → giữ field NULL, UI hiển thị placeholder. **Phase 2A chỉ auto-fill + hiển thị** — chỉnh metadata tay để phase sau nếu cần.
- `ModelMetadataService.TryFillAsync(model)` — chạy chain theo thứ tự, ghi kết quả vào entity (chỉ điền field còn NULL, không ghi đè giá trị đã có), save DB. Mọi lỗi → log warning, model vẫn thêm bình thường.

### 3.4 DI registration (`MauiProgram.cs`)

```csharp
services.AddHttpClient("provider-probe", c => c.Timeout = TimeSpan.FromSeconds(10));
services.AddSingleton<IProviderService, ProviderService>();
services.AddSingleton<IModelService, ModelService>();
services.AddSingleton<IModelMetadataService, ModelMetadataService>();
services.AddSingleton<IModelMetadataProvider, ProviderEndpointMetadataProvider>();
services.AddSingleton<IModelMetadataProvider, StaticCatalogMetadataProvider>();
```

- Đăng ký thứ tự endpoint → static (chain resolve `IEnumerable<IModelMetadataProvider>` theo thứ tự đăng ký).
- Thêm package `Microsoft.Extensions.Http` vào Core csproj **nếu** chưa transitively có (verify khi implement).

## 4. Kiến trúc UI

### 4.1 Shared components (tạo mới — `router-balancing/Components/Shared/`)

| Component | Behavior |
|---|---|
| `Modal.razor` | Parameters: `Visible`, `Title`, `OnClose`, `ChildContent`; backdrop click + **ESC** đóng; slot Header/Body/Footer; focus trap nhẹ (autofocus element đầu); render khi `Visible` |
| `ConfirmDialog.razor` | Dùng `Modal` bên trong: `Message`, `Danger` (nút đỏ), `OnConfirm`, `OnCancel`; default focus nút Cancel |
| `Pager.razor` | `Total`, `PageSize` (10/25/50/100), `Page` (bind), `PageChanged`; render khi `Total > PageSize`; hiển thị "Trang x / y (tổng n)" + ô goto + Trước/Sau |
| `Badge.razor` | `Text`, `Variant` (neutral/success/danger/warning/info) → Tailwind classes map tĩnh |
| `EmptyState.razor` | `Message`, optional `Icon`; bảng trống / chưa có provider |

Tất cả follow theme qua CSS variable (pattern Phase 1), Tailwind classes, comments why-only tiếng Việt.

### 4.2 Page `Providers.razor` — `@page "/providers"`

**Toolbar:** nút **+ Thêm provider** (mở modal Add).

**Bảng providers** — cột:

| Cột | Nội dung |
|---|---|
| ▸ | chevron expand toggle (state theo provider id, `@key`) |
| Name | tên + badge Type (`OpenAI`/`Anthropic`) |
| BaseUrl | text, `truncate` |
| Models | `{enabled}/{total}` — click cũng expand |
| Enabled | toggle switch → `SetEnabledAsync` |
| Last test | ✅/❌ badge + thời điểm (relative tooltip `title` = message); null → "— " |
| Actions | **Test** (nút nhanh, đã lưu → spinner → toast kết quả), ✏️ Edit, 🗑 Delete |

**Expand row → model panel inline** (dưới row provider):

- Toolbar: ô input **Add model** (Enter để thêm) + nút **Bulk** (mở textarea trong modal nhỏ — hoặc inline toggle; implementer chọn inline toggle cho gọn), nút **Fetch tự động**, **Bật tất cả / Tắt tất cả**, **Xóa tất cả** (confirm).
- Bảng models: `ModelId` (+ `DisplayName` nếu có), badges metadata (`ctx: 128k` / `vision` / `think` — ẩn khi null), badge `manual` (nếu `IsManual`), toggle Enabled, nút 🗑 Xóa (confirm).
- Đang fetch/add → spinner trên nút; kết quả → toast "Đã thêm N, bỏ qua M".

**Modal Add/Edit provider:**

- Fields: Name (text), Type (select `OpenAI`/`Anthropic`), BaseUrl (text), API Key (password input; mode Edit: placeholder "Đã lưu — để trống để giữ nguyên"), MaxConcurrent (number spinner, default từ setting `DefaultMaxConcurrent`).
- Nút **Test connection** + dòng kết quả inline (spinner → ✅/❌ + message).
- **Rule (spec gốc):** provider **chưa lưu** → Save **chỉ bật khi test pass** (test với key đang gõ trên form); fail → hiện lý do trong modal. Provider **đã lưu** → Save luôn enabled (test là tùy chọn; kết quả hiển thị badge).
- Validation inline theo `ProviderValidator` (nhóm lỗi dưới field / trên footer, pattern SettingsPanel).
- Save → `CreateAsync`/`UpdateAsync` → toast thành công → refresh list. Lỗi persist → toast lỗi + `Log.Error`.

**Xóa provider:** `ConfirmDialog` — "Xóa provider **{name}**? Toàn bộ **{N} model** sẽ bị xóa (không thể hoàn tác)." (text i18n).

### 4.3 NavMenu

Thêm `NavLink` **Providers** (`href="providers"`, icon tùy chọn theo pattern hiện có), thứ tự: Dashboard → **Providers** → Logs → Settings.

### 4.4 i18n

- Keys mới (cả EN + VI, `Translations.cs`): `nav.providers`, `providers.*` (title, add, edit, delete, test, testing, lastTest, never, enabled, disabled, error.name, error.baseUrl, error.maxConcurrent, confirmDelete, deleted, saved, testPassed, testFailed…), `models.*` (count, add, bulk, fetch, fetching, fetched, enableAll, disableAll, deleteAll, confirmDeleteAll, empty, manual, deleted, saved…), `modal.*` (close), `pager.*` (page, of, total, goto, prev, next, pageSize).
- Liệt kê chính xác khi viết implementation plan (spec này quy định naming pattern + nhóm key).

## 5. Testing strategy

### Unit (xUnit — `router balancing test/`)

Tên `Method_WhenX_ExpectY`. TDD (RED → GREEN).

- **`ProviderServiceTests`:** Create encrypts key (roundtrip DPAPI thật, pattern Phase 1); Update empty-key keeps old key + updates `UpdatedAt`; Update with new key re-encrypts; Delete removes cascade models; SetEnabled persists; validation rules (name/baseUrl/maxConcurrent — mỗi rule hợp lệ + không hợp lệ); `ListAsync` includes models.
- **`ProviderTestConnectionTests`** (fake `HttpMessageHandler`): 200 → success + persist `LastTest*` (với provider đã lưu); 401/500 → fail message; timeout/exception → fail; OpenAI → header Bearer; Anthropic → header `x-api-key` + version; `apiKeyOverride` ưu tiên key đã lưu.
- **`ModelServiceTests`:** Fetch parses OpenAI shape → adds missing, skips existing (dedupe); Fetch parses Anthropic shape; AddManual sets `IsManual`; Bulk trims/dedups/skips empty lines; RemoveAll count đúng; SetAllEnabled bật/tắt hết; duplicate add → skip không throw.
- **`ProviderValidatorTests`:** mỗi rule (pattern `SettingsValidatorTests`).
- **`ModelMetadataServiceTests`:** chain thử endpoint trước → null → static catalog; endpoint throw → fallback catalog; catalog không match → null (không throw).

**Baseline:** 61 test (Phase 1) → number cụ thể do plan tính.

### Manual checklist (chạy tay trước merge — pattern Phase 1)

1. Add provider OpenAI thật → test pass → Save → list hiện.
2. Add provider key sai → test fail hiện lý do, Save **bị disabled**.
3. Edit provider: key để trống → giữ nguyên (verify bằng Test pass lại).
4. Fetch models → toast N thêm/M skip; toggle model; metadata badges hiện (gpt-*/claude-*).
5. Bulk add (một dòng duplicate) → skip 1.
6. Xóa provider → confirm hiện đúng số model → xóa.
7. Expand row persist khi toggle các thao tác khác; Pager đổi PageSize/goto.
8. Language EN/VI đổi đủ text mới; theme dark hiện đủ badge/component.
9. `dotnet test` xanh, build 0W.

## 6. Rủi ro & lưu ý

- **CSPRNG key**: form key chỉ nằm trong component state — không log, không toast.
- **HTTP không rõ proxy**: `IHttpClientFactory` client đi thẳng internet (không qua proxy Kestrel của app) — đúng ý.
- **CORS/SSRF**: chỉ `http(s)` BaseUrl đã validate; bind loopback — user tự nhập URL, chấp nhận (app local, user-specified target).
- **Provider trả shape lạ** khi fetch models → hiển thị toast lỗi, không crash.
- Metadata fetch timeout riêng (có thể dài hơn 10s?) — dùng chung 10s, đủ cho lần đầu; failure → catalog/null.

## 7. Deliverables

1. `src/RouterBalancing.Core/Providers/` (hoặc `Domain/Services/` — plan quyết định namespace: khuyến nghị `RouterBalancing.Core.Providers`): interfaces + implementations + `ProviderDraft` + `ProviderValidator` + metadata chain + static catalog.
2. `router-balancing/Components/Shared/`: `Modal`, `ConfirmDialog`, `Pager`, `Badge`, `EmptyState`.
3. `router-balancing/Components/Pages/Providers.razor`.
4. `NavMenu.razor` NavLink; `MauiProgram.cs` DI; `Translations.cs` keys EN+VI; `vite-project/tailwind.config.js` safelist nếu cần.
5. Tests trong `router balancing test/Providers/` (hoặc `Services/` theo pattern hiện có — plan quyết).
6. `wwwroot/build` artifacts (npm build) commit cùng.
