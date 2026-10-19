# Spec: OpenCode v2 — trang công cụ sinh config + nâng cấp sync model free

- **Ngày:** 2026-10-19
- **Trạng thái:** Draft — chờ user review
- **Pipeline:** brainstorming → spec → plan → SDD
- **Branch dự kiến:** `feat/opencode-tools-page`

## 1. Mục tiêu

Hai phần liên quan chặt (chung một pipeline dữ liệu):

1. **Nâng cấp sync model free cho preset "OpenCode Free"**: hiện chỉ lấy model có hậu tố `-free` từ `https://opencode.ai/zen/v1/models` → **thiếu** các model free thật của opencode v2 (vd `big-pickle`, `nemotron-3-ultra-free`), và **không có** capabilities (context/vision/think/tools/limit). Chuyển sang nguồn chuẩn mà chính opencode v2 dùng: **`https://models.opencode.ai/api.json`** (public).
2. **Trang công cụ mới ("Tools")**: sinh **config opencode v2** (đúng schema `Config.Providers[...].models` của opencode v2) trỏ về proxy của app, để opencode CLI dùng các model của app (free + trả phí) qua một endpoint.

## 2. Non-goals (rõ ràng KHÔNG làm)

- **Không đổi BaseUrl** của preset OpenCode Free (`https://opencode.ai/zen`) — fingerprint gate (`OpenCodeFreeFingerprint`) giữ nguyên; route v2 (`/inference/openai/v1`) cũng qua được gate nhưng không cần đổi.
- **Không sửa `/v1/models`** payload hiện có (giữ `context_window/vision/audio/video/thinking`).
- **Không** ghi trực tiếp file config vào thư mục opencode của user — chỉ copy/tải file, user tự merge.
- **Không** auto-sync capabilities cho provider khác ngoài OpenCode Free.
- **Không** thêm search/filter cho danh sách model ở trang Tools (v1).
- **Không** đổi balancer/COMBO/logic chọn model — các cột mới chỉ được *ghi thêm*.

## 3. Quyết định chốt (từ brainstorming 2026-10-19)

| # | Nguồn | Quyết định |
|---|---|---|
| U1 | User | Model free: **bỏ `cost`** khi sinh config; lấy mọi field khác |
| U2 | User | **Bỏ luôn `disabled`** |
| U3 | User | Free = model **cost = 0** (không chỉ hậu tố `-free`) |
| U4 | User | Trang công cụ sinh config **cho opencode dùng**; baseURL trỏ về **app này** (proxy localhost) |

Call của tôi (chưa phải user chốt — cần user check khi review):

| # | Call | Lý do |
|---|---|---|
| C1 | Nguồn sync **chỉ catalog** (`models.opencode.ai`), bỏ zen list cho OpenCode Free | Catalog là nguồn chuẩn opencode v2 dùng; zen list đã cũ (bằng chứng §4). **Hệ quả: `jev-1.13-free` (có trong zen list, không có trong catalog) sẽ bị xoá khi merge vì không còn trong tập free**; muốn giữ thì thêm tay (IsManual). |
| C2 | Provider id trong config = **`router-balancing`** (cố định), name = "Router Balancing" | Đơn giản; user đổi được trong file |
| C3 | `apiKey` trong config: **mặc định không có field**; UI cho nhập tay hoặc tạo client key mới | Proxy mở khi chưa bật client key (middleware hiện tại); nếu proxy yêu cầu key thì user tạo key ngay trên trang |
| C4 | Model mặc định được tick: **các model đang Enabled của preset OpenCode Free**; provider khác để user tự tick | Đúng nhu cầu "model free cho opencode" |
| C5 | `limit`/`capabilities`: **chỉ ghi field đã biết** (null → bỏ field, để opencode dùng default) | Trung thực với dữ liệu; tránh khoá cứng sai |
| C6 | Không đổi `$schema` trong file sinh ra (không emit) | Tránh trỏ vào schema v1 cũ |

## 4. Cách opencode v2 lấy model free (bằng chứng — đọc source + probe live 2026-10-19)

Nguồn: repo `anomalyco/opencode` (nhánh `dev`), các file `packages/core/src/models-dev.ts`, `packages/core/src/model.ts`, `packages/schema/src/model.ts`, `packages/schema/src/config/provider.ts`, `packages/schema/src/config.ts`.

