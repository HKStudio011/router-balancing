# Free Account No-Key Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cho phép provider account **không có API key** (free endpoint) hoạt động ở mọi đường đi — chat forward, test connection, test-all, health probe, metadata/sync — thay vì bị filter bỏ; UI có toggle "Không dùng API key" + gate **test-before-save** cho account no-key.

**Spec:** `docs/superpowers/specs/2026-10-03-free-account-no-key-design.md` (đã review "ok"). Quyết định D1–D10 của spec là nguồn duy nhất khi plan mơ hồ.

**Architecture:** Không migration — no-key = `ProviderAccount.ApiKeyEncrypted == ""` (D1). `ProviderKeyResolver` phân biệt `null` (không có account → chặn) vs `""` (account no-key → probe/forward không auth) (D2). `ProviderRequestFactory` bỏ `Authorization`/`x-api-key` khi key rỗng (D7). Drafts thêm cờ `NoKey` (create provider + account CRUD). UI: toggle ở form tạo provider và form account; account no-key phải test pass mới save (D6, guard UI + service boundary). `ModelHealthWatchdog` **không đổi code** — dựa semantics mới của resolver (D8).

**Tech Stack:** .NET 10, EF Core SQLite (không migration), xUnit, MAUI Blazor Hybrid, Tailwind (pre-compile).

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên.
- Comment tiếng Việt cho "tại sao"; XML doc cho public API; exception message tiếng Anh.
- 1 task = 1 commit; message tiếng Anh, conventional (`feat:`/`fix:`/`test:`/`docs:`).
- Test infra: `TestDb` (file SQLite trong temp), `DbInitializer.Initialize(factory)`, `DpapiSecretProtector`, `NullLog`.
- Gates mỗi task (từ repo root, app `router-balancing` phải đóng):
  1. `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  2. `dotnet test "router balancing test/router balancing test.csproj"`
  - Test chạy nhanh hơn với filter `--filter "FullyQualifiedName~<ClassName>"` trong lúc dev; **task vẫn phải đóng bằng full test suite**.
- **Known flakes** (rerun → PASS, đừng chấp nhận fail thật): `ProxyControlApiTests`, `ProxyRetryIntegrationTests`, `ProxyQueueIntegrationTests.Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId`.
- i18n: key mới thêm vào CẢ 2 dict `English` + `Vietnamese` trong `src/RouterBalancing.Core/Localization/Translations.cs` (chèn cạnh nhóm `accounts.*` hiện có, cùng vị trí tương đối ở 2 dict); không dùng HTML entity cho tiếng Việt. `TranslationParityTests` tự kiểm EN/VI lệch key.
- **Không** build `.slnx`; build app chỉ `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` (Task 10).
- Không push; commit local trên master.

---

## File Map (decomposition)

| File | Trách nhiệm |
|---|---|
| `src/.../Providers/ProviderKeyResolver.cs` (sửa) | `""` = account no-key, không skip account; filter chỉ `Enabled` (D2) |
| `src/.../Providers/ProviderRequestFactory.cs` (sửa) | Bỏ auth header khi key rỗng (D7) |
| `src/.../Engine/ChatCompletionsHandler.cs` (sửa) | Guard `Unprotect("")` khi forward no-key |
| `src/.../Providers/ProviderService.cs` (sửa) | `CreateAsync` bắt buộc key hoặc `NoKey` (D4); `TestConnectionAsync` override `""` = ép no-key (D9) |
| `src/.../Providers/IProviderService.cs` (sửa) | XML doc 2 method trên |
| `src/.../Providers/ProviderDraft.cs` (sửa) | Thêm `NoKey` |
| `src/.../Providers/ProviderAccountDraft.cs` (sửa) | Thêm `NoKey` |
| `src/.../Providers/ProviderAccountValidator.cs` (sửa) | Bắt key chỉ khi `requireApiKey && !NoKey` |
| `src/.../Providers/ProviderAccountService.cs` (sửa) | Create/Update theo `NoKey` (D5); `TestAllAsync` guard `Unprotect("")` |
| `src/.../Providers/IProviderAccountService.cs` (sửa) | XML doc Create/Update |
| `src/.../Localization/Translations.cs` (sửa) | 3 key × 2 dict |
| `router-balancing/Components/Pages/Providers.razor` (sửa) | Toggle NoKey (create provider + account form), gate test-before-save, nút Test account, nhãn "no key" |
| Test: `Providers/ProviderKeyResolverTests.cs` (bổ sung) | 3 test semantics mới |
| Test: `Providers/ProviderRequestFactoryTests.cs` (bổ sung) | 2 test bỏ header |
| Test: `Providers/ProviderTestConnectionTests.cs` (sửa + bổ sung) | Đổi 1 test, thêm 2 test D9 |
| Test: `Engine/ChatCompletionsHandlerTests.cs` (sửa + bổ sung) | Stub bắt key, thêm 1 test |
| Test: `Providers/ProviderServiceTests.cs` (sửa) | Thay 1 test create-blank bằng 2 test |
| Test: `Providers/ProviderAccountServiceTests.cs` (bổ sung) | 3 test no-key CRUD/TestAll |
| Test: `Engine/ModelHealthWatchdogTests.cs` (bổ sung) | Pin probe no-key (characterization) |
| `.superpowers/sdd/progress.md` (sửa) | Ledger entry cho việc #2 |

---

### Task 1: Resolver — phân biệt "không có account" vs "account no-key"

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderKeyResolver.cs`
- Modify: `router balancing test/Providers/ProviderKeyResolverTests.cs`

**Interfaces:**
- Produces: `ResolveFirstEnabledKey` trả `""` khi account enabled đầu tiên không có key; KHÔNG skip qua account kế (chọn theo Priority/Id trên tập `Enabled`); `null` chỉ khi không có account enabled nào.
- Consumes: none.

- [ ] **Step 1: Viết 3 test failing trong `ProviderKeyResolverTests.cs`** (chèn sau test `ResolveFirstEnabledKey_WhenNoAccounts_ReturnsNull`, cuối file):

