# Spec: Model capabilities (sửa metadata tay) + Chuẩn hoá URL `/v1`

- **Ngày:** 2026-09-27
- **Trạng thái:** Approved — đã implement (branch feat/model-capabilities-url)
- **Phạm vi:** Phase 2B-small — fix trên `feat/phase2-provider-mgmt` (sau khi merge Phase 2A), trước Phase 3 (Engine)
- **Spec liên quan:** [2026-09-26-provider-model-management-design.md](2026-09-26-provider-model-management-design.md) (§3.3 metadata chain), [2026-09-25-router-balancing-design.md](2026-09-25-router-balancing-design.md) (§ retry/watchdog)

## 1. Bối cảnh & root cause

Triệu chứng: cột **Metadata** trong expand panel `/providers` trống — không badge `ctx:`/Vision/Think cho model thật.

Điều tra (đã xác nhận bằng DB probe + live API call):

- DB: `ContextWindow=NULL, SupportsVision=0, SupportsThink=0` cho mọi model (schema đúng, fill hook chạy — model tạo sau Task 6).
- Bước 1 (endpoint): `GET {base}/v1/models/{id}` trên gateway thật (`unsloth.hkstudio.id.vn`) trả 200 với shape `id, object, created, owned_by, loaded, quant, display_name` — **không có** `context_window`/`supported_modalities` → trả null (đúng contract).
- Bước 2 (static catalog): chỉ match prefix `gpt-4o*`, `gpt-4.1*`, `o1`, `o3`, `claude-*`, `deepseek-*` → `unsloth/Qwen3.8-27B-GGUF` không match → null.
- → Chain best-effort hoạt động đúng thiết kế nhưng **không nguồn nào cover model của user**.

Kèm theo: `ProviderRequestFactory` ghép `BaseUrl.TrimEnd('/') + path` → base dán sẵn `/v1` (user hay paste) thành `/v1/v1/models` (bug thật, reproduce được).

## 2. Mục tiêu

1. User **sửa tay** capabilities (Context window, Vision, Think) cho mọi model — kể cả model lạ/không có trong catalog.
2. Endpoint parser đọc thêm field tên thay thế mà gateway phổ biến dùng.
3. Cột Metadata không còn trống trơn khi chưa có data.
4. BaseUrl có hoặc không có `/v1` đều cho ra request đúng — không phụ thuộc user nhớ cú pháp.

**Non-goals:** sửa tay JSON fields (`ThinkEfforts`, `InputModalities`, `OutputModalities`); mở rộng static catalog (dữ liệu ước lượng sai còn tệ hơn không có); auto re-fill sau khi clear metadata; migration data cho row cũ (parser/sửa tay tự xử lý).

## 3. Thiết kế

### 3.1 Sửa metadata inline (feature chính)

- Trong expand panel `/providers`, cột **Metadata** thêm nút ✏️ mỗi dòng model → dòng chuyển sang edit mode: ô number **Context window** + checkbox **Vision** + checkbox **Think** + nút ✓/✕.
- Chỉ 1 model edit tại 1 thời điểm (guard `_busy` single-flight, pattern `TestAsync` Task 8).
- Lưu qua service mới:

```csharp
// IModelService
Task UpdateCapabilitiesAsync(long modelId, int? contextWindow, bool supportsVision, bool supportsThink, CancellationToken ct = default);
```

  - `contextWindow` null = clear (để trống ô → xóa giá trị); range hợp lệ `1..10_000_000` (0/negative → ném `ArgumentOutOfRangeException` → UI bắt, toast i18n).
  - Unknown `modelId` → `KeyNotFoundException` (thống nhất contract service hiện hữu).
  - Persist + UI reload giữ expand state (pattern `ReloadKeepExpandAsync`).
- **Tương tác với fill chain:** fill chỉ ghi field còn NULL (spec §3.3 hiện hữu) → giá trị tay không bao giờ bị ghi đè khi fetch lại. Clear ctx → fill **không** tự chạy lại (chỉ chạy lúc add model) — chấp nhận, user tự nhập lại.

