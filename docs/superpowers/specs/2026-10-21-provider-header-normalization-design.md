# Spec: Chuẩn hoá `Providers.ExtraHeaders` thành bảng `ProviderHeaders`

- **Ngày:** 2026-10-21
- **Trạng thái:** Implemented — 2026-10-21 (migration `20261021031043_AddProviderHeaders`)
- **Nguồn gốc:** `docs/backlog.md` mục C5 (provider header; `AccountModelPattern` để sau)
- **Điều kiện đầu:** master `0e04681` + nhánh `feat/combo-item-integrity` (C2/D21) đã xong, chưa merge

## 1. Vấn đề

`Providers.ExtraHeaders` là JSON object trong một cột TEXT:

- `ProviderRequestFactory` phải parse JSON trên **mọi request** gửi upstream; JSON hỏng (sửa tay DB) bị bỏ qua âm thầm.
- Không có ràng buộc unique theo `(ProviderId, Name)`; trùng key case-insensitive chỉ được chặn ở validator form.
- Không query/index được theo tên header; backup/khôi phục phải parse JSON.

## 2. Quyết định

1. Bảng `ProviderHeaders(ProviderId, Name, Value)`:
   - PK ghép `(ProviderId, Name)`; `Name` dùng collation `NOCASE` để unique không phân biệt hoa thường (HTTP semantics), giữ nguyên chữ gốc để hiển thị.
   - `Value` NOT NULL (chuỗi rỗng hợp lệ); FK `ProviderId` → `Providers.Id` `ON DELETE CASCADE`.
2. `Provider.Headers` navigation; bật `AutoInclude()` để mọi query provider đều có header — tránh lỗi "quên Include" ở các đường tạo request (dispatcher, test connection, sync model).
3. Migration `AddProviderHeaders`:
   - tạo bảng;
   - backfill từ JSON: chỉ nhận `json_valid` + `json_type = 'object'`, key là string sau `trim()` khác rỗng, value là string; `INSERT OR IGNORE` để key trùng case-insensitive lấy dòng đầu;
   - bỏ cột `Providers.ExtraHeaders`.
   - `Down` tạo lại cột rỗng, không phục hồi JSON (một chiều, giống các migration chuẩn hoá trước).
4. Service/UI:
   - `ProviderService` map `ProviderDraft.ExtraHeaders` → `provider.Headers` (trim key, gộp trùng case-insensitive, last-wins như dictionary cũ).
   - `ProviderRequestFactory` đọc `provider.Headers`, vẫn giữ luật "built-in/fingerprint thắng" (`TryAddWithoutValidation` chỉ khi header chưa tồn tại).
   - `Providers.razor` map `provider.Headers` → dòng form; bỏ helper parse JSON.
5. Không đổi: `ProviderDraft.ExtraHeaders` (form vẫn là danh sách key/value), validator, i18n, hành vi HTTP.

## 3. Ngoài phạm vi

| Việc | Lý do |
|---|---|
| `AccountModelPattern` thay `ProviderAccounts.ModelPatterns` | Backlog ghi "độ lợi thấp, làm sau" |
| Mã hoá/secret cho header value | Hiện là plaintext có chủ đích; header thường là metadata, không phải key |
| Đổi thứ tự hiển thị header | Thứ tự không có ý nghĩa HTTP; UI sort theo `Name` cho ổn định |

## 4. Kiểm chứng

- Schema: trùng `Name` khác hoa thường trong cùng provider bị chặn; xoá provider cascade header; cùng tên ở hai provider được phép.
- Migration: DB cũ có JSON hợp lệ (kể cả key trùng case), JSON hỏng, key rỗng → bảng mới đúng, migration không ném.
- Service: create/update ghi bảng; update thay toàn bộ; danh sách rỗng → không có dòng.
- Request factory: header được gắn; header trùng tên với `Authorization` built-in không đè.
- Gate: full test suite xanh; build core + app Windows xanh.

## 5. Kết quả triển khai

- `ProviderHeader` + PK ghép `(ProviderId, Name)` `NOCASE`, FK cascade; `Provider.Headers` bật `AutoInclude`.
- Migration backfill JSON object hợp lệ (bỏ JSON hỏng, key rỗng, value không phải string; key trùng case first-wins) rồi bỏ cột `ExtraHeaders`.
- `ProviderService` diff header khi update (không xoá-tạo cùng key trong một `SaveChanges`); `ProviderRequestFactory` đọc bảng; `Providers.razor` map bảng về form.
- Test: 4 migration/schema test + cập nhật `ProviderServiceTests`/`ProviderRequestFactoryTests`.
- Gate: core build 0 warning, app Windows TFM 0 warning; full suite 1316/1317 — 1 fail là flake đã biết `ProxyHostTests.StartAsync_WhenLanAccessEnabled_RespondsOnNonLoopbackAddress`, pass khi chạy riêng.
