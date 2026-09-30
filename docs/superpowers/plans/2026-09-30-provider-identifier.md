# Provider Identifier + Account Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) hoặc superpowers:subagent-driven-development để implement plan này task-by-task. Steps dùng checkbox (`- [ ]`).

**Goal:** Thêm mã định danh `identifier/model` cho provider (routing pin + hiển thị) và toggle bật/tắt nhanh cho account.

**Architecture:** Cột `Provider.Identifier` (slug unique, backfill từ `Name` khi migrate) → `ComboResolver` pin provider khi client gửi `identifier/model` → validation unique/segment ở service → UI form/bảng/optgroup. Account toggle theo đúng pattern `SetEnabledAsync` sẵn có của provider/model.

**Tech Stack:** .NET 10, EF Core 10 (SQLite, auto-migrate), Blazor Hybrid, xUnit

## Global Constraints

- Spec: [`docs/superpowers/specs/2026-09-30-provider-identifier-design.md`](../specs/2026-09-30-provider-identifier-design.md)
- `dotnet test "router balancing test/router balancing test.csproj"` phải xanh **sau mỗi task**
- `dotnet build router-balancing.slnx` phải xanh
- Nhánh: `feat/retry-circuit-3c`
- Nullable enable, không nuốt exception, comment tiếng Việt cho lý do "tại sao"
- i18n: mọi key mới phải có trong **cả 2 dict** `English` và `Vietnamese` của `Translations.cs`
- Commit message tiếng Anh, conventional commit, 1 task = 1 commit

---

### Task 0: Commit docs

**Files:**
- Create (đã viết sẵn): `docs/superpowers/specs/2026-09-30-provider-identifier-design.md`
- Create (đã viết sẵn): `docs/superpowers/plans/2026-09-30-provider-identifier.md` (file này)

**Steps:**

- [ ] **Step 0.1: Commit:**

```bash
git add docs/superpowers/specs/2026-09-30-provider-identifier-design.md docs/superpowers/plans/2026-09-30-provider-identifier.md
git commit -m "docs: add provider identifier design spec and plan"
```

---

### Task 1: Provider.Identifier column + migration + backfill

**Files:**
- Modify: `src/RouterBalancing.Core/Domain/Entities/Provider.cs`
- Modify: `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs:27-31`
- Create: migration qua `dotnet ef` → `src/RouterBalancing.Core/Storage/Migrations/`
- Modify: `src/RouterBalancing.Core/Storage/DbInitializer.cs`
- Test: `router balancing test/Storage/DbInitializerTests.cs`

**Interfaces:**
- Produces: `Provider.Identifier` (`string?`), slugify qua `DbInitializer` khi khởi động.

- [ ] **Step 1.1: Thêm property vào `Provider.cs` (sau `Name`):**

```csharp
    /// <summary>Mã định danh slug (unique) — client pin provider bằng "{Identifier}/{ModelId}". Nullable chỉ để migration an toàn; backfill tự chạy khi khởi động.</summary>
    public string? Identifier { get; set; }
```

- [ ] **Step 1.2: Config trong `RouterBalancingDbContext.OnModelCreating` — entity config `Provider` (line 27-31):**

```csharp
        modelBuilder.Entity<Provider>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Identifier).HasMaxLength(50);
            // Identifier slug unique toàn cục — nhiều NULL vẫn OK (SQLite distinct NULLs),
            // hàng cũ được backfill trong DbInitializer sau Migrate
            e.HasIndex(x => x.Identifier).IsUnique();
            e.Property(x => x.BaseUrl).IsRequired().HasMaxLength(2000);
        });
```

- [ ] **Step 1.3: Build để chắc snapshot chưa sync là OK trước khi migrate:**

```bash
dotnet build router-balancing.slnx
```
Expected: build xanh (EF chưa cần snapshot đồng bộ cho build).

- [ ] **Step 1.4: Sinh migration (đúng command phase1/phase2 đã dùng):**

```bash
dotnet ef migrations add AddProviderIdentifier --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
```

Expected: file mới `src/RouterBalancing.Core/Storage/Migrations/<timestamp>_AddProviderIdentifier.cs` + `.Designer.cs`, snapshot có `Identifier`. Kiểm tra:

```bash
git status --short
rg -n "Identifier" src/RouterBalancing.Core/Storage/Migrations/RouterBalancingDbContextModelSnapshot.cs
```
Expected: `b.Property<string>("Identifier")` + unique index trong snapshot.

