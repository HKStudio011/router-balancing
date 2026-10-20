using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

/// <summary>
/// Task 9 (U13/C-N): user sửa capability qua ba trạng thái — ghi Source=User, ClearKeys xóa
/// fact bất kể nguồn, scalar limits cập nhật cùng lời gọi, reset xóa user fact rồi lấy lại catalog.
/// </summary>
public class ModelServiceCapabilityEditTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly ModelCapabilityWriter _writer;
    private readonly ModelCapabilityReader _reader;

    public ModelServiceCapabilityEditTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _writer = new ModelCapabilityWriter(_db, new NullLog());
        _reader = new ModelCapabilityReader(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private ModelService ServiceWith(IModelMetadataService? metadata = null) =>
        new(_db, _protector, new StubFactory(), new NullLog(),
            metadata ?? new NoopMetadataService(), _writer);

    private ModelMetadataService MetadataServiceWith(params IModelMetadataProvider[] chain) =>
        new(_db, new NullLog(), chain, _writer, _reader);

    private async Task<(long ProviderId, long ModelId)> SeedModelAsync()
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
        provider.Models.Add(new Model { ModelId = "gpt-4o" });
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return (provider.Id, provider.Models[0].Id);
    }

    private async Task SeedCatalogAsync(long modelId, params (CapabilityKind Kind, bool Value)[] capabilities)
    {
        var patch = new CapabilityPatch(
            capabilities.ToDictionary(x => x.Kind, x => x.Value),
            new Dictionary<(ModalityDirection, ModalityKind), bool>(),
            new HashSet<string>());
        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog, patch, sourceIsCompleteList: true);
    }

    private async Task SeedCatalogModalityAsync(
        long modelId, ModalityDirection direction, ModalityKind modality, bool value)
    {
        var patch = new CapabilityPatch(
            new Dictionary<CapabilityKind, bool>(),
            new Dictionary<(ModalityDirection, ModalityKind), bool> { [(direction, modality)] = value },
            new HashSet<string>());
        await _writer.ApplyAsync(modelId, CapabilitySource.Catalog, patch, sourceIsCompleteList: true);
    }

    private static CapabilityPatch Patch(
        Dictionary<CapabilityKind, bool>? capabilities = null,
        Dictionary<(ModalityDirection, ModalityKind), bool>? modalities = null,
        HashSet<(string Kind, string Key)>? clearKeys = null) =>
        new(capabilities ?? new Dictionary<CapabilityKind, bool>(),
            modalities ?? new Dictionary<(ModalityDirection, ModalityKind), bool>(),
            new HashSet<string>())
        {
            ClearKeys = clearKeys ?? new HashSet<(string Kind, string Key)>(),
        };

    [Fact]
    public async Task SetCapabilitiesAsync_UserSaysNo_WritesKnownAbsentRegardlessOfSourceList()
    {
        var (_, modelId) = await SeedModelAsync();
        await SeedCatalogAsync(modelId, (CapabilityKind.Tools, true));
        await SeedCatalogModalityAsync(modelId, ModalityDirection.Input, ModalityKind.Image, true);

        await ServiceWith().SetCapabilitiesAsync(
            modelId, 128_000, 4_096,
            Patch(
                new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = false },
                new Dictionary<(ModalityDirection, ModalityKind), bool>
                {
                    [(ModalityDirection.Input, ModalityKind.Image)] = false,
                }));

        var view = await _reader.ReadAsync(modelId);
        Assert.False(view.Tools);
        Assert.False(view.Vision);
        Assert.Equal(CapabilitySource.User, view.SourceOf(CapabilityKind.Tools));
        Assert.Equal(CapabilitySource.User, view.SourceOf(ModalityDirection.Input, ModalityKind.Image));

        using var db = _db.CreateDbContext();
        var saved = await db.Models.SingleAsync(m => m.Id == modelId);
        Assert.Equal(128_000, saved.ContextWindow);
        Assert.Equal(4_096, saved.MaxOutputTokens);
    }

    [Fact]
    public async Task SetCapabilitiesAsync_ClearKey_RemovesFact()
    {
        var (_, modelId) = await SeedModelAsync();
        await SeedCatalogAsync(modelId,
            (CapabilityKind.Tools, true),
            (CapabilityKind.Reasoning, false));
        await SeedCatalogModalityAsync(modelId, ModalityDirection.Input, ModalityKind.Image, true);

        await ServiceWith().SetCapabilitiesAsync(
            modelId, null, null,
            Patch(clearKeys:
            [
                CapabilityPatch.CapabilityClearKey(CapabilityKind.Tools),
                CapabilityPatch.ModalityClearKey(ModalityDirection.Input, ModalityKind.Image),
            ]));

        var view = await _reader.ReadAsync(modelId);
        Assert.Null(view.Tools);
        Assert.Null(view.Vision);
        Assert.False(view.Reasoning); // key không nằm trong patch → giữ nguyên
        Assert.Equal(CapabilitySource.Catalog, view.SourceOf(CapabilityKind.Reasoning));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_000_001)]
    public async Task SetCapabilitiesAsync_OutOfRangeContext_Throws(int badValue)
    {
        var (_, modelId) = await SeedModelAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ServiceWith().SetCapabilitiesAsync(modelId, badValue, null, Patch()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_000_001)]
    public async Task SetCapabilitiesAsync_OutOfRangeMaxOutput_Throws(int badValue)
    {
        var (_, modelId) = await SeedModelAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ServiceWith().SetCapabilitiesAsync(modelId, null, badValue, Patch()));
    }

    [Fact]
    public async Task SetCapabilitiesAsync_UnknownModel_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            ServiceWith().SetCapabilitiesAsync(999_999, 1_000, 1_000, Patch()));
    }

    [Fact]
    public async Task SetCapabilitiesAsync_PatchNull_Throws()
    {
        var (_, modelId) = await SeedModelAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ServiceWith().SetCapabilitiesAsync(modelId, 1_000, 1_000, null!));
    }

    [Fact]
    public async Task SetCapabilitiesAsync_UpdatesContextAndMaxOutput()
    {
        var (_, modelId) = await SeedModelAsync();
        var service = ServiceWith();

        await service.SetCapabilitiesAsync(modelId, 64_000, 8_000, Patch());

        using (var db = _db.CreateDbContext())
        {
            var saved = await db.Models.SingleAsync(m => m.Id == modelId);
            Assert.Equal(64_000, saved.ContextWindow);
            Assert.Equal(8_000, saved.MaxOutputTokens);
        }

        // Ô trống trong UI → null = clear
        await service.SetCapabilitiesAsync(modelId, null, null, Patch());

        using var db2 = _db.CreateDbContext();
        var cleared = await db2.Models.SingleAsync(m => m.Id == modelId);
        Assert.Null(cleared.ContextWindow);
        Assert.Null(cleared.MaxOutputTokens);
    }

    [Fact]
    public async Task SetCapabilitiesAsync_AudioVideoModalities_SetAndClear()
    {
        var (_, modelId) = await SeedModelAsync();
        var service = ServiceWith();

        await service.SetCapabilitiesAsync(
            modelId, null, null,
            Patch(modalities: new Dictionary<(ModalityDirection, ModalityKind), bool>
            {
                [(ModalityDirection.Input, ModalityKind.Audio)] = true,
                [(ModalityDirection.Input, ModalityKind.Video)] = true,
            }));

        var set = await _reader.ReadAsync(modelId);
        Assert.True(set.AudioInput);
        Assert.True(set.VideoInput);

        await service.SetCapabilitiesAsync(
            modelId, null, null,
            Patch(modalities: new Dictionary<(ModalityDirection, ModalityKind), bool>
            {
                [(ModalityDirection.Input, ModalityKind.Audio)] = false,
                [(ModalityDirection.Input, ModalityKind.Video)] = false,
            }));

        var cleared = await _reader.ReadAsync(modelId);
        Assert.False(cleared.AudioInput);
        Assert.False(cleared.VideoInput);
        Assert.Equal(CapabilitySource.User, cleared.SourceOf(ModalityDirection.Input, ModalityKind.Audio));
    }

    [Fact]
    public async Task SetCapabilitiesAsync_UnmentionedFactsKeepTheirSource()
    {
        var (_, modelId) = await SeedModelAsync();
        await SeedCatalogAsync(modelId,
            (CapabilityKind.Tools, true),
            (CapabilityKind.Reasoning, false));
        await SeedCatalogModalityAsync(modelId, ModalityDirection.Input, ModalityKind.Image, true);

        await ServiceWith().SetCapabilitiesAsync(
            modelId, null, null,
            Patch(modalities: new Dictionary<(ModalityDirection, ModalityKind), bool>
            {
                [(ModalityDirection.Output, ModalityKind.Audio)] = true,
            }));

        var view = await _reader.ReadAsync(modelId);
        Assert.True(view.Tools);
        Assert.False(view.Reasoning);
        Assert.True(view.Vision);
        Assert.True(view.AudioOutput);
        Assert.Equal(CapabilitySource.Catalog, view.SourceOf(CapabilityKind.Tools));
        Assert.Equal(CapabilitySource.Catalog, view.SourceOf(CapabilityKind.Reasoning));
        Assert.Equal(CapabilitySource.Catalog, view.SourceOf(ModalityDirection.Input, ModalityKind.Image));
        Assert.Equal(CapabilitySource.User, view.SourceOf(ModalityDirection.Output, ModalityKind.Audio));
    }

    [Fact]
    public async Task ResetToCatalogAsync_RemovesUserFactsAndRefillsFromCatalog()
    {
        var (_, modelId) = await SeedModelAsync();
        await _writer.ApplyUserEditAsync(modelId, Patch(
            new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = false },
            new Dictionary<(ModalityDirection, ModalityKind), bool>
            {
                [(ModalityDirection.Output, ModalityKind.Audio)] = true,
            }));

        using (var db = _db.CreateDbContext())
        {
            var model = await db.Models.SingleAsync(m => m.Id == modelId);
            model.ContextWindow = 5_000;
            model.MaxOutputTokens = 1_000;
            await db.SaveChangesAsync();
        }

        var catalog = new FixedProvider(new ModelMetadata(
            128_000, SupportsVision: true, SupportsThink: true,
            ThinkEfforts: ["low", "high"], InputModalities: ["text", "image"], OutputModalities: ["text"],
            SupportsTools: true));
        await MetadataServiceWith(catalog).ResetToCatalogAsync(modelId);

        var view = await _reader.ReadAsync(modelId);
        Assert.True(view.Tools);
        Assert.True(view.Reasoning);
        Assert.True(view.Vision);
        // User từng đặt output audio = có; catalog full-list chỉ nói output ["text"] nên
        // sau reset giá trị phải là false + Source=Catalog (user fact đã bị xóa).
        Assert.False(view.AudioOutput);
        Assert.Equal(CapabilitySource.Catalog, view.SourceOf(CapabilityKind.Tools));
        Assert.Equal(CapabilitySource.Catalog, view.SourceOf(ModalityDirection.Input, ModalityKind.Image));
        Assert.Equal(CapabilitySource.Catalog, view.SourceOf(ModalityDirection.Output, ModalityKind.Audio));

        using var db2 = _db.CreateDbContext();
        var saved = await db2.Models.SingleAsync(m => m.Id == modelId);
        Assert.Equal(5_000, saved.ContextWindow); // scalar không phải fact → giữ
        Assert.Equal(1_000, saved.MaxOutputTokens);
    }

    [Fact]
    public async Task ResetToCatalogAsync_WhenCatalogKnowsNothing_LeavesFactsEmpty()
    {
        var (_, modelId) = await SeedModelAsync();
        await _writer.ApplyUserEditAsync(modelId, Patch(
            new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = false }));

        using (var db = _db.CreateDbContext())
        {
            var model = await db.Models.SingleAsync(m => m.Id == modelId);
            model.ContextWindow = 5_000;
            await db.SaveChangesAsync();
        }

        await MetadataServiceWith(new FixedProvider(null)).ResetToCatalogAsync(modelId);

        var view = await _reader.ReadAsync(modelId);
        Assert.True(view.IsEmpty);

        using var db2 = _db.CreateDbContext();
        Assert.Equal(5_000, (await db2.Models.SingleAsync(m => m.Id == modelId)).ContextWindow);
    }

    private sealed class NoopMetadataService : IModelMetadataService
    {
        public Task TryFillAsync(long modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> FillMissingAsync(long providerId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task ResetToCatalogAsync(long modelId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedProvider(ModelMetadata? result) : IModelMetadataProvider
    {
        public bool ReturnsCompleteFacts => true;
        public CapabilitySource Source => CapabilitySource.Catalog;

        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("HTTP không được gọi trong test capability edit.");
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoopHandler(), disposeHandler: false);
    }
}