1. **Catalog**: v2 core fetch `https://models.opencode.ai/api.json` (`defaultSource` trong `models-dev.ts`), cache + refresh mỗi 5 phút; CLI nhúng snapshot cùng shape (`packages/core/src/models-dev/snapshot.txt`). Public, không cần auth (probe 200, 5.8MB, 232 providers; provider `opencode` = 127 model).
2. **"Free"**: model thuộc provider `opencode` và **`cost.input == 0`** (`preferFree` trong `core_model.ts`); signed-out chỉ bật free; thứ tự ưu tiên default = `PREFERRED_FREE = [big-pickle, deepseek-v4.1-flash-free, glm-5.3-flash-free, muse-spark-1.3-contributor-free]`.
3. **`status`**: catalog có `status: alpha|beta|deprecated` (mặc định active). Model cost=0 **deprecated đã chết** (probe: 400 "Model is unavailable" / 410 ModelDeprecated).
4. **zen `/v1/models` là list CŨ** — model active không có trong list vẫn gọi được; bảng probe (fingerprint hiện tại của app, cả `/zen/v1` lẫn `/inference/openai/v1` đều qua gate):

| model | catalog | zen list | probe live |
|---|---|---|---|
| `big-pickle` | cost 0, active | có | **200** |
| `nemotron-3-ultra-free` | cost 0, active | **không** | **200** |
| `grok-code`, `glm-5-free`, `deepseek-v4-flash-free`… (34 model cost=0) | deprecated | không | 400 "Model is unavailable" |
| `muse-spark-1.3-contributor-free` | deprecated | không | 410 ModelDeprecated |
| `jev-1.13` (trả phí) | — | có | 401 |

5. **Kết quả rule mới** (catalog cost=0, output=0, `status != deprecated`):
   - Hiện tại app có **9** model (zen ∩ hậu tố `-free`).
   - Rule mới cho **10** model: 9 cũ (trừ `jev-1.13-free` — call C1) + **`big-pickle`** + **`nemotron-3-ultra-free`**.

6. **Config v2** (schema thật, `packages/schema/src/config/provider.ts` + `config.ts`):
   - Root: `{ "providers": { "<id>": ProviderInfo } }` (record, không phải array).
   - `Config.Provider.Info`: `{ canonical?, name?, env?[], package?, compatibility?, settings?, headers?, body?, models? }`.
   - `Config.Model`: `{ modelID?, family?, name?, releaseDate?, compatibility?, package?, settings?, headers?, body?, capabilities?, variants?, cost?, disabled?, limit? }`.
   - `capabilities` (partial): `{ tools?, input?, output?, temperature?, reasoning? }`; `reasoning` = `{supported:false}` | `{supported:true, toggle?, effort?: string[], budget?}`.
   - `limit` (partial): `{ context?, input?, output? }`.
   - `cost`/`disabled` optional → **bỏ** theo U1/U2.
   - Độ sâu merge: field bỏ trống → fallback capabilities của base model → default (tools=true, input=[text,image], output=[text]).

## 5. Data model + Migration

`AddModelToolsOutputLimit`:

```csharp
// Model.cs
/// <summary>Model hỗ trợ tool/function calling; null = chưa rõ (chỉ có khi sync từ catalog).</summary>
public bool? SupportsTools { get; set; }

/// <summary>Giới hạn token output; null = chưa rõ.</summary>
public int? MaxOutputTokens { get; set; }
```

- Command: `dotnet ef migrations add AddModelToolsOutputLimit --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations` (pattern hiện có).
- Quy ước format 2 cột string có sẵn (theo XML doc `ModelMetadata`): `ThinkEfforts`, `InputModalities`, `OutputModalities` = **JSON array** (`["low","high"]`, `["text","image"]`). Riêng `ThinkEfforts` thêm ước lệ: phần tử literal **`"toggle"`** = model có công tắc think (không phải effort levels). Cột mới `SupportsTools` dùng `bool?` (khác các bool cũ `false = chưa rõ`) để phân biệt "biết là false" vs "chưa rõ".

## 6. Nâng cấp sync — OpenCode Free

### 6.1 FreeProviderCatalog

```csharp
public sealed record Entry(string DisplayName, string BaseUrl, FreeDetectKind DetectKind, string? CatalogUrl = null);

new("OpenCode Free", "https://opencode.ai/zen", FreeDetectKind.OpenCodeCatalog, "https://models.opencode.ai/api.json"),
```

`FreeDetectKind` thêm giá trị `OpenCodeCatalog` (detect theo catalog, không theo OpenAI shape).

### 6.2 FreeModelDetector — parser mới (pure function)

`ParseOpenCodeCatalog(string json)` → `IReadOnlyList<FreeModelInfo>`:

1. Root `{"opencode": {"models": { "<id>": {...} } }}` — thiếu key → `FreeModelSyncException` (giống guard `data` array hiện có).
2. Với mỗi model: free khi `cost.input == 0 && cost.output == 0` **và** `status` khác `"deprecated"` (status vắng = active).
3. `FreeModelInfo` mở rộng (record — thêm param optional, tương thích ngược cho 3 kind cũ):
   `(ModelId, DisplayName, ContextWindow, bool? SupportsTools, bool? SupportsVision, bool? SupportsThink, string? ThinkEfforts, string? InputModalities, string? OutputModalities, int? MaxOutputTokens)`.
