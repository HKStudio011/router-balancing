# Spec: Provider Free — preset catalog 4 nhà cung cấp free + sync model free

- **Ngày:** 2026-10-01
- **Trạng thái:** Draft — chờ user review
- **Pipeline:** brainstorming → spec → plan → SDD (item 4 trong roadmap)
- **Branch:** `feat/retry-circuit-3c`

## 1. Mục tiêu

4 nhà cung cấp LLM **miễn phí** được **khai báo sẵn** (seed vào DB lúc khởi động), hiển thị trong tab Providers hiện tại như provider thường (kèm badge **Free**), và có chức năng **tự động lấy danh sách model free** của từng provider (chạy nền định kỳ + nút manual).

## 2. Non-goals (rõ ràng KHÔNG làm)

- **Không đổi balancer/routing** — provider free tham gia pool như mọi provider bình thường (user bật/tắt tự quyết định traffic).
- **Không** quota/cost tracking riêng cho free.
- **Không** config catalog ngoài file (JSON/appsettings) — catalog hardcode trong code.
- **Không** cho xoá 4 provider preset.
- **Không** auto-assign API key — user tự thêm account/key như provider khác.

## 3. Quyết định đã chốt (brainstorming Q1–Q9)

| # | Quyết định |
|---|---|
| D1 | "Provider free" = preset catalog 4 provider free, **chỉ phục model free**, **balancer không đổi hành vi** (option A + tự động lấy model free). **Bỏ Antigravity** (user chốt — không có endpoint list model probe được) |
| D2 | Seed vào DB lúc khởi động (giống pattern legacy settings của client-keys); user bật/tắt/sửa được |
| D3 | Fetch model: **periodic nền mỗi 6h** + **nút manual** luôn có |
| D4 | Free detection: **pricing = 0** HOẶC **pattern free** (`:free`, `-free`, free tier) per provider |
| D5 | Sync semantics: **Merge** — giữ model user tự thêm, sync model free từ API, xoá model fetched đã biến mất |
| D6 | Preset **không xoá được** (luôn có mặt; chỉ bật/tắt/sửa tên/URL) |
| D7 | UI: nằm trong **tab Providers hiện tại**, xuất hiện như provider thường + badge "Free" |
| D8 | **Approach 1**: catalog hardcode + cột `IsPreset` mới; dùng lại `Model.IsManual` (KHÔNG thêm cột Model) |
| D9 | Seed default: **`Enabled = false`** — user tự bật, tránh 4 provider lạ tự nhận traffic |

## 4. Catalog 4 provider (đã research + probe thật)

Probe ngày 2026-10-01 (không cần API key, HTTP 200 cả 4):

| Key | Tên hiển thị | BaseUrl (chat) | Models endpoint | DetectKind | Probe |
|---|---|---|---|---|---|
| `opencode` | OpenCode Free | `https://opencode.ai/zen/v1` | `GET {base}/models` | `FreeSuffix` (`-free`) | 200, 84 models, 11 id `*-free`, `pricing` **null** |
| `openrouter` | OpenRouter Free | `https://openrouter.ai/api/v1` | `GET {base}/models` | `PricingZeroOrFreeSuffix` | 200, 462 models, 20 pricing=0, 16 `:free` |
| `nvidia-nim` | NVIDIA NIM Free | `https://integrate.api.nvidia.com/v1` | `GET {base}/models` | `AllFree` | 200, 81 models, không có field pricing |
| `ollama-cloud` | Ollama Cloud Free | `https://ollama.com/v1` | `GET {base}/models` | `AllFree` | 200, 17 models, không pricing (free tier) |

Ghi chú từng provider:

- **OpenCode Zen** — `GET https://opencode.ai/zen/v1/models` (OpenAI-compatible). Free models đặt id hậu tố `-free` (vd `deepseek-v4-flash-free`, `mimo-v2.5-free`); **không có pricing** → detect theo suffix. Nguồn: opencode.ai/docs/zen.
- **OpenRouter** — `GET https://openrouter.ai/api/v1/models` → `{data:[{id, name, pricing:{prompt, completion}, context_length, ...}]}`. Free = `pricing.prompt=="0" && pricing.completion=="0"` **HOẶC** `id` endswith `:free`. Nguồn: openrouter.ai/docs/overview/models ("A value of 0 indicates the feature is free").
- **NVIDIA NIM** — `GET https://integrate.api.nvidia.com/v1/models` (OpenAI list). build.nvidia.com: "All models … are free to prototype with" (rate limit 40 RPM) → **mọi model trong list đều free** → `AllFree`.
- **Ollama Cloud** — `GET https://ollama.com/v1/models` (OpenAI list, probe 200 không key; `/api/tags` cũng 200). Cloud models free tier, không per-token billing → `AllFree`.

## 5. Data model + Migration

### 5.1 `Provider` — thêm 1 cột

```csharp
/// <summary>Provider preset (free catalog) — không cho xoá, seed lúc startup.</summary>
public bool IsPreset { get; set; }
```

Migration: `AddFreeProviderPresets` (dotnet ef, theo command chuẩn AGENTS.md).

### 5.2 `Model` — KHÔNG thêm cột

Dùng lại **`Model.IsManual`** (đã có, ngữ nghĩa sẵn):

- `IsManual = false` → model do sync/seed tạo (nguồn API) → được phép xoá khi biến mất khỏi fetch.
- `IsManual = true` → user tự thêm → **giữ vĩnh viễn**, không đụng tới khi merge.

### 5.3 `Provider` — thêm 1 cột sync state (cùng migration `AddFreeProviderPresets`)

```csharp
/// <summary>Lần sync model free gần nhất (null = chưa sync).</summary>
public DateTimeOffset? LastModelSyncAt { get; set; }
```

(Lưu ý: tách khỏi `LastTestAt` — test connection và sync model là 2 việc khác nhau.)

## 6. FreeModelSyncService

### 6.1 Interface

```csharp
public interface IFreeModelSyncService
{
    /// <summary>Sync model free cho 1 provider preset. Trả về số model sau sync; lỗi → ném FreeModelSyncException.</summary>
    Task<int> SyncProviderAsync(long providerId, CancellationToken ct = default);

    /// <summary>Sync toàn bộ provider preset đang Enabled. Trả về (providerId, số model) từng provider thành công.</summary>
    Task<IReadOnlyList<(long ProviderId, int ModelCount)>> SyncAllEnabledAsync(CancellationToken ct = default);
}
```

### 6.2 Thuật toán `SyncProviderAsync`

1. Load provider theo id; nếu không tồn tại → `FreeModelSyncException("not found")`. Nếu `!IsPreset` → `FreeModelSyncException("not a preset")`.
2. Resolve catalog entry: match theo **`Provider.Name` == `entry.DisplayName`** (name gán lúc seed). Nếu user đã **đổi tên** preset → không match được → skip + log warning (periodic) / lỗi rõ cho user (manual), không sync. Không thêm cột `PresetKey` để theo rename. **[Quyết định S1 — xem §12]**
3. Fetch:
   - HTTP `GET` models endpoint, timeout **30s**, header `Authorization: Bearer {enabled account key}` nếu provider có account key (không bắt buộc với 4 endpoint public).
   - Non-200 / timeout / parse fail → ném `FreeModelSyncException` kèm status; **không đụng DB**.
4. Parse + filter qua `IFreeModelDetector` (§6.3) → tập `F` (model ids free).
5. **Guard:** nếu `F` rỗng → ném `FreeModelSyncException("empty result")` — **không bao giờ xoá sạch** khi API trả rỗng bất thường.
6. Merge (transaction):
   - `existing` = models của provider.
   - Với model `m` có `!m.IsManual && m.ModelId ∉ F` → **delete**.
   - Với `id ∈ F`: nếu chưa có → **insert** (`IsManual=false`, `Enabled=true`, `DisplayName`/`ContextWindow` nếu API có); nếu đã có `&& !IsManual` → **update metadata** (DisplayName, ContextWindow nếu API có); nếu đã có `&& IsManual` → **bỏ qua** (giữ nguyên của user, kể cả metadata).
   - Cập nhật `provider.LastModelSyncAt = now`.
7. Trả về `F.Count` (số model free sau filter).

### 6.3 Detector — `IFreeModelDetector` / `FreeModelDetector`

