# Thiết kế: chuẩn hoá capability model (mô hình quan hệ) + trang Tools vòng 2

Ngày: 2026-10-19 · Cập nhật 2026-10-21 (đã thực thi) · Trạng thái: **ĐÃ THỰC THI** — phase 1 ở PR #31 / `v1.6.0`; phase 2 ở PR #33 (nhánh `feat/tools-round-2`); C2 + D21 xong ở `feat/combo-item-integrity` (migration `AddComboItemIntegrity`)
Branch kế tiếp: `feat/capability-model-and-tools-round-2` (tên do plan quyết)
Liên quan: `docs/backlog.md` (mục A, B, C), PR #30 (`feat/opencode-tools-page`), spec trước: `docs/superpowers/specs/2026-10-19-opencode-tools-page-free-sync-design.md`

## 0. Số đo hiện trạng (chứng minh bằng lệnh, không phải cảm nhận)

DB đang chạy: `C:\Users\trucn\AppData\Roaming\router-balancing\router-balancing.db` (6.6MB, 13 bảng, `integrity_check ok`, `freelist_count 0`, WAL).

| Số liệu | Giá trị đo |
|---|---|
| Model trong DB | 137 (NVIDIA NIM Free 81, Ollama Cloud Free 22, OpenRouter Free …, OpenCode Free 10) |
| `SupportsAudio = true` | **1** · `SupportsVideo = true` **1** (chỉ do user tick tay) |
| `SupportsTools IS NULL` | **127** · `ContextWindow IS NULL` **102** · có `InputModalities` **10** |
| ModelId trùng giữa các provider | có thật: `qwen3.8-flash-next-iq3_s` (Strata + Strata Cloud Temp), `unsloth/Qwen3.8-27B-GGUF` (2 provider Unsloth) |
| `Identifier` của preset | `opencode-free`, `openrouter-free`, `nvidia-nim-free`, `ollama-cloud-free` |
| Capability trong schema opencode | `https://opencode.ai/config.json` (bản config user đang trỏ): `provider` = **deprecated**, `providers` = `Config.ProviderEncoded`; `Config.Model.Capabilities` = `tools,input,output,temperature,reasoning`; `Config.Provider.Settings` có `timeout`. CLI đang cài: `@opencode/cli@2.0.32` (nhận cả hai shape) |
| Config user | `~/.config/opencode/opencode.json`, 20.713 byte, 589 dòng, indent 4 space, **không có comment**, có block `provider.router-balancing` kiểu cũ với key model `opencode-free/<id>`, `options.timeout:false`, `family`, `temperature`, `tool_call`, `modalities`, `limit` |

## 1. Intent và vì sao

**Vì sao làm cái này:** cùng một sự thật ("model này có vision không") đang được chứa ở hai nơi (`SupportsVision` bool và `InputModalities` chứa `"image"`), và hai nơi đó đã lệch thật sự — badge audio/video không bao giờ hiện dù catalog trả `modalities.input` đầy đủ, vì không có đường nào ghi `SupportsAudio/Video` từ dữ liệu catalog. User phải tick tay từng model để có badge.

**Vì sao phase 2 cần phase 1:** trang Tools vòng 2 phải (a) hiện badge đúng sự thật, (b) sinh config với capability chính xác, (c) phát ra combo như một entry. Cả ba đều đọc trên cùng một mô hình capability; làm ngược thứ tự sẽ phải viết lại hai lần.

**Thành công trông ra sao:** bật/tắt một capability là sửa **một** dòng; không tồn tại hai giá trị trái nhau cho cùng một model; model mới được lấy từ bất kỳ preset nào cũng có capability nếu provider đó thật sự công bố; phần provider không công bố thì để trống và user tự tick, tick đó **không bị** lần sync sau xóa.

## 2. Goals / Non-goals / Anti-goals

