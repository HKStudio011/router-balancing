using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Combos;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

/// <summary>
/// Test service trang Tools: dựng bảng chọn từ DB (SQLite tạm) + tick mặc định C7 + dedupe khi generate.
/// Plan 1 Task 4: capability của row/model phải đến từ fact tables qua IModelCapabilityReader.
/// Plan 2: combo thành một entry config (U12), key model có prefix Identifier (C-A), skipped key được báo.
/// </summary>
public class OpenCodeConfigServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly string _configDir = Path.Combine(
        Path.GetTempPath(), "rb-tools-service-" + Guid.NewGuid().ToString("N"));

    public OpenCodeConfigServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db); // 4 preset free, Enabled=false (D9)
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        _testDb.Dispose();
        try
        {
            if (Directory.Exists(_configDir))
            {
                foreach (var file in Directory.GetFiles(_configDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_configDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dọn best-effort — không che test failure.
        }
    }

    private OpenCodeConfigService Service() =>
        new(_db, new ModelCapabilityReader(_db),
            new ComboCapabilityCalculator(_db, new ModelCapabilityReader(_db)),
            new OpenCodeConfigFileWriter());

    private static JsonElement Models(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("providers")
            .GetProperty("router-balancing").GetProperty("models");

    private static JsonElement CapabilitiesOf(string json, string key) =>
        Models(json).GetProperty(key).GetProperty("capabilities");

    private async Task SetProviderEnabledAsync(string providerName, bool enabled)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Name == providerName);
        provider.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    private async Task<long> ProviderIdAsync(string providerName)
    {
        using var db = _db.CreateDbContext();
        return (await db.Providers.AsNoTracking().SingleAsync(p => p.Name == providerName)).Id;
    }

    /// <summary>Thêm 1 model vào provider preset; trả về Id entity để test chọn model theo Id như UI.</summary>
    private async Task<long> AddModelAsync(
        string providerName, string modelId, bool enabled, string? displayName = null, int? contextWindow = null)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Name == providerName);
        var model = new Model
        {
            ProviderId = provider.Id,
            ModelId = modelId,
            Enabled = enabled,
            DisplayName = displayName,
            ContextWindow = contextWindow,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    /// <summary>Provider thường (không preset) để test đường key bare khi Identifier null.</summary>
    private async Task<long> AddProviderAsync(string name, string? identifier = null)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            Identifier = identifier,
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            Enabled = true,
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    private async Task<long> AddModelToProviderAsync(
        long providerId, string modelId, string? displayName = null, int? contextWindow = null)
    {
        using var db = _db.CreateDbContext();
        var model = new Model
        {
            ProviderId = providerId,
            ModelId = modelId,
            Enabled = true,
            DisplayName = displayName,
            ContextWindow = contextWindow,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private async Task<long> AddComboAsync(string name, params long[] modelIds)
    {
        using var db = _db.CreateDbContext();
        var combo = new Combo { Name = name };
        for (var i = 0; i < modelIds.Length; i++)
        {
            combo.Items.Add(new ComboItem { TargetModelId = modelIds[i], Position = i });
        }
        db.Combos.Add(combo);
        await db.SaveChangesAsync();
        return combo.Id;
    }

    private async Task SeedCapabilityAsync(long modelId, CapabilityKind kind, bool present)
    {
        using var db = _db.CreateDbContext();
        db.ModelCapabilities.Add(new ModelCapability
        {
            ModelId = modelId,
            Capability = kind,
            KnownAbsent = !present,
            Source = CapabilitySource.Catalog,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedModalityAsync(long modelId, ModalityDirection direction, ModalityKind kind, bool present)
    {
        using var db = _db.CreateDbContext();
        db.ModelModalities.Add(new ModelModality
        {
            ModelId = modelId,
            Direction = direction,
            Modality = kind,
            KnownAbsent = !present,
            Source = CapabilitySource.Catalog,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetPageData_ProviderDisabled_FlagsGroupAndSkipsDefaultTick()
    {
        // seed mặc định: preset Enabled=false
        var data = await Service().GetPageDataAsync();

        var free = data.Groups.Single(g => g.IsFreePreset);
        Assert.False(free.ProviderEnabled);
        Assert.Empty(data.DefaultSelectedIds);               // C7: provider tắt → không tick mặc định
        Assert.All(free.Models, m => Assert.True(m.Id > 0));
    }

    [Fact]
    public async Task GetPageData_DefaultsToFreePresetModels_WhenProviderEnabled()
    {
        await SetProviderEnabledAsync("OpenCode Free", true);
        await AddModelAsync("OpenCode Free", "big-pickle", enabled: true);
        await AddModelAsync("OpenCode Free", "hidden-model", enabled: false);

        var data = await Service().GetPageDataAsync();

        var free = data.Groups.Single(g => g.IsFreePreset);
        Assert.True(free.ProviderEnabled);
        Assert.Single(free.Models);                          // chỉ model Enabled được list
        Assert.Equal(data.DefaultSelectedIds, free.Models.Select(m => m.Id));
    }

    [Fact]
    public async Task GenerateAsync_SameModelIdInDifferentProviders_EmitsBothPrefixedKeys()
    {
        // C-A: prefix Identifier hết nhập nhằng ModelId trùng giữa provider — không còn dedupe oan.
        await SetProviderEnabledAsync("OpenCode Free", true);
        await SetProviderEnabledAsync("OpenRouter Free", true);
        var freeRow = await AddModelAsync("OpenCode Free", "shared-id", enabled: true, displayName: "Free copy");
        var otherRow = await AddModelAsync("OpenRouter Free", "shared-id", enabled: true, displayName: "Other copy");

        var result = await Service().GenerateAsync([otherRow, freeRow], [], "http://127.0.0.1:8317/v1", null);

        Assert.Empty(result.SkippedKeys);
        var models = Models(result.Json);
        Assert.Equal("Free copy", models.GetProperty("opencode-free/shared-id").GetProperty("name").GetString());
        Assert.Equal("Other copy", models.GetProperty("openrouter-free/shared-id").GetProperty("name").GetString());
    }

    [Fact]
    public void BuildBaseUrl_UsesProxyPort_WhenRunning_FallsBackToSettings()
    {
        Assert.Equal("http://127.0.0.1:9000/v1", OpenCodeConfigService.BuildBaseUrl(9000, 8317));
        Assert.Equal("http://127.0.0.1:8317/v1", OpenCodeConfigService.BuildBaseUrl(null, 8317));
    }

    [Fact]
    public async Task GenerateAsync_EmptySelection_EmitsEmptyModels()
    {
        var result = await Service().GenerateAsync([], [], "http://127.0.0.1:8317/v1", null);
        Assert.Empty(Models(result.Json).EnumerateObject());
    }

    [Fact]
    public async Task GetPageData_ListsOnlyEnabledModels_SkipsEmptyGroups()
    {
        await SetProviderEnabledAsync("OpenRouter Free", true);
        await AddModelAsync("OpenRouter Free", "disabled-only", enabled: false);

        var data = await Service().GetPageDataAsync();

        // Provider bật nhưng 0 model Enabled → nhóm rỗng, không xuất hiện trên bảng chọn
        Assert.DoesNotContain(data.Groups, g => g.ProviderName == "OpenRouter Free");
    }

    [Fact]
    public async Task GetPageData_ModelWithoutFacts_RowFlagsAreUnknownNull()
    {
        // Task 4: row dựng từ reader — model chưa có dòng fact phải ra toàn null ("chưa biết"),
        // KHÔNG phải false; đây là guard chống reader bị thay bằng đọc cột cũ (default false).
        await SetProviderEnabledAsync("OpenCode Free", true);
        await AddModelAsync("OpenCode Free", "no-fact-model", enabled: true);

        var data = await Service().GetPageDataAsync();

        var row = data.Groups.Single(g => g.IsFreePreset).Models.Single(m => m.ModelId == "no-fact-model");
        Assert.Null(row.Tools);
        Assert.Null(row.Reasoning);
        Assert.Null(row.Vision);
        Assert.Null(row.AudioInput);
        Assert.Null(row.VideoInput);
        Assert.Empty(row.InputModalities!);
        Assert.Empty(row.OutputModalities!);
    }

    [Fact]
    public async Task GetPageData_FactRows_MapThreeStateToRow()
    {
        // Ba trạng thái phải đi qua row nguyên vẹn: present→true, KnownAbsent→false, không dòng→null.
        await SetProviderEnabledAsync("OpenCode Free", true);
        var id = await AddModelAsync("OpenCode Free", "fact-model", enabled: true);
        await SeedCapabilityAsync(id, CapabilityKind.Tools, present: true);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Image, present: false);

        var data = await Service().GetPageDataAsync();

        var row = data.Groups.Single(g => g.IsFreePreset).Models.Single(m => m.ModelId == "fact-model");
        Assert.True(row.Tools);            // dòng present → true
        Assert.False(row.Vision);          // KnownAbsent → false (đã biết là không), khác null
        Assert.Null(row.Reasoning);        // không có dòng → chưa biết
        Assert.Empty(row.InputModalities!); // chỉ khẳng định thuận — image KnownAbsent bị loại, text chưa seed
    }

    [Fact]
    public async Task GenerateAsync_FactRows_EmitCapabilitiesFromView()
    {
        // Đường generate cũng phải đọc fact (ReadManyAsync một lần cho cả nhóm), không đọc cột cũ.
        await SetProviderEnabledAsync("OpenCode Free", true);
        var id = await AddModelAsync("OpenCode Free", "gen-fact-model", enabled: true);
        await SeedCapabilityAsync(id, CapabilityKind.Tools, present: false);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Text, present: true);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Image, present: true);
        using (var db = _db.CreateDbContext())
        {
            db.ModelReasoningEfforts.Add(new ModelReasoningEffort
            {
                ModelId = id, Level = "high", Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync();
        }

        var result = await Service().GenerateAsync([id], [], "http://127.0.0.1:8317/v1", null);

        var caps = CapabilitiesOf(result.Json, "opencode-free/gen-fact-model");
        Assert.Equal(JsonValueKind.False, caps.GetProperty("tools").ValueKind); // KnownAbsent → "tools": false
        // view chỉ phát khẳng định thuận, theo ModalityKind tăng dần: text(0) < image(1)
        Assert.Equal(new[] { "text", "image" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.False(caps.TryGetProperty("reasoning", out _)); // view.Reasoning chưa biết → không emit cả khi có effort
    }

    [Fact]
    public async Task GetPageData_GroupCarriesProviderId()
    {
        // Trang Tools lưu trạng thái thu/giãn theo ProviderId — record group phải mang id thật.
        var data = await Service().GetPageDataAsync();
        var providerId = await ProviderIdAsync("OpenCode Free");

        var free = data.Groups.Single(g => g.IsFreePreset);
        Assert.Equal(providerId, free.ProviderId);
    }

    [Fact]
    public async Task GenerateAsync_ModelUsesProviderIdentifierPrefix()
    {
        var providerId = await AddProviderAsync("Custom Provider", identifier: "px");
        var modelId = await AddModelToProviderAsync(providerId, "m1");

        var result = await Service().GenerateAsync([modelId], [], "http://127.0.0.1:8317/v1", null);

        Assert.True(Models(result.Json).TryGetProperty("px/m1", out _));
    }

    [Fact]
    public async Task GenerateAsync_ProviderWithoutIdentifier_UsesBareModelId()
    {
        // C-A: Identifier null (hàng cũ chưa backfill) → giữ id trần, không bịa prefix.
        var providerId = await AddProviderAsync("No Id Provider");
        var modelId = await AddModelToProviderAsync(providerId, "bare-model");

        var result = await Service().GenerateAsync([modelId], [], "http://127.0.0.1:8317/v1", null);

        Assert.True(Models(result.Json).TryGetProperty("bare-model", out _));
    }

    [Fact]
    public async Task GenerateAsync_KeyConflictWithoutIdentifier_ReportsSkippedKey()
    {
        // RF-4: hai provider chưa có Identifier + cùng ModelId → key trần trùng; first-wins theo Id,
        // key bị bỏ được báo để UI ghi chú (không im lặng mất model).
        var providerA = await AddProviderAsync("No Id A");
        var providerB = await AddProviderAsync("No Id B");
        var modelA = await AddModelToProviderAsync(providerA, "dup", displayName: "A model");
        var modelB = await AddModelToProviderAsync(providerB, "dup", displayName: "B model");

        var result = await Service().GenerateAsync([modelB, modelA], [], "http://127.0.0.1:8317/v1", null);

        var models = Models(result.Json);
        Assert.Single(models.EnumerateObject());
        Assert.Equal("A model", models.GetProperty("dup").GetProperty("name").GetString()); // Id nhỏ hơn thắng
        Assert.Equal(new[] { "dup" }, result.SkippedKeys);
    }

    [Fact]
    public async Task GetPageData_ListsCombosWithCapabilitiesAndMemberCount()
    {
        await SetProviderEnabledAsync("OpenCode Free", true);
        var modelA = await AddModelAsync("OpenCode Free", "combo-a", enabled: true, contextWindow: 128_000);
        var modelB = await AddModelAsync("OpenCode Free", "combo-b", enabled: true, contextWindow: 64_000);
        await SeedCapabilityAsync(modelA, CapabilityKind.Tools, present: true);
        await SeedCapabilityAsync(modelB, CapabilityKind.Tools, present: true);
        await SeedModalityAsync(modelA, ModalityDirection.Input, ModalityKind.Text, present: true);
        await SeedModalityAsync(modelA, ModalityDirection.Input, ModalityKind.Image, present: true);
        await SeedModalityAsync(modelA, ModalityDirection.Output, ModalityKind.Text, present: true);
        await SeedModalityAsync(modelA, ModalityDirection.Output, ModalityKind.Audio, present: true);
        await SeedModalityAsync(modelB, ModalityDirection.Input, ModalityKind.Text, present: true);
        await SeedModalityAsync(modelB, ModalityDirection.Output, ModalityKind.Text, present: true);
        var comboId = await AddComboAsync("studio", modelA, modelB);

        var data = await Service().GetPageDataAsync();

        var row = Assert.Single(data.Combos, c => c.Id == comboId);
        Assert.Equal(2, row.MemberCount);
        Assert.Equal(ComboMode.RoundRobin, row.Mode);
        Assert.Equal(64_000, row.ContextWindow);            // min member
        Assert.True(row.Tools);
        Assert.Equal(new[] { "text" }, row.InputModalities);  // giao [text,image] ∩ [text]
        Assert.Equal(new[] { "text" }, row.OutputModalities); // giao [text,audio] ∩ [text]
    }

    [Fact]
    public async Task GenerateAsync_ComboOnly_EmitsSingleEntryAndNoMemberModels()
    {
        // U12: tick combo = một entry duy nhất key tên combo; model thành viên KHÔNG bị nhân bản.
        await SetProviderEnabledAsync("OpenCode Free", true);
        var member = await AddModelAsync("OpenCode Free", "member-model", enabled: true);
        await SeedCapabilityAsync(member, CapabilityKind.Tools, present: true);
        await SeedModalityAsync(member, ModalityDirection.Input, ModalityKind.Text, present: true);
        await SeedModalityAsync(member, ModalityDirection.Input, ModalityKind.Image, present: true);
        var comboId = await AddComboAsync("unsloth-studio", member);

        var result = await Service().GenerateAsync([], [comboId], "http://127.0.0.1:8317/v1", null);

        var models = Models(result.Json);
        Assert.Single(models.EnumerateObject());
        var entry = models.GetProperty("unsloth-studio");
        Assert.Equal("unsloth-studio", entry.GetProperty("name").GetString());
        Assert.Equal(new[] { "text", "image" },
            entry.GetProperty("capabilities").GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.False(models.TryGetProperty("opencode-free/member-model", out _));
    }

    [Fact]
    public async Task GenerateAsync_ComboCapabilities_UseIntersectionWeakestMember()
    {
        // C-L: entry combo nói capability của member yếu nhất, không phải mạnh nhất.
        var providerId = await AddProviderAsync("Combo Provider");
        var strong = await AddModelToProviderAsync(providerId, "strong");
        var weak = await AddModelToProviderAsync(providerId, "weak");
        await SeedCapabilityAsync(strong, CapabilityKind.Tools, present: true);
        await SeedCapabilityAsync(weak, CapabilityKind.Tools, present: false);
        await SeedModalityAsync(strong, ModalityDirection.Input, ModalityKind.Text, present: true);
        await SeedModalityAsync(strong, ModalityDirection.Input, ModalityKind.Audio, present: true);
        await SeedModalityAsync(weak, ModalityDirection.Input, ModalityKind.Text, present: true);
        await SeedModalityAsync(weak, ModalityDirection.Input, ModalityKind.Audio, present: false);
        var comboId = await AddComboAsync("weak-combo", strong, weak);

        var result = await Service().GenerateAsync([], [comboId], "http://127.0.0.1:8317/v1", null);

        var caps = CapabilitiesOf(result.Json, "weak-combo");
        Assert.False(caps.GetProperty("tools").GetBoolean()); // true ∧ false = false
        Assert.Equal(new[] { "text" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString())); // audio bị member yếu loại
    }

    [Fact]
    public async Task WriteAsync_SelectedModelAndCombo_WritesBlockAndReturnsPaths()
    {
        await SetProviderEnabledAsync("OpenCode Free", true);
        var model = await AddModelAsync("OpenCode Free", "big-pickle", enabled: true);
        var comboMember = await AddModelAsync("OpenCode Free", "member", enabled: true);
        var comboId = await AddComboAsync("studio", comboMember);
        var path = Path.Combine(_configDir, "opencode.json");
        const string original = "{ \"plugin\": [\"keep-me\"] }";
        await File.WriteAllTextAsync(path, original);

        var result = await Service().WriteAsync(path, [model], [comboId], "http://127.0.0.1:8317/v1", null);

        Assert.Equal(Path.GetFullPath(path), result.Path);
        Assert.Equal(path + ".bak", result.BackupPath);
        Assert.Equal(original, await File.ReadAllTextAsync(result.BackupPath!));
        var written = await File.ReadAllTextAsync(path);
        Assert.Contains("\"plugin\": [\"keep-me\"]", written);
        var models = Models(written);
        Assert.True(models.TryGetProperty("opencode-free/big-pickle", out _));
        Assert.True(models.TryGetProperty("studio", out _));
    }

    [Fact]
    public async Task WriteAsync_InvalidConfigFile_Throws_LeavesFileUntouched()
    {
        var path = Path.Combine(_configDir, "broken.json");
        const string original = "{ \"providers\": { \"a\": } }";
        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAsync<OpenCodeConfigFormatException>(() =>
            Service().WriteAsync(path, [], [], "http://127.0.0.1:8317/v1", null));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task WriteAsync_NoSelection_WritesProviderBlockWithEmptyModels()
    {
        // Service không chặn selection rỗng (UI chặn bằng nút disabled) — ghim hành vi: block hợp lệ, models {}.
        var path = Path.Combine(_configDir, "empty.json");

        var result = await Service().WriteAsync(path, [], [], "http://127.0.0.1:8317/v1", null);

        Assert.Null(result.BackupPath);
        Assert.Empty(Models(await File.ReadAllTextAsync(path)).EnumerateObject());
    }
}
