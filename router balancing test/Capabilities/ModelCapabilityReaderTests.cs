using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Capabilities;

/// <summary>
/// ModelCapabilityReader — phía đọc của mô hình capability quan hệ (spec capability-model §7):
/// quy tắc biến đổi fact → view (KnownAbsent=0 → true, KnownAbsent=1 → false, không có dòng →
/// key không có trong dictionary, tra ra null [RF-1]); thứ tự ổn định của
/// InputModalities/OutputModalities (theo ModalityKind tăng dần) và ReasoningEfforts (ordinal);
/// ReadManyAsync đúng MỘT query cho mỗi bảng (không N+1); model không có fact → Empty.
/// </summary>
public class ModelCapabilityReaderTests : IDisposable
{
    private static readonly DateTimeOffset OldStamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ModelCapabilityReader _reader;

    public ModelCapabilityReaderTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _reader = new ModelCapabilityReader(_db);
    }

    public void Dispose() => _testDb.Dispose();

    // ---------- model không có fact → Empty ----------

    [Fact]
    public async Task ReadManyAsync_ReturnsEmptyForModelsWithoutFacts()
    {
        var idA = await AddModelAsync("reader-empty-a");
        var idB = await AddModelAsync("reader-empty-b");
        // model có fact để chắc chắn "Empty" chỉ áp dụng cho model không có dòng
        var idC = await AddModelAsync("reader-empty-with-facts");
        await SeedCapabilityAsync(idC, CapabilityKind.Tools, knownAbsent: false, CapabilitySource.Catalog);

        var views = await _reader.ReadManyAsync([idA, idB]);

        // Key vẫn có trong kết quả cho mọi model được yêu cầu — consumer không cần check presence.
        Assert.Equal(2, views.Count);
        var viewA = views[idA];
        Assert.True(viewA.IsEmpty);
        Assert.Empty(viewA.Capabilities);
        Assert.Empty(viewA.Modalities);
        Assert.Empty(viewA.ReasoningEfforts);
        Assert.Null(viewA.Tools);
        Assert.Null(viewA.Reasoning);
        Assert.Null(viewA.Vision);
        Assert.Empty(viewA.InputModalities);
        Assert.Empty(viewA.OutputModalities);
        Assert.Empty(viewA.Efforts);
        Assert.Equal(ModelCapabilityView.Empty, views[idB]);

        var single = await _reader.ReadAsync(idA);
        Assert.Equal(ModelCapabilityView.Empty, single);

        var withFacts = await _reader.ReadAsync(idC);
        Assert.False(withFacts.IsEmpty);
    }

    // ---------- quy tắc biến đổi dòng fact → giá trị view ----------

    [Fact]
    public async Task KnownAbsentRow_MapsToFalse()
    {
        var id = await AddModelAsync("reader-known-absent");
        await SeedCapabilityAsync(id, CapabilityKind.Tools, knownAbsent: true, CapabilitySource.Catalog);
        await SeedCapabilityAsync(id, CapabilityKind.Reasoning, knownAbsent: true, CapabilitySource.User);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Image, knownAbsent: true, CapabilitySource.Catalog);
        await SeedModalityAsync(id, ModalityDirection.Output, ModalityKind.Audio, knownAbsent: true, CapabilitySource.Catalog);

        var view = await _reader.ReadAsync(id);

        // KnownAbsent=1 → false, PHẢI phân biệt được với null (chưa biết) — v3,
        // Assert.False(bool?) fail khi giá trị null.
        Assert.False(view.Tools);
        Assert.False(view.Reasoning);
        Assert.False(view.Vision);
        Assert.False(view.AudioOutput);
        Assert.False(view.Supports(ModalityDirection.Input, ModalityKind.Image));
        Assert.False(view.Capabilities[CapabilityKind.Tools]);
        Assert.False(view.Modalities[(ModalityDirection.Output, ModalityKind.Audio)]);
        // negative không được lọt vào danh sách modality thuận (generator chỉ phát biểu true).
        Assert.Empty(view.InputModalities);
        Assert.Empty(view.OutputModalities);
    }

    [Fact]
    public async Task PresentRow_MapsToTrue()
    {
        var id = await AddModelAsync("reader-present");
        await SeedCapabilityAsync(id, CapabilityKind.Tools, knownAbsent: false, CapabilitySource.Catalog);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Image, knownAbsent: false, CapabilitySource.Endpoint);
        await SeedModalityAsync(id, ModalityDirection.Output, ModalityKind.Text, knownAbsent: false, CapabilitySource.Catalog);
        await SeedEffortAsync(id, "high", CapabilitySource.Catalog);

        var view = await _reader.ReadAsync(id);

        Assert.True(view.Tools);
        Assert.True(view.Capabilities[CapabilityKind.Tools]);
        Assert.True(view.Vision);
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Image));
        Assert.True(view.Supports(ModalityDirection.Output, ModalityKind.Text));
        // key không có dòng → vẫn null, kể cả khi model có fact ở mục khác
        Assert.Null(view.Reasoning);
        Assert.True(view.ReasoningEfforts.SetEquals(new[] { "high" }));
    }

    [Fact]
    public async Task ReadAsync_ExposesSourceForCapabilitiesAndModalities()
    {
        var id = await AddModelAsync("reader-sources");
        await SeedCapabilityAsync(id, CapabilityKind.Tools, knownAbsent: false, CapabilitySource.Catalog);
        await SeedCapabilityAsync(id, CapabilityKind.Reasoning, knownAbsent: true, CapabilitySource.User);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Image, knownAbsent: false, CapabilitySource.Endpoint);
        await SeedModalityAsync(id, ModalityDirection.Output, ModalityKind.Audio, knownAbsent: false, CapabilitySource.User);
        await SeedEffortAsync(id, "high", CapabilitySource.Catalog);

        var view = await _reader.ReadAsync(id);

        Assert.Equal(CapabilitySource.Catalog, view.SourceOf(CapabilityKind.Tools));
        Assert.Equal(CapabilitySource.User, view.SourceOf(CapabilityKind.Reasoning));
        Assert.Equal(CapabilitySource.Endpoint, view.SourceOf(ModalityDirection.Input, ModalityKind.Image));
        Assert.Equal(CapabilitySource.User, view.SourceOf(ModalityDirection.Output, ModalityKind.Audio));
        // Không có dòng → không bịa nguồn (kể cả khi model có fact ở mục khác).
        Assert.Null(view.SourceOf(ModalityDirection.Output, ModalityKind.Video));
        Assert.Null(view.SourceOf(ModalityDirection.Input, ModalityKind.File));
    }

    [Fact]
    public async Task ReadAsync_UnknownFactHasNullSource()
    {
        var id = await AddModelAsync("reader-no-source");

        var view = await _reader.ReadAsync(id);

        Assert.Null(view.SourceOf(CapabilityKind.Tools));
        Assert.Null(view.SourceOf(CapabilityKind.Reasoning));
        Assert.Null(view.SourceOf(ModalityDirection.Input, ModalityKind.Image));
        Assert.Empty(view.CapabilitySources);
        Assert.Empty(view.ModalitySources);
    }

    /// <summary>
    /// [RF-1] Không có dòng fact = chưa biết — view không được biến "không biết" thành
    /// "biết là không" (C-I). R-T2-2: ReasoningEfforts rỗng = chưa biết, không phải
    /// "biết là không có mức nào" — negative của reasoning nằm ở Capabilities[Reasoning]=false.
    /// </summary>
    [Fact]
    public async Task AbsentModality_IsNullNotFalse()
    {
        var id = await AddModelAsync("reader-absent-modality");
        await SeedCapabilityAsync(id, CapabilityKind.Tools, knownAbsent: false, CapabilitySource.Catalog);

        var view = await _reader.ReadAsync(id);

        Assert.Null(view.Vision);
        Assert.Null(view.AudioInput);
        Assert.Null(view.VideoInput);
        Assert.Null(view.AudioOutput);
        Assert.Null(view.Reasoning);
        Assert.Null(view.Supports(ModalityDirection.Output, ModalityKind.Text));
        // "không có dòng" = key KHÔNG có trong dictionary (tra ra null), không phải key → false.
        Assert.False(view.Modalities.ContainsKey((ModalityDirection.Input, ModalityKind.Image)));
        Assert.False(view.Capabilities.ContainsKey(CapabilityKind.Reasoning));
        Assert.Empty(view.ReasoningEfforts);
        Assert.Empty(view.Efforts);
    }

    // ---------- thứ tự ổn định của danh sách modality/effort ----------

    [Fact]
    public async Task InputModalities_ListsOnlyTrueValuesInStableOrder()
    {
        var id = await AddModelAsync("reader-modality-order");
        // Chèn theo thứ tự Lộn xộn để chứng minh kết quả không phụ thuộc thứ tự dòng trong DB.
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Video, knownAbsent: false, CapabilitySource.Catalog);
        await SeedModalityAsync(id, ModalityDirection.Output, ModalityKind.Audio, knownAbsent: false, CapabilitySource.Catalog);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Audio, knownAbsent: true, CapabilitySource.Catalog);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Text, knownAbsent: false, CapabilitySource.Catalog);
        await SeedModalityAsync(id, ModalityDirection.Output, ModalityKind.Text, knownAbsent: false, CapabilitySource.Catalog);
        await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Image, knownAbsent: false, CapabilitySource.Catalog);

        var view = await _reader.ReadAsync(id);

        // Chỉ giá trị true, theo ModalityKind tăng dần: text(0) &lt; image(1) &lt; video(3);
        // audio bị loại vì KnownAbsent=1.
        Assert.Equal(new[] { "text", "image", "video" }, view.InputModalities);
        Assert.Equal(new[] { "text", "audio" }, view.OutputModalities);
        // Gọi lại vẫn cùng thứ tự (stable) — generator config so output byte-for-byte.
        Assert.Equal(view.InputModalities, view.InputModalities);
    }

    /// <summary>
    /// Efforts sort theo ordinal (brief: "đã sort theo ordinal") — output generator/UI
    /// phải ổn định bất kể thứ tự dòng trong DB. R-T3-1: cả bản thân set
    /// <see cref="ModelCapabilityView.ReasoningEfforts"/> cũng phải iterate ordinal
    /// (reader dựng bằng SortedSet + StringComparer.Ordinal).
    /// </summary>
    [Fact]
    public async Task Efforts_OrdersOrdinalStably()
    {
        var id = await AddModelAsync("reader-efforts-order");
        foreach (var level in new[] { "xhigh", "minimal", "high", "medium", "low" })
            await SeedEffortAsync(id, level, CapabilitySource.Catalog);

        var view = await _reader.ReadAsync(id);

        Assert.Equal(new[] { "high", "low", "medium", "minimal", "xhigh" }, view.Efforts);
        // Set phải tự enumerate theo ordinal — consumer không cần gọi Efforts mới có thứ tự.
        Assert.Equal(new[] { "high", "low", "medium", "minimal", "xhigh" }, view.ReasoningEfforts);
    }

    // ---------- ReadManyAsync: một query cho mỗi bảng (không N+1) ----------

    /// <summary>
    /// Đếm command qua DbCommandInterceptor trên cùng file DB: ba bảng = ba SELECT bất kể
    /// số model. Kèm assert hành vi 20 model (phương án fallback brief nêu) — kết quả đúng
    /// cho cả 20, model có fact và model không có fact.
    /// </summary>
    [Fact]
    public async Task ReadManyAsync_UsesOneQueryPerTable()
    {
        var ids = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            var id = await AddModelAsync($"reader-batch-{i}");
            ids.Add(id);
            if (i % 2 == 0)
            {
                await SeedCapabilityAsync(id, CapabilityKind.Tools, knownAbsent: i % 4 == 0, CapabilitySource.Catalog);
                await SeedModalityAsync(id, ModalityDirection.Input, ModalityKind.Image, knownAbsent: false, CapabilitySource.Catalog);
                await SeedEffortAsync(id, "high", CapabilitySource.Catalog);
            }
        }

        var counter = new CountingDbCommandInterceptor();
        var reader = new ModelCapabilityReader(CreateInstrumentedFactory(counter));

        var views = await reader.ReadManyAsync(ids);

        // Một query cho mỗi bảng fact — không có query theo từng model (N+1).
        Assert.Equal(3, counter.ReaderQueryCount);
        Assert.Equal(20, views.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var view = views[ids[i]];
            if (i % 2 == 0)
            {
                Assert.Equal(i % 4 != 0, view.Tools);
                Assert.True(view.Vision);
                Assert.True(view.ReasoningEfforts.SetEquals(new[] { "high" }));
            }
            else
            {
                Assert.True(view.IsEmpty, $"model lẻ {i} phải trả về Empty.");
            }
        }
    }

    // ---------- helpers ----------

    private async Task<long> AddModelAsync(string publicModelId)
    {
        using var db = _db.CreateDbContext();
        var providerId = db.Providers.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).First();
        var model = new Model { ProviderId = providerId, ModelId = publicModelId, CreatedAt = OldStamp };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    // Seed trực tiếp entity fact (không qua writer) — reader không được phụ thuộc writer,
    // và các ca "dòng lạ" cần ghi thẳng không qua chuẩn hóa của writer.
    private async Task SeedCapabilityAsync(long modelId, CapabilityKind kind, bool knownAbsent, CapabilitySource source)
    {
        using var db = _db.CreateDbContext();
        db.ModelCapabilities.Add(new ModelCapability
        {
            ModelId = modelId,
            Capability = kind,
            KnownAbsent = knownAbsent,
            Source = source,
            UpdatedAt = OldStamp,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedModalityAsync(long modelId, ModalityDirection direction, ModalityKind modality,
        bool knownAbsent, CapabilitySource source)
    {
        using var db = _db.CreateDbContext();
        db.ModelModalities.Add(new ModelModality
        {
            ModelId = modelId,
            Direction = direction,
            Modality = modality,
            KnownAbsent = knownAbsent,
            Source = source,
            UpdatedAt = OldStamp,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedEffortAsync(long modelId, string level, CapabilitySource source)
    {
        using var db = _db.CreateDbContext();
        db.ModelReasoningEfforts.Add(new ModelReasoningEffort
        {
            ModelId = modelId,
            Level = level,
            Source = source,
            UpdatedAt = OldStamp,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Cùng file DB với <see cref="_testDb"/> nhưng options riêng để gắn interceptor đếm command.</summary>
    private IDbContextFactory<RouterBalancingDbContext> CreateInstrumentedFactory(DbCommandInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<RouterBalancingDbContext>()
            .UseSqlite($"Data Source={_testDb.DbPath};Pooling=False")
            .AddInterceptors(interceptor)
            .Options;
        return new SingleOptionsFactory(options);
    }

    private sealed class SingleOptionsFactory(DbContextOptions<RouterBalancingDbContext> options)
        : IDbContextFactory<RouterBalancingDbContext>
    {
        public RouterBalancingDbContext CreateDbContext() => new(options);
    }

    /// <summary>Đếm SELECT mà EF thật sự gửi đi — bằng chứng trực tiếp "một query cho mỗi bảng".</summary>
    private sealed class CountingDbCommandInterceptor : DbCommandInterceptor
    {
        private int _readerQueryCount;

        public int ReaderQueryCount => Volatile.Read(ref _readerQueryCount);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _readerQueryCount);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readerQueryCount);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
