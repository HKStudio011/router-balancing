# Spec: Audio/Video capabilities cho model + Combo capabilities tính tự động

- **Ngày:** 2026-10-14
- **Trạng thái:** Approved (brainstorming + mockup visual companion đã chốt)
- **Scope:** Mở rộng capability model — thêm audio/video cho model (auto-detect + chỉnh tay), expose capabilities qua `/v1/models` (nested object), combo có capabilities tính tự động (AND / min-ctx / đệ quy).

## Bối cảnh

App hiện có capability model cho `Model`: `ContextWindow`, `SupportsVision`, `SupportsThink` (+ `ThinkEfforts`, `InputModalities`, `OutputModalities` JSON). UI Providers cho chỉnh inline 3 field (ctx/vision/thinking); auto-detect chạy qua metadata chain (`IModelMetadataProvider`: ProviderEndpoint → StaticCatalog) điền field còn trống.

Thiếu:

1. **Audio/video** — LLM multimodal ngày càng có audio (speech-in, speech-out) và video input; client cần biết để chọn model/combo đúng.
2. **Expose capabilities cho client** — `/v1/models` (ProxyApp.cs:408) hiện chỉ trả `id/object/created/owned_by`; client (opencode...) không thấy model/combo hỗ trợ gì.
3. **Combo capabilities** — combo là model ảo client chọn qua trường `model`; hiện không có cách nào biết combo "kế thừa" được gì từ các member.

Quyết định user đã chốt:

- Capabilities **chỉ informational** — không bao giờ block request.
- Client contract: **nested `"capabilities"` object** trong mỗi entry `/v1/models`.
- Combo capabilities **chỉ tính tự động** — bool = AND mọi member, ctx = min, bất kỳ member nào unset → unset; áp đệ quyết combo lồng.

## Non-goals

- Không đổi selection/dispatcher — capabilities không tham gia routing, không block request gửi đi.
- Không thêm endpoint mới (`/v1/files`, moderation...); chỉ đọc được qua `/v1/models` + UI.
- Không đổi `FreeModelSyncService` — merge catalog free chỉ set `ContextWindow`; audio/video không bị sync ghi đè.
- Không thêm audio/video entries vào static catalog ở thay đổi này (data bổ sung sau; logic derive đã sẵn sàng khi catalog có `InputModalities` chứa "audio"/"video").
- Không expose `ThinkEfforts`/`InputModalities`/`OutputModalities` thô qua `/v1/models` — chỉ 5 trường chuẩn hóa trong `capabilities`.

---

## Phần A — Model entity + Migration

### A1 — Hai cột mới

`Model` (Domain/Entities/Model.cs) thêm:

```csharp
public bool SupportsAudio { get; set; }
public bool SupportsVideo { get; set; }
```

Cùng convention `SupportsVision`: `false` = không hỗ trợ / chưa rõ (fill chỉ viết `true`; user chỉnh tay được cả hai chiều).

### A2 — Migration `AddModelAudioVideoCapabilities`

- Thêm 2 cột `SupportsAudio`, `SupportsVideo` — `INTEGER NOT NULL DEFAULT 0` (SQLite).
- Data cũ không đổi (false = chưa rõ, đúng semantics).
- Migration test theo pattern `AddStatsJournalColumnsMigrationTests` (Migrate(PreviousId) → seed → Migrate(TargetId) → assert cột + default; Down drop cột).

---

## Phần B — Auto-detect (metadata chain)

### B1 — Derive từ `InputModalities`

`IModelMetadataProvider` chain **không đổi interface** — cả ProviderEndpoint (`{base}/v1/models/{id}` trả `input_modalities`) lẫn StaticCatalog đã trả `InputModalities` JSON.

`ModelMetadataService.TryFillAsync` (ModelMetadataService.cs:87-96) thêm 2 nhánh derive, chạy cùng block fill hiện tại:

```csharp
// meta.InputModalities là JSON array ["text","image",...] — parse 1 lần,
// contains "audio"/"video" (OrdinalIgnoreCase) → set flag (chỉ khi đang false).
```

- Chỉ ghi `false → true` (không bao giờ set về false — "false = chưa rõ" như vision).
- JSON hỏng/null → bỏ qua (không throw, fill là best-effort).
- Catalog entries thêm audio/video về sau chỉ cần đưa `InputModalities` đúng — không sửa code.

### B2 — `FreeModelSyncService` không đổi

Merge (FreeModelSyncService.cs:88-117) chỉ cập nhật `ContextWindow`/`DisplayName`/`Enabled` — không chạm vision/think, sẽ không chạm audio/video.

---

## Phần C — Edit capabilities (service + UI Providers)

### C1 — `IModelService.UpdateCapabilitiesAsync`

Ký hiệu mở rộng (IModelService.cs:40, ModelService.cs:217):

```csharp
Task UpdateCapabilitiesAsync(long modelId, int? contextWindow,
    bool supportsVision, bool supportsThink,
    bool supportsAudio, bool supportsVideo,
    CancellationToken ct = default);
```