**Goals**
1. Capability của `Model` biểu diễn theo mô hình quan hệ: hết JSON-array-trong-cột, hết bool dẫn xuất trùng nguồn.
2. Phân biệt được ba trạng thái: **chưa biết** / **đã biết là có** / **đã biết là không**.
3. Biết **ai nói** (catalog của provider, endpoint của provider, hay user) và tôn trọng ý user khi ghi lại.
4. Điền capability hàng loạt gắn vào đúng 2 nút đã có ("Lấy model", "Tải model free") + khi thêm model thủ công, dùng **nguồn chính thức của chính provider đó**, chỉ lấy phần tường minh.
5. Trang Tools vòng 2: nhớ lựa chọn, reset về mặc định, tìm kiếm, thu/giãn theo provider, chọn hết/bỏ hết theo provider, badge tools/audio/video, và **ghi thẳng vào config opencode** (shape v2, thay đúng block của `router-balancing`).
6. Combo xuất hiện trong config dưới dạng **một entry** duy nhất (quyết định của user 2026-10-19).

**Non-goals**
- Không chuẩn hoá `AppSettings(Key, ValueJson)`.
- Không làm `UsageDaily` / `ProbeResult` / `ProviderHeader` trong cùng đợt này (đã ghi backlog C3–C5; tách ra sau khi C1 xong).
- Không đưa `temperature` vào mô hình capability (catalog có nhưng không nằm trong nhu cầu đã nêu; để backlog).
- Không tự động hoá việc đoán capability từ **văn mô tả** (prose) — xem anti-goal.
- Không tách log/telemetry, không đặt retention cho `LogEntries` (backlog B6).

**Anti-goals** (làm ra là thất bại dù kỹ thuật chạy)
- Một badge **sai** còn hại hơn badge thiếu: không được đoán capability từ câu chữ marketing của nhà cung cấp.
- Không được để lần sync sau làm mất nội dung user đã tick tay.
- Không phá 20KB config opencode của user khi ghi block của app.

## 3. Constraints

- `Nullable=enable`, `ImplicitUsings=enable`; build/test phải xanh sau mỗi slice; không `dotnet build router-balancing.slnx` (NETSDK1082 tồn tại từ trước).
- Migration EF chạy bằng: `dotnet ef migrations add <Name> --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations`.
- App MAUI đang chạy sẽ **không** chặn test (test project net10.0 chỉ tham chiếu Core); build app TFM cần đóng app nếu bị khóa file.
- Comment tiếng Việt cho "tại sao"; XML doc cho public API; exception message tiếng Anh; identifier + commit message tiếng Anh.
- Người dùng có file WIP trong `src/RouterBalancing.Core/Engine/*` — không được đụng.

## 4. Quyết định nghiệp vụ (user chốt, ghi lại để plan không bàn lại)

| # | Quyết định | Ghi chú |
|---|---|---|
| U1 | Đi theo **mô hình quan hệ**, không dùng cột JSON tổng hợp | user 2026-10-19 |
| U2 | **Một migration**: dời dữ liệu rồi **bỏ hẳn** cột capability cũ | user 2026-10-19 |
| U3 | Capability chỉ lấy từ **nguồn chính thức của chính provider**; **không** dùng `models.dev` vì có thể lệch thực tế | user 2026-10-19 |
| U4 | Chỉ lấy phần **chắc chắn** (có cấu trúc tường minh); phần còn lại để **user tick tay** | user 2026-10-19 |
| U5 | Điền hàng loạt **gắn vào 2 nút đã có** ("Lấy model" cho provider thường, "Tải model free" cho preset), và kèm theo khi thêm model **thủ công** nếu có thể | user 2026-10-19 |
| U6 | Thêm nút **"Reset capability về catalog"** | user 2026-10-19 |
| U7 | Trang Tools: **nhớ lựa chọn đã tick** + nút **về mặc định** | user 2026-10-19 |
| U8 | Trang Tools: tìm kiếm model/combo; thu/giãn theo tên provider; chọn hết/bỏ hết theo từng provider | user 2026-10-19 |
| U9 | Trang Tools: **ghi config vào opencode** bằng nút cạnh "Sao chép"; **xóa block cũ của router-balancing, ghi block mới** | user 2026-10-19 |
| U10 | Block mới viết theo **shape v2** (`providers`/`package`/`settings`/`capabilities`) | user 2026-10-19 |
| U11 | Trang Providers/Combos: hiện **tools** trong danh sách capability của model | user 2026-10-19 |
| U12 | Tick một combo → **một entry duy nhất** trong config (key = tên combo) | user 2026-10-19 |
| U13 | Trang Providers: phần tick tay phải có **cả hướng output** cho audio/video (hiện tại chỉ có hướng input) | user 2026-10-20 |

