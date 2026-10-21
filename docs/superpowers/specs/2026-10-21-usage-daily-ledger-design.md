# Spec: Sổ usage ngày `UsageDaily` thay counter trong bảng cấu hình

- **Ngày:** 2026-10-21
- **Trạng thái:** Implemented - 2026-10-21 (migration `20261021033526_AddUsageDaily`)
- **Nguồn gốc:** `docs/backlog.md` mục C3
- **Điều kiện đầu:** master `0e04681` + nhánh `feat/combo-item-integrity` (C2/D21/C5 đã xong, chưa merge)

## 1. Vấn đề

Counter usage nằm trực tiếp trong bảng cấu hình:

- `ClientKeys.RequestsUsed`, `TokensUsed`, `UsageDate`;
- `ProviderAccounts.RequestsUsed`, `TokensUsed`, `UsageDate`.

Hệ quả:

- Sang ngày mới phải reset lười tại lần ghi đầu tiên; lịch sử ngày cũ **mất**.
- `ProviderAccounts` chưa từng được ghi counter (Phase 3 chưa nối), nên UI luôn hiển thị 0.
- Không query được usage theo khoảng ngày, theo provider, hoặc tổng hợp nhiều chủ thể.
- Mỗi bảng cấu hình vừa chứa cấu hình vừa chứa số liệu thống kê — hai trách nhiệm khác nhau.

## 2. Quyết định

### 2.1. Schema

Bảng `UsageDaily`:

| Cột | Kiểu | Ghi chú |
|---|---|---|
| `SubjectKind` | TEXT | `client_key` hoặc `provider_account` |
| `SubjectId` | INTEGER | Id trong bảng tương ứng; **không FK** |
| `Date` | TEXT | `yyyy-MM-dd`, ngày UTC |
| `Requests` | INTEGER | số request đã ghi nhận |
| `Tokens` | INTEGER | prompt + completion |

- PK ghép `(SubjectKind, SubjectId, Date)` — đủ để upsert atomic.
- Index `(SubjectKind, Date)` cho truy vấn tổng theo ngày sau này.
- Không FK vì `SubjectId` là quan hệ đa hình; xóa key/account vẫn giữ lịch sử usage. Đây là chủ ý, không phải dữ liệu hỏng.
- `UsageSubjectKind` lưu TEXT qua `UsageSubjectKinds` để DB bên ngoài đọc được; `DateOnly` map tường minh sang `yyyy-MM-dd`.

### 2.2. Bỏ counter khỏi bảng cấu hình

- Xóa `RequestsUsed`, `TokensUsed`, `UsageDate` khỏi `ClientKey` và `ProviderAccount`.
- Giữ lại cùng tên property nhưng đánh dấu `[NotMapped]` để UI hiện tại không phải đổi:
  - `ClientKeyService.ListAsync` nạp tổng hôm nay từ `UsageDaily`;
  - `ProviderAccountService.ListAsync` nạp tổng hôm nay từ `UsageDaily`.
- Không đổi label/i18n/UI; dữ liệu hiển thị vẫn là "hôm nay".

### 2.3. Ghi usage

- **Request của client key**: `ApiKeyMiddleware` gọi `ClientKeyService.RecordRequestAsync` sau khi auth qua — cập nhật `LastUsedAt` và cộng `Requests=1`.
- **Token của client key**: `RequestUsageSink` cộng `Tokens=prompt+completion`, đồng thời cộng window TPM in-memory.
- **Provider account**: sau **mọi response 2xx**, `ProxyRequestHandler` gọi sink với `Requests=1`; `Tokens` là usage parse được, hoặc 0 nếu upstream không trả usage. Nhờ vậy account vẫn đếm request dù thiếu usage.
- Sink đổi tên `IClientKeyUsageSink`/`ClientKeyUsageSink` → `IRequestUsageSink`/`RequestUsageSink` vì phạm vi không còn chỉ client key.
- Ghi ledger là atomic upsert:

```sql
INSERT INTO UsageDaily (...)
SELECT ...
FROM ClientKeys           -- hoặc ProviderAccounts
WHERE Id = @subjectId
ON CONFLICT (SubjectKind, SubjectId, Date)
DO UPDATE SET Requests = Requests + excluded.Requests,
              Tokens   = Tokens   + excluded.Tokens;
```

