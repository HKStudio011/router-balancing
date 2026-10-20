using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

/// <summary>
/// Test service trang Tools: dựng bảng chọn từ DB (SQLite tạm) + tick mặc định C7 + dedupe khi generate.
/// Task 4: capability của row/model phải đến từ fact tables qua IModelCapabilityReader —
/// không còn đọc cột capability cũ trên Models.
/// </summary>
public class OpenCodeConfigServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public OpenCodeConfigServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db); // 4 preset free, Enabled=false (D9)
    }

    public void Dispose() => _testDb.Dispose();

    private OpenCodeConfigService Service() => new(_db, new ModelCapabilityReader(_db));

    private async Task SetProviderEnabledAsync(string providerName, bool enabled)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Name == providerName);
        provider.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    /// <summary>Thêm 1 model vào provider; trả về Id entity để test chọn model theo Id như UI.</summary>
    private async Task<long> AddModelAsync(string providerName, string modelId, bool enabled, string? displayName = null)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Name == providerName);
        var model = new Model
        {
            ProviderId = provider.Id,
            ModelId = modelId,
            Enabled = enabled,
            DisplayName = displayName,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
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
    public async Task GenerateAsync_DuplicateIds_KeepsFreePresetThenIdOrder_AndReportsSkipped()
    {
        await SetProviderEnabledAsync("OpenCode Free", true);
        var freeRow = await AddModelAsync("OpenCode Free", "shared-id", enabled: true, displayName: "Free copy");
        var otherRow = await AddModelAsync("OpenRouter Free", "shared-id", enabled: true, displayName: "Other copy"); // provider khác, cùng ModelId

        var result = await Service().GenerateAsync([otherRow, freeRow], "http://127.0.0.1:8317/v1", null);

        Assert.Equal(new[] { "shared-id" }, result.SkippedDuplicateModelIds);
        var name = JsonDocument.Parse(result.Json).RootElement.GetProperty("providers")
            .GetProperty("router-balancing").GetProperty("models").GetProperty("shared-id").GetProperty("name");
        Assert.Equal("Free copy", name.GetString());   // preset OpenCode Free thắng (IsFreePreset sort trước)
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
        var result = await Service().GenerateAsync([], "http://127.0.0.1:8317/v1", null);
        Assert.Empty(JsonDocument.Parse(result.Json).RootElement.GetProperty("providers")
            .GetProperty("router-balancing").GetProperty("models").EnumerateObject());
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
        using (var db = _db.CreateDbContext())
        {
            db.ModelCapabilities.Add(new ModelCapability
            {
                ModelId = id, Capability = CapabilityKind.Tools, KnownAbsent = false,
                Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            db.ModelModalities.Add(new ModelModality
            {
                ModelId = id, Direction = ModalityDirection.Input, Modality = ModalityKind.Image,
                KnownAbsent = true, Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync();
        }

        var data = await Service().GetPageDataAsync();

        var row = data.Groups.Single(g => g.IsFreePreset).Models.Single(m => m.ModelId == "fact-model");
        Assert.True(row.Tools);            // dòng present → true
        Assert.False(row.Vision);          // KnownAbsent → false (đã biết là không), khác null
        Assert.Null(row.Reasoning);        // không có dòng → chưa biết
        Assert.Empty(row.InputModalities); // chỉ khẳng định thuận — image KnownAbsent bị loại, text chưa seed
    }

    [Fact]
    public async Task GenerateAsync_FactRows_EmitCapabilitiesFromView()
    {
        // Đường generate cũng phải đọc fact (ReadManyAsync một lần cho cả nhóm), không đọc cột cũ.
        await SetProviderEnabledAsync("OpenCode Free", true);
        var id = await AddModelAsync("OpenCode Free", "gen-fact-model", enabled: true);
        using (var db = _db.CreateDbContext())
        {
            db.ModelCapabilities.Add(new ModelCapability
            {
                ModelId = id, Capability = CapabilityKind.Tools, KnownAbsent = true,
                Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            db.ModelModalities.Add(new ModelModality
            {
                ModelId = id, Direction = ModalityDirection.Input, Modality = ModalityKind.Text,
                KnownAbsent = false, Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            db.ModelModalities.Add(new ModelModality
            {
                ModelId = id, Direction = ModalityDirection.Input, Modality = ModalityKind.Image,
                KnownAbsent = false, Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            db.ModelReasoningEfforts.Add(new ModelReasoningEffort
            {
                ModelId = id, Level = "high", Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync();
        }

        var result = await Service().GenerateAsync([id], "http://127.0.0.1:8317/v1", null);

        var caps = JsonDocument.Parse(result.Json).RootElement.GetProperty("providers")
            .GetProperty("router-balancing").GetProperty("models").GetProperty("gen-fact-model")
            .GetProperty("capabilities");
        Assert.Equal(JsonValueKind.False, caps.GetProperty("tools").ValueKind); // KnownAbsent → "tools": false
        // view chỉ phát khẳng định thuận, theo ModalityKind tăng dần: text(0) < image(1)
        Assert.Equal(new[] { "text", "image" },
            caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.False(caps.TryGetProperty("reasoning", out _)); // view.Reasoning chưa biết → không emit cả khi có effort
    }
}
