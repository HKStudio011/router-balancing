# Ubiquitous Language

Từ vựng miền của **router-balancing** — proxy định tuyến request tới nhiều nhà cung cấp LLM.
Dùng đúng các thuật ngữ dưới đây trong code, UI, test và tài liệu.

## Định tuyến & lựa chọn

| Term | Definition | Aliases to avoid |
| --- | --- | --- |
| **Provider** | Một nhà cung cấp LLM đã cấu hình (BaseUrl, loại API, model, account, giới hạn đồng thời). | Vendor, upstream, "provider instance" |
| **ProviderAccount** | Một API key thuộc đúng một **Provider**, có giới hạn đồng thời và usage riêng; UI đặt tên kiểu `nvidia 1`. | Account, key, "nvidia provider" |
| **Model** | Một model id cụ thể do **Provider** phơi ra (ví dụ `nvidia/...`). | Alias, deployment |
| **Combo** | Một đơn vị định tuyến có tên, gồm một **ComboMode** và danh sách con (Model hoặc Combo lồng nhau). | Pool, group, tổ hợp |
| **ComboMode** | Cách chọn con của **Combo**: `RoundRobin` hoặc `Fallback`. | Strategy, policy |
| **Candidate** | Cặp (**Provider**, **Model**) đã resolve và đủ điều kiện phục vụ request. | Option, endpoint |
| **Selection tree** | Cây resolve của một **Combo**: nút trong là **ComboUnit**, lá là **CandidateLeaf**. | Route tree (dễ nhầm với **Route** của trace) |
| **RoundRobin** | Mode chia request giữa các con eligible, ưu tiên con ít in-flight nhất rồi xoay vòng. | Load balancing, random |
| **Fallback** | Mode thử lần lượt các con theo thứ tự cấu hình cho tới khi thành công. | Failover (failover là hành vi, không phải mode) |

## Vòng đời request

| Term | Definition | Aliases to avoid |
| --- | --- | --- |
| **Request** | Một lần client gọi proxy (`/v1/chat/completions` hoặc `/v1/responses`), định danh bằng `RequestId`. | Call, prompt, job |
| **Queue** (Hàng đợi) | Tập request đã nhận nhưng chưa giữ **execution slot**; gồm cả request **Parked**. | Buffer, backlog |
| **Parked** | Request bị đẩy lại **Queue** vì chưa có **execution slot** trống. | Blocked, retry queue |
| **ExecutionList** (Danh sách thực thi) | Sổ đăng ký các request đang giữ **execution slot**, là nơi thực thi giới hạn đồng thời. | Running list, thread pool |
| **Execution slot** (chỗ) | Một đơn vị năng lực đồng thời của **ProviderAccount** (`MaxConcurrent`). | Queue slot, capacity |
| **In-flight** | Request đang giữ một **execution slot** (có entry trong **ExecutionList**). | Running (không đồng nghĩa — xem bên dưới) |
| **Attempt** | Một lần gửi request lên upstream tới một **Candidate**; retry sinh thêm Attempt. | Try, retry (retry là hành động, Attempt là bản ghi) |
| **Running** (đang thực thi) | Request đã rời **Queue** và đang ở stage `DispatchStarted` hoặc `Attempt`; đếm theo **request**, không theo account. | Active, in-flight, "đang chạy account" |
| **Priority** | Mức lập lịch `Highest` / `High` / `Normal`; tối đa một request `Highest` tại một thời điểm. | Urgent, weight |
| **Canceled** | Request bị client ngắt; kết thúc ở node Client với màu vàng. | Aborted, killed |

## Live Trace (Dashboard)

| Term | Definition | Aliases to avoid |
| --- | --- | --- |
| **Trace node** | Trạng thái sống của đúng một **Request** trên sơ đồ Live Trace. | Dot, circle (dot là hình vẽ của node) |
| **Stage** | Bước hiện tại của **Trace node**: `Received`, `DispatchStarted`, `Attempt`, `Parked`, `Finished`, `Canceled`. | State, phase |
| **Anchor** | Vị trí ngữ nghĩa của **Trace node** trên sơ đồ: `Queue`, `Dispatch`, `Attempt`, `Client`. | Position, coordinate |
| **Route** | Đường đã chọn của một lần **Attempt**: Combo, Provider, ProviderAccount (kèm ComboPath nếu lồng nhau). | Path, target |
| **Account chip** | Thẻ đại diện một **ProviderAccount** ở cột Account; mỗi **dot** trên đó là một **Request** đang neo vào account ấy. | Account node, key card |
| **Overflow chip** | Chip `+N` gộp các **ProviderAccount** vượt cap 4 chip; bấm để mở danh sách đầy đủ. | "+N idle" (chỉ đúng khi mọi TK bị gộp đều idle) |
| **Idle account** | **ProviderAccount** không được **Trace node** còn sống nào tham chiếu. | Free account, unused key |
| **Client** | Điểm cuối trên sơ đồ, đại diện cho bên gọi proxy. | User, caller app |