## 5. Quyết định kỹ thuật (của tôi — user có thể phủ quyết)

| # | Quyết định | Lý do / rủi ro nếu sai |
|---|---|---|
| C-A | Key model trong config luôn ở dạng **`<Identifier>/<ModelId>`**; chỉ dùng id trần khi provider chưa có `Identifier` | Khớp đúng format file user đang chạy và hết nhập nhằng khi ModelId trùng giữa các provider. Rủi ro: tên model hiển thị trong opencode dài hơn (`router-balancing/opencode-free/big-pickle`) |
| C-B | **Không** phát field `modelID` trong config | Không có chứng cứ opencode 2.0.32 dùng nó để đổi `model` gửi lên upstream; block hiện tại của user cũng không có |
| C-C | **Không** phát `family` | App không có dữ liệu family đáng tin; catalog opencode không được parse field này |
| C-D | Phát `settings.timeout: false` trong block provider | Block hiện tại của user đặt `options.timeout:false`; sang v2 `Config.Provider.Settings` có key `timeout`. Nếu bỏ, request chat dài có thể bị opencode cắt giữa dòng |
| C-E | Luật ghi: một nguồn **chỉ được ghi dòng chưa có, hoặc dòng do chính nguồn đó tạo**; `user` không bao giờ bị `catalog`/`endpoint` đè | Giữ đúng ý user đã nói ("cả hai đều lưu vào DB"); muốn khôi phục theo catalog thì dùng nút U6 |
| C-F | Nút U6 đặt ở **menu từng model** (không làm hàng loạt) | Hàng loạt đã có 2 nút ở U5 lo. Rủi ro: user muốn reset nhiều model một lúc |
| C-G | Trạng thái thu/giãn nhóm provider và lựa chọn tick lưu trong `AppSettings` (key/value JSON) | Cùng khuân mẫu config app đang dùng; không lập bảng mới |
| C-H | Capability `audio`/`video`/`vision` **không còn là capability**: chỉ là `ModelModality`. `ModelCapability` chỉ giữ `tools` và `reasoning` | Diệt tận gốc lớp lỗi "một sự thật hai nơi". Rủi ro: code cũ đọc `SupportsVision` phải đổi hết (blast radius đã liệt kê ở §8) |
| C-I | Không có dòng = **chưa biết**; có dòng `KnownAbsent=1` = **đã biết là không**; badge chỉ hiện khi đã biết | Nhất quán với "không đoán"; badge "không có" cần thiết kế UI rõ, nếu không thì chỉ hiện badge khi có |
| C-J | `ContextWindow`/`MaxOutputTokens` giữ là cột trên `Models` | Scalar nguyên tử, dùng để hiển thị/sắp xếp, không phải multi-value |
| C-K | Khi dời dữ liệu: `SupportsX = false` cũ → **không tạo dòng nào** | Semantics cũ của `false` là "không hoặc chưa rõ"; bịa thành "đã biết là không" là thêm dữ liệu sai |
| C-L | Capability của một combo (để phát entry trong config) = **giao** an toàn các thành viên, tính bằng `IComboCapabilityCalculator` đang có; UI hàng combo ghi rõ "capability theo model yếu nhất trong nhóm" | Combo failover mà quảng cáo capability của model mạnh nhất sẽ nói dối với opencode (gửi ảnh cho model text-only). Chốt sau builder-check: giữ giao, vì một entry nói quá sẽ hỏng request thật |
| C-M | **Chính sách phát negative vào config** (user ủy quyền, 2026-10-20): generator chỉ phát `capabilities.tools=false`, `reasoning=false`, và `input`/`output` tường minh khi fact đó đến từ **nguồn liệt kê đầy đủ** (bốn nguồn ở §7) hoặc do **user** đặt; **không** phát negative từ endpoint probe, và không phát negative từ bool cũ dời lên | Không có negative thì opencode tự suy từ catalog riêng của họ (mà key `<Identifier>/<id>` của ta thì catalog đó không biết) → dễ gửi ảnh/âm thanh cho model chỉ nhận text. Negative sai thì mất tính năng (tool calling) — nên giới hạn ở nguồn full-list + user, và UI hiện rõ nguồn để bạn sửa |
| C-N | **User edit thắng catalog**: dòng bị user sửa đổi `Source=user` và catalog/endpoint không được đè; nút U6 (Reset về catalog) xóa **mọi** fact của model rồi điền lại từ catalog | Trả lời builder-check Q1; cần cho Goal 3 |
| C-O | `ModelModality` có `KnownAbsent` đối xứng với `ModelCapability` | Trả lời builder-check Q2: nếu không thì "ba trạng thái" chỉ đúng với tools/reasoning, và cái NVIDIA công bố tường minh ("Input Modalities: Text, Image" = không audio) không lưu được |
| C-P | Mỗi nguồn khai báo **complete-enumeration** hay không; chỉ nguồn full-list mới được sinh dòng `KnownAbsent=1`. Opencode catalog (`tool_call:false` tường minh), ollama `capabilities`, NVIDIA sidebar, OpenRouter `supported_parameters` = full-list; probe `/v1/models/{id}` của endpoint riêng = **không** full-list | Trả lời builder-check Q3; hiện thực hóa U4 + anti-goal "badge sai hại hơn badge thiếu" |
| C-Q | Dời dữ liệu cũ: `SupportsTools=false` → `KnownAbsent=1, Source=catalog` (code cũ ghi nó từ catalog, semantics rõ) — **ngoại lệ có chủ đích của C-K**; `SupportsVision/Think/Audio/Video = false` → không tạo dòng; các bool `true` cũ → `Direction=input` (đúng doc của cột: "Model nhận input audio") | Trả lời builder-check Q4/Q5; DB user hiện có 0 dòng `SupportsTools=false` nên acceptance không đổi |

