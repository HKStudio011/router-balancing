# Plan: Sổ usage ngày `UsageDaily` (C3)

- **Spec:** `docs/superpowers/specs/2026-10-21-usage-daily-ledger-design.md`
- **Backlog:** C3
- **Nhánh:** `feat/combo-item-integrity` (tiếp nối C2/D21/C5, chưa merge)
- **Trạng thái:** Done - 2026-10-21
- **Gate:** core build 0 warning + full `dotnet test` + app Windows TFM build

## Task 1 - Domain + schema + migration

**Test trước** (`router balancing test/Storage/AddUsageDailyMigrationTests.cs`):

1. `Up_BackfillsCountersAndDropsColumns`:
   - migrate tới `20261021031043_AddProviderHeaders`;
   - seed client key/provider account với counter > 0, counter 0, `UsageDate` NULL và `UsageDate` hỏng;
   - migrate tới `20261021033526_AddUsageDaily`;
   - assert bảng + index tồn tại, chỉ dòng hợp lệ được backfill, `SubjectKind` đúng, cột cũ biến mất.
2. `Down_DropsTableAndRestoresEmptyColumns`: migrate tới target rồi lùi; assert bảng mất, 3 cột được tạo lại.

**Code:**

- `Domain/Entities/UsageDaily.cs`, `Domain/Enums/UsageSubjectKind.cs`, `Domain/UsageTotals.cs`.
- `RouterBalancingDbContext`: `DbSet<UsageDaily>`, PK ghép, converter `SubjectKind`/`Date`, index `(SubjectKind, Date)`.
- Xóa `RequestsUsed`/`TokensUsed`/`UsageDate` khỏi `ClientKey`/`ProviderAccount`; thêm `[NotMapped]` cùng tên cho UI.
- Migration `AddUsageDaily` (generate bằng `dotnet ef`, rồi chèn SQL backfill trước `DropColumn`).

## Task 2 - Usage service

**Test trước** (`router balancing test/Usage/UsageDailyServiceTests.cs`):

- cộng dồn cùng ngày;
- tách dòng khi khác ngày;
- chủ thể không tồn tại → không tạo orphan;
- `0/0` → không tạo dòng;
- `GetTodayTotalsAsync` lọc đúng kind + tập id;
- cùng Id số khác kind → hai dòng.

**Code:**

- `IUsageDailyService`/`UsageDailyService` với `TimeProvider`.
- Upsert atomic `INSERT ... SELECT ... ON CONFLICT DO UPDATE`.
- `GetTodayTotalsAsync` một query theo tập id.

## Task 3 - Nối write path + DI

**Test trước:**

- `ClientKeyServiceTests`: request/token ghi ledger, giữ dòng ngày cũ, key bị xóa không throw/không orphan.
- `RequestUsageSinkTests`: client key, provider account, account thiếu usage vẫn đếm request, fail-open, cancellation.
- `ProxyRequestHandlerTests`: sink nhận đúng account id cho 2xx có/không usage.
- `ProviderAccountServiceTests`: `ListAsync` nạp tổng hôm nay.

**Code:**

- `ClientKeyService`: inject `IUsageDailyService`; `RecordRequestAsync` cập nhật `LastUsedAt` + ledger; `RecordTokensAsync` ghi token; `ListAsync` nạp tổng.
- `ProviderAccountService`: inject usage service; `ListAsync` nạp tổng.
- Đổi `IClientKeyUsageSink`/`ClientKeyUsageSink` → `IRequestUsageSink`/`RequestUsageSink`; thêm `providerAccountId`.
- `ProxyRequestHandler`: gọi sink sau mọi 2xx, kể cả khi usage null (token 0).
- `ProxyApp` + `MauiProgram`: đăng ký `IUsageDailyService` và sink mới.
- `ApiKeyMiddleware`: giữ fail-open, cập nhật message sang usage.

## Task 4 - Docs + backlog

- Spec/plan này.
- `docs/backlog.md`: đánh dấu C3 xong, ghi rõ enforcement `Daily*Limit` là follow-up.

## Verification

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj" --no-build --no-progress -- --parallel none
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

Kết quả:

- Core: 0 warning/0 error.
- Full suite: 1329/1329 pass (`--parallel none`).
- App Windows: 0 warning/0 error.
- Parallel mặc định có 2 flake đã biết; chạy riêng cả hai đều pass.
