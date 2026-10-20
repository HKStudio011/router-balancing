using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

/// <summary>
/// Task 6: hành vi cũ của <see cref="ModelMetadataService"/> (điền field trống, không đè
/// giá trị user đã đặt, dừng chain ở bước đầu biết, scope <c>ProxyTarget</c> quanh probe)
/// được DỊCH sang assert trên fact/reader vì service không còn ghi 8 cột capability cũ.
/// Premise "JSON modalities hỏng" không còn tồn tại trong kiểu typed — thay bằng premise
/// tương đương "token modality lạ bị bỏ, không ném" (tiền lệ Task 4).
/// </summary>
public class ModelMetadataServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public ModelMetadataServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private ModelMetadataService ServiceWith(params IModelMetadataProvider[] chain) =>
        new(_db, new NullLog(), chain, new ModelCapabilityWriter(_db, new NullLog()),
            new ModelCapabilityReader(_db));

    private ModelCapabilityReader Reader => new(_db);

    private async Task<Model> ReadModelAsync(long modelId)
    {
        using var db = _db.CreateDbContext();
        return await db.Models.SingleAsync(m => m.Id == modelId);
    }

    private sealed class FixedProvider(
        ModelMetadata? result,
        bool returnsCompleteFacts = false,
        CapabilitySource source = CapabilitySource.Catalog) : IModelMetadataProvider
    {
        public int Calls { get; private set; }
        public bool ReturnsCompleteFacts { get; } = returnsCompleteFacts;
        public CapabilitySource Source { get; } = source;

        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>Bước chain ghi lại ProxyTarget tại thời điểm gọi — bắt lỗi thiếu scope probe.</summary>
    private sealed class TargetRecordingProvider : IModelMetadataProvider
    {
        public ProxyTarget? CapturedTarget { get; private set; }
        public bool ReturnsCompleteFacts => false;
        public CapabilitySource Source => CapabilitySource.Catalog;

        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
        {
            CapturedTarget = ProxyTarget.Current.Value;
            return Task.FromResult<ModelMetadata?>(null);
        }
    }

    // NullLog: dùng lại từ `router balancing test/TestDoubles.cs` — KHÔNG khai báo inner class.

    private async Task<long> SeedModelAsync()
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
        provider.Models.Add(new Model { ModelId = "gpt-4o" });
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Models[0].Id;
    }

    [Fact]
    public async Task TryFillAsync_WhenFirstProviderKnows_AppliesFactsAndStopsChain()
    {
        var modelId = await SeedModelAsync();
        var first = new FixedProvider(new ModelMetadata(
            128_000, SupportsVision: true, SupportsThink: true,
            ThinkEfforts: ["low", "high"], InputModalities: ["text", "image"], OutputModalities: ["text"],
            SupportsTools: true));
        var second = new FixedProvider(new ModelMetadata(
            null, SupportsVision: false, SupportsThink: false, null, null, null, SupportsTools: false));
        var service = ServiceWith(first, second);

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.Tools);
        Assert.True(view.Reasoning);
        Assert.True(view.Vision);
        Assert.Equal(new[] { "high", "low" }, view.Efforts); // reader sort ordinal
        Assert.Equal(128_000, (await ReadModelAsync(modelId)).ContextWindow);
        // Bước 1 trả non-null → service dừng, bước 2 không chạy (fact của nó không được ghi)
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task TryFillAsync_EndpointProvider_WritesEndpointSourceWithoutNegatives()
    {
        var modelId = await SeedModelAsync();
        var endpoint = new FixedProvider(
            new ModelMetadata(
                null, SupportsVision: true, SupportsThink: null,
                ThinkEfforts: null, InputModalities: ["text", "image"], OutputModalities: null,
                SupportsTools: true),
            returnsCompleteFacts: false,
            source: CapabilitySource.Endpoint);
        var service = ServiceWith(endpoint);

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.Tools);
        Assert.True(view.Vision);
        Assert.Equal(CapabilitySource.Endpoint, view.SourceOf(CapabilityKind.Tools));
        Assert.Equal(CapabilitySource.Endpoint, view.SourceOf(ModalityDirection.Input, ModalityKind.Image));
        // Endpoint không phải full-list → không được sinh KnownAbsent cho modality vắng mặt.
        Assert.Null(view.Supports(ModalityDirection.Input, ModalityKind.Audio));
        Assert.DoesNotContain(
            (ModalityDirection.Input, ModalityKind.Audio),
            view.Modalities.Keys);
    }

    [Fact]
    public async Task TryFillAsync_WhenFirstReturnsNull_FallsBackToSecond()
    {
        var modelId = await SeedModelAsync();
        var second = new FixedProvider(new ModelMetadata(99_999, null, null, null, null, null, SupportsTools: true));
        var service = ServiceWith(new FixedProvider(null), second);

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.Tools);
        Assert.Equal(99_999, (await ReadModelAsync(modelId)).ContextWindow);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task TryFillAsync_WhenAllNull_LeavesFactsEmpty()
    {
        var modelId = await SeedModelAsync();
        var service = ServiceWith(new FixedProvider(null));

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.IsEmpty);
        Assert.Null((await ReadModelAsync(modelId)).ContextWindow);
    }

    [Fact]
    public async Task TryFillAsync_WhenContextWindowAlreadySet_KeepsExistingValue()
    {
        var modelId = await SeedModelAsync();
        using (var db = _db.CreateDbContext())
        {
            var seeded = await db.Models.SingleAsync(m => m.Id == modelId);
            seeded.ContextWindow = 42;
            await db.SaveChangesAsync();
        }
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(128_000, null, null, null, null, null, SupportsTools: true)));

        await service.TryFillAsync(modelId);

        var model = await ReadModelAsync(modelId);
        Assert.Equal(42, model.ContextWindow); // không ghi đè giá trị đã có (spec §3.3)
        Assert.True((await Reader.ReadAsync(modelId)).Tools); // fact chưa có vẫn được điền
    }

    [Fact]
    public async Task TryFillAsync_WhenUserFactExists_DoesNotOverwriteUserValue()
    {
        var modelId = await SeedModelAsync();
        var writer = new ModelCapabilityWriter(_db, new NullLog());
        await writer.ApplyUserEditAsync(modelId, new CapabilityPatch(
            new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = false },
            new Dictionary<(ModalityDirection, ModalityKind), bool>(),
            new HashSet<string>()));
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, null, null, null, null, null, SupportsTools: true),
            returnsCompleteFacts: true));

        await service.TryFillAsync(modelId);

        // C-N: user thắng catalog — dòng user đã có không bị đè giá trị/nguồn.
        Assert.False((await Reader.ReadAsync(modelId)).Tools);
        using var db = _db.CreateDbContext();
        var row = await db.ModelCapabilities
            .SingleAsync(c => c.ModelId == modelId && c.Capability == CapabilityKind.Tools);
        Assert.Equal(CapabilitySource.User, row.Source);
        Assert.True(row.KnownAbsent);
    }

    [Fact]
    public async Task TryFillAsync_WhenUserModalityFactExists_NotClearedByCatalogList()
    {
        var modelId = await SeedModelAsync();
        var writer = new ModelCapabilityWriter(_db, new NullLog());
        await writer.ApplyUserEditAsync(modelId, new CapabilityPatch(
            new Dictionary<CapabilityKind, bool>(),
            new Dictionary<(ModalityDirection, ModalityKind), bool>
            {
                [(ModalityDirection.Input, ModalityKind.Audio)] = true,
            },
            new HashSet<string>()));
        // Catalog full-list chỉ nói ["text"] — sẽ sinh audio=false nếu không có luật user thắng.
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, null, null, null, ["text"], null), returnsCompleteFacts: true));

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.AudioInput);
        using var db = _db.CreateDbContext();
        var row = await db.ModelModalities.SingleAsync(m =>
            m.ModelId == modelId && m.Direction == ModalityDirection.Input && m.Modality == ModalityKind.Audio);
        Assert.Equal(CapabilitySource.User, row.Source);
    }

    [Fact]
    public async Task TryFillAsync_WhenModelUnknown_DoesNothing()
    {
        var service = ServiceWith(new FixedProvider(null));

        // Không ném — fill là best-effort
        await service.TryFillAsync(999);
    }

    [Fact]
    public async Task TryFillAsync_ScopesProxyTargetWithJunctionsForProbeChain()
    {
        long proxyId;
        using (var db = _db.CreateDbContext())
        {
            var proxy = new OutboundProxy { Scheme = "socks5", Host = "127.0.0.1", Port = 1080 };
            db.OutboundProxies.Add(proxy);
            await db.SaveChangesAsync();
            proxyId = proxy.Id;
        }
        long providerId, modelId;
        using (var db = _db.CreateDbContext())
        {
            var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
            provider.Models.Add(new Model { ModelId = "gpt-4o" });
            db.Providers.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
            modelId = provider.Models[0].Id;
        }
        using (var db = _db.CreateDbContext())
        {
            db.Set<ProviderProxy>().Add(new ProviderProxy { ProviderId = providerId, ProxyId = proxyId });
            await db.SaveChangesAsync();
        }

        var probe = new TargetRecordingProvider();
        var service = ServiceWith(probe);

        await service.TryFillAsync(modelId);

        // Regression 2026-10-06: thiếu ProxyTarget → request metadata rơi về global pool
        // (D7), provider không gán proxy vẫn đi qua SOCKS; junction phải được load để
        // provider CÓ proxy resolve đúng.
        Assert.NotNull(probe.CapturedTarget);
        Assert.Equal(providerId, probe.CapturedTarget!.Provider.Id);
        Assert.Single(probe.CapturedTarget.Provider.ProviderProxies);
        Assert.Equal(proxyId, probe.CapturedTarget.Provider.ProviderProxies[0].ProxyId);
        Assert.Null(ProxyTarget.Current.Value); // finally phải reset scope
    }

    [Fact]
    public async Task TryFillAsync_InputModalitiesContainAudio_DerivesAudioInput()
    {
        var modelId = await SeedModelAsync();
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, null, null, null, ["text", "audio"], null)));

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.AudioInput);
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Text));
        // Nguồn không full-list (probe endpoint) → im lặng ở video là "chưa biết", không phải false
        Assert.Null(view.VideoInput);
    }

    [Fact]
    public async Task TryFillAsync_InputModalitiesContainVideo_DerivesVideoInput()
    {
        var modelId = await SeedModelAsync();
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, null, null, null, ["text", "video"], null)));

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.VideoInput);
        Assert.Null(view.AudioInput);
    }

    [Fact]
    public async Task TryFillAsync_UnknownModalityToken_DroppedWithoutThrowing()
    {
        var modelId = await SeedModelAsync();
        // Typed equivalent của premise "JSON hỏng" cũ: nguồn trả token ngoài từ vựng.
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, null, null, null, ["not-a-modality"], null),
            returnsCompleteFacts: true));

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.Empty(view.InputModalities); // token lạ bị bỏ, không ném
        Assert.False(view.Vision);
        Assert.False(view.AudioInput);
        Assert.False(view.VideoInput);
    }

    [Fact]
    public async Task TryFillAsync_MixedCaseModalityTokens_IncompleteProvider_PreservesPositiveVision()
    {
        var modelId = await SeedModelAsync();
        // Regression: endpoint giữ case gốc ("Text"/"Image"), còn CapabilityNames.TryFromDb
        // so khớp tuyệt đối. Mapper phải normalize trước khi tra từ vựng, nếu không list sẽ
        // ghi Image=false đè lên SupportsVision=true.
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, SupportsVision: true, SupportsThink: null, ThinkEfforts: null,
                InputModalities: ["Text", "Image"], OutputModalities: null),
            returnsCompleteFacts: false));

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Text));
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Image));
        // Probe positive-only: không có audio trong list = chưa biết, không được sinh negative.
        Assert.Null(view.AudioInput);
        using var db = _db.CreateDbContext();
        Assert.Empty(await db.ModelModalities.Where(m =>
            m.ModelId == modelId
            && m.Direction == ModalityDirection.Input
            && m.Modality == ModalityKind.Audio).ToListAsync());
    }

    [Fact]
    public async Task TryFillAsync_MixedCaseModalityTokens_CompleteProvider_WritesCorrectNegatives()
    {
        var modelId = await SeedModelAsync();
        // Full-list: list có mặt nghĩa là các modality không được nhắc (audio/video/file)
        // là "đã biết là không"; token mixed-case vẫn phải bật đúng positive text/image.
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, SupportsVision: true, SupportsThink: null, ThinkEfforts: null,
                InputModalities: ["Text", "Image"], OutputModalities: null),
            returnsCompleteFacts: true));

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Text));
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Image));
        Assert.False(view.AudioInput);
        Assert.False(view.VideoInput);
    }

    [Fact]
    public async Task TryFillAsync_CompleteProvider_ToolsFalse_WritesKnownAbsent()
    {
        var modelId = await SeedModelAsync();
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, null, null, null, null, null, SupportsTools: false),
            returnsCompleteFacts: true));

        await service.TryFillAsync(modelId);

        Assert.False((await Reader.ReadAsync(modelId)).Tools);
        using var db = _db.CreateDbContext();
        var row = await db.ModelCapabilities
            .SingleAsync(c => c.ModelId == modelId && c.Capability == CapabilityKind.Tools);
        Assert.True(row.KnownAbsent);
        Assert.Equal(CapabilitySource.Catalog, row.Source);
    }

    [Fact]
    public async Task TryFillAsync_IncompleteProvider_ToolsFalse_WritesNothing()
    {
        var modelId = await SeedModelAsync();
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(null, null, null, null, null, null, SupportsTools: false),
            returnsCompleteFacts: false));

        await service.TryFillAsync(modelId);

        // Probe positive-only không được sinh KnownAbsent (C-P/C-M) — vẫn "chưa biết".
        Assert.Null((await Reader.ReadAsync(modelId)).Tools);
        using var db = _db.CreateDbContext();
        Assert.Empty(await db.ModelCapabilities.Where(c => c.ModelId == modelId).ToListAsync());
    }

    [Fact]
    public async Task TryFillAsync_WritesFactsOnly_NotLegacyColumns()
    {
        var modelId = await SeedModelAsync();
        var service = ServiceWith(new FixedProvider(
            new ModelMetadata(128_000, true, true, ["high"], ["text", "image", "audio", "video"], ["text", "audio"],
                SupportsTools: true),
            returnsCompleteFacts: true));

        await service.TryFillAsync(modelId);

        var view = await Reader.ReadAsync(modelId);
        Assert.True(view.Tools);
        Assert.True(view.Vision);
        Assert.True(view.AudioInput);
        Assert.True(view.VideoInput);
        Assert.Equal(new[] { "high" }, view.Efforts);

        // 8 cột capability cũ nằm im (chỉ còn schema + consumer cũ đọc) cho tới Task 11.
        var model = await ReadModelAsync(modelId);
        Assert.Null(model.SupportsTools); // bool? cũ — chưa từng được fill ghi
        Assert.False(model.SupportsVision);
        Assert.False(model.SupportsThink);
        Assert.False(model.SupportsAudio);
        Assert.False(model.SupportsVideo);
        Assert.Null(model.ThinkEfforts);
        Assert.Null(model.InputModalities);
        Assert.Null(model.OutputModalities);
    }
}