## 6. Mô hình dữ liệu mới (phase 1)

```
ModelCapability   (ModelId FK, Capability TEXT in ('tools','reasoning'), KnownAbsent INTEGER NOT NULL DEFAULT 0,
                   Source INTEGER NOT NULL, UpdatedAt TEXT NOT NULL, PK (ModelId, Capability))
ModelModality     (ModelId FK, Direction TEXT in ('input','output'), Modality TEXT, KnownAbsent INTEGER NOT NULL DEFAULT 0,
                   Source INTEGER NOT NULL, UpdatedAt TEXT NOT NULL, PK (ModelId, Direction, Modality))
ModelReasoningEffort (ModelId FK, Level TEXT, Source INTEGER NOT NULL, UpdatedAt TEXT NOT NULL,
                   PK (ModelId, Level))
```

- `Source`: `catalog` (danh mục chính thức của provider — opencode catalog / ollama tags / nvidia model page), `endpoint` (probe `/v1/models/{id}`), `user` (tick tay). Không có giá trị "models.dev".
- Quan hệ: FK → `Models(Id)` với `ON DELETE CASCADE` (xóa model là sạch hết fact).
- Đọc: **một accessor duy nhất** (`ModelCapabilityView`) cung cấp `HasTools/KnownTools`, `HasVision/Audio/Video` (suy từ modality), `HasReasoning`, `Efforts`, `InputModalities/OutputModalities`. Mọi consumer (badge Providers/Tools/Combos, combo calculator, generator config) chỉ đi qua accessor này.
- Ghi: một writer duy nhất per-source (nó biết `Source` của mình) — không cho phép code UI ghi trực tiếp bảng.
- Bỏ cột trên `Models`: `SupportsVision`, `SupportsThink`, `SupportsTools`, `SupportsAudio`, `SupportsVideo`, `ThinkEfforts`, `InputModalities`, `OutputModalities` (8 cột). `ContextWindow`, `MaxOutputTokens`, `DisplayName`, `ModelId`, `Enabled`, `IsManual` giữ.