- [ ] **Step 1.5: Viết test backfill FAIL — thêm vào `DbInitializerTests.cs`:**

```csharp
    [Fact]
    public void Initialize_BackfillsIdentifier_SlugifiesDedupesAndFallsBack()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);

        using (var db = factory.CreateDbContext())
        {
            db.Providers.AddRange(
                new Provider { Name = "Nhà cung cấp A", Type = ProviderType.OpenAI, BaseUrl = "https://a.example" },
                new Provider { Name = "Nhà cung cấp A", Type = ProviderType.OpenAI, BaseUrl = "https://b.example" },
                new Provider { Name = "   ---   ", Type = ProviderType.OpenAI, BaseUrl = "https://c.example" });
            db.SaveChanges();
            // Mô phỏng hàng cũ trước khi có backfill: Identifier NULL
            foreach (var p in db.Providers) p.Identifier = null;
            db.SaveChanges();
        }

        DbInitializer.Initialize(factory);

        using (var db = factory.CreateDbContext())
        {
            var providers = db.Providers.OrderBy(p => p.Id).ToList();
            Assert.Equal("nha-cung-cap-a", providers[0].Identifier);
            Assert.Equal("nha-cung-cap-a-2", providers[1].Identifier); // dedupe
            Assert.Equal($"provider-{providers[2].Id}", providers[2].Identifier); // slug rỗng

            // Idempotent — chạy lần nữa giá trị không đổi
            DbInitializer.Initialize(factory);
        }
        using (var db = factory.CreateDbContext())
        {
            Assert.Equal("nha-cung-cap-a",
                db.Providers.OrderBy(p => p.Id).First().Identifier);
        }
    }
```

- [ ] **Step 1.6: Chạy test — kỳ vọng FAIL (backfill chưa có):**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~DbInitializerTests
```
Expected: FAIL `Initialize_BackfillsIdentifier_SlugifiesDedupesAndFallsBack` (Identifier vẫn `null`).

- [ ] **Step 1.7: Implement backfill trong `DbInitializer.cs`:**

Thêm `using System.Globalization;` và `using System.Text;` vào đầu file, khai báo hằng `MaxIdentifierLength` ngay trong class, rồi:

```csharp
    // Spec §2: identifier ≤50 ký tự — SQLite không enforce HasMaxLength(50) nên phải cắt ngay khi backfill.
    private const int MaxIdentifierLength = 50;

    public static void Initialize(IDbContextFactory<RouterBalancingDbContext> factory)
    {
        Directory.CreateDirectory(StoragePathProvider.GetDataDirectory());
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        BackfillIdentifiers(db);
    }

    /// <summary>
    /// Backfill Identifier từ Name cho hàng cũ (migration thêm cột nullable) — idempotent.
    ///_slugify Unicode cần C# (Normalize + bỏ combining mark) nên làm ở đây thay vì SQL trong migration.
    /// </summary>
    internal static void BackfillIdentifiers(RouterBalancingDbContext db)
    {
        var taken = db.Providers.AsNoTracking()
            .Where(p => p.Identifier != null && p.Identifier != "")
            .Select(p => p.Identifier!)
            .ToHashSet(StringComparer.Ordinal);

        var pending = db.Providers
            .Where(p => p.Identifier == null || p.Identifier == "")
            .OrderBy(p => p.Id)
            .ToList();
        if (pending.Count == 0) return;

        foreach (var provider in pending)
        {
            // Backfill idempotent: giá trị đã ghi không bao giờ được sửa lại, nên identifier
            // >50 ký tự (Name tối đa 200) sẽ "dính" vĩnh viễn — Task 3 thêm HasMaxLength(50) +
            // validator ^[a-z0-9]+(-[a-z0-9]+)*$ sẽ không cho lưu những hàng này nữa (spec §2).
            var slug = TruncateSlug(Slugify(provider.Name), MaxIdentifierLength);
            if (slug.Length == 0) slug = $"provider-{provider.Id}";

            var candidate = slug;
            for (var n = 2; taken.Contains(candidate); n++)
            {
                // Suffix -{n} cũng phải nằm trong 50: cắt phần slug còn lại theo độ dài suffix;
                // TrimEnd('-') do cắt sinh ra (kể cả "--" trước suffix) để không phá slug regex.
                var suffix = $"-{n}";
                var fitted = TruncateSlug(slug, MaxIdentifierLength - suffix.Length);
                if (fitted.Length == 0) fitted = $"provider-{provider.Id}";
                candidate = $"{fitted}{suffix}";
            }

            provider.Identifier = candidate;
            taken.Add(candidate);
        }

        db.SaveChanges();
    }

    /// <summary>Cắt slug về tối đa <paramref name="maxLength"/> rồi bỏ dấu '-' còn sót cuối chuỗi; có thể trả về rỗng.</summary>
    private static string TruncateSlug(string slug, int maxLength)
    {
        if (slug.Length <= maxLength) return slug;
        return slug[..maxLength].TrimEnd('-');
    }

    /// <summary>Lowercase ASCII + chữ Việt có dấu → bỏ dấu; ký tự ngoài [a-z0-9] → '-' (collapse, trim).</summary>
    internal static string Slugify(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(ch);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(lower);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        return sb.ToString().Trim('-');
    }