```csharp
[Fact]
public void ResolveFirstEnabledKey_WhenEnabledAccountHasNoKey_ReturnsEmptyString()
{
    var provider = new Provider
    {
        Accounts =
        [
            new ProviderAccount { Id = 1, Name = "free", Enabled = true, ApiKeyEncrypted = string.Empty },
        ],
    };

    // "" ≠ null: "" = account no-key (probe/forward KHÔNG auth), null = không có account (chặn)
    Assert.Equal(string.Empty, ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector));
}

[Fact]
public void ResolveFirstEnabledKey_WhenFirstEnabledIsNoKey_DoesNotSkipToNextAccount()
{
    var provider = new Provider
    {
        Accounts =
        [
            new ProviderAccount { Id = 1, Name = "free", Enabled = true, Priority = 0, ApiKeyEncrypted = string.Empty },
            new ProviderAccount { Id = 2, Name = "paid", Enabled = true, Priority = 5, ApiKeyEncrypted = _protector.Protect("sk-paid") },
        ],
    };

    // Chọn theo Priority trên tập Enabled — account no-key đứng trước thì trả "" chứ
    // không nhảy sang account có key (spec D2: không hidden failover ở tầng resolver)
    Assert.Equal(string.Empty, ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector));
}

[Fact]
public void ResolveFirstEnabledAccount_WhenFirstEnabledHasNoKey_ReturnsThatAccount()
{
    var provider = new Provider
    {
        Accounts =
        [
            new ProviderAccount { Id = 1, Name = "free", Enabled = true, ApiKeyEncrypted = string.Empty },
        ],
    };

    var account = ProviderKeyResolver.ResolveFirstEnabledAccount(provider);

    Assert.NotNull(account);
    Assert.Equal("free", account.Name);
}
```

- [ ] **Step 2: Chạy red** — `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderKeyResolverTests"` → 3 test mới FAIL (hiện filter `!string.IsNullOrEmpty` loại account không key → trả `null`).

- [ ] **Step 3: Sửa `ProviderKeyResolver.cs`** — thay toàn bộ nội dung 2 method + XML doc:

```csharp
/// <summary>
/// Key plaintext của account enabled đầu tiên (Priority tăng dần, tie-break Id tăng dần):
/// <see langword="null"/> nếu không có account khả dụng hoặc nav Accounts chưa load;
/// <c>""</c> nếu account đó là no-key (spec free-account D2 — khác null để caller
/// phân biệt "không probe/forward" vs "probe không auth").
/// </summary>
public static string? ResolveFirstEnabledKey(Provider provider, ISecretProtector protector)
{
    var account = ResolveFirstEnabledAccount(provider);
    if (account is null)
    {
        return null;
    }
    // Không Unprotect("") — DPAPI ném CryptographicException; no-key lưu cột rỗng
    return string.IsNullOrEmpty(account.ApiKeyEncrypted)
        ? string.Empty
        : protector.Unprotect(account.ApiKeyEncrypted);
}

/// <summary>
/// Account enabled đầu tiên (Priority tăng, tie-break Id) — null nếu không có.
/// Chỉ filter Enabled: account no-key (key rỗng) VẪN được chọn (spec free-account D2).
/// </summary>
public static ProviderAccount? ResolveFirstEnabledAccount(Provider provider) =>
    provider.Accounts?.Where(a => a.Enabled)
        .OrderBy(a => a.Priority).ThenBy(a => a.Id).FirstOrDefault();
```

- [ ] **Step 4: Chạy xanh** — Step 2 filter → PASS. Gates task: build Core + full test → PASS (chưa có caller nào bị đổi hành vi trong test hiện có: `TestConnection_WhenNoEnabledAccounts_SendsEmptyKey` vẫn qua vì account bị disable → vẫn `null`).

- [ ] **Step 5: Commit** — `feat: resolve enabled no-key account to empty key in provider key resolver`

---

### Task 2: Request factory — bỏ auth header khi key rỗng

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs`
- Modify: `router balancing test/Providers/ProviderRequestFactoryTests.cs`
- Modify: `router balancing test/Providers/ProviderTestConnectionTests.cs` (đổi 1 test cũ theo hành vi mới)

**Interfaces:**
- Produces: `Create(provider, apiKey:"")` → OpenAI không có header `Authorization`; Anthropic không có `x-api-key`, vẫn có `anthropic-version`.
- Consumes: key `""` từ Task 1.

- [ ] **Step 1: Viết 2 test failing trong `ProviderRequestFactoryTests.cs`** (chèn cuối file):

```csharp
[Fact]
public void Create_WhenKeyEmpty_OpenAiOmitsAuthorizationHeader()
{
    using var request = ProviderRequestFactory.Create(P("https://api.openai.com"), string.Empty);

    // Key rỗng = free endpoint không auth — KHÔNG gửi "Bearer" trần (D7)
    Assert.Null(request.Headers.Authorization);
}

[Fact]
public void Create_WhenKeyEmpty_AnthropicOmitsApiKeyButKeepsVersion()
{
    var provider = P("https://api.anthropic.com");
    provider.Type = ProviderType.Anthropic;

    using var request = ProviderRequestFactory.Create(provider, string.Empty);

    Assert.False(request.Headers.Contains("x-api-key"));
    // anthropic-version bắt buộc kể cả không key
    Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
}
```

- [ ] **Step 2: Chạy red** — `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderRequestFactoryTests"` → 2 test mới FAIL (`AuthenticationHeaderValue("Bearer", "")` vẫn được gắn; `TryAddWithoutValidation("x-api-key", "")` vẫn thêm header rỗng).

- [ ] **Step 3: Sửa `ProviderRequestFactory.cs`** — thay `switch` (giữ nguyên phần canonicalize phía trên) và cập nhật XML doc summary:

```csharp
switch (provider.Type)
{
    case ProviderType.OpenAI:
        // Key rỗng = account no-key → bỏ hẳn Authorization (D7) — "Bearer" trần làm
        // nhiều upstream trả 401 oan dù server không yêu cầu key
        if (apiKey.Length > 0)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        break;
    case ProviderType.Anthropic:
        if (apiKey.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        break;
    default:
        throw new ArgumentOutOfRangeException(nameof(provider.Type), provider.Type, "Unsupported provider type.");
}
```

XML doc method — dòng summary hiện tại: `...Header theo Type: OpenAI → <c>Authorization: Bearer</c>; Anthropic → <c>x-api-key</c> + <c>anthropic-version</c>.` → thêm: `(key rỗng = bỏ header auth — no-key, vẫn giữ anthropic-version)`. Param `<paramref name="apiKey"/>`: `Key plaintext sẽ gắn Authorization/x-api-key; rỗng = không gắn header auth.`

- [ ] **Step 4: Chạy xanh factory tests** — Step 2 filter → PASS.

- [ ] **Step 5: Cập nhật test cũ `ProviderTestConnectionTests.TestConnection_WhenNoEnabledAccounts_SendsEmptyKey`** — hành vi mới: không có account enabled → key `""` → **không có** header Authorization (trước: header `Bearer` trần). Thay method (đổi luôn tên cho đúng nghĩa):