**Dời dữ liệu trong cùng migration** (theo C-K + C-Q): `SupportsVision/Think = true` → dòng capability `Source=user`, `SupportsAudio/Video = true` → dòng `ModelModality(input, audio|video)` `Source=user` (hướng `input` vì doc cột cũ ghi rõ "Model nhận input audio"); `SupportsTools = true/false` → dòng `ModelCapability(tools)` với `KnownAbsent = 0/1`, `Source=user` (riêng cột này `false` từng mang nghĩa "đã biết là không" do code cũ ghi nó từ catalog — ngoại lệ có chủ đích); `SupportsVision/Think/Audio/Video = false` → **không tạo dòng**; ba cột JSON → parse ra dòng modality/effort, `Source=catalog` nếu model thuộc preset catalog (OpenCode Free), ngược lại `user`; JSON parse hỏng → bỏ dòng đó và log cảnh báo. Dữ liệu thật cần dời rất nhỏ: 12 vision, 16 think, 1 audio, 1 video, 0 tools-false, 10 modalities, 10 efforts — 137 model nên vòng lặp nhẹ.

**Ràng buộc rẻ kèm theo** (backlog B4/B5, cùng migration vì rẻ): unique index trên `Providers.Name`; cho phép `ProviderAccounts.ApiKeyEncrypted` NULL (app đã có luồng tài khoản không key; đang dùng chuỗi rỗng làm sentinel — giữ tương thích bằng cách coi rỗng = null khi đọc).

## 7. Nguồn capability per-provider (đã probe ngày 2026-10-19)

| Provider/preset | Nguồn | Lấy được gì | Chi phí / phạm vi phủ |
|---|---|---|---|
| OpenCode Free | `models.opencode.ai/api.json` (đang dùng) | tools, reasoning + effort levels, modalities, context/output — có cấu trúc | 0 request thêm; 10/10 |
| OpenRouter Free | `openrouter.ai/api/v1/models` — **ngay trong list**: `architecture.modality`, `architecture.input_modalities/output_modalities`, `supported_parameters` (có `tools`, `reasoning`…), `context_length`, `reasoning`, `supported_voices`, `pricing` | vision/audio/video, tools, reasoning, limits | 0 request thêm; toàn bộ |
| Ollama Cloud Free | `ollama.com/api/tags` → `capabilities: [completion, thinking, tools, vision, decision]`, `details.family` | reasoning→`reasoning`, tools→`tools`, vision→modality `image`; **không có audio/video** | 1 request; 22/22 (model id trong DB có dạng `name:tag` → khớp bằng cách bỏ `:tag`) |
| NVIDIA NIM Free | `build.nvidia.com/models.md` (index 200 trang) rồi trang model `<publisher>/<slug>` có `<aside aria-label="Model information">`: `Context Length`, `Parameters`, `Input Modalities`, `Output Modalities`, mục Capabilities `Function Calling/Structured Output/Reasoning = Supported` | chỉ trường in tường minh: context limit, input/output modalities, tools, reasoning | 1 request index + 1 request/trang; **chỉ 25/81** model trong DB còn có trang; 56 model legacy bỏ trống |
| Endpoint riêng (OpenAI-compatible user tự thêm) | `GET /v1/models/{id}` (đã có `ProviderEndpointMetadataProvider`) | `context_window`/`context_length`/`max_model_len`, `supported_modalities`/`architecture.*`, vision | best-effort per model |
| Không có nguồn | — | để trống, user tick tay | NVIDIA legacy 56, Ollama audio/video |

**Luật chi phí**: khi bấm nút ở U5, chỉ probe model **còn thiếu** dữ liệu; với NVIDIA phải lấy index một lần để biết id nào có trang (tránh 81 request vô ích); chạy tuần tự hoặc tối đa 2 request song song, có progress trên UI và có giới hạn thời gian tổng. Kết quả từng model lỗi không được làm hỏng cả đợt (best-effort, log).

## 8. Blast radius của phase 1 (những nơi phải sửa khi bỏ 8 cột)

