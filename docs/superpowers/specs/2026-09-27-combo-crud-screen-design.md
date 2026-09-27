# Spec: Combo CRUD Screen (Router panel)

- **Ngày:** 2026-09-27
- **Trạng thái:** Approved (user "ok" 2026-09-27)
- **Phạm vi:** Phase 2C (Bloc B) — sau Phase 2B (capabilities/URL), trước Đa tài khoản provider & Phase 3 (Engine)
- **Spec liên quan:** [2026-09-25-router-balancing-design.md](2026-09-25-router-balancing-design.md) §8 (UI), §94-95 (entities), §9 (UI system), §299 (Phase 5)

## 1. Bối cảnh

Phase 1 đã dựng entity `Combo`/`ComboItem`, enum `ComboMode`, bảng DB (unique index `Combo.Name`, FK cascade model→item, restrict combo→combo) nhưng **chưa có** service, nav link, hay page. Spec gốc §8 đặt Combo CRUD ở Phase 5 (sau Engine) — user chọn làm sớm phần CRUD vì không phụ thuộc Engine.

## 2. Mục tiêu & scope (đã chốt với user)

1. **Chỉ CRUD screen**: list + form modal (Name, Mode, items có thứ tự) + cycle check + delete warning + nav link.
2. **Nesting + cycle check làm luôn**: item trỏ Model hoặc Combo con; cycle bị chặn lúc Save (unit test được, không cần Engine).

**Non-goals:** dispatcher/nested resolution runtime, `/v1/models` trả combo, combo test-before-save, cảnh báo "Y route" (chưa có route), combo status active/total runtime.

## 3. Service contract & validation

Namespace mới `RouterBalancing.Core.Combos` (thư mục `src/RouterBalancing.Core/Combos/`), đăng ký DI trong `MauiProgram` theo pattern `ProviderService`.

```csharp
public interface IComboService
{
    Task<IReadOnlyList<Combo>> ListAsync(CancellationToken ct = default);   // Include Items, sắp theo Name
    Task<Combo> CreateAsync(ComboDraft draft, CancellationToken ct = default);
    Task UpdateAsync(long id, ComboDraft draft, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetReferencingComboNamesAsync(long id, CancellationToken ct = default);
}
```

- **`ComboDraft`**: mutable record (giống `ProviderDraft`, phục vụ Blazor `@bind`): `string Name`, `ComboMode Mode`, `List<ComboItemDraft> Items` — mỗi item `long? TargetModelId` **XOR** `long? TargetComboId`. `Position` gán `0..n-1` theo thứ tự list lúc save (thay thế toàn bộ items khi Update).
- **Validation** ném `ComboValidationException(ComboValidationError Code)` — enum: `DuplicateName`, `EmptyItems`, `CycleDetected`, `InvalidItemTarget`, `TargetNotFound`, `NameTooLong`. UI bắt theo `Code` → toast i18n `combos.error.*`.
- **Unknown id** (Update/Delete không thấy) → `KeyNotFoundException` (nhất quán contract hiện hữu).
- Rules: Name trim, bắt buộc, ≤200 ký tự, unique (index DB là backstop); Name rỗng (sau trim) → `ArgumentException` (nhất quán `ThrowIfNullOrWhiteSpace` của repo — UI `CanSave` chặn trước nên đường này khó tới); ≥1 item; mỗi item đúng 1 target; `TargetModelId`/`TargetComboId` phải tồn tại (pre-check, không để FK nổ).
- **Cycle check**: DFS từ mọi `TargetComboId` đã chọn trên graph combo hiện có (adjacency: combo → các combo con trỏ tới nó) — nếu chạm id đang sửa → `CycleDetected`. `CreateAsync` không thể cycle (id mới chưa được combo nào trỏ tới). Self-reference (`TargetComboId == id`) bị bắt ngay.
- **Delete**: UI gọi trước `GetReferencingComboNamesAsync` → nếu có tên → ConfirmDialog kèm warning; `DeleteAsync` cũng tự chặn khi còn reference (FK Restrict là backstop). Cascade model→item (Phase 1) được giữ nguyên — combo có thể còn 0 item sau khi xóa model; save lại yêu cầu ≥1.

## 4. UI

### Nav

`NavMenu.razor`: thêm `NavLink href="combos"` — `@L["nav.combos"]`, đặt giữa Providers và Logs.

### Page `Combos.razor` (`@page "/combos"`)

Pattern `Providers.razor`: `@implements IDisposable`, inject `IComboService`, `LocalizationService L`, `ToastService Toast`, `ILogService Log`; guard `_busy` single-flight.

- Header: tiêu đề + nút **＋ Add** (mở modal rỗng).
- Bảng: **Name | Mode badge | Items (N) | Actions (✏️ 🗑)** — Mode badge `RoundRobin` → `BadgeVariant.Info`, `Fallback` → `BadgeVariant.Warning`.
- Empty → `EmptyState`.
- Không phân trang (YAGNI — combo ít; bảng cuộn `overflow-x-auto`).

### Modal form (tạo/sửa)

