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

    /// <inheritdoc/>
    public ModelMetadataService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ILogService log,
        IEnumerable<IModelMetadataProvider> chain,
        IModelCapabilityWriter capabilities)
    {
        _db = db;
        _log = log;
        _chain = chain;
        _capabilities = capabilities;
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
            if (CapabilityNames.TryFromDb(token, out ModalityKind kind))
                modalities[(direction, kind)] = true;
        }
    }
}