`Model.cs`, `ModelMetadata.cs` + `ModelMetadataService`, `ProviderEndpointMetadataProvider`, `StaticCatalogMetadataProvider` (bỏ hẳn — thay bằng các provider theo nguồn chính thức), `FreeModelDetector` + `FreeModelSyncService` (merge/insert), `ModelService` (fetch/add/update), `ComboCapabilityCalculator`, `OpenCodeConfigService` (dựng `OpenCodeConfigModel`), `IOpenCodeConfigService.ToolsModelRow`, `Server/ProxyApp` (4 chỗ đọc flag), `Tools.razor` (badge), `Providers.razor` (bảng model + modal tick tay), `Combos.razor` (badge capability), migration mới + snapshot, và các test: `ModelServiceTests`, `ModelMetadataServiceTests`, `FreeModelSync*`, `OpenCodeConfig*`, `ComboCapability*`, `ProxyApp` liên quan. Plan phải chia slice để mỗi lần sửa là một commit xanh.

## 9. Trang Tools vòng 2 (phase 2) — hành vi

1. **Nhớ lựa chọn**: khi mở trang, nếu `AppSettings` có bản ghi lựa chọn cho lần trước thì dùng nó (bỏ id không còn tồn tại/đã bị xóa khỏi DB), nếu không thì theo quy tắc mặc định C7 như hiện tại. Mọi thay đổi tick được ghi lại ngay (không cần nút Lưu).
2. **Về mặc định**: nút tính lại đúng tập mặc định C7 (model `Enabled` của preset OpenCode Free khi provider bật), ghi đè bản ghi nhớ, và preview regenerate.
3. **Tìm kiếm**: một ô lọc, khớp không phân biệt hoa thường trên `DisplayName`, `ModelId`, và **tên combo**; khi lọc, nhóm nào không còn kết quả thì ẩn; số đếm hiển thị "x/y".
4. **Thu/giãn**: tên provider là header bấm được; mặc định giãn (như hiện tại); trạng thái thu lưu theo provider id.
5. **Chọn hết / bỏ hết theo provider**: hai nút nhỏ trong header nhóm; chỉ áp dụng model **đang hiển thị sau lọc** (để hành vi với tìm kiếm là dự đoán được).
6. **Combo trong bảng chọn**: mỗi combo của app xuất hiện như một hàng riêng (khuôn "Combo", phân biệt với model), tick = đưa combo vào config dưới **một entry** key = tên combo, capabilities = giao-an toàn các thành viên (C-L). Model thành viên **không** bị nhân bản ra config nếu đã tick combo. Hàng combo hiển thị mode (RR/FB) và số thành viên.
7. **Badge**: vision/audio/video/tools/reasoning chỉ hiện khi **đã biết**; model chưa biết thì không badge (không hiện "không có").
8. **Ghi vào config opencode** (nút cạnh Sao chép):
   - Đường dẫn mặc định `~/.config/opencode/opencode.json`; cho user đổi trong phần Cài đặt (một field, có nút "mặc định").
   - Đọc file, dùng parser JSON ở mức token để **định vị chính xác** object `provider["router-balancing"]` và `providers["router-balancing"]` (nếu có), cắt nguyên khối đó và chèn block mới vào `providers`; **mọi phần khác của file giữ nguyên byte** (plugin/mcp/agents/indent 4 space). File không có comment nhưng cách làm phải an toàn cho file có comment.
   - Không có key `providers` → chèn sau khi cắt block cũ (đúng indent của file); file hỏng JSON → báo lỗi rõ, **không ghi**.
   - Backup `<path>.bak` trước khi ghi; ghi tạm rồi đổi tên (atomic); nếu file bị khóa/quyền → toast lỗi, không làm hỏng preview đang xem.
   - Nội dung block: như generator hiện tại + `settings.timeout:false` (C-D) + key `<Identifier>/<ModelId>` (C-A) + entry combo (U12). Negative (`capabilities.tools=false`, `reasoning:{supported:false}`, `input`/`output` tường minh) chỉ được phát theo C-M: khi fact đến từ nguồn full-list hoặc do user đặt.