```

Lưu ý: EF chưa translate được `p.Identifier != ""` trên mọi provider — nếu build/test báo lỗi LINQ, thay bằng `!(p.Identifier == null || p.Identifier == "")` tương đương.

Lưu ý (fix review): cap 50 ký tự bao gồm cả suffix dedupe `-{n}` — truncate rồi `TrimEnd('-')` để không sinh dấu '-' cuối (vi phạm slug regex của Task 3); slug rỗng sau truncate → fallback `provider-{Id}`. Kèm test `Initialize_BackfillsIdentifier_CapsAt50KeepsSlugShapeAndStaysIdempotent` (tên 60 ký tự, va chạm dedupe, cắt rơi vào '-', idempotent lần 2).

- [ ] **Step 1.8: Chạy test — kỳ vọng PASS:**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~DbInitializerTests
```
Expected: PASS tất cả `DbInitializerTests` (5 test: 4 cũ + 1 cap-50).

- [ ] **Step 1.9: Full suite:**

```bash
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: toàn bộ PASS (chưa có test nào đụng `Identifier` yêu cầu giá trị).

- [ ] **Step 1.10: Commit:**

```bash
git add src/RouterBalancing.Core/Domain/Entities/Provider.cs src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs src/RouterBalancing.Core/Storage/Migrations/ src/RouterBalancing.Core/Storage/DbInitializer.cs "router balancing test/Storage/DbInitializerTests.cs"
git commit -m "feat: add provider identifier column with slug backfill"
```

---

### Task 2: Pin provider by identifier prefix trong ComboResolver

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ComboResolver.cs:16-30`
- Test: `router balancing test/Engine/ComboResolverTests.cs`

**Interfaces:**
- Consumes: `Provider.Identifier` (Task 1).
- Produces: `ResolveAsync("prov/model")` → chỉ candidate của provider có `Identifier == "prov"`.

- [ ] **Step 2.1: Helper + 3 test FAIL — thêm vào `ComboResolverTests.cs`:**

```csharp
    private void SetIdentifier(long providerId, string identifier)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        db.Providers.Find(providerId)!.Identifier = identifier;
        db.SaveChanges();
    }

    [Fact]
    public async Task Resolve_WhenIdentifierPrefixProvided_PinsToThatProvider()
    {
        var pinned = SeedProvider("p1", modelIds: ["gpt-4o"]);
        SeedProvider("p2", modelIds: ["gpt-4o"]); // cùng model id, không pin
        SetIdentifier(pinned, "myazure");

        var result = await CreateSut(out _).ResolveAsync("myazure/gpt-4o", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        var candidate = Assert.Single(ok.Candidates);
        Assert.Equal("p1", candidate.Provider.Name);
        Assert.Equal("gpt-4o", candidate.Model.ModelId);
    }

    [Fact]
    public async Task Resolve_WhenPrefixNotAnIdentifier_MatchesWholeModelId()
    {
        // Model id kiểu OpenRouter chứa '/' — không có identifier "openai" thì phải match nguyên chuỗi
        SeedProvider("p1", modelIds: ["openai/gpt-4o"]);

        var result = await CreateSut(out _).ResolveAsync("openai/gpt-4o", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Equal("openai/gpt-4o", Assert.Single(ok.Candidates).Model.ModelId);
    }

    [Fact]
    public async Task Resolve_WhenPrefixMatchesButModelMissingOnProvider_ReturnsNotFound()
    {
        var pinned = SeedProvider("p1", modelIds: ["other"]);
        SetIdentifier(pinned, "myazure");

        var result = await CreateSut(out _).ResolveAsync("myazure/ghost", default);

        var fail = Assert.IsType<SelectionFailure>(result);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }
```

