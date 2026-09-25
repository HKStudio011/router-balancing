# AGENTS.md — Hướng dẫn cho Agent

## Tổng quan dự án

**router-balancing** — ứng dụng **.NET MAUI Blazor Hybrid** cân bằng tải (load balancing) các request gửi tới các nhà cung cấp LLM (provider LLM: OpenAI, Anthropic, Azure OpenAI, v.v.).

Trạng thái hiện tại: scaffold MAUI Blazor Hybrid (net10.0) + thư mục test xUnit. Chi tiết thiết kế sẽ được thảo luận và bổ sung sau khi khởi tạo dự án.

### Cấu trúc

| Thành phần | Đường dẫn | Mô tả |
|---|---|---|
| Solution | `router-balancing.slnx` | File solution |
| App MAUI Blazor Hybrid | `router-balancing/` | Ứng dụng chính (Razor Components, `wwwroot/`, `Platforms/`) |
| Test project | `router balancing test/` | Unit test (xUnit) |
| Platform-specific | `router-balancing/Platforms/` | Android, iOS, MacCatalyst, Windows |

### Stack

- .NET 10 / .NET MAUI (`UseMaui=true`, `SingleProject`)
- Blazor Hybrid: `Microsoft.AspNetCore.Components.WebView.Maui`
- XAML Source Generation (`MauiXamlInflator=SourceGen`)
- Test: xUnit
- `Nullable=enable`, `ImplicitUsings=enable` — **bắt buộc giữ nguyên**

### Lệnh thường dùng

```bash
dotnet build router-balancing.slnx          # build toàn solution
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # build app trên Windows
dotnet test "router balancing test/router balancing test.csproj"   # chạy unit test
```

---

## CodeGraph

Repo này (dự kiến) được index bằng CodeGraph — SQLite knowledge graph của symbols/edges/files. **Ưu tiên dùng CodeGraph thay cho grep/find/đọc file rải rác** khi cần hiểu hoặc định vị mã nguồn.

### Cách dùng

- **MCP tool (khuyến nghị):** `codegraph_explore` — một lệnh gọi trả về source verbatim (kèm số dòng) của các symbol liên quan + đường gọi (call path) giữa chúng, kể cả dynamic dispatch mà grep không theo được. Truyền `projectPath` = đường dẫn gốc repo (`D:\Code\router-balancing`).
  - Ví dụ câu hỏi: `"BalancerMiddleware Invoke"` hoặc `"provider selection strategy"` hoặc `"MauiProgram cách đăng ký services"`.
- **Shell (luôn chạy được):**
  ```bash
  codegraph explore "<symbol names or câu hỏi>"   # in cùng output ra terminal
  ```

### Nếu chưa có index

- Kiểm tra: `Test-Path .codegraph`
- Nếu chưa có, báo người dùng rằng họ có thể chạy `codegraph init` để bật CodeGraph cho repo này (**không tự chạy init** — việc index là quyết định của người dùng).
- Khi chưa có index, quay lại công cụ mặc định: `glob` (tìm file), `grep` (tìm nội dung), `read` (đọc file).

---

## Quy chuẩn chất lượng code

### Nguyên tắc chung

1. **Compile & test phải xanh:** sau mỗi thay đổi chạy `dotnet build` (và `dotnet test` nếu sửa logic/đơn vị test liên quan). Không báo hoàn thành khi chưa xác minh bằng lệnh.
2. **Nullable reference types bật:** mọi tham chiếu có thể null phải được xử lý tường minh (`?`, `!` chỉ khi chắc chắn, `is null`/`ArgumentNullException.ThrowIfNull` khi cần).
3. **Không để lại code rác:** không debug print, không code comment-out, không TODO vô chủ (TODO phải kèm tên người/nhóm).
4. **Tên đặt có ý nghĩa:** `PascalCase` cho type/method/property; `camelCase` cho biến local/parameter; `snake_case` cho file Razor CSS scope nếu theo template. Tránh tên như `data2`, `temp`, `handler`.
5. **Mỗi unit một trách nhiệm:** hàm ngắn, dễ test; tách interface khi cần mock (`IHttpClientFactory`, strategy, handler...).
6. **Xử lý lỗi chủ động:** không nuốt exception (`catch {} rỗng` cấm); dùng `ILogger` để ghi log lỗi; phân biệt lỗi retry được (5xx, timeout) vs không retry được (4xx) — đặc biệt quan trọng với project load-balancing LLM.
7. **Bảo mật:** không commit API key/secret; key chỉ qua configuration/người dùng; không ghi log nội dung request chứa secret.
8. **Test:** logic cân bằng tải (chọn provider, health check, failover, retry, weighting) **phải có unit test**. Test name mô tả hành vi: `SelectProvider_WhenPrimaryFails_FailoverToSecondary`.
9. **Commit nhỏ, một việc một commit**; message tiếng Anh, concise, theo conventional commit (`feat:`, `fix:`, `test:`, `docs:`, `refactor:`).

### Quy chuẩn comment theo ngôn ngữ