### 3.2 Mở rộng endpoint parser

`ProviderEndpointMetadataProvider` — không phá shape hiện tại, đọc thêm:

| Field | Nguồn | Ghi vào |
|---|---|---|
| `context_length` | OpenRouter | ContextWindow |
| `max_model_len` | vLLM | ContextWindow |
| `architecture.input_modalities` / `architecture.output_modalities` (mảng string) | OpenRouter | Input/OutputModalities + SupportsVision nếu có `"image"` |

- Ưu tiên ContextWindow: `context_window` > `context_length` > `max_model_len` (tie-break tường minh, test bao phủ).
- Gateway hiện tại vẫn trả field lạ → vẫn null → catalog → sửa tay (§3.1). Vòng an toàn không đổi.

### 3.3 Placeholder cột Metadata

- Khi model không có badge nào (không Manual, ctx null, vision/think false) → hiện ký hiệu `—` (language-neutral, không qua i18n) thay vì ô trống.
- Badge có gì hiện nấy, không đổi logic render hiện tại.

### 3.4 Chuẩn hoá URL `/v1`

- **Tại save** (`ProviderService.CreateAsync`/`UpdateAsync`): canonicalize `BaseUrl` = `TrimEnd('/')`, rồi lặp strip hậu tố `/v1` cho đến khi hết (xử lý cả base từng bị lưu `.../v1/v1`) — VD `https://x.com/v1/` → `https://x.com`; `https://x.com/api/v1` → `https://x.com/api` — lưu base không kèm v1.
- **Tại compose** (`ProviderRequestFactory.Create`): idempotent — sau canonicalize, nếu base vẫn endswith `/v1` và path bắt đầu `/v1` → không nhân đôi. Fix runtime cho row lưu trước fix, không cần migration.
- Chỉ strip đúng hậu tố `/v1` — không đoán `/api/v1`, `/openai/v1` hay path khác.
- Áp dụng đồng nhất cho mọi path ghép: `/v1/models` (fetch), `/v1/models/{id}` (metadata).

## 4. i18n

Keys mới, EN/VI identical sets (giữ parity 149/149 hiện có + tăng):

| Key | EN | VI |
|---|---|---|
| `models.action.editCapabilities` | Edit | Sửa |
| `models.capabilities.title` | Capabilities | Khả năng |
| `models.capabilities.contextWindow` | Context window | Context window |
| `models.capabilities.vision` | Vision | Vision |
| `models.capabilities.think` | Think | Think |
| `models.error.contextRange` | Context window must be 1–10,000,000 | Context window phải trong khoảng 1–10.000.000 |
| `models.msg.capabilitiesSaved` | Capabilities saved | Đã lưu khả năng |

## 5. Testing (TDD)

- `IModelService.UpdateCapabilitiesAsync`: set mới / overwrite / clear (`null`) / range sai → `ArgumentOutOfRangeException` / unknown id → `KeyNotFoundException` / persist qua DB reload.
- `ProviderEndpointMetadataProvider`: `context_length`, `max_model_len`, tie-break precedence, `architecture.*modalities` parse + vision từ `"image"`, shape sạch → null (giữ test cũ).
- `ProviderRequestFactory`: base có `/v1` / không `/v1` / trailing slash / path explicit `/v1/models/{id}` → URL đúng, không đôi v1.
- `ProviderService.CreateAsync`/`UpdateAsync`: canonicalize save.
- **Gates:** `dotnet test` (100 cũ + mới), app build `net10.0-windows10.0.19041.0` 0W/0E, `npm run build` exit 0.

## 6. Kết quả kiểm chứng

| Phase | Trạng thái |
|---|---|
| Unit test + build + npm | Thực thi khi implement |
| Manual checklist §5 Phase 2A | Controller tự chạy trên app thật (provider Unsloth trong DB) trước khi merge 2A |