- [ ] **Step 2.2: Chạy — kỳ vọng FAIL:**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ComboResolverTests
```
Expected: 3 test mới FAIL (resolver chưa parse prefix). Test cũ vẫn PASS.

- [ ] **Step 2.3: Implement trong `ComboResolver.cs` — thay `ResolveAsync` và thêm helper:**

```csharp
    /// <inheritdoc/>
    public async Task<SelectionResult> ResolveAsync(string model, CancellationToken ct)
    {
        var candidates = await QueryModelCandidatesAsync(model, ct);
        var mode = ComboMode.RoundRobin;
        if (candidates.Count == 0)
        {
            var combo = await LoadComboAsync(model, ct);
            if (combo is null)
                return new SelectionFailure(model, ResolveFailure.NotFound);
            mode = combo.Mode;
            candidates = await ResolveComboAsync(combo, [combo.Id], ct);
        }

        return Finalize(model, candidates, mode);
    }

    /// <summary>
    /// "prefix/rest": nếu prefix là Identifier của bất kỳ provider nào → pin đúng provider
    /// (m.ModelId == rest). Không match / không có '/' → match toàn bộ ModelId như cũ (fallback spec §5).
    /// Query Identifier chỉ chạy khi model có '/' — chuỗi thường không tốn query thêm.
    /// </summary>
    private async Task<List<ModelCandidate>> QueryModelCandidatesAsync(string model, CancellationToken ct)
    {
        var separator = model.IndexOf('/');
        if (separator > 0)
        {
            var prefix = model[..separator];
            await using var context = await db.CreateDbContextAsync(ct);
            var isIdentifier = await context.Providers.AsNoTracking()
                .AnyAsync(p => p.Identifier == prefix, ct);
            if (isIdentifier)
            {
                var rest = model[(separator + 1)..];
                return await QueryCandidatesAsync(
                    m => m.Provider!.Identifier == prefix && m.ModelId == rest, ct);
            }
        }

        return await QueryCandidatesAsync(m => m.ModelId == model, ct);
    }
```

`ResolveAsync` giữ nguyên phần còn lại (combo fallback, `Finalize`). Import `Microsoft.EntityFrameworkCore` đã có trong file.

- [ ] **Step 2.4: Chạy — kỳ vọng PASS:**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ComboResolverTests
```
Expected: PASS toàn bộ (gồm regression test cũ).

- [ ] **Step 2.5: Full suite:**

```bash
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: PASS.

- [ ] **Step 2.6: Commit:**

```bash
git add src/RouterBalancing.Core/Engine/ComboResolver.cs "router balancing test/Engine/ComboResolverTests.cs"
git commit -m "feat: pin provider by identifier prefix in combo resolver"
```

---

### Task 3: Validation + Form + Display + i18n

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderDraft.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderValidator.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderValidationException.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderService.cs` (CreateAsync:52, UpdateAsync:82)
- Modify: `src/RouterBalancing.Core/Providers/IProviderService.cs` (XML doc exceptions)
- Modify: `router-balancing/Components/Pages/Providers.razor` (form :296-348, bảng :36/:56-61, OpenEdit :1014, SaveAsync :1126)
- Modify: `router-balancing/Components/Pages/Combos.razor:122`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (2 dict)
- Test: `router balancing test/Providers/ProviderValidatorTests.cs`, `router balancing test/Providers/ProviderServiceTests.cs`

**Interfaces:**
- Consumes: `Provider.Identifier` (Task 1).
- Produces: `ProviderValidationException.Errors` (`IReadOnlyDictionary<string,string>` field → i18n key); error keys `providers.error.identifier*`.

- [ ] **Step 3.1: Test FAIL — `ProviderValidatorTests.cs`:**

Sửa `ValidDraft()` thêm `Identifier = "openai-prod",` và thêm:

```csharp
    [Fact]
    public void Validate_WhenIdentifierBlank_ReturnsIdentifierRequired()
    {
        var blank = ProviderValidator.Validate(ValidDraft() with { Identifier = "   " });
        var missing = ProviderValidator.Validate(ValidDraft() with { Identifier = "" });

        Assert.Equal("providers.error.identifierRequired", blank[nameof(ProviderDraft.Identifier)]);
        Assert.Equal("providers.error.identifierRequired", missing[nameof(ProviderDraft.Identifier)]);
    }

    [Theory]
    [InlineData("OpenAI")]        // uppercase
    [InlineData("has space")]     // space
    [InlineData("a--b")]          // gạch đôi
    [InlineData("-leading")]      // bắt đầu bằng gạch
    [InlineData("trailing-")]     // kết thúc bằng gạch
    [InlineData("openai/prod")]   // '/' → cũng là segment-trùng mầm móng
    [InlineData("Việt-Nam")]      // có dấu
    public void Validate_WhenIdentifierNotSlug_ReturnsIdentifierFormat(string identifier)
    {
        var errors = ProviderValidator.Validate(ValidDraft() with { Identifier = identifier });

        Assert.Equal("providers.error.identifierFormat", errors[nameof(ProviderDraft.Identifier)]);
    }

    [Fact]
    public void Validate_WhenIdentifierTooLong_ReturnsIdentifierFormat()
    {
        var errors = ProviderValidator.Validate(ValidDraft() with { Identifier = new string('a', 51) });

        Assert.Equal("providers.error.identifierFormat", errors[nameof(ProviderDraft.Identifier)]);
    }

    [Theory]
    [InlineData("p1")]
    [InlineData("openai-prod")]
    [InlineData("a")]
    public void Validate_WhenIdentifierValidSlug_NoIdentifierError(string identifier)
    {
        var errors = ProviderValidator.Validate(ValidDraft() with { Identifier = identifier });

        Assert.False(errors.ContainsKey(nameof(ProviderDraft.Identifier)));
    }
```

- [ ] **Step 3.2: Test FAIL — `ProviderServiceTests.cs`:**

Sửa helper `Draft` (line 34-41) thêm tham số + property:

```csharp
    private static ProviderDraft Draft(string name = "OpenAI", string key = "sk-secret",
        string identifier = "openai-prod") => new()
    {
        Name = name,
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.openai.com/",
        ApiKey = key,
        Identifier = identifier,
        MaxConcurrent = 4,
    };
```

Thêm tests:

```csharp
    [Fact]
    public async Task Create_DuplicateIdentifier_ThrowsWithDuplicateKey()
    {
        await _service.CreateAsync(Draft(name: "A"));

        var ex = await Assert.ThrowsAsync<ProviderValidationException>(
            () => _service.CreateAsync(Draft(name: "B")));

        Assert.Equal("providers.error.identifierDuplicate", ex.Errors[nameof(ProviderDraft.Identifier)]);
    }

    [Fact]
    public async Task Create_IdentifierCollidesWithModelIdPrefix_ThrowsWithSegmentKey()
    {
        var provider = await _service.CreateAsync(Draft(name: "A"));
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = provider.Id, ModelId = "openai/gpt-4o" });
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<ProviderValidationException>(
            () => _service.CreateAsync(Draft(name: "B", identifier: "openai")));

        Assert.Equal("providers.error.identifierSegmentCollision", ex.Errors[nameof(ProviderDraft.Identifier)]);
    }

    [Fact]
    public async Task Update_SameProviderIdentifier_DoesNotThrowAndTrims()
    {
        var provider = await _service.CreateAsync(Draft(identifier: "  openai-prod "));
        Assert.Equal("openai-prod", provider.Identifier);

        await _service.UpdateAsync(provider.Id, Draft(identifier: "renamed-prod"));

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("renamed-prod", saved.Identifier);
    }

    [Fact]
    public async Task Update_DuplicateIdentifierOtherProvider_Throws()
    {
        var first = await _service.CreateAsync(Draft(name: "A", identifier: "taken"));
        var second = await _service.CreateAsync(Draft(name: "B", identifier: "free"));

        await Assert.ThrowsAsync<ProviderValidationException>(
            () => _service.UpdateAsync(second.Id, Draft(name: "B", identifier: "taken")));
        Assert.NotNull(first);
    }
```

Lưu ý: 2 test `Create_WhenBaseUrlEndsWithV1_PersistsCanonicalBaseUrl` / `Update_WhenBaseUrlEndsWithV1_PersistsCanonicalBaseUrl` dùng `new ProviderDraft { ... }` inline **không set Identifier** — service không validate required nên vẫn pass (mỗi test 1 provider, không trùng ""). Không cần sửa.

- [ ] **Step 3.3: Chạy — kỳ vọng FAIL (build error do thiếu member):**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderValidatorTests|FullyQualifiedName~ProviderServiceTests"
```
Expected: FAIL với compiler error `ProviderDraft' does not contain a definition for 'Identifier'` — red hợp lệ cho typed language.

- [ ] **Step 3.4: Implement backend.**

`ProviderDraft.cs` — thêm sau `Name`:

```csharp
    /// <summary>Mã định danh slug — bắt buộc, unique. Client pin provider bằng "{Identifier}/{ModelId}".</summary>
    public string Identifier { get; set; } = string.Empty;
```

`ProviderValidator.cs` — thêm `using System.Text.RegularExpressions;`, đổi class thành `public static partial class ProviderValidator`, thêm regex + rule (giữ nguyên các rule cũ):

```csharp
    /// <summary>Slug: một_segment gồm chữ thường/số, các segment nối bằng đúng một dấu gạch.</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdentifierPattern();
```

Trong `Validate`, sau rule `MaxConcurrent`:

```csharp
        if (string.IsNullOrWhiteSpace(draft.Identifier))
        {
            errors[nameof(ProviderDraft.Identifier)] = "providers.error.identifierRequired";
        }
        else if (draft.Identifier.Length > 50 || !IdentifierPattern().IsMatch(draft.Identifier))
        {
            errors[nameof(ProviderDraft.Identifier)] = "providers.error.identifierFormat";
        }
```

Create `src/RouterBalancing.Core/Providers/ProviderValidationException.cs`:

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>
/// Lỗi business cần DB (unique, segment-trùng) khi tạo/cập nhật provider —
/// mang dict key i18n cùng format <see cref="ProviderValidator.Validate"/> để UI hiển thị field-level.
/// </summary>
public sealed class ProviderValidationException(IReadOnlyDictionary<string, string> errors)
    : Exception("Provider validation failed.")
{
    /// <summary>Field name (nameof property) → i18n key lỗi.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}
```

`ProviderService.cs`:

Đổi `CreateAsync` (line 52-79) thành:

```csharp
    public async Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var identifier = draft.Identifier.Trim();
        await EnsureIdentifierUsableAsync(db, identifier, excludeId: null, ct);

        var provider = new Provider
        {
            Name = draft.Name.Trim(),
            Identifier = identifier,
            Type = draft.Type,
            BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl),
            MaxConcurrent = draft.MaxConcurrent,
        };

        // Key ở create = tạo kèm account "Default" — key sống hoàn toàn ở ProviderAccount (spec §4.2)
        if (!string.IsNullOrEmpty(draft.ApiKey))
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "Default",
                ApiKeyEncrypted = _protector.Protect(draft.ApiKey),
                Enabled = true,
                Weight = 100,
                Priority = 0,
            });
        }

        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider;
    }
```

Đổi `UpdateAsync` (line 82-96) thành:

```csharp
    public async Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        var identifier = draft.Identifier.Trim();
        await EnsureIdentifierUsableAsync(db, identifier, excludeId: id, ct);

        provider.Name = draft.Name.Trim();
        provider.Identifier = identifier;
        provider.Type = draft.Type;
        provider.BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl);
        provider.MaxConcurrent = draft.MaxConcurrent;
        // draft.ApiKey bị BỎ QUA khi update — key quản lý ở ProviderAccount (spec §4.2)
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }
```

Thêm private helper cuối class `ProviderService`:

```csharp
    /// <summary>
    /// Unique + chống segment-trùng (cần DB nên tách khỏi ProviderValidator) —
    /// ném ProviderValidationException mang dict key i18n.
    /// </summary>
    private static async Task EnsureIdentifierUsableAsync(
        RouterBalancingDbContext db, string identifier, long? excludeId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var duplicate = await db.Providers.AsNoTracking()
            .AnyAsync(p => p.Identifier == identifier
                && (excludeId == null || p.Id != excludeId), ct);
        if (duplicate)
        {
            errors[nameof(ProviderDraft.Identifier)] = "providers.error.identifierDuplicate";
        }
        else
        {
            // Identifier = segment đầu của model id hiện có (vd model "openai/gpt-4o" của OpenRouter)
            // → client gửi chuỗi đó sẽ bị pin nhầm — so sánh ordinal để không over-reject với SQLite LIKE
            var prefix = identifier + "/";
            var modelIds = await db.Models.AsNoTracking()
                .Where(m => m.ModelId.StartsWith(prefix))
                .Select(m => m.ModelId)
                .ToListAsync(ct);
            if (modelIds.Any(m => m.StartsWith(prefix, StringComparison.Ordinal)))
            {
                errors[nameof(ProviderDraft.Identifier)] = "providers.error.identifierSegmentCollision";
            }
        }

        if (errors.Count > 0)
        {
            throw new ProviderValidationException(errors);
        }
    }