```csharp
[Fact]
public async Task TestConnection_WhenNoEnabledAccounts_SendsNoAuthorizationHeader()
{
    var handler = new FakeHandler(HttpStatusCode.OK);
    var provider = await SavedProviderAsync();
    provider.Accounts[0].Enabled = false; // entity trong tay — resolver phải bỏ account tắt
    var service = ServiceWith(handler);

    await service.TestConnectionAsync(provider, apiKeyOverride: null);

    // Không có account enabled → key "" → factory bỏ hẳn header auth (D7)
    Assert.Null(handler.LastRequest!.Headers.Authorization);
}
```

- [ ] **Step 6: Chạy xanh** — `--filter "FullyQualifiedName~ProviderTestConnectionTests"` → PASS. Gates task: build Core + full test → PASS.

- [ ] **Step 7: Commit** — `fix: omit provider auth headers when api key is empty`

---

### Task 3: Chat forward — no-key account gửi key rỗng, không Unprotect

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs`
- Modify: `router balancing test/Engine/ChatCompletionsHandlerTests.cs`

**Interfaces:**
- Produces: `ForwardAsync` với account `ApiKeyEncrypted == ""` → gọi upstream với `apiKey == ""`, trả `DispatchOutcome.Handled` khi upstream 200.
- Consumes: resolver Task 1.

- [ ] **Step 1: Sửa stub `StubUpstream` trong `ChatCompletionsHandlerTests.cs`** (dòng 58–63) để bắt key:

```csharp
private sealed class StubUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
{
    public string? LastApiKey { get; private set; }

    public Task<HttpResponseMessage> PostChatCompletionAsync(
        Provider provider, string apiKey, byte[] body, CancellationToken ct)
    {
        LastApiKey = apiKey;
        return Task.FromResult(factory());
    }
}
```

- [ ] **Step 2: Viết test failing** (chèn ngay sau test `ForwardAsync_WhenNoEnabledKey_ReturnsError503WithoutWritingResponse`, dòng ~183):

```csharp
[Fact]
public async Task ForwardAsync_WhenNoKeyAccount_SendsEmptyKeyAndHandles()
{
    var log = new CapturingLog();
    var provider = SeedProvider();
    provider.Accounts[0].ApiKeyEncrypted = string.Empty; // account no-key đã lưu
    var upstream = new StubUpstream(() => Upstream(200, "{}"));
    var sut = Create(upstream, log);
    var ctx = Ctx();

    var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

    Assert.IsType<DispatchOutcome.Handled>(outcome);
    Assert.Equal(string.Empty, upstream.LastApiKey); // upstream không auth, không throw
}
```

- [ ] **Step 3: Chạy red** — `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ChatCompletionsHandlerTests"` → test mới FAIL với `CryptographicException` (dòng 102 `protector.Unprotect(account.ApiKeyEncrypted)` ném với `""` — bắt buộc fail kiểu này, KHÔNG nuốt bằng try/catch chung).

- [ ] **Step 4: Sửa `ChatCompletionsHandler.cs`** — thay dòng 102:

```csharp
// No-key account: cột key rỗng → gửi "" (upstream không auth) — Unprotect("") ném
// CryptographicException nên phải rẽ nhánh tường minh (spec free-account D7)
var key = string.IsNullOrEmpty(account.ApiKeyEncrypted)
    ? string.Empty
    : protector.Unprotect(account.ApiKeyEncrypted);
```

- [ ] **Step 5: Chạy xanh** — Step 3 filter → PASS (test `ForwardAsync_WhenNoEnabledKey_...` cũ không ảnh hưởng: `SeedProvider(withKey: false)` = không có account → vẫn 503). Gates task: build Core + full test → PASS.

- [ ] **Step 6: Commit** — `fix: forward chat request for no-key account without decrypting`

---

### Task 4: TestConnectionAsync — override `""` ép probe không key (D9)

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderService.cs`
- Modify: `src/RouterBalancing.Core/Providers/IProviderService.cs` (XML doc)
- Modify: `router balancing test/Providers/ProviderTestConnectionTests.cs`

**Interfaces:**
- Produces: `TestConnectionAsync(provider, apiKeyOverride: "")` → probe KHÔNG header auth kể cả account đã lưu key; `apiKeyOverride: null` → giữ nguyên fallback account enabled đầu tiên (hành vi cũ).
- Consumes: Task 2 (factory bỏ header khi `""`).

- [ ] **Step 1: Viết 2 test failing trong `ProviderTestConnectionTests.cs`** (chèn sau test `TestConnection_WhenNoOverride_DecryptsFirstEnabledAccountKey`):

```csharp
[Fact]
public async Task TestConnection_WhenOverrideEmpty_ForcesNoKeyProbe()
{
    var handler = new FakeHandler(HttpStatusCode.OK);
    var provider = await SavedProviderAsync(); // key đã lưu = "sk-saved"
    var service = ServiceWith(handler);

    // Override "" = ép probe không key dù account có key — UI test account no-key (D9);
    // null mới là fallback account
    var result = await service.TestConnectionAsync(provider, apiKeyOverride: "");

    Assert.True(result.Success);
    Assert.Null(handler.LastRequest!.Headers.Authorization);
}

[Fact]
public async Task TestConnection_WhenNoKeyAccount_SucceedsWithoutAuthorization()
{
    var handler = new FakeHandler(HttpStatusCode.OK);
    var provider = await SavedProviderAsync();
    provider.Accounts[0].ApiKeyEncrypted = string.Empty; // account no-key
    var service = ServiceWith(handler);

    var result = await service.TestConnectionAsync(provider, apiKeyOverride: null);

    // Trước khi fix: Unprotect("") ném → caught → Success=false
    Assert.True(result.Success);
    Assert.Null(handler.LastRequest!.Headers.Authorization);
}
```

- [ ] **Step 2: Chạy red** — `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderTestConnectionTests"` → test 1 FAIL (override `""` rơi vào `string.IsNullOrEmpty(key)` → fallback → `Bearer sk-saved` không null), test 2 FAIL (`Unprotect("")` → `CryptographicException` → caught → `Success=false`).

- [ ] **Step 3: Sửa `ProviderService.TestConnectionAsync`** — thay đoạn lines 151–159 (comment + gán `key`); giữ nguyên phần `ProxyTarget` bên dưới:

```csharp
// apiKeyOverride: null = fallback account enabled đầu tiên; "" = ÉP probe không key
// (spec free-account D9) — KHÔNG gộp 2 nhánh bằng string.IsNullOrEmpty(apiKeyOverride),
// vì "" chính là tín hiệu "force no-key" của UI test account.
// Unprotect PHẢI nằm trong đây: key DPAPI hỏng (CryptographicException) rơi vào
// catch → fail với lý do, không ném ra UI.
var account = ProviderKeyResolver.ResolveFirstEnabledAccount(provider);
string key;
if (apiKeyOverride is not null)
{
    key = apiKeyOverride;
}
else if (account is not null && !string.IsNullOrEmpty(account.ApiKeyEncrypted))
{
    key = _protector.Unprotect(account.ApiKeyEncrypted);
}
else
{
    // Không có account, hoặc account no-key (cột rỗng)
    key = string.Empty;
}
```

> Lưu ý: viết đúng `apiKeyOverride is not null` — KHÔNG phải `!string.IsNullOrEmpty(apiKeyOverride)`.

- [ ] **Step 4: Sửa XML doc `IProviderService.TestConnectionAsync`** (dòng ~35–39) — câu hiện tại mô tả `apiKeyOverride rỗng → ...` thay bằng:

```csharp
/// <summary>
/// Test kết nối: GET {BaseUrl}/v1/models.
/// <paramref name="apiKeyOverride"/> <see langword="null"/> = dùng key account enabled đầu tiên;
/// <c>""</c> = ép probe không key (no-key, không gửi header auth) — spec free-account D9.
/// </summary>
```

- [ ] **Step 5: Chạy xanh** — Step 2 filter → PASS. Gates task: build Core + full test → PASS.

- [ ] **Step 6: Commit** — `feat: honor empty apiKeyOverride as force-no-key connection test`

---

### Task 5: Provider create — cờ `NoKey`, luôn tạo ≥1 account (D4)

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderDraft.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderService.cs`
- Modify: `src/RouterBalancing.Core/Providers/IProviderService.cs` (XML doc `CreateAsync`)
- Modify: `router balancing test/Providers/ProviderServiceTests.cs` (thay 1 test)

**Interfaces:**
- Produces: `ProviderDraft.NoKey` (bool, default `false`); `CreateAsync` ném `ArgumentException` khi key rỗng/trắng và `NoKey=false`; khi `NoKey=true` luôn tạo account "Default" với `ApiKeyEncrypted == ""`.
- Consumes: none.

- [ ] **Step 1: Thay test cũ `Create_WhenKeyBlank_NoAccountCreated`** (ProviderServiceTests dòng 60–67) bằng 2 test:

```csharp
[Fact]
public async Task Create_WhenNoKeyFlag_CreatesDefaultAccountEmptyKey()
{
    var draft = Draft(key: string.Empty);
    draft.NoKey = true;

    var provider = await _service.CreateAsync(draft);

    using var db = _db.CreateDbContext();
    var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == provider.Id);
    Assert.Equal("Default", account.Name);
    // No-key = cột rỗng, KHÔNG Protect("") (D1) — resolver đọc cột rỗng = no-key
    Assert.Equal(string.Empty, account.ApiKeyEncrypted);
}

[Fact]
public async Task Create_WhenKeyBlankWithoutNoKey_ThrowsArgumentException()
{
    // UI chặn trước bằng lỗi keyOrNoKey; service boundary vẫn bắt buộc key hoặc NoKey (D4)
    await Assert.ThrowsAsync<ArgumentException>(
        () => _service.CreateAsync(Draft(key: string.Empty)));
}
```

- [ ] **Step 2: Chạy red** — `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderServiceTests"` → FAIL (compile lỗi `NoKey` chưa tồn tại → thêm property trước rồi chạy lại để thấy red đúng semantics: test 1 FAIL vì không tạo account, test 2 FAIL vì không throw).

- [ ] **Step 3: Thêm property vào `ProviderDraft.cs`** (sau `ApiKey`, dòng 22):

```csharp
/// <summary>Tạo account "Default" không key (free endpoint) — true thì <see cref="ApiKey"/> bị bỏ qua
/// và cột key lưu rỗng (spec free-account D4).</summary>
public bool NoKey { get; set; }
```

- [ ] **Step 4: Sửa `ProviderService.CreateAsync`** — chèn guard **đầu method** (sau `using var db`, trước `EnsureIdentifierUsableAsync`, dòng ~61), và thay block tạo account lines 73–84:

```csharp
// Không có cờ NoKey mà key trống = form chưa điền — chặn ở service boundary (D4);
// UI đã hiện lỗi accounts.error.keyOrNoKey trước đó
if (!draft.NoKey && string.IsNullOrWhiteSpace(draft.ApiKey))
{
    throw new ArgumentException("API key is required unless NoKey is set.");
}
```

```csharp
// Key ở create = tạo kèm account "Default" — key sống hoàn toàn ở ProviderAccount (spec §4.2).
// Luôn tạo đúng 1 account: có key → mã hóa DPAPI; NoKey → cột rỗng (free endpoint, D1/D4)
provider.Accounts.Add(new ProviderAccount
{
    Name = "Default",
    ApiKeyEncrypted = draft.NoKey ? string.Empty : _protector.Protect(draft.ApiKey),
    Enabled = true,
    Weight = 100,
    Priority = 0,
});
```

- [ ] **Step 5: Sửa XML doc `IProviderService.CreateAsync`** (dòng ~14): mô tả hiện tại `draft.ApiKey không rỗng → tạo kèm account "Default"` → thay bằng:

```csharp
/// <summary>Tạo provider mới từ bản nháp. Luôn tạo kèm account "Default":
/// <c>draft.NoKey</c> = true → cột key rỗng (no-key); ngược lại <c>draft.ApiKey</c> bắt buộc
/// (rỗng mà không NoKey → <see cref="ArgumentException"/>; spec free-account D4).</summary>
```

- [ ] **Step 6: Chạy xanh** — Step 2 filter → PASS (các test còn lại dùng `Draft()` default key `"sk-secret"` nên không dính guard). Gates task: build Core + full test → PASS.

- [ ] **Step 7: Commit** — `feat: allow creating provider without api key via NoKey flag`

---