## Hạ tầng

| Term | Definition | Aliases to avoid |
| --- | --- | --- |
| **Proxy server** | Tiến trình Kestrel của ứng dụng, nhận request tại `http://127.0.0.1:{port}/v1`. | Proxy (mơ hồ — xem bên dưới) |
| **Outbound proxy** | HTTP proxy dùng để gửi request ra ngoài, gán theo Provider/Account. | Proxy, upstream proxy |
| **Cooldown** | Trạng thái tạm thời không dùng một **Outbound proxy** sau lỗi; UI hiện viền đứt + đếm ngược. | Down, ban, circuit open |

## Relationships

- Một **Provider** sở hữu 1..N **ProviderAccount**; mỗi **ProviderAccount** thuộc đúng một **Provider**.
- Một **Provider** phơi ra 1..N **Model**.
- Một **Combo** có đúng một **ComboMode** và 1..N con (Model hoặc Combo lồng nhau).
- Một **Request** resolve thành một **Selection tree**; mỗi lá là một **Candidate**.
- Một **Request** giữ tối đa một **execution slot** tại một thời điểm; đổi **Candidate** thì nhả slot cũ và xin slot mới.
- Mỗi **Attempt** thuộc đúng một **Request** và nhắm tới một **Route**.
- Mỗi **Trace node** ánh xạ 1-1 với một **Request**; **Route** của nó có thể đổi qua nhiều **Attempt**.
- Mỗi **Account chip** đại diện một **ProviderAccount**; **Overflow chip** đại diện N account bị gộp.
- **RunningCount** đếm **Request**; số **Account chip** đếm **ProviderAccount** — hai đại lượng khác nhau.

## Example dialogue

> **Dev:** "Header ghi **5 đang thực thi** mà sơ đồ chỉ có 4 **account chip**. Mất một **Request** rồi à?"
>
> **Domain expert:** "Không. **RunningCount** đếm **Request** đang ở `DispatchStarted`/`Attempt`; còn chip đếm **ProviderAccount**. Với cap 4 chip, account thứ 5 phải gộp vào **overflow chip**."
>
> **Dev:** "Nếu account thứ 5 đang chạy thì chip ghi gì?"
>
> **Domain expert:** "Ghi `+1 more` (không phải `+1 idle`), bấm vào thấy account đó với dot xanh. Dot của **Attempt** vẫn neo vào **account chip** tương ứng."
>
> **Dev:** "Vậy 5 **Request** chạy trên 4 account mà hiện 4 chip là đúng?"
>
> **Domain expert:** "Đúng — chip là **ProviderAccount**, không phải **Request**. Hai **Request** cùng account sẽ xếp 2 dot trên một chip."

## Flagged ambiguities

- **"Provider" bị dùng cho cả Provider và ProviderAccount** — card `PROVIDER` trong sơ đồ là **Provider** (ví dụ NVIDIA NIM Free); các mục `nvidia 1..5` là **ProviderAccount**, không phải provider.
- **"Đang thực thi" = Request, không phải account** — 5 request đang chạy có thể nằm trên 4 account. Đừng suy ra "5 account đang chạy" từ **RunningCount**.
- **"Running" ≠ "In-flight"** — UI đếm cả `DispatchStarted` (vừa rời queue, chưa chắc đã giữ slot); **In-flight** chỉ là request đã có entry trong **ExecutionList**.
- **"Slot"/"chỗ"** — luôn là năng lực đồng thời của **ProviderAccount**; số request trong **Queue** hiển thị là "chờ", không phải "chỗ".
- **"Proxy"** — cần nói rõ **Proxy server** (ứng dụng) hay **Outbound proxy** (proxy mạng ra ngoài); đừng dùng trần "proxy".
- **"Account"** — trong miền này luôn là **ProviderAccount** (định danh API key), không phải tài khoản người dùng/đăng nhập.
- **"Idle"** — chỉ account không có **Trace node** sống; **Overflow chip** có thể chứa cả account đang chạy, nên nhãn phải là `+N more` thay vì `+N idle`.
- **"Attempt" ≠ "Request"** — một request có thể có nhiều attempt (retry/failover); đừng gọi attempt là request.