```

`IProviderService.cs` — thêm vào XML doc của `CreateAsync` và `UpdateAsync`:

```csharp
    /// <exception cref="ProviderValidationException">Identifier trùng hoặc xung đột segment model id.</exception>
```

- [ ] **Step 3.5: Chạy backend tests — kỳ vọng PASS:**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderValidatorTests|FullyQualifiedName~ProviderServiceTests"
```
Expected: PASS.

- [ ] **Step 3.6: UI — `Providers.razor`:**

**(a) Form** — chèn label sau `Name` label (sau line 301):

```razor
        <label class="flex flex-col gap-1 text-sm">
            @L["providers.field.identifier"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   maxlength="50" spellcheck="false" placeholder="openai-prod"
                   @bind="_draft.Identifier" />
            <span class="text-xs opacity-70">@L["providers.hint.identifier"]</span>
        </label>
```

**(b) Error block** — chèn sau error block `MaxConcurrent` (sau line 348):

```razor
    @if (ModalError(nameof(ProviderDraft.Identifier)) is { } identifierError)
    {
        <div class="mt-1 text-sm text-danger">@L[identifierError]</div>
    }
```

**(c) Bảng provider — header** — chèn sau `<th>@L["providers.col.name"]</th>` (line 36):

```razor
                    <th class="px-2 py-2">@L["providers.col.identifier"]</th>
```

**(d) Bảng provider — cell** — chèn sau td chứa `@p.Name` (sau line 61):

```razor
                        <td class="px-2 py-2 font-mono text-xs opacity-80">@p.Identifier</td>
```

**(e) `OpenEdit` (line 1014-1021)** — thêm vào initializer:

```csharp
            Identifier = provider.Identifier ?? string.Empty,
```

**(f) `SaveAsync` catch (line 1126-1130)** — thêm catch **trước** `catch (Exception ex)`:

```csharp
            catch (ProviderValidationException vex)
            {
                // Lỗi business (unique/segment) trả key i18n theo field — hiển thị như lỗi validator
                foreach (var (field, key) in vex.Errors)
                {
                    _errors[field] = key;
                }
            }
```

- [ ] **Step 3.7: `Combos.razor` optgroup — thay line 122:**

```razor
                                <optgroup label="@(string.IsNullOrEmpty(provider.Identifier)
                                    ? provider.Name
                                    : $"{provider.Name} ({provider.Identifier})")">
```

- [ ] **Step 3.8: i18n — `Translations.cs`, thêm đủ 2 dict:**

Dict `English` — chèn `providers.col.identifier` sau line 114 (`providers.col.actions`):

```csharp
        ["providers.col.identifier"] = "Identifier",
```

Chèn field/hint sau line 135 (`providers.field.maxConcurrent`):

```csharp
        ["providers.field.identifier"] = "Identifier",
        ["providers.hint.identifier"] = "Pin this provider via \"{identifier}/{model}\" in the model field.",
```

Chèn errors sau line 140 (`providers.error.maxConcurrent`):

```csharp
        ["providers.error.identifierRequired"] = "Identifier is required.",
        ["providers.error.identifierFormat"] = "Lowercase letters, digits and dashes only (e.g. openai-prod).",
        ["providers.error.identifierDuplicate"] = "Identifier already exists.",
        ["providers.error.identifierSegmentCollision"] = "Identifier conflicts with an existing model id prefix.",
```

Dict `Vietnamese` — chèn tương ứng sau line 337, 358, 363:

```csharp
        ["providers.col.identifier"] = "Mã định danh",
```
```csharp
        ["providers.field.identifier"] = "Mã định danh",
        ["providers.hint.identifier"] = "Pin provider qua \"{identifier}/{model}\" trong trường model.",
```
```csharp
        ["providers.error.identifierRequired"] = "Mã định danh là bắt buộc.",
        ["providers.error.identifierFormat"] = "Chỉ chữ thường, số và dấu gạch (vd: openai-prod).",
        ["providers.error.identifierDuplicate"] = "Mã định danh đã tồn tại.",
        ["providers.error.identifierSegmentCollision"] = "Mã định danh trùng tiền tố model id đang có.",
```

*(Line numbers chỉ mang tính tham chiếu — tìm key lân cận nếu đã dịch chuyển.)*

- [ ] **Step 3.9: Build solution (compile razor):**

```bash
dotnet build router-balancing.slnx
```
Expected: xanh — razor compile qua, không còn lỗi reference.

- [ ] **Step 3.10: Full suite:**