### Task 6: Account CRUD no-key + TestAll không Unprotect rỗng (D5)

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderAccountDraft.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderAccountValidator.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderAccountService.cs`
- Modify: `src/RouterBalancing.Core/Providers/IProviderAccountService.cs` (XML doc Create/Update)
- Modify: `router balancing test/Providers/ProviderAccountServiceTests.cs`

**Interfaces:**
- Produces: `ProviderAccountDraft.NoKey`; Create với `NoKey=true` lưu cột rỗng kể cả `ApiKey` có chữ; Create `NoKey=false` + key trống vẫn throw (`requireApiKey`); Update `NoKey=true` → XÓA key đã lưu; Update `NoKey=false` + key trống → giữ key cũ (hành vi cũ, đã có test pin); `TestAllAsync` probe account no-key với key `""` không throw.
- Consumes: Task 2 (factory), Task 4 (pattern guard).

- [ ] **Step 1: Viết 3 test failing trong `ProviderAccountServiceTests.cs`** (chèn sau test `Create_EmptyPattern_ThrowsArgument` cho 2 test Create/Update; test TestAll chèn sau `TestAllAsync_NoEnabledAccounts_SetsProviderTestNull`):

```csharp
[Fact]
public async Task Create_NoKeyWithEmptyKey_SavesEmptyCiphertext()
{
    var providerId = await SeedProviderAsync();
    var draft = Draft(providerId, key: string.Empty);
    draft.NoKey = true;

    var account = await _service.CreateAsync(draft);

    // No-key = cột rỗng — KHÔNG Protect("") (D1)
    Assert.Equal(string.Empty, account.ApiKeyEncrypted);
}
```

```csharp
[Fact]
public async Task Update_NoKeyTrue_ClearsSavedKey()
{
    var providerId = await SeedProviderAsync();
    var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));
    var draft = Draft(providerId, name: "acct", key: string.Empty);
    draft.NoKey = true;

    await _service.UpdateAsync(account.Id, draft);

    using var db = _db.CreateDbContext();
    var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
    // NoKey=true là hành động chủ động xóa key (D5) — khác key trống mặc định = giữ (đã có test pin)
    Assert.Equal(string.Empty, saved.ApiKeyEncrypted);
}
```

```csharp
[Fact]
public async Task TestAllAsync_NoKeyAccount_ProbesWithoutAuthorization()
{
    var providerId = await SeedProviderAsync(("free", true, 0)); // seed có key "sk-free"
    using (var db = _db.CreateDbContext())
    {
        var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == providerId);
        account.ApiKeyEncrypted = string.Empty; // chuyển sang no-key
        await db.SaveChangesAsync();
    }
    var handler = new CapturingHandler(HttpStatusCode.OK);
    var service = ServiceWith(handler);

    var results = await service.TestAllAsync(providerId);

    // Trước khi fix: Unprotect("") ném CryptographicException → caught → success=false
    Assert.True(Assert.Single(results).Success);
    Assert.Null(handler.LastRequest!.Headers.Authorization);
}
```

- [ ] **Step 2: Thêm helper `CapturingHandler`** (chèn ngay sau `FixedHandler`, dòng ~66):

```csharp
/// <summary>Trả status cố định cho mọi request + ghi request để assert header.</summary>
private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }
}
```

- [ ] **Step 3: Chạy red** — `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderAccountServiceTests"` → compile lỗi `NoKey` chưa tồn tại; thêm property (Step 4) rồi chạy lại → 3 test FAIL theo semantics (test 2: `Update` không xóa; test 3: `success=false`).

- [ ] **Step 4: Thêm property vào `ProviderAccountDraft.cs`** (sau `ApiKey`, dòng 16):

```csharp
/// <summary>Không dùng API key (free endpoint): Create/Update ghi cột key rỗng,
/// bỏ qua <see cref="ApiKey"/> (spec free-account D5).</summary>
public bool NoKey { get; set; }
```

- [ ] **Step 5: Sửa `ProviderAccountValidator.cs`** — dòng 23:

```csharp
if (requireApiKey && !draft.NoKey && string.IsNullOrWhiteSpace(draft.ApiKey))
{
    throw new ArgumentException("API key is required.");
}
```

Cập nhật XML doc `<param name="requireApiKey">`: `Create bắt buộc key (trừ khi draft.NoKey); update rỗng = giữ key cũ.`

- [ ] **Step 6: Sửa `ProviderAccountService.CreateAsync`** — dòng 65:

```csharp
// NoKey → cột rỗng (không Protect("")); có key → DPAPI như cũ
ApiKeyEncrypted = draft.NoKey ? string.Empty : _protector.Protect(draft.ApiKey.Trim()),
```

- [ ] **Step 7: Sửa `ProviderAccountService.UpdateAsync`** — thay block lines 96–101:

```csharp
// Key rỗng (kể cả toàn khoảng trắng) khi sửa = giữ nguyên key cũ —
// nếu không, "   ".Trim() sẽ persist key rỗng và traffic 401 âm thầm.
// NoKey=true = chủ động XÓA key → cột rỗng (spec free-account D5)
if (draft.NoKey)
{
    account.ApiKeyEncrypted = string.Empty;
}
else if (!string.IsNullOrWhiteSpace(draft.ApiKey))
{
    account.ApiKeyEncrypted = _protector.Protect(draft.ApiKey.Trim());
}
```

- [ ] **Step 8: Sửa `ProviderAccountService.TestAllAsync`** — dòng 173:

```csharp
// No-key account (cột rỗng) → probe không header auth; Unprotect("") sẽ ném
var key = string.IsNullOrEmpty(account.ApiKeyEncrypted)
    ? string.Empty
    : _protector.Unprotect(account.ApiKeyEncrypted);
```

- [ ] **Step 9: Sửa XML doc `IProviderAccountService`**:
  - `CreateAsync` (dòng ~11): `Tạo account mới; <c>draft.ApiKey</c> bắt buộc.` → `Tạo account mới; <c>draft.ApiKey</c> bắt buộc trừ khi <c>draft.NoKey</c> (lưu key rỗng).`
  - `UpdateAsync` (dòng ~17): thêm vào câu hiện tại: `; <c>draft.NoKey</c> = true thì XÓA key đã lưu (cột rỗng).`

- [ ] **Step 10: Chạy xanh** — Step 3 filter → PASS (test Theory `Create_InvalidDraft_ThrowsArgument` case key rỗng vẫn throw vì `NoKey` default `false`; 2 test giữ-key-cũ cũ không đổi hành vi). Gates task: build Core + full test → PASS.

- [ ] **Step 11: Commit** — `feat: support no-key accounts in account CRUD and test-all`

---

### Task 7: Watchdog — pin probe no-key (characterization, D8)

**Files:**
- Modify: `router balancing test/Engine/ModelHealthWatchdogTests.cs`

**Interfaces:**
- Produces: test pin — account no-key → watchdog gọi upstream với `apiKey == ""` (code watchdog KHÔNG sửa — dựa Task 1).
- Consumes: Task 1.

- [ ] **Step 1: Mở rộng `ScriptedUpstream`** (dòng 230–244) — thêm property + ghi nhận:

