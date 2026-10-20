using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

/// <summary>
/// Task 8 (U5): điền metadata hàng loạt chỉ cho model CÒN THIẾU fact (R-T8-2), tối đa
/// 2 request song song và có budget thời gian mềm (R-T8-4). Fake provider + DB thật
/// (<see cref="TestDb"/>) để kiểm cả hành vi ghi fact, không chỉ đếm call.
/// </summary>
public class ModelMetadataFillMissingTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public ModelMetadataFillMissingTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private ModelMetadataService ServiceWith(IModelMetadataProvider provider, TimeSpan? fillTimeout = null) =>
        new(_db, new NullLog(), [provider], new ModelCapabilityWriter(_db, new NullLog()),
            new ModelCapabilityReader(_db), fillTimeout);

    private async Task<(long ProviderId, IReadOnlyList<long> ModelIds)> SeedProviderAsync(params string[] modelIds)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
        foreach (var modelId in modelIds)
        {
            provider.Models.Add(new Model { ModelId = modelId });
        }
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return (provider.Id, provider.Models.Select(m => m.Id).ToList());
    }

    /// <summary>Ghi sẵn một fact để model không còn là candidate (IsEmpty = false).</summary>
    private async Task SeedFactAsync(long modelId)
    {
        var writer = new ModelCapabilityWriter(_db, new NullLog());
        await writer.ApplyAsync(modelId, CapabilitySource.Catalog, new CapabilityPatch(
            new Dictionary<CapabilityKind, bool> { [CapabilityKind.Tools] = true },
            new Dictionary<(ModalityDirection, ModalityKind), bool>(),
            new HashSet<string>()), sourceIsCompleteList: false);
    }

    private static ModelMetadata Facts(bool tools = true) =>
        new(null, null, null, null, null, null, SupportsTools: tools);

    /// <summary>Provider giả ghi lại model id đã probe và uỷ quyền hành vi cho từng test.</summary>
    private sealed class RecordingProvider(Func<Provider, Model, CancellationToken, Task<ModelMetadata?>> fetch)
        : IModelMetadataProvider
    {
        private readonly object _gate = new();
        private readonly List<long> _modelIds = [];

        public IReadOnlyList<long> ModelIds
        {
            get
            {
                lock (_gate) return _modelIds.ToList();
            }
        }

        public bool ReturnsCompleteFacts => false;

        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
        {
            lock (_gate) _modelIds.Add(model.Id);
            return fetch(provider, model, ct);
        }
    }

    [Fact]
    public async Task FillMissingAsync_WhenProviderUnknown_ReturnsZero()
    {
        var provider = new RecordingProvider((_, _, _) => Task.FromResult<ModelMetadata?>(Facts()));
        var service = ServiceWith(provider);

        // Không ném KeyNotFound: fill hàng loạt là best-effort, provider vừa bị xoá
        // giữa lúc UI bấm cũng chỉ là không có model nào để điền.
        var filled = await service.FillMissingAsync(999_999);

        Assert.Equal(0, filled);
        Assert.Empty(provider.ModelIds);
    }

    [Fact]
    public async Task FillMissingAsync_SkipsModelsThatAlreadyHaveFacts()
    {
        var (providerId, modelIds) = await SeedProviderAsync("with-facts");
        await SeedFactAsync(modelIds[0]);
        var provider = new RecordingProvider((_, _, _) => Task.FromResult<ModelMetadata?>(Facts()));
        var service = ServiceWith(provider);

        var filled = await service.FillMissingAsync(providerId);

        // R-T8-2: model đã có bất kỳ fact nào không bị probe lại — nguồn NVIDIA không
        // bao giờ nói tools/reasoning nên định nghĩa theo dimension sẽ probe vô hạn.
        Assert.Equal(0, filled);
        Assert.Empty(provider.ModelIds);
    }

    [Fact]
    public async Task FillMissingAsync_ProbesOnlyMissingOnes()
    {
        var (providerId, modelIds) = await SeedProviderAsync("full", "empty-a", "empty-b");
        await SeedFactAsync(modelIds[0]);
        var provider = new RecordingProvider((_, _, _) => Task.FromResult<ModelMetadata?>(Facts()));
        var service = ServiceWith(provider);

        var filled = await service.FillMissingAsync(providerId);

        Assert.Equal(2, filled);
        Assert.Equal(2, provider.ModelIds.Count);
        Assert.DoesNotContain(modelIds[0], provider.ModelIds);
        Assert.Contains(modelIds[1], provider.ModelIds);
        Assert.Contains(modelIds[2], provider.ModelIds);
    }

    [Fact]
    public async Task FillMissingAsync_OneModelFails_ContinuesOthers()
    {
        var (providerId, modelIds) = await SeedProviderAsync("boom", "ok-1", "ok-2");
        var provider = new RecordingProvider((_, model, _) => model.ModelId == "boom"
            ? throw new HttpRequestException("boom")
            : Task.FromResult<ModelMetadata?>(Facts()));
        var service = ServiceWith(provider);

        var filled = await service.FillMissingAsync(providerId);

        // Lỗi từng model chỉ log + đi tiếp (spec §7) — một model chết không làm hỏng đợt.
        Assert.Equal(2, filled);
        Assert.True((await new ModelCapabilityReader(_db).ReadAsync(modelIds[0])).IsEmpty);
        Assert.True((await new ModelCapabilityReader(_db).ReadAsync(modelIds[1])).Tools);
        Assert.True((await new ModelCapabilityReader(_db).ReadAsync(modelIds[2])).Tools);
    }

    [Fact]
    public async Task FillMissingAsync_MaxTwoConcurrent()
    {
        var (providerId, _) = await SeedProviderAsync("a", "b", "c", "d", "e", "f");
        var gate = new object();
        var active = 0;
        var maxObserved = 0;
        var provider = new RecordingProvider(async (_, _, ct) =>
        {
            lock (gate)
            {
                active++;
                maxObserved = Math.Max(maxObserved, active);
            }
            try
            {
                // Giữ request đủ lâu để 2 worker chồng lấn; nếu service chạy tuần tự
                // maxObserved sẽ là 1 và test đỏ.
                await Task.Delay(100, ct);
                return Facts();
            }
            finally
            {
                lock (gate) active--;
            }
        });
        var service = ServiceWith(provider);

        var filled = await service.FillMissingAsync(providerId);

        Assert.Equal(6, filled);
        Assert.Equal(2, maxObserved);
    }

    [Fact]
    public async Task FillMissingAsync_Timeout_StopsAndReturnsFilledCount()
    {
        var (providerId, _) = await SeedProviderAsync("fast", "slow");
        var provider = new RecordingProvider(async (_, model, ct) =>
        {
            if (model.ModelId == "slow")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return Facts();
        });
        var service = ServiceWith(provider, fillTimeout: TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        var filled = await service.FillMissingAsync(providerId);
        stopwatch.Stop();

        // Timeout mềm: model nhanh đã xong vẫn được đếm, đợt dừng thay vì ném.
        Assert.Equal(1, filled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Fill phải dừng gần timeout 300ms, thực tế {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task FillMissingAsync_WhenCallerCancels_Throws()
    {
        var (providerId, _) = await SeedProviderAsync("a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new RecordingProvider((_, _, _) => Task.FromResult<ModelMetadata?>(Facts()));
        var service = ServiceWith(provider);

        // Caller huỷ là huỷ thật — không được nuốt như timeout nội bộ (R-T8-4).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FillMissingAsync(providerId, cts.Token));
    }

    [Fact]
    public async Task FillMissingAsync_CountsOnlyViewsThatBecameNonEmpty()
    {
        var (providerId, _) = await SeedProviderAsync("filled", "still-empty");
        var provider = new RecordingProvider((_, model, _) => Task.FromResult<ModelMetadata?>(
            model.ModelId == "filled"
                ? Facts()
                : new ModelMetadata(null, null, null, null, null, null, SupportsTools: null)));
        var service = ServiceWith(provider);

        var filled = await service.FillMissingAsync(providerId);

        // Provider trả metadata rỗng không tạo fact row → view vẫn IsEmpty, không đếm.
        Assert.Equal(1, filled);
    }
}