4. Map từ entry catalog (xem §7.2); field thiếu → null (không đoán).

### 6.3 FreeModelSyncService

- Nhánh mới: `entry.DetectKind == OpenCodeCatalog` → GET **absolute URL** `entry.CatalogUrl` (không auth, không fingerprint; vẫn set `ProxyTarget.Current` như hiện có để đi đúng proxy của provider), named client `free-model-sync` (timeout 30s), parse bằng `ParseOpenCodeCatalog`.
- Merge (giữ nguyên semantics §6.2.6 spec provider-free — delete model non-manual biến mất, giữ manual, guard rỗng không xoá, 1 SaveChanges):
  - Insert/update thêm các field capabilities từ `FreeModelInfo` (chỉ ghi khi non-null).
  - **Không** đụng tới model `IsManual`.
- Provider khác (OpenRouter/NVIDIA/Ollama) giữ nguyên đường cũ.
- BaseUrl/fingerprint chat giữ nguyên (non-goal §2).

## 7. Trang Tools

### 7.1 Điều hướng + UI

- `router-balancing/Components/Pages/Tools.razor`, `@page "/tools"`.
- `NavMenu.razor`: mục "Tools" (`nav.tools`) sau Combos.

Các khối trên trang:

1. **Trạng thái proxy**: `IProxyHost.Port` + `IsRunning` → hiển thị `http://127.0.0.1:{port}/v1`; nếu proxy chưa chạy → cảnh báo (vẫn cho sinh config với port từ settings, kèm ghi chú — call).
2. **Client key (optional)**: nếu proxy chưa bật key nào → info "không cần key"; ngược lại có ô nhập key + nút "Tạo key cho OpenCode" (gọi `IClientKeyService.CreateAsync`, hiện plaintext 1 lần, tự điền vào ô). Ô nhập → gồm vào `settings.apiKey` khi khác rỗng. Cảnh báo "file config chứa key — giữ kín".
3. **Chọn model**: bảng nhóm theo provider (chỉ model `Enabled`), checkbox mặc định tick = model của preset OpenCode Free; nút "Chọn nhóm free" / "Bỏ chọn". Hiển thị context + badge caps (vision/think/tools).
4. **Preview + hành động**: `<pre>` JSON, nút **Sao chép** (`IClipboardService` có sẵn) + **Tải file** (JS blob download — thêm `wwwroot/rb-tools.js` nhỏ, hàm `rbTools.download(filename, text)`; tên gợi ý `opencode-router-balancing.jsonc`) + hướng dẫn merge vào `opencode.json(c)` của opencode.

### 7.2 Generator — `IOpenCodeConfigGenerator` (Core, pure + 1 service đọc DB)

Service: `GenerateAsync(selectedModelIds, baseUrl, apiKey?)` → string JSON (indent 2). Mapping:

| Nguồn (DB Model) | config v2 | Ghi chú |
|---|---|---|
| `ModelId` (key) | key của `models` | |
| `DisplayName ?? ModelId` | `name` | |
| `SupportsTools` (`bool?`) | `capabilities.tools` | null → **bỏ field** (opencode default true) |
| `InputModalities` (JSON array) | `capabilities.input` | null → `["text"]`; có vision mà thiếu array → `["text","image"]` |
| `OutputModalities` | `capabilities.output` | null → `["text"]` |
| `SupportsThink` + `ThinkEfforts` | `capabilities.reasoning` | false → bỏ; true → `{supported:true}` + `toggle:true` nếu có `"toggle"` + `effort:[...]` cho các giá trị còn lại |
| `ContextWindow` | `limit.context` | null → bỏ field |
| `MaxOutputTokens` | `limit.output` | null → bỏ field |
| — | `cost`, `disabled` | **không emit** (U1/U2) |

Provider entry: `package = "@opencode/ai/providers/openai-compatible"`, `settings.baseURL = baseUrl` (+ `apiKey` nếu có), `name = "Router Balancing"`, id = `router-balancing` (C2).

Ví dụ output (2 model tiêu biểu):

```jsonc
{
  "providers": {
    "router-balancing": {
      "name": "Router Balancing",
      "package": "@opencode/ai/providers/openai-compatible",
      "settings": { "baseURL": "http://127.0.0.1:8317/v1" },
      "models": {
        "big-pickle": {
          "name": "Big Pickle",
          "capabilities": { "tools": true, "input": ["text"], "output": ["text"], "reasoning": { "supported": true } },
          "limit": { "context": 200000, "output": 32000 }
        },
        "deepseek-v4.1-flash-free": {
          "name": "DeepSeek V4.1 Flash Free",
          "capabilities": { "tools": true, "input": ["text", "image"], "output": ["text"],
                            "reasoning": { "supported": true, "effort": ["low", "high", "max"] } },
          "limit": { "context": 200000, "output": 32000 }
        }
      }
    }
  }
}
```