```csharp
public int Calls => Volatile.Read(ref _calls);
public byte[]? LastBody { get; private set; }
public string? LastApiKey { get; private set; }

public Task<HttpResponseMessage> PostChatCompletionAsync(
    Provider provider, string apiKey, byte[] body, CancellationToken ct)
{
    Interlocked.Increment(ref _calls);
    LastBody = body;
    LastApiKey = apiKey;
    return Task.FromResult(factory());
}
```

- [ ] **Step 2: Thêm helper + test pin** (helper đặt cạnh `Candidate`, test đặt sau `ProbeDueAsync_WhenProviderHasNoEnabledKey_RecordsProbeFailureWithoutCall`):

```csharp
// Candidate có account no-key (cột rỗng) — pin D8: resolver "" → VẪN probe, không chặn
private ModelCandidate NoKeyCandidate()
{
    var candidate = Candidate();
    candidate.Provider.Accounts[0].ApiKeyEncrypted = string.Empty;
    return candidate;
}
```

```csharp
[Fact]
public async Task ProbeDueAsync_WhenAccountHasNoKey_ProbesWithEmptyKey()
{
    OpenFuse("m1");
    var upstream = new ScriptedUpstream(() => Sse());
    var sut = CreateWatchdog(upstream, new StubResolver(NoKeyCandidate()));

    await sut.ProbeDueAsync(CancellationToken.None);

    // "" ≠ null: probe CHẠY với key rỗng (không auth) — test chặn null mới vào nhánh fail
    Assert.Equal(1, upstream.Calls);
    Assert.Equal(string.Empty, upstream.LastApiKey);
}
```

- [ ] **Step 3: Chạy xanh** — `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ModelHealthWatchdogTests"` → PASS (characterization: hành vi này đã đúng từ Task 1; nếu FAIL nghĩa là Task 1 sai hoặc bị regress). Gates task: build Core + full test → PASS.

- [ ] **Step 4: Commit** — `test: pin no-key account probe in model health watchdog`

---

### Task 8: i18n — 3 key × 2 dict

**Files:**
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs`

**Interfaces:**
- Produces: `accounts.field.noKey`, `accounts.placeholder.noKey`, `accounts.error.keyOrNoKey` ở cả `English` và `Vietnamese`.
- Consumes: UI Task 9.

- [ ] **Step 1: Dict `English`** — chèn 3 dòng vào 3 vị trí (cạnh key cùng nhóm):
  - Sau `["accounts.field.apiKey"]` (dòng 246): `["accounts.field.noKey"] = "No API key",`
  - Sau `["accounts.placeholder.keySaved"]` (dòng 255): `["accounts.placeholder.noKey"] = "No key required",`
  - Sau `["accounts.error.lastAccount"]` (dòng 267): `["accounts.error.keyOrNoKey"] = "Enter an API key or enable \"No API key\".",`

- [ ] **Step 2: Dict `Vietnamese`** — chèn tương ứng:
  - Sau `["accounts.field.apiKey"]` (dòng 557): `["accounts.field.noKey"] = "Không dùng API key",`
  - Sau `["accounts.placeholder.keySaved"]` (dòng 566): `["accounts.placeholder.noKey"] = "Không cần key",`
  - Sau `["accounts.error.lastAccount"]` (dòng 578): `["accounts.error.keyOrNoKey"] = "Nhập API key hoặc bật \"Không dùng API key\".",`

> Bắt buộc gõ trực tiếp tiếng Việt (không HTML entity); cả 2 dict phải cùng bộ key thứ tự tương đương.

- [ ] **Step 3: Chạy xanh** — `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~Translation"` → PASS (parity). Gates task: build Core + full test → PASS.

- [ ] **Step 4: Commit** — `feat: add no-key translation keys`

---

### Task 9: UI Providers.razor — toggle + gate test-before-save

**Files:**
- Modify: `router-balancing/Components/Pages/Providers.razor`

**Interfaces:**
- Produces: checkbox "Không dùng API key" ở form tạo provider và form account; nút Test trong form account (chỉ hiện khi NoKey); `CanSave`/`CanSaveAccount` chặn cứng (D6); nhãn "no key" trong cột info danh sách account.
- Consumes: Task 4 (`apiKeyOverride: ""`), Task 5/6 (`NoKey` flags), Task 8 (keys).

> Task UI không có unit test — Blazor page. Kiểm bằng checklist **Manual verification** ở cuối task; gates build vẫn chạy.

- [ ] **Step 1: Thêm state fields** — sau `_accountForm` declaration (dòng ~663):

```csharp
private AccountForm? _accountForm;
private ProviderAccount? _confirmDeleteAccount;
// Test "no-key" trong form account — signature pin Type|BaseUrl|NoKey, đổi form = test mất hiệu lực
private ProviderTestResult? _accountTestResult;
private string? _accountTestedSignature;
private bool _testingAccountNoKey;
```

- [ ] **Step 2: Thêm `NoKey` vào `AccountForm`** (sau `ApiKey`, dòng ~673):

```csharp
public string ApiKey { get; set; } = string.Empty;

/// <summary>Không dùng API key — input key bị disable, phải test pass mới save được.</summary>
public bool NoKey { get; set; }
```

- [ ] **Step 3: Reset test state** — thêm helper (đặt cạnh `CancelAccountForm`) và gọi ở 3 nơi:

```csharp
private void ResetAccountTestState()
{
    _accountTestResult = null;
    _accountTestedSignature = null;
}
```

- `AddAccountRow` (dòng 1253):
```csharp
private void AddAccountRow()
{
    _accountForm = new AccountForm();
    ResetAccountTestState();
}
```
- `BeginEditAccount` (dòng 1255–1266): thêm `NoKey = string.IsNullOrEmpty(account.ApiKeyEncrypted),` vào initializer (sau `Id`) + gọi `ResetAccountTestState();` — chuyển method thành block:

```csharp
private void BeginEditAccount(ProviderAccount account)
{
    _accountForm = new AccountForm
    {
        Id = account.Id,
        // Account đang lưu không key = toggle NoKey được bật sẵn (D5)
        NoKey = string.IsNullOrEmpty(account.ApiKeyEncrypted),
        Name = account.Name,
        Enabled = account.Enabled,
        PatternsText = DecodePatterns(account.ModelPatterns),
        Weight = account.Weight,
        Priority = account.Priority,
        DailyTokenLimit = account.DailyTokenLimit,
        DailyRequestLimit = account.DailyRequestLimit,
    };
    ResetAccountTestState();
}
```
- `CancelAccountForm` (dòng 1268):
```csharp
private void CancelAccountForm()
{
    _accountForm = null;
    ResetAccountTestState();
}
```