Comment phải **mô tả "tại sao" (why), không lặp lại "cái gì" (what)** code đã nói rõ. Ưu tiên đặt tên biến/hàm tốt thay vì comment. Comment **bắt buộc** trong các trường hợp: ràng buộc kinh doanh/lý do kỹ thuật không đoán được từ code, workaround/bug workaround, thuật toán phức tạp, cảnh bảo an toàn.

#### C# (.NET / MAUI)

Theo chuẩn XML Documentation Comment cho public API; comment nội bộ dùng `//`.

```csharp
/// <summary>
/// Chọn provider có trọng số cao nhất còn sống để gửi request.
/// </summary>
/// <param name="candidates">Danh sách provider đã qua health check.</param>
/// <returns>Provider được chọn; <see langword="null"/> nếu không còn provider khả dụng.</returns>
/// <exception cref="InvalidOperationException">Khi <paramref name="candidates"/> rỗng.</exception>
public LlmProvider SelectProvider(IReadOnlyList<LlmProvider> candidates)
{
    // Throttling-aware: chỉ tính trọng số khi provider đã phục hồi đầy đủ
    // (xem ADR-003) — tránh dồn traffic khi latency còn cao.
    var healthy = candidates.Where(p => p.IsHealthy).ToList();
    ...
}
```

- XML doc (`///`) **bắt buộc** với: public/protected type, method, property có ý nghĩa API.
- `//` cho giải thích bên trong hàm; **không** dùng `/* */` block cho C#.
- Không comment code đã comment-out.
- Giữ comment cập nhật khi sửa code — comment sai còn tệ hơn không comment.

#### Razor / Razor Components (`.razor`)

```razor
@* Chỉ comment HTML/Razor khi markup không tự mô tả được mục đích *@
<div class="provider-list">
    @* Lặp bằng Key để tránh re-render toàn bộ list khi đổi provider *@
    @foreach (var p in Providers)
    {
        <ProviderCard @key="p.Id" Provider="p" />
    }
</div>

@code {
    /// <summary>
    /// Danh sách provider đang hoạt động, tải mỗi 30 giây.
    /// </summary>
    private List<LlmProvider> Providers { get; set; } = [];
}
```

- Comment Razor/HTML: `@* ... *@`; C# bên trong `@code`/`@functions`: theo chuẩn C#.
- Dùng XML doc cho `[Parameter]` public properties.

#### XAML (`.xaml`)

```xml
<!-- FontSize 14: theo design token Body/Medium, đừng chỉnh trực tiếp — đổi trong Styles -->
<Label FontSize="14" Text="{Binding ProviderName}" />
```

- Dùng `<!-- -->`; thường chỉ cần khi giải thích magic value/design token.

#### CSS (`.razor.css`, `app.css`)

```css
/* Z-index 100: phải nằm trên navigation drawer (z-index 50) — see MainLayout.razor */
.provider-badge { z-index: 100; }
```

- Dùng `/* */`; bắt buộc với magic number (z-index, offset, breakpoint).

#### JavaScript / TypeScript (nếu có trong `wwwroot`)

```js
// Hàm này chạy trên cả WebView cũ (Android 7) nên tránh optional chaining
function pingProvider(cfg) { ... }
```

- Dùng `//` / `/* */`; JSDoc/`/** */` cho hàm export/public.

#### Python (script tooling, nếu có)

```python
def retry_request(fn: Callable[[], T], attempts: int = 3) -> T:
    """Gọi fn tối đa `attempts` lần, backoff mũ.

    Tham số:
        fn: Hàm thực thi cần thử lại.
        attempts: Số lần thử tối đa (gồm lần đầu).
    Trả về:
        Kết quả của lần gọi thành công đầu tiên.
    Raises:
        RuntimeError: Khi hết lượt thử mà fn vẫn lỗi.
    """
```

- Docstring (""" """) cho module/class/function public; `#` cho giải thích nội bộ.
- Theo Google style docstring cho Python.

#### JSON / YAML / config

- JSON không hỗ trợ comment — giải thích bằng key mô tả hoặc file README/ADR kèm theo.
- YAML: `#` comment cho giá trị cấu hình không hiển nhiên (ví dụ timeout, endpoint).

#### Markdown tài liệu (README, tài liệu kế hoạch, ADR)

- Tiếng Việt; tiêu đề rõ ràng; code block kèm language tag; link nội bộ dùng đường dẫn tương đối.

### Quy ước tài liệu & giao tiếp

- **Tiếng Việt** là ngôn ngữ mặc định cho: giao tiếp với người dùng, kế hoạch (plan), tài liệu (README, ADR, ghi chú, comment giải thích high-level).
- **Comment code:** ngôn ngữ theo quy chuẩn từng ngôn ngữ ở trên — ưu tiên tiếng Việt cho giải thích "tại sao"; XML doc/API surface có thể dùng tiếng Anh nếu phục vụ interoperability (quyết định theo từng context, giữ nhất quán trong cùng một file).
- **Commit message / code identifier:** tiếng Anh.
- Tên biến/type: tiếng Anh (không dùng tiếng Việt có dấu trong identifier).