Validate ctx giữ nguyên (1..10_000_000; null = clear). Test cập nhật theo `ModelServiceTests.UpdateCapabilities_*`.

### C2 — UI Providers.razor

- **Badge hàng model** (Providers.razor:285-297): thêm `🔊 audio` (variant `Info`), `🎬 video` (variant `Neutral`) — chỉ render khi true, sau badge vision/think.
- **Form edit** (`BeginEditCapabilities`/`SaveCapabilitiesAsync`, Providers.razor:1087-1141): thêm 2 checkbox `_editAudio`, `_editVideo`, load từ model khi mở form, gửi kèm khi save; toast/error handling giữ nguyên.
- Điều kiện "chưa auto-detect được gì" (Providers.razor:297) mở rộng: thêm `&& !m.SupportsAudio && !m.SupportsVideo`.
- Localization: thêm key `models.badge.audio`, `models.badge.video` vào resource hiện có.

---

## Phần D — Contract `/v1/models` (nested capabilities)

### D1 — Shape

Mỗi entry (model lẫn combo) thêm:

```json
"capabilities": {
  "context_window": 128000,
  "vision": true,
  "audio": false,
  "video": false,
  "thinking": true
}
```

| Field | Model (direct) | Combo |
|---|---|---|
| `context_window` | `int \| null` (null = chưa rõ) | `int \| null` (tính D2) |
| `vision/audio/video/thinking` | `bool` luôn có (false = chưa rõ theo convention entity) | `bool \| null` (null = chưa rõ — tính D2) |

### D2 — Combo capability computation

Service mới `IComboCapabilityCalculator` (Core.Combos) — tính **on-the-fly**, không lưu DB (không stale, không cần sync trigger):

```csharp
public sealed record ComboCapabilities(
    int? ContextWindow, bool? Vision, bool? Audio, bool? Video, bool? Thinking);

public interface IComboCapabilityCalculator
{
    Task<IReadOnlyDictionary<long, ComboCapabilities>> ComputeAllAsync(CancellationToken ct = default);
}
```

Thuật toán (một lần load toàn bộ graph combos + items + models, memoize theo combo Id):

- **Member = model:** lấy giá trị row (bool nguyên vẹn, ctx nullable).
- **Member = combo con:** lấy giá trị đã tính (đệ quy).
- **Bool (vision/audio/video/thinking):** AND tất cả member — mọi member đều `true` → `true`; bất kỳ `false` → `false`; còn lại (có `null`) → `null`.
- **Ctx:** min các giá trị khác null; bất kỳ member nào ctx `null` → toàn bộ combo ctx `null`.
- **Combo rỗng (0 item):** toàn bộ field `null`.
- **Chống cycle:** visited set theo combo Id — edge quay lại bị skip (data DB hợp lệ là DAG; guard phòng thủ, không throw).

### D3 — Endpoint `/v1/models` (ProxyApp.cs:408-441)

- Query models: thêm `capabilities` object thẳng từ row (không qua calculator).
- Query combos: giữ list row + gọi `ComputeAllAsync` một lần, gắn `capabilities` (toàn bộ field nullable) — serialize `null` tường minh (không omit) để client phân biệt "thừa kế false" vs "không rõ".

### D4 — Combos.razor — cột "Khả năng" read-only

- Thêm cột sau "Items": badge nhóm per combo từ `ComputeAllAsync` (fetch 1 lần khi load trang).
- Hiển thị: `ctx: 128K` (hoặc `ctx: ?` grey khi null) + badge từng capability khi `true` + `✕` suffix grey khi `false` (vd `👁 ✕`); `null` → không render badge (không rõ ≠ không hỗ trợ) — tooltip `title` trên nhóm badge giải thích nguồn: "AND mọi member · ctx = min · member chưa rõ → combo chưa rõ".
- Không form sửa (chỉ đọc) — mockup đã chốt.
- Localization: key `combos.col.capabilities` + chuỗi tooltip.

---

## Phần E — Tests

| Test | Nội dung |
|---|---|
| `ModelServiceTests.UpdateCapabilities_*` | audio/video persist; ctx clear giữ nguyên |
| `ModelMetadataServiceTests` | derive audio/video từ `InputModalities` chứa "audio"/"video"; JSON hỏng bỏ qua; true cũ không bị overwrite |
| `ComboCapabilityCalculatorTests` (mới) | AND bool (true/mixed/null); min-ctx; null propagation; combo rỗng; combo lồng 2 cấp; cycle guard skip |
| `ProxyAppChatIntegrationTests.Models_*` | model entry có `capabilities` object đủ 5 field; combo entry có `capabilities` nullable tính từ member |
| Migration test mới | cột + default + Down |

## Phần F — Trình tự triển khai (gợi ý)

1. Entity + migration + migration test.
2. `UpdateCapabilitiesAsync` + service test + Providers.razor (badge + form).
3. Derive audio/video trong `ModelMetadataService` + test.
4. `ComboCapabilityCalculator` + test (core logic, không phụ thuộc UI).
5. `/v1/models` expose + integration test.
6. Combos.razor cột capabilities + localization.