- [ ] **Step 4: Form tạo provider — toggle NoKey** (thay block `@if (_editingId is null)` lines 358–367):

```razor
@if (_editingId is null)
{
    <label class="flex items-center gap-1.5 text-sm sm:col-span-2">
        <input type="checkbox" disabled="@_busy" @bind="_draft.NoKey" />
        @L["accounts.field.noKey"]
    </label>
    <label class="flex flex-col gap-1 text-sm sm:col-span-2">
        @L["providers.field.apiKey"]
        @* Key tạo account "Default" khi save (spec §4.2) — NoKey = bỏ qua key, save bắt buộc test pass *@
        <input type="password" class="rounded border border-border bg-surface px-2 py-1.5"
               autocomplete="off" spellcheck="false"
               disabled="@(_busy || _draft.NoKey)"
               placeholder="@(_draft.NoKey ? L["accounts.placeholder.noKey"] : null)"
               @bind="_draft.ApiKey" />
    </label>
}
```

- [ ] **Step 5: Hiện lỗi `keyOrNoKey` dưới modal** — chèn sau block lỗi `Identifier` (dòng 382–385):

```razor
@if (ModalError(nameof(ProviderDraft.ApiKey)) is { } apiKeyError)
{
    <div class="mt-1 text-sm text-danger">@L[apiKeyError]</div>
}
```

- [ ] **Step 6: `DraftSignature` + `CanSave` + validation tạo** (thay lines 1140–1149 và bổ sung helper):

```csharp
// NoKey trong signature: test pass rồi mới bật/tắt toggle → test mất hiệu lực, phải test lại
private string DraftSignature() =>
    $"{_draft.Type}|{ProviderUrl.Canonicalize(_draft.BaseUrl)}|{_draft.ApiKey}|{_draft.NoKey}";

/// <summary>Tạo mới mà chưa chọn NoKey lẫn chưa gõ key — chặn Save/Test với lỗi keyOrNoKey.</summary>
private bool CreateKeyMissing =>
    _editingId is null && !_draft.NoKey && string.IsNullOrWhiteSpace(_draft.ApiKey);

/// <summary>
/// Provider mới: chỉ Save khi test pass với đúng nội dung hiện tại của form
/// và đã chọn key hoặc NoKey; provider đã lưu: Save luôn bật — test là tùy chọn (spec §4.2).
/// </summary>
private bool CanSave =>
    _editingId is not null
    || (_testResult is { Success: true }
        && _testedSignature == DraftSignature()
        && !CreateKeyMissing);
```

- [ ] **Step 7: Nạp lỗi `keyOrNoKey` ở `TestDraftAsync` + `SaveAsync`** — trong `TestDraftAsync` (sau dòng 1156 `if (_errors.Count > 0) return;` thì KHÔNG kịp — chèn **trước** dòng đó) và trong `SaveAsync` (trước dòng 1199 `if (_errors.Count > 0) return;`), cùng 2 dòng:

```csharp
if (CreateKeyMissing)
{
    _errors[nameof(ProviderDraft.ApiKey)] = "accounts.error.keyOrNoKey";
}
if (_errors.Count > 0) return;
```

Thứ tự trong mỗi method: `_errors = ProviderValidator.Validate(_draft)...;` → 2 dòng trên → `if (_errors.Count > 0) return;`.

- [ ] **Step 8: `TestDraftAsync` — override đúng khi NoKey** — thay dòng 1171–1172:

```csharp
// NoKey → override "" ép probe không key (D9); ngược lại giữ hành vi cũ
// (key đang gõ thì override, trống thì fallback account)
_testResult = await ProviderSvc.TestConnectionAsync(target,
    _draft.NoKey ? "" : (string.IsNullOrEmpty(_draft.ApiKey) ? null : _draft.ApiKey));
```

- [ ] **Step 9: Form account — toggle NoKey + disable input key** — chèn label checkbox **ngay sau** label `accounts.field.apiKey` (sau dòng 488), và sửa input key của label đó:

```razor
<label class="flex flex-col gap-1">
    @L["accounts.field.apiKey"]
    <input type="password" class="rounded border border-border bg-surface px-2 py-1"
           autocomplete="off" spellcheck="false"
           disabled="@(_busy || accountForm.NoKey)"
           placeholder="@(accountForm.NoKey
               ? L["accounts.placeholder.noKey"]
               : (accountForm.Id is null ? L["accounts.placeholder.key"] : L["accounts.placeholder.keySaved"]))"
           @bind="accountForm.ApiKey" @bind:event="oninput" />
</label>
<label class="flex items-center gap-1.5">
    <input type="checkbox" disabled="@_busy" @bind="accountForm.NoKey" />
    @L["accounts.field.noKey"]
</label>
```

- [ ] **Step 10: Footer form account — nút Test (chỉ khi NoKey) + badge result** — thay block footer lines 524–533:

```razor
<div class="flex justify-end gap-2">
    @if (accountForm.NoKey)
    {
        @* Account no-key phải test pass mới được lưu (D6) — test result hiển thị cạnh nút *@
        <button type="button" class="btn btn-outline-info"
                disabled="@(_busy || _saving || _testingAccountNoKey)"
                @onclick="TestAccountNoKeyAsync">
            @(_testingAccountNoKey ? L["providers.testing"] : L["providers.action.test"])
        </button>
        @if (_accountTestResult is { } accountTestResult)
        {
            <Badge Variant="@(accountTestResult.Success ? BadgeVariant.Success : BadgeVariant.Danger)"
                   Text="@(accountTestResult.Success ? L["providers.lastTest.ok"] : L["providers.lastTest.fail"])" />
        }
    }
    <button type="button" class="btn btn-outline-secondary" disabled="@_busy" @onclick="CancelAccountForm">
        @L["confirm.cancel"]
    </button>
    <button type="button" class="btn btn-primary"
            disabled="@(!CanSaveAccount || _busy || _saving)"
            @onclick="SaveAccountAsync">
        @L["settings.action.save"]
    </button>
</div>
```

- [ ] **Step 11: Thêm method `TestAccountNoKeyAsync` + signature** (đặt cạnh `CanSaveAccount`):