## 8. Error handling

| Tình huống | Hành vi |
|---|---|
| Catalog fetch lỗi/timeout/parse fail (periodic) | log warning, giữ list cũ, bỏ provider trong vòng sync (pattern hiện có) |
| Catalog fetch lỗi (manual) | toast lỗi `tools`/`providers.sync.failed` + chi tiết, giữ list cũ |
| Catalog trả 0 free | `FreeModelSyncException` — KHÔNG xoá list (guard hiện có) |
| Sinh config khi chưa chọn model | nút Generate disable + hint |
| Proxy chưa chạy | cảnh báo rõ; vẫn cho generate (dùng port settings) |
| Tạo client key lỗi | toast lỗi, không điền ô |
| Copy thất bại (WebView từ chối clipboard) | toast như `IClipboardService` hiện có |

Không `catch {}` rỗng; mọi catch `LogWarning`/`LogError`.

## 9. i18n (Translations.cs — EN + VI, parity test có sẵn)

`nav.tools`, `tools.title`, `tools.proxy.status`, `tools.proxy.stopped`, `tools.baseUrl`, `tools.apiKey.label`, `tools.apiKey.hint`, `tools.apiKey.notNeeded`, `tools.apiKey.create`, `tools.apiKey.created`, `tools.apiKey.warning`, `tools.models.title`, `tools.models.freeGroup`, `tools.selectFree`, `tools.clearAll`, `tools.preview`, `tools.copy`, `tools.copied`, `tools.download`, `tools.empty`, `tools.hint.merge`.

## 10. Security

- `apiKey` (nếu user nhập) chỉ xuất hiện trong preview/file tải — không log, không lưu DB ngoài bảng `ClientKeys` (đã hash).
- Fetch catalog: public, không gửi key/secret; không deserialize field ngoài whitelist; timeout 30s.
- Không đổi auth middleware/proxy.

## 11. Testing

- `OpenCodeCatalogParserTests` (mới): fixture rút từ snapshot thật — free theo cost, loại deprecated, thiếu field → null, root thiếu `opencode.models` → exception, `big-pickle`/`nemotron-3-ultra-free` có mặt, `jev-1.13-free` không có.
- `FreeModelDetectorTests`: giữ nguyên cho 3 kind cũ.
- `FreeModelSyncServiceTests`/`MergeTests`: nhánh catalog — URL tuyệt đối, capabilities ghi đúng vào cột (kể cả `SupportsTools`, `MaxOutputTokens`), manual không bị đụng, guard rỗng, provider khác không đổi.
- `OpenCodeConfigGeneratorTests` (mới): mapping đủ bảng §7.2 + edge (reasoning toggle/effort/mix, modalities null→["text"], vision-mà-thiếu-array, tools null→bỏ field, limit null→bỏ field, apiKey optional, JSON đúng schema key).
- Gates: `dotnet build` (Core + TFM Windows) + `dotnet test` toàn bộ; i18n parity xanh.
- Smoke tay: sync OpenCode Free → 10 model; generate config → dán vào opencode → model hiện + gọi được (user kiểm; nếu opencode báo thiếu `apiKey` khi proxy mở → điền placeholder — ghi chú UI).

## 12. Out of scope / deferred

- Ghi trực tiếp config vào thư mục opencode; auto-merge file `opencode.json` có sẵn.
- Search/filter + sắp xếp danh sách model ở trang Tools.
- Đổi BaseUrl zen → inference; bỏ fingerprint khi không cần nữa.
- `/v1/models` thêm `tools`/`max_output_tokens`.
- Sync capabilities cho provider khác.

## 13. References

- Repo opencode (v2): `anomalyco/opencode` — `packages/core/src/models-dev.ts` (`defaultSource`), `packages/core/src/model.ts` (`preferFree`, `PREFERRED_FREE`), `packages/schema/src/model.ts`, `packages/schema/src/config.ts`, `packages/schema/src/config/provider.ts`.
- Catalog v2: `https://models.opencode.ai/api.json` (snapshot: `packages/core/src/models-dev/snapshot.txt`).
- Probe live 2026-10-19: zen vs `inference/openai/v1`, kết quả ở §4 (script trong temp: `zen_probe*.js`).
- Nội bộ: spec `2026-10-01-provider-free-design.md` (§6 sync semantics), `2026-09-14`-era sidecar/fingerprint spec, `IClientKeyService`, `IProxyHost.Port`, `IClipboardService`, `ModelMetadata` (convention JSON array).