```csharp
public enum FreeDetectKind
{
    /// <summary>pricing parse về decimal == 0 ("0", "0.0") HOẶC id endswith ":free" (OpenRouter).</summary>
    PricingZeroOrFreeSuffix,
    /// <summary>id endswith "-free" (OpenCode Zen — không có pricing).</summary>
    FreeSuffix,
    /// <summary>Mọi model trong response đều free (NVIDIA NIM, Ollama Cloud).</summary>
    AllFree,
}
```

- Parse JSON linh hoạt theo kind: `data[]` (OpenAI-style) — field theo kind (`pricing`, `id`).
- `ContextWindow` map từ `context_length` (OpenRouter/OpenCode nếu có); `DisplayName` từ `name` nếu có, không có thì để null.
- Detector là pure function → unit test trực tiếp với JSON fixture.

### 6.4 Seed — `DbInitializer`

```csharp
// Free provider catalog — seed 4 preset nếu DB chưa có (không re-seed sau khi user sửa tên).
FreeProviderCatalog.Entries: { Key, DisplayName, BaseUrl, ModelsEndpoint, DetectKind, NeedsKey }
```

- Với mỗi entry: nếu `!context.Providers.Any(p => p.Name == entry.DisplayName)` → insert `{ Name, BaseUrl, IsPreset = true, Enabled = false, MaxConcurrent = 4, CreatedAt/UpdatedAt = now }`, **không** insert model nào (chờ lần sync đầu).
- **Không bao giờ update/xoá** provider đã tồn tại (user đã sửa là của user).

## 7. FreeModelSyncWorker (nền định kỳ)

Tái dùng pattern `LogRetentionWorker` (`PeriodicTimer` + `IAsyncDisposable`):

- `PeriodicTimer(TimeSpan.FromHours(6))`.
- **Lần đầu chạy:** trễ **60s** sau khi worker start (không chặn app startup; app đóng sớm thì cancel qua CancellationToken).
- Mỗi tick: `SyncAllEnabledAsync()` — chỉ provider `IsPreset && Enabled`.
  - Provider lỗi: **log warning + skip** (không abort cả vòng), không raise UI.
- `SyncNowAsync(providerId)` / `SyncAllNowAsync()` public → nút manual gọi thẳng (không qua timer), chạy **fire-and-forget với kết quả trả về Task** để UI await được.
- Đăng ký DI: singleton + start khi app start (theo cách `LogRetentionWorker` đang được start — xem plan).

## 8. UI — Providers tab

1. **Badge "Free"**: card provider có `IsPreset` → badge (i18n `providers.badge.free`) cạnh tên. Vị trí/render theo style badge Enabled hiện có.
2. **Ẩn nút xoá** cho `IsPreset` (nút delete trong card + mọi entry menu liên quan).
3. **Guard service** (`ProviderService.DeleteAsync`): nếu `IsPreset` → throw `InvalidOperationException("preset không xoá được")` (defense-in-depth, không chỉ ẩn UI).
4. **Nút "Tải model free"** (chỉ hiện trên provider `IsPreset`): gọi `SyncNowAsync(id)` → spinner mờ trong lúc chạy → thành công: toast/thông báo `providers.sync.success` (kèm số model) + cập nhật `LastModelSyncAt`; lỗi: hiện message lỗi `providers.sync.failed` ( transient).
5. **"Đã sync: {time}"** nhỏ trên card preset (i18n, null → không hiện).
6. Sửa tên/URL/Enabled/MaxConcurrent: giữ nguyên luồng hiện có (không đổi form).
7. Model list: hiển thị như hiện tại (badge manual/…) — **không đổi**.

## 9. Error handling

| Tình huống | Hành vi |
|---|---|
| Endpoint lỗi/timeout/parse fail khi periodic | log warning (`ILogger`), giữ list cũ, bỏ provider đó trong vòng sync |
| Endpoint lỗi khi manual | UI hiện `providers.sync.failed` + chi tiết (status/message), giữ list cũ |
| Fetch trả 0 model free | `FreeModelSyncException("empty result")` — KHÔNG xoá model cũ (guard §6.2.5) |
| Provider không match catalog (bị rename) | skip + log warning, coi như chưa sync (periodic); manual → lỗi rõ cho user |
| Provider `IsPreset` nhưng không Enabled | periodic bỏ qua; manual vẫn cho phép (user chủ động) |
| App đóng giữa chừng sync | CancellationToken hủy; DB đã commit giữ nguyên (transaction mỗi provider) |

