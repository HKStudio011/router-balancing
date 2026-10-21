# Plan: Chuẩn hoá `Providers.ExtraHeaders` → bảng `ProviderHeaders`

- **Spec:** `docs/superpowers/specs/2026-10-21-provider-header-normalization-design.md`
- **Backlog:** C5 (phần provider header; `AccountModelPattern` để sau)
- **Nhánh:** `feat/combo-item-integrity` (tiếp nối C2/D21, chưa merge)
- **Trạng thái:** Done — 2026-10-21
- **Gate:** core build 0 warning + full `dotnet test` + app Windows TFM build

## Task 1 — Schema + migration + test (đỏ → xanh)

**Test trước** (`router balancing test/Storage/AddProviderHeadersMigrationTests.cs`):

1. `Up_BackfillsJsonAndDropsColumn`:
   - migrate tới `20261021023602_AddComboItemIntegrity`;
   - seed 5 provider: JSON hợp lệ (có key trùng hoa thường), JSON hỏng, NULL, JSON array, JSON có key rỗng/value không phải string;
   - migrate tới migration mới;
   - assert: đúng số dòng, key trùng case chỉ còn 1 (first-wins), key rỗng/number bị bỏ, cột `ExtraHeaders` biến mất.
2. `Down_RecreatesColumnAndDropsTable`:
   - migrate tới target rồi lùi về previous;
   - assert cột `ExtraHeaders` tồn tại (rỗng), bảng `ProviderHeaders` biến mất.
3. `DuplicateNameDifferentCase_IsRejected`:
   - insert 2 header `X-Custom` / `x-custom` cùng provider qua ADO → `SqliteException`.
4. `DeleteProvider_CascadesHeaders`:
   - bật `Foreign Keys=True`, xoá provider → header con biến mất.

**Code:**

- `Domain/Entities/ProviderHeader.cs`: `ProviderId`, `Name`, `Value`, navigation `Provider`.
- `Provider.cs`: bỏ `ExtraHeaders`, thêm `List<ProviderHeader> Headers`.
- `RouterBalancingDbContext`:
  - `DbSet<ProviderHeader> ProviderHeaders`;
  - PK `(ProviderId, Name)`, `Name` `UseCollation("NOCASE")`, max 200;
  - FK cascade;
  - `Entity<Provider>().Navigation(p => p.Headers).AutoInclude()` — tránh quên Include ở dispatcher/test/sync.
- Migration `AddProviderHeaders` (generate bằng `dotnet ef`, rồi chèn SQL backfill trước `DropColumn`):
  ```sql
  INSERT OR IGNORE INTO ProviderHeaders (ProviderId, Name, Value)
  SELECT p.Id, trim(je.key), je.value
  FROM Providers p
  JOIN json_each(
      CASE
          WHEN p.ExtraHeaders IS NULL
               OR json_valid(p.ExtraHeaders) = 0
               OR json_type(p.ExtraHeaders) <> 'object' THEN '{}'
          ELSE p.ExtraHeaders
      END) AS je
  WHERE je.type = 'text' AND length(trim(je.key)) > 0;
  ```
- `Down`: tạo lại cột `ExtraHeaders` nullable, drop bảng (một chiều — ghi chú trong migration).

## Task 2 — Service / request factory / UI

**Test trước:**

- `ProviderServiceTests`: thay 4 test JSON bằng:
  - create → `provider.Headers` + DB có đúng key/value, key trim;
  - create rỗng → không có dòng;
  - update đổi value cùng tên → update tại chỗ;
  - update danh sách mới → dòng cũ bị xoá, dòng mới thêm;
  - update rỗng → xoá hết.
- `ProviderRequestFactoryTests`:
  - set `provider.Headers` thay JSON;
  - bỏ test JSON hỏng (không còn parse JSON);
  - giữ test `Authorization` built-in thắng.

**Code:**

- `ProviderService`:
  - `CreateAsync`: map draft → `provider.Headers`;
  - `UpdateAsync`: diff theo `StringComparer.OrdinalIgnoreCase` (update value, remove, add) — không xoá-tạo cùng key trong một `SaveChanges` để tránh tracking conflict;
  - bỏ `SerializeExtraHeaders`.
- `ProviderRequestFactory.ApplyExtraHeaders`: duyệt `provider.Headers`, giữ `TryAddWithoutValidation` + built-in thắng.
- `Providers.razor`: `OpenEdit` map `provider.Headers` (sort theo tên), xoá `DeserializeExtraHeaders`; `System.Text.Json` vẫn dùng cho `ModelPatterns`.

## Task 3 — Docs + gate

- Cập nhật `docs/backlog.md` (tick C5 phần provider header, ghi rõ `AccountModelPattern` còn lại).
- Cập nhật spec: trạng thái Implemented + ghi chú migration.
- Chạy:
  - `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj` (0 warning);
  - `dotnet test "router balancing test/router balancing test.csproj"`;
  - `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`.
- Flake đã biết: `ProxyQueueIntegrationTests.Stream_LargeUpstreamBody_CompletesWithoutDeadlock`, `ProxyHostTests.*NonLoopback` → chạy lại / `--parallel none`, không filter bỏ.

## Commit

1. `feat: store provider headers in a normalized table` — entity/schema/migration/service/factory/UI/tests.
2. `docs: mark provider header normalization complete` — backlog/spec.