```csharp
/// <summary>Chữ ký test account no-key: Type|BaseUrl|NoKey của form — đổi bất kỳ field nào = phải test lại (D6).</summary>
private string AccountTestSignature(AccountForm form) =>
    $"{_draft.Type}|{ProviderUrl.Canonicalize(_draft.BaseUrl)}|{form.NoKey}";

private bool AccountNoKeyTestPassed(AccountForm form) =>
    _accountTestResult is { Success: true }
    && _accountTestedSignature == AccountTestSignature(form);

private async Task TestAccountNoKeyAsync()
{
    if (_busy || _editingId is not { } providerId || _accountForm is not { } form) return;

    _busy = true;
    _testingAccountNoKey = true;
    try
    {
        // Lấy entity đã lưu (có Type/BaseUrl hiện tại từ modal) rồi vá theo form —
        // TestConnectionAsync với override "" ép probe không key (D9)
        var target = await ProviderSvc.GetAsync(providerId)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");
        target.Type = _draft.Type;
        target.BaseUrl = ProviderUrl.Canonicalize(_draft.BaseUrl);

        _accountTestResult = await ProviderSvc.TestConnectionAsync(target, apiKeyOverride: "");
        _accountTestedSignature = AccountTestSignature(form);
    }
    catch (Exception ex)
    {
        // GetAsync/network lỗi hiển thị như test fail — không nuốt (pattern TestDraftAsync)
        Log.Error("Không test được tài khoản không key.", ex);
        Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        _accountTestResult = new ProviderTestResult(false, ex.Message, DateTimeOffset.UtcNow);
        _accountTestedSignature = AccountTestSignature(form);
    }
    finally
    {
        _testingAccountNoKey = false;
        _busy = false;
    }
}
```

- [ ] **Step 12: `CanSaveAccount` — gate D6** — thay lines 1288–1291:

```csharp
private bool CanSaveAccount =>
    _accountForm is { } form
    && form.Name.Trim().Length > 0
    // Tạo: bắt buộc key hoặc NoKey; sửa: key tùy chọn (để trống = giữ)
    && (form.Id is not null || form.NoKey || form.ApiKey.Trim().Length > 0)
    // Account no-key: test pass với đúng Type|BaseUrl|NoKey hiện tại mới được lưu (D6)
    && (!form.NoKey || AccountNoKeyTestPassed(form));
```

> `SaveAccountAsync` giữ nguyên guard `if (!CanSaveAccount) return;` (dòng 1296) — đó là chặn cứng D6; service boundary cũng đã có validator từ Task 6.

- [ ] **Step 13: `SaveAccountAsync` — truyền cờ NoKey, ép key rỗng** — thay dòng 1300–1311 (block `new ProviderAccountDraft`):

```csharp
var draft = new ProviderAccountDraft
{
    ProviderId = providerId,
    Name = form.Name.Trim(),
    // NoKey → không gửi key (dù ô disabled còn giữ chữ cũ) — service ghi cột rỗng (D5)
    ApiKey = form.NoKey ? string.Empty : form.ApiKey.Trim(),
    NoKey = form.NoKey,
    Enabled = form.Enabled,
    ModelPatterns = patterns,
    Weight = form.Weight,
    Priority = form.Priority,
    DailyTokenLimit = form.DailyTokenLimit,
    DailyRequestLimit = form.DailyRequestLimit,
};
```

- [ ] **Step 14: Nhãn "no key" trong cột info danh sách account** — thay cell lines 442–444:

```razor
<td class="px-2 py-1.5 opacity-80">
    W@(account.Weight) P@(account.Priority) · @UsageText(account)
    @if (string.IsNullOrEmpty(account.ApiKeyEncrypted))
    {
        @* Nhãn language-neutral theo spec §5 — không cần i18n (xem comment dòng 425) *@
        <span class="opacity-60"> · no key</span>
    }
</td>
```

- [ ] **Step 15: Gates** — `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` + full test + `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0 warning / 0 error.

- [ ] **Step 16: Manual verification checklist** (chạy app, trang Providers):
  1. Tạo provider mới → tick "Không dùng API key" → ô key disable + placeholder "Không cần key"; gõ key trước đó rồi tick → key bị bỏ qua khi save.
  2. Chưa test → nút Save disabled; test pass → Save bật; đổi toggle sau test → Save tắt lại (signature đổi).
  3. Bỏ tick mà key còn trống → hiện lỗi `Enter an API key or enable "No API key".` ngay dưới modal.
  4. Save thành công → modal đóng, provider mới có đúng 1 account "Default", cột info hiện `· no key`.
  5. Mở edit provider → form account: account no-key có toggle bật sẵn, ô key disable, nút **Test** xuất hiện; bấm Test → badge OK → Save bật; bấm Test fail (sai BaseUrl) → badge Fail → Save tắt.
  6. Account có key → tick NoKey → ô key disable, nhấn Test → Save (sau pass) → reload: cột key rỗng, danh sách vẫn hiện `· no key`.
  7. Untick NoKey với ô key trống (edit) → Save không yêu cầu test (giữ nguyên behavior cũ — key trống = giữ key đã lưu).

- [ ] **Step 17: Commit** — `feat: add no-key toggle and test gate to provider account UI`

---

### Task 10: Full gates + ledger

**Files:**
- Modify: `.superpowers/sdd/progress.md`

- [ ] **Step 1: Full gates** (app đóng):
  1. `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0 warning / 0 error.
  2. `dotnet test "router balancing test/router balancing test.csproj"` → toàn PASS (flake known → rerun + isolation, không chấp nhận fail thật).
  3. `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0 warning / 0 error.

- [ ] **Step 2: Cập nhật ledger** `.superpowers/sdd/progress.md` — thêm entry cho việc #2 Free account no-key: spec + plan path, các commit, gates kết quả; đánh dấu roadmap #2 DONE.

- [ ] **Step 3: Commit** — `docs: record free-account no-key completion in ledger`

---

## Verification & Success Criteria

1. Toàn bộ test suite xanh (bao gồm 3 flake known đã được rerun xác nhận PASS).
2. Build Core + app TFM Windows 0 warning / 0 error.
3. Các hành vi bàn giao theo spec:
   - `ResolveFirstEnabledKey` phân biệt `null`/`""`; account no-key không bị skip.
   - Chat forward/test/test-all/probe với account no-key không throw `CryptographicException`, không gửi header auth.
   - `TestConnectionAsync("")` ép no-key dù account có key; `null` giữ fallback.
   - `CreateAsync` provider: `NoKey` → account rỗng; key trống không NoKey → `ArgumentException`.
   - Account CRUD: `NoKey` tạo/xóa key rỗng; key trống khi update vẫn giữ key cũ (test cũ còn nguyên).
   - UI: toggle 2 chỗ, gate test-before-save cả create provider lẫn account form, nhãn `no key`.
   - i18n đủ 3 key ở cả 2 dict, parity test xanh.
4. Không migration; DB schema không đổi.
