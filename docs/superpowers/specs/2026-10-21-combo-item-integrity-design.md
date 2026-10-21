# Spec: Toàn vẹn `ComboItems` + chặn tên combo chứa `/`

- **Ngày:** 2026-10-21
- **Trạng thái:** Implemented (2026-10-21) — Approved: user "ok làm đi" cho hướng đề xuất (C2 + D21)
- **Nguồn gốc:** `docs/backlog.md` mục C2 và D21 (brainstorm 2026-10-19; review Plan 2 ngày 2026-10-21)
- **Điều kiện đầu:** master tại `0e04681` (sau PR #33), working tree sạch

## 1. Vấn đề

### 1.1 `ComboItems` là liên kết đa hình không được DB bảo vệ

`ComboItem` (`src/RouterBalancing.Core/Domain/Entities/ComboItem.cs`) trỏ tới **hoặc** model **hoặc** combo con qua hai cột nullable. `ComboService.ValidateAsync` đã kiểm tra XOR và chặn chu trình, nhưng tầng DB không có ràng buộc tương ứng:

- Không có CHECK: dòng `TargetModelId IS NULL AND TargetComboId IS NULL` hoặc cả hai cùng set vẫn ghi được (import, sửa tay, bug tương lai).
- `IX_ComboItems_ComboId_Position` đang **non-unique** (`RouterBalancingDbContext` dòng 101) → hai item cùng `Position` trong một combo; mode Fallback sẽ phụ thuộc thứ tự không xác định.
- Chặn chu trình đã có ở service (`ValidateNoCycleAsync`) và có test; SQLite không biểu diễn được ràng buộc đệ quy, nên giữ nguyên ở tầng service.

### 1.2 Tên combo chứa `/` đụng namespace model

`ComboResolver.ResolveAsync` thử model trước combo: chuỗi có dạng `<Identifier>/<ModelId>` được pin theo provider; chỉ khi không có candidate nào mới fallback sang tên combo. Combo đặt tên chứa `/` có thể bị che hoàn toàn bởi một model trùng chuỗi, hoặc gây nhầm lẫn giữa hai shape (`<Identifier>/<ModelId>` và tên combo).

## 2. Quyết định

1. **Giữ một bảng `ComboItems`**, không tách `ComboModelItem`/`ComboComboItem`.
   - `Position` là thứ tự chung của combo; hai bảng sẽ **không** enforce được unique `(ComboId, Position)` xuyên bảng và buộc merge in-memory khi resolve.
   - FK hiện có đã bảo đảm target tồn tại; thiếu duy nhất là ràng buộc XOR.
2. **CHECK `CK_ComboItems_ExactlyOneTarget`**: `(TargetModelId IS NULL) <> (TargetComboId IS NULL)`.
3. **Unique index** `IX_ComboItems_ComboId_Position` trên `(ComboId, Position)`.
4. **Migration `AddComboItemIntegrity` tiền xử lý dữ liệu bẩn** trước khi thêm ràng buộc — app tự migrate lúc khởi động nên DB cũ không được chặn app:
   - dòng cả hai target đều NULL: **xoá** (không mang nghĩa);
   - dòng cả hai target cùng set: **giữ `TargetModelId`, xoá `TargetComboId`** (combo lồng là shape thêm sau; model là target đơn giản hơn và không thể gây chu trình);
   - trùng `Position` trong cùng combo: **đánh số lại 0..n-1** theo `(Position, Id)`.
   - Không hoàn tác các thay đổi dữ liệu này ở `Down` (giống migration `DropLegacyModelCapabilityColumns`).
5. **D21 — cấm `/` trong tên combo** tại `ComboService.ValidateAsync` (cả create và update) với `ComboValidationError.NameContainsSlash`. Không đổi thứ tự resolve của engine.
   - Tên combo không chứa `/` không thể trùng namespace `<Identifier>/<ModelId>` (shape này luôn có `/`).
   - Không tự đổi tên combo cũ trong DB (dữ liệu người dùng). Combo cũ có `/` vẫn chạy như trước; lần sửa kế tiếp sẽ bị chặn cho tới khi đổi tên.

## 3. Ngoài phạm vi

| Việc | Lý do |
|---|---|
| Tách bảng `ComboModelItem` / `ComboComboItem` | Xem quyết định 1 — mất uniqueness của Position xuyên bảng |
| Chặn chu trình ở tầng DB (trigger) | SQLite không có ràng buộc đệ quy; service đã chặn + có test |
| Đổi ưu tiên resolve (combo trước model) | Phá vỡ provider-scoped model id; chỉ cần chặn tên chứa `/` |
| Tự rename combo cũ chứa `/` | Đổi dữ liệu người dùng ngoài yêu cầu |
| `Combo.Name` không rỗng / chống trùng với model id | Cùng lớp lỗi nhưng ngoài phạm vi C2/D21; ghi nhận ở backlog nếu cần |

## 4. Kiểm chứng

- **Schema** (`ComboItemSchemaTests`): cả hai target NULL bị chặn; cả hai set bị chặn; trùng Position trong một combo bị chặn; cùng Position ở hai combo khác nhau được phép.
- **Migration** (`ComboItemSchemaTests`): DB ở schema trước đó, chèn đủ 3 loại dòng bẩn (cả hai NULL, cả hai set, trùng Position) → `Migrate()` chạy xong; dòng vô nghĩa bị xoá, dòng hai target giữ model, Position được đánh lại 0..n-1.
- **Validation** (`ComboServiceTests`): create/update tên chứa `/` → `NameContainsSlash`; translation parity test đảm bảo key mới có đủ EN/VI.
- **Gate:** full test suite xanh; build app Windows TFM xanh.