- **Name** (`input text`, required, ≤200, `@bind` oninput), **Mode** radio `RoundRobin | Fallback`.
- **Items list** — mỗi dòng:
  - `select` loại **Model**: `<optgroup label="{Provider.Name}">` chứa model `Enabled` của từng provider (data lấy từ `IProviderService.ListAsync` — đã có); hoặc loại **Combo**: combo khác, UI loại trừ self + tổ tiên (best effort; service vẫn là authoritative).
  - Nút **✕** xóa dòng; 2 nút **＋ Add model** / **＋ Add combo** append dòng mới (dòng mới có select trống tới khi user chọn).
  - **↑ ↓** đổi thứ tự (không drag) — có ý nghĩa với mode Fallback.
  - dòng chưa chọn target bị **bỏ qua** lúc save; nếu không còn dòng nào có target → lỗi `emptyItems`.
- **Lưu**: gọi `CreateAsync`/`UpdateAsync` → toast `combos.msg.saved` → reload list; bắt `ComboValidationException` → toast `combos.error.{Code}` (lowerCamel), `KeyNotFoundException` → `dashboard.msg.failed`.
- **Xóa**: ✕/🗑 → `GetReferencingComboNamesAsync` → có reference: ConfirmDialog `combos.delete.referenced` + `string.Join(", ", names)` (không cần interpolation — i18n flat key); không: ConfirmDialog `combos.delete.confirm`. Thành công → toast `combos.msg.deleted`.

### i18n — 27 keys mới (parity 156 → 183, EN/VI identical sets)

| Key | EN | VI |
|---|---|---|
| `nav.combos` | Combos | Bộ kết hợp |
| `combos.title` | Router combos | Bộ kết hợp |
| `combos.add` | Add | Thêm |
| `combos.col.name` | Name | Tên |
| `combos.col.mode` | Mode | Chế độ |
| `combos.col.items` | Items | Thành phần |
| `combos.col.actions` | Actions | Thao tác |
| `combos.mode.roundRobin` | Round robin | Chia đều |
| `combos.mode.fallback` | Fallback | Thử lần lượt |
| `combos.empty` | No combos yet. Create one! | Chưa có bộ kết hợp nào. Tạo ngay! |
| `combos.form.title.new` | New combo | Bộ kết hợp mới |
| `combos.form.title.edit` | Edit combo | Sửa bộ kết hợp |
| `combos.form.name` | Name | Tên |
| `combos.form.mode` | Mode | Chế độ |
| `combos.form.items` | Items | Thành phần |
| `combos.form.addModelItem` | Add model | Thêm model |
| `combos.form.addComboItem` | Add combo | Thêm combo con |
| `combos.delete.confirm` | Delete this combo? | Xóa bộ kết hợp này? |
| `combos.delete.referenced` | Used by: | Đang được dùng bởi: |
| `combos.error.duplicateName` | A combo with this name already exists. | Tên bộ kết hợp đã tồn tại. |
| `combos.error.emptyItems` | Add at least one item. | Cần ít nhất một thành phần. |
| `combos.error.cycleDetected` | A combo cannot include itself, directly or indirectly. | Không thể lồng bộ kết hợp vào chính nó, trực tiếp hay gián tiếp. |
| `combos.error.invalidItemTarget` | Each item must target exactly one model or combo. | Mỗi thành phần phải trỏ đúng một model hoặc combo. |
| `combos.error.targetNotFound` | Target model or combo no longer exists. | Model hoặc combo được chọn không còn tồn tại. |
| `combos.error.nameTooLong` | Name must be 200 characters or fewer. | Tên tối đa 200 ký tự. |
| `combos.msg.saved` | Combo saved. | Đã lưu bộ kết hợp. |
| `combos.msg.deleted` | Combo deleted. | Đã xóa bộ kết hợp. |

(Bảng đúng 27 keys; mọi chuỗi hiển thị qua `L["key"]`, lỗi chung tái dùng `dashboard.msg.failed`. mapping `ComboValidationError.PascalCase` → key `combos.error.{camelCase}`.)

## 5. Testing (TDD)

`router balancing test/Combos/ComboServiceTests.cs` (pattern `TestDb`/`NullLog`):

- **Create:** draft hợp lệ → Position 0..n-1 đúng thứ tự, Name trim; từng Code: `DuplicateName`, `EmptyItems`, `NameTooLong` (>200), `InvalidItemTarget` (cả 2 null / cả 2 != null), `TargetNotFound` (model/combo id không có).
- **Cycle:** item trỏ chính nó → `CycleDetected`; X→Y, sửa Y trỏ X (indirect) → `CycleDetected`; create combo mới trỏ X → OK (không cycle); Update/Delete unknown id → `KeyNotFoundException`.
- **Update:** đổi Name/Mode; items thay thế toàn bộ (xóa dòng cũ, Position gán lại).
- **Delete:** không tham chiếu → xóa sạch items (cascade); có tham chiếu → `GetReferencingComboNamesAsync` trả đúng tên, `DeleteAsync` ném (Restrict backstop).
- **Cascade:** xóa model đang là target → item biến mất (xác nhận contract Phase 1).

**Gates:** `dotnet test "router-balancing test/router balancing test.csproj" --nologo` (127 cũ + mới), app build `net10.0-windows10.0.19041.0` 0W/0E, `npm run build` exit 0 (class CSS mới → commit `wwwroot/build/` kèm). UI không unit test — controller self-test qua CDP harness.

## 6. Kết quả kiểm chứng

| Phase | Trạng thái |
|---|---|
| Unit test + build + npm | Thực thi khi implement |
| Manual self-test CDP (list/add/edit/cycle warning/delete/i18n) | Controller tự chạy trước khi merge |
