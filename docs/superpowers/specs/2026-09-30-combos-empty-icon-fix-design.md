# Spec: Fix icon empty-state trang Combos (HTML entity trong component parameter)

**Ngày:** 2026-09-30
**Trạng thái:** Approved

## Vấn đề

Emoji empty-state trên trang Combos không hiển thị. `Combos.razor:23` truyền
`Icon="&#128218;"` vào component `EmptyState`.

**Root cause:** Razor **không decode HTML entity** khi truyền component
parameter — chuỗi `"&#128218;"` được compile nguyên vẹn, rồi `@Icon` trong
`EmptyState.razor:4` encode thêm một lần nữa → UI hiện text hỏng
`&#128218;` thay vì emoji 📖. Bug ghi nhận 30/09/2026.

Entity ở text node (markup thường, ví dụ `&times;`) KHÔNG bị ảnh hưởng —
browser tự decode. Chỉ entity làm **giá trị trọn vẹn của attribute** mới hỏng.

## Phạm vi

- Thay HTML entity bằng ký tự UTF-8 trực tiếp tại đúng 2 violations:
  - `router-balancing/Components/Pages/Combos.razor:23`: `&#128218;` → `📖`
  - `router-balancing/Components/Pages/Providers.razor:27`: `&#9881;&#65039;` → `⚙️`
- Commit guard test sẵn có `router balancing test/Components/RazorParameterEntityTests.cs`
  (đang untracked) — chặn tái phát bằng regex trên toàn bộ `*.razor`.

## Ngoài phạm vi

- Không sửa component `EmptyState`, không thay đổi API/behavior khác.
- Không đụng tới emoji ở text node (`✏️🗑` trong bảng, `🗙` trong index.html).

## Kiểm chứng

1. Guard test FAIL trước fix (2 violations), PASS sau fix.
2. Full suite `dotnet test "router balancing test/router balancing test.csproj"` xanh.
