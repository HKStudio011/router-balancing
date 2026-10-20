using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IModelMetadataService"/>
public sealed class ModelMetadataService : IModelMetadataService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ILogService _log;
    private readonly IEnumerable<IModelMetadataProvider> _chain;
    private readonly IModelCapabilityWriter _capabilities;
    private readonly IModelCapabilityReader _reader;
    private readonly TimeSpan _fillTimeout;

    // Spec §7: tổng budget cho một đợt fill hàng loạt; inject để test timeout tất định.
    private static readonly TimeSpan DefaultFillTimeout = TimeSpan.FromSeconds(120);
    private const int FillMaxDegreeOfParallelism = 2;

    /// <inheritdoc/>
    public ModelMetadataService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ILogService log,
        IEnumerable<IModelMetadataProvider> chain,
        IModelCapabilityWriter capabilities,
        IModelCapabilityReader reader,
        TimeSpan? fillTimeout = null)
    {
        _db = db;
        _log = log;
        _chain = chain;
        _capabilities = capabilities;
        _reader = reader;
        _fillTimeout = fillTimeout ?? DefaultFillTimeout;
    }

    /// <inheritdoc />
    public async Task TryFillAsync(long modelId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        // Provider! : nav nullable theo model — row luôn có Provider (FK bắt buộc),
        // null-check thực sự nằm ngay dưới sau khi query xong.
        var model = await db.Models
            .Include(m => m.Provider)
            .ThenInclude(p => p!.Accounts)
            .Include(m => m.Provider)
            .ThenInclude(p => p!.ProviderProxies) // resolver đọc junction — thiếu thì provider có proxy cũng resolve Direct
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model?.Provider is null) return;

        // Probe đi theo assignment của provider (pattern TestConnectionAsync, D4): thiếu
        // ProxyTarget → ProxyHealthHandler rơi về global pool (D7) → provider không gán
        // proxy vẫn bị gửi qua SOCKS proxy của pool (bug 2026-10-06).
        ProxyTarget.Current.Value = new ProxyTarget(model.Provider, null);
        try
        {
            foreach (var provider in _chain)
            {
                ModelMetadata? meta;
                try
                {
                    meta = await provider.FetchAsync(model.Provider, model, ct);
                }
                catch (Exception ex)
                {
                    // Contract: bước thường trả null; ném exception là lệch contract →
                    // log warning và nhường bước sau (spec §3.3: mọi lỗi → log warning).
                    _log.Warn($"Metadata provider {provider.GetType().Name} lỗi cho model {modelId}: {ex.Message}");
                    continue;
                }
                if (meta is null) continue;

                // ContextWindow vẫn là cột scalar (C-J) — chỉ ghi khi còn trống (spec §3.3);
                // capability đi qua writer để giữ luật nguồn (C-E/C-N).
                if (meta.ContextWindow is not null && model.ContextWindow is null)
                {
                    model.ContextWindow = meta.ContextWindow;
                    await db.SaveChangesAsync(ct);
                }

                // Mọi dimension khác là fact — nguồn full-list mới được sinh KnownAbsent
                // (R-T7-2: lấy từ chính provider, không lấy từ Entry của preset).
                await _capabilities.ApplyAsync(
                    model.Id,
                    CapabilitySource.Catalog,
                    MetadataPatchMapper.From(meta),
                    provider.ReturnsCompleteFacts,
                    ct);
                return; // bước đầu biết → dừng chain
            }
        }
        finally
        {
            ProxyTarget.Current.Value = null;
        }
    }

    /// <inheritdoc />
    public async Task<int> FillMissingAsync(long providerId, CancellationToken ct = default)
    {
        // Chỉ lấy id — không cần load entity; reader là nguồn duy nhất quyết định
        // "còn thiếu fact" (R-T8-2: IsEmpty, không phải thiếu từng dimension).
        using var db = _db.CreateDbContext();
        var modelIds = await db.Models.AsNoTracking()
            .Where(m => m.ProviderId == providerId)
            .Select(m => m.Id)
            .ToListAsync(ct);
        if (modelIds.Count == 0) return 0;

        var before = await _reader.ReadManyAsync(modelIds, ct);
        var candidates = modelIds
            .Where(id => before.TryGetValue(id, out var view) && view.IsEmpty)
            .ToList();
        if (candidates.Count == 0) return 0;

        using var fillCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fillCts.CancelAfter(_fillTimeout);
        try
        {
            await Parallel.ForEachAsync(candidates, new ParallelOptions
            {
                MaxDegreeOfParallelism = FillMaxDegreeOfParallelism,
                CancellationToken = fillCts.Token,
            }, async (modelId, token) =>
            {
                try
                {
                    await TryFillAsync(modelId, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Hết budget (hoặc caller huỷ) — để vòng ngoài quyết định mềm/cứng.
                    throw;
                }
                catch (Exception ex)
                {
                    // Lỗi DB/lock riêng của một model không được làm hỏng cả đợt (spec §7).
                    _log.Warn($"Fill metadata lỗi cho model {modelId}: {ex.Message}");
                }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout là dừng MỀM (R-T8-4): model đã xong vẫn được đếm; lần bấm sau
            // tiếp tục nhờ cache của provider (Ollama/NVIDIA TTL 24h).
            _log.Warn($"Fill metadata provider {providerId} dừng sau {_fillTimeout.TotalSeconds:0.##}s.");
        }

        // Caller huỷ phải propagate — chỉ timeout nội bộ mới được nuốt (R-T8-4).
        ct.ThrowIfCancellationRequested();

        // Đếm bằng đúng MỘT ReadManyAsync sau đợt (không đọc từng model).
        var after = await _reader.ReadManyAsync(candidates, ct);
        return candidates.Count(id => after.TryGetValue(id, out var view) && !view.IsEmpty);
    }
}

/// <summary>
/// Dịch <see cref="ModelMetadata"/> (shape provider trả về) sang <see cref="CapabilityPatch"/>
/// (shape writer nhận) — tách riêng để Task 8 giải phóng <c>FillMissingAsync</c> khỏi logic map.
/// Luật: bool non-null → một dòng capability; mảng modality CÓ MẶT → positive cho token nhận
/// diện được + negative toàn từ vựng cho hướng đó (writer tự suppress negative nếu nguồn
/// không phải full-list); token lạ bị bỏ, không ném; mảng vắng = không phát biểu (R-T5-7).
/// </summary>
internal static class MetadataPatchMapper
{
    public static CapabilityPatch From(ModelMetadata metadata)
    {
        var capabilities = new Dictionary<CapabilityKind, bool>();
        if (metadata.SupportsTools is { } tools) capabilities[CapabilityKind.Tools] = tools;
        if (metadata.SupportsThink is { } think) capabilities[CapabilityKind.Reasoning] = think;

        var modalities = new Dictionary<(ModalityDirection Direction, ModalityKind Modality), bool>();
        if (metadata.SupportsVision is { } vision)
            modalities[(ModalityDirection.Input, ModalityKind.Image)] = vision;
        AddModalities(modalities, ModalityDirection.Input, metadata.InputModalities);
        AddModalities(modalities, ModalityDirection.Output, metadata.OutputModalities);

        var efforts = metadata.ThinkEfforts is null
            ? new HashSet<string>()
            : new HashSet<string>(metadata.ThinkEfforts, StringComparer.Ordinal);

        return new CapabilityPatch(capabilities, modalities, efforts);
    }

    private static void AddModalities(
        Dictionary<(ModalityDirection Direction, ModalityKind Modality), bool> modalities,
        ModalityDirection direction,
        IReadOnlyList<string>? tokens)
    {
        if (tokens is null) return;
        // Mảng có mặt = nguồn liệt kê đầy đủ cho hướng này → mọi loại không được nhắc
        // là "đã biết là không" (writer quyết định có ghi hay không theo full-list của nguồn).
        foreach (var kind in Enum.GetValues<ModalityKind>())
            modalities[(direction, kind)] = false;
        foreach (var token in tokens)
        {
            // Provider giữ case gốc ("Text"/"Image"), còn từ vựng DB so khớp tuyệt đối
            // lowercase — normalize để token hợp lệ khác case vẫn bật positive thay vì
            // bị vòng negative phía trên ghi đè (regression mixed-case).
            var normalized = token.Trim().ToLowerInvariant();
            if (CapabilityNames.TryFromDb(normalized, out ModalityKind kind))
                modalities[(direction, kind)] = true;
        }
    }
}