9. **Trang Providers/Combos**: thêm badge tools vào cột capability (U11) và dùng accessor chung (C-H).
10. **Editor capability ba trạng thái ở trang Providers** (thay modal tick tay hiện tại): mỗi mục `tools` / `reasoning` / `structured output` và mỗi modality theo **từng hướng input và output** có ba lựa chọn *có / không / chưa rõ* (U13 — hướng output là thứ modal cũ chưa từng có, vì cột audio/video cũ chỉ nói về input). Ghi qua writer với `Source=user`; UI hiện rõ nguồn của giá trị đang hiển thị (catalog / endpoint / user), và có nút U6 cho từng model.

## 10. Checklist nghiệm thu

- [ ] Migration chạy trên DB bản sao của user: 8 cột biến mất, các dòng fact đúng bằng số liệu ở §0 (12 vision → 12 modality `image` **input**, 1 audio, 1 video…); **không** sinh dòng known-absent từ `false` của các bool cũ, trừ `SupportsTools=false` (DB user hiện có 0 dòng này — test bằng fixture).
- [ ] Trang Providers: tick tay vision → badge hiện; bấm "Tải model free" cho OpenCode Free → 10 model có tools/reasoning/modalities đúng catalog; badge audio/video hiện cho model thật sự có (`mimo-v2.6-flash-free` input audio+video theo chính catalog opencode).
- [ ] Bấm lại nút sync/fill **không** làm mất một tick tay nào user đã đặt (kiểm chứng bằng test).
- [ ] Nút "Reset capability về catalog" xóa fact của model đó rồi điền lại từ catalog; với model không có catalog → về trạng thái chưa biết.
- [ ] Trang Tools: tắt app, mở lại → lựa chọn cũ còn nguyên; "Về mặc định" đưa về đúng tập C7.
- [ ] Tìm "beam" ra cả model `beam-free` (OpenCode) và combo `beam` nếu có; thu một nhóm rồi mở lại → trạng thái đúng.
- [ ] Tick combo `unsloth-studio` → config chỉ có **một** entry `unsloth-studio`, không có model thành viên; opencode gọi `router-balancing/unsloth-studio` → request vào dashboard app và đi theo mode của combo.
- [ ] Ghi config: file user giữ nguyên 100% phần khác; block cũ trong `provider` biến mất, block mới trong `providers` xuất hiện với key `opencode-free/big-pickle`; `.bak` tồn tại; mở opencode thấy model và gọi được qua proxy.
- [ ] Editor capability ba trạng thái: tick "audio **output** = có" trên một model → config phát `output:["text","audio"]`; chọn "chưa rõ" → hết fact, không còn negative nào.
- [ ] Negative trong config: model chỉ có fact từ endpoint probe (không full-list) → config **không** được chứa `tools:false` / `input` tường minh; model có fact từ ollama/NVIDIA/OpenRouter full-list hoặc do user đặt → được phép, và UI chỉ đúng nguồn của fact đó.
- [ ] Toàn bộ test hiện có xanh; mỗi slice mới có test (migration backfill, writer theo source, generator key/prefix, config-file splice, selection memory).

## 11. Để ngỏ cho builder/plan

- Cách đặt tên bảng/DTO chính xác (`ModelCapabilityView` hay `CapabilityReader`…), và cách tách slice phase 1 để mỗi commit đều xanh (bỏ 8 cột là thay đổi lớn: plan nên làm reader/accessor trước, cho các consumer chuyển sang reader, rồi mới migration bỏ cột — hoặc migration một lần theo U2 kèm một commit sửa consumer ngay sau đó; đây là việc của plan, không đổi nghiệp vụ).
- Bố cục cụ thể của editor capability ba trạng thái (§9.10) — bảng hay danh sách, cách hiển thị nguồn — để plan quyết, miễn giữ đúng hành vi đã mô tả.
- Có nên in badge "không hỗ trợ" cho fact `KnownAbsent` ở trang Tools/Providers hay không. **Mặc định của tôi: không** — badge tiêu cực chỉ hiện trong editor capability, để tránh người dùng đọc nhầm "chưa biết" thành "không có".

Đã trả lời xong builder-check (2026-10-20): Q1 → C-N, Q2 → C-O, Q3 → C-P, Q4 → U13 + C-Q, Q5 → C-Q; C-L chốt giao an toàn.