`INSERT ... SELECT` bảo đảm chủ thể đã bị xóa thì không tạo dòng mồ côi. Không ghi dòng `0/0`.

### 2.4. Migration

`AddUsageDaily`:

1. Tạo bảng + index.
2. Backfill từ counter cũ **trước khi** drop cột:
   - chỉ lấy dòng có `date(UsageDate)` hợp lệ và `(RequestsUsed <> 0 OR TokensUsed <> 0)`;
   - `date(UsageDate)` chuẩn hóa về `yyyy-MM-dd`, bỏ giá trị hỏng thay vì đưa dòng không đọc được vào `DateOnly`;
   - backfill cả provider account dù trước đây chưa từng ghi, để DB cũ không mất dữ liệu nếu có.
3. Drop 3 cột khỏi cả hai bảng.
4. `Down` tạo lại cột rỗng và drop bảng — một chiều, không dựng lại counter từ ledger (cùng lý do với các migration chuẩn hóa trước).

### 2.5. Fail-open

Usage là telemetry:

- Lỗi DB khi ghi ledger không được phá response đã 2xx; sink log Error.
- Request bị hủy đúng lúc ghi chỉ log Debug, không log Error.
- Middleware bắt exception của `RecordRequestAsync` và vẫn cho request đi tiếp.

## 3. Ngoài phạm vi

| Việc | Lý do |
|---|---|
| Enforce `ProviderAccount.DailyTokenLimit` / `DailyRequestLimit` | C3 chỉ ghi sổ; enforcement vẫn là follow-up (hiện limit chỉ để hiển thị) |
| Dashboard/lịch sử usage nhiều ngày | Schema đã đủ cho việc này nhưng UI chưa làm |
| Retention/export `UsageDaily` | Chưa có yêu cầu; bảng tăng 1 dòng/chủ thể/ngày |
| Đếm request lỗi (4xx/5xx/network) cho provider account | Chỉ đếm 2xx để khớp nghĩa "đã phục vụ thành công" |
| Backfill dòng 0/0 | Không có thông tin usage thực; tránh rác |
| Timezone khác UTC | Toàn bộ app đang dùng ngày UTC |

## 4. Kiểm chứng

- `UsageDailyServiceTests`: cộng dồn cùng ngày; tách dòng khác ngày; chủ thể bị xóa không tạo orphan; `0/0` không tạo dòng; lọc theo kind/id; cùng Id số nhưng khác kind không ghi đè.
- `AddUsageDailyMigrationTests`: backfill đúng `client_key`/`provider_account`, bỏ dòng zero/ngày hỏng/NULL, drop cột; `Down` xóa bảng và tạo lại cột rỗng.
- `ClientKeyServiceTests`: request/token ghi ledger, giữ nguyên dòng ngày cũ, key bị xóa không throw/không orphan.
- `RequestUsageSinkTests`: client key, provider account, account thiếu usage vẫn đếm request, fail-open, cancellation.
- `ProviderAccountServiceTests`: `ListAsync` nạp usage hôm nay từ ledger.
- `ProxyRequestHandlerTests`: sink được gọi cho 2xx có usage và không usage, với đúng account id.

Gate:

- Core build: 0 warning, 0 error.
- App Windows TFM build: 0 warning, 0 error.
- Full suite: **1329/1329 pass** với `--parallel none`. Chạy parallel mặc định có 2 flake đã biết (`ProxyQueueIntegrationTests.Stream_LargeUpstreamBody_CompletesWithoutDeadlock`, `ProxyHostTests...NonLoopback`); chạy riêng đều pass.

## 5. Kết quả triển khai

- Thêm `UsageDaily`, `UsageSubjectKind`, `UsageTotals`, `IUsageDailyService`, `UsageDailyService`.
- Xóa counter khỏi entity; `ClientKeyService`/`ProviderAccountService` nạp tổng hôm nay qua `[NotMapped]`.
- `RequestUsageSink` ghi token client key + request/token provider account; handler gọi sau mọi 2xx.
- Migration `20261021033526_AddUsageDaily` tạo bảng, backfill rồi drop cột.
- Đăng ký `IUsageDailyService` ở cả proxy container (`ProxyApp`) và UI container (`MauiProgram`).