Không có `catch {}` rỗng; mọi catch đều `LogWarning`/`LogError`.

## 10. i18n (Translations.cs — đủ 2 dict English + Vietnamese)

| Key | EN (mô tả) |
|---|---|
| `providers.badge.free` | Free |
| `providers.sync.now` | Load free models |
| `providers.sync.success` | Synced {0} free models |
| `providers.sync.failed` | Free model sync failed: {0} |
| `providers.sync.lastSync` | Synced: {0} |
| `providers.delete.preset` | Preset providers cannot be deleted |

(Kiểm tra parity qua `TranslationParityTests` — có sẵn.)

## 11. Security

- Không log API key; `Authorization`/`?key=` chỉ đưa vào request header, không vào message log.
- Catalog chỉ chứa URL công khai, không secret.
- Response từ endpoint bên thứ ba → chỉ đọc field cần thiết, không deserialize dư thừa (tránh gadget), timeout 30s chống treo.

## 12. NEEDS CONFIRM (cần user chốt trước/sau plan)

1. **[S1] Match catalog theo `Name`**: user đổi tên provider preset → mất link sync (skip + warning), không tạo cột `PresetKey`. OK? (Phương án thay thế: thêm cột `PresetKey` để rename vẫn sync.)

## 13. Testing

Unit (Core):

- `FreeModelDetectorTests` — mỗi `FreeDetectKind` với JSON fixture thật đã probe (OpenRouter pricing/suffix, OpenCode suffix+pricing null, AllFree); edge: pricing `"0.0"`, id `:free` nhưng pricing > 0, response rỗng.
- `FreeModelSyncMergeTests` — merge: giữ `IsManual=true`, xoá `IsManual=false` biến mất, upsert model mới, update metadata, **guard rỗng không xoá gì**; transaction fail → DB không đổi.
- `DbInitializerFreeProviderTests` — fresh DB → seed đủ 5, `IsPreset=true`, `Enabled=false`, 0 model; DB có sẵn (đổi tên 1 cái) → không nhân đôi, không ghi đè.
- `ProviderServiceDeletePresetTests` — xoá `IsPreset` → throw; xoá thường → OK.
- `FreeModelSyncServiceTests` — not-found / not-preset / HTTP 500 / timeout / rename-skip → đúng exception + không đụng DB.

Integration / worker:

- `FreeModelSyncIntegrationTests` — fake HTTP server trả fixture → `SyncProviderAsync` → assert DB (số model, IsManual, LastModelSyncAt); chạy sync lần 2 với fixture đổi → assert delete/upsert.
- Worker: `SyncNowAsync` trigger thủ công → service được gọi (mock), periodic tick gọi `SyncAllEnabledAsync` (tương tự test worker hiện có của LogRetentionWatchdog — xem pattern).

Gates (chuẩn AGENTS.md): `dotnet build` Core + TFM Windows + `dotnet test` toàn bộ (baseline **419/419**); i18n parity xanh.

## 14. Out of scope / deferred

- Đổi balancer/prefer-free (D1 option B/C) — nếu cần làm sau, spec riêng.
- Auto-seed API key free (mỗi provider có key public free) — không làm.
- UI thống kê "đã tiết kiệm được bao nhiêu" — không làm.
- Sync model cho provider không phải preset — không làm.

## 15. References

- OpenRouter models API: https://openrouter.ai/docs/overview/models
- OpenCode Zen: https://opencode.ai/docs/zen/ (+ opencode.ai/zen/v1/models probe)
- NVIDIA free tier: https://build.nvidia.com/models (FAQ "Are all models free to use?")
- Ollama cloud API: https://docs.ollama.com/api/tags + https://docs.ollama.com/cloud
- Pattern nội bộ: `LogRetentionWorker` (PeriodicTimer), `DbInitializer` (seed client-keys), `Model.IsManual`, `TranslationParityTests`.