```bash
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: PASS.

- [ ] **Step 3.11: Commit:**

```bash
git add src/RouterBalancing.Core/Providers/ src/RouterBalancing.Core/Localization/Translations.cs router-balancing/Components/Pages/Providers.razor router-balancing/Components/Pages/Combos.razor "router balancing test/Providers/ProviderValidatorTests.cs" "router balancing test/Providers/ProviderServiceTests.cs"
git commit -m "feat: add identifier validation, form field and display"
```

---

### Task 4: Account quick toggle

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/IProviderAccountService.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderAccountService.cs`
- Modify: `router-balancing/Components/Pages/Providers.razor` (bảng account :399-402, thêm method cạnh `ToggleProviderEnabledAsync`)
- Test: `router balancing test/Providers/ProviderAccountServiceTests.cs`

**Interfaces:**
- Produces: `IProviderAccountService.SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)`.

- [ ] **Step 4.1: Test FAIL — thêm vào `ProviderAccountServiceTests.cs`:**

```csharp
    [Fact]
    public async Task SetEnabled_WhenToggled_FlipsAndPersists()
    {
        var providerId = await SeedProviderAsync(("a1", true, 0));
        var accountId = (await _service.ListAsync(providerId))[0].Id;

        await _service.SetEnabledAsync(accountId, false);

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == accountId);
        Assert.False(saved.Enabled);
    }

    [Fact]
    public async Task SetEnabled_WhenAccountMissing_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.SetEnabledAsync(999, false));
    }
```

- [ ] **Step 4.2: Chạy — kỳ vọng FAIL (build error, thiếu `SetEnabledAsync`):**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ProviderAccountServiceTests
```
Expected: compiler error — interface chưa có method.

- [ ] **Step 4.3: Implement.**

`IProviderAccountService.cs` — thêm sau `DeleteAsync`:

```csharp
    /// <summary>Bật/tắt nhanh account — không qua draft, không đụng key (pattern SetEnabledAsync của provider/model).</summary>
    /// <exception cref="KeyNotFoundException">Account không tồn tại.</exception>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);
```

`ProviderAccountService.cs` — thêm method (cùng pattern `ProviderService.SetEnabledAsync`):

```csharp
    /// <inheritdoc/>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException($"Account {id} not found.");
        account.Enabled = enabled;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
```

- [ ] **Step 4.4: Chạy — kỳ vọng PASS:**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ProviderAccountServiceTests
```
Expected: PASS.

*(Test account tắt bị `ProviderKeyResolver` loại đã có sẵn trong `ProviderKeyResolverTests` — không viết lại.)*

- [ ] **Step 4.5: UI — `Providers.razor`:**

**(a)** Thay cell Badge (line 399-402) bằng checkbox:

```razor
                                    <td class="px-2 py-1.5">
                                        <input type="checkbox" checked="@account.Enabled"
                                               disabled="@(_busy || _accountForm is not null)"
                                               @onchange="() => ToggleAccountEnabledAsync(account)" />
                                    </td>
```

**(b)** Thêm method — đặt ngay sau `ToggleProviderEnabledAsync` (line ~715), mirror y hệt pattern:

```csharp
    private async Task ToggleAccountEnabledAsync(ProviderAccount account)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await AccountSvc.SetEnabledAsync(account.Id, !account.Enabled);
            await LoadAccountsAsync(account.ProviderId);
        }
        catch (Exception ex)
        {
            Log.Error("Không đổi được trạng thái tài khoản.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }
```

- [ ] **Step 4.6: Build + full suite:**

```bash
dotnet build router-balancing.slnx
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: xanh cả hai.

- [ ] **Step 4.7: Commit:**

```bash
git add src/RouterBalancing.Core/Providers/IProviderAccountService.cs src/RouterBalancing.Core/Providers/ProviderAccountService.cs router-balancing/Components/Pages/Providers.razor "router balancing test/Providers/ProviderAccountServiceTests.cs"
git commit -m "feat: add quick enabled toggle for provider accounts"
```

---

### Task 5: Final verification

- [ ] **Step 5.1:** `dotnet build router-balancing.slnx` → xanh.
- [ ] **Step 5.2:** `dotnet test "router balancing test/router balancing test.csproj"` → 100% PASS (tắt app `router-balancing` đang mở trước khi chạy — `SingleInstanceGuardTests` fail nếu app giữ mutex).
- [ ] **Step 5.3:** `git status --short` → sạch; `git log --oneline -6` → đủ 5 commit (0→4).
