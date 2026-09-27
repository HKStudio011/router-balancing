using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IModelMetadataService"/>
public sealed class ModelMetadataService : IModelMetadataService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ILogService _log;
    private readonly IEnumerable<IModelMetadataProvider> _chain;

    /// <inheritdoc/>
    public ModelMetadataService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ILogService log,
        IEnumerable<IModelMetadataProvider> chain)
    {
        _db = db;
        _log = log;
        _chain = chain;
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
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model?.Provider is null) return;

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

            // Chỉ ghi field còn trống — không ghi đè giá trị đã có (spec §3.3);
            // bool không nullable trên entity coi false = "chưa rõ"
            // (fill chỉ chạy lúc model được tạo — false tại thời điểm đó luôn là "chưa rõ").
            var changed = false;
            if (meta.ContextWindow is not null && model.ContextWindow is null)
            {
                model.ContextWindow = meta.ContextWindow;
                changed = true;
            }
            if (meta.SupportsVision == true && !model.SupportsVision)
            {
                model.SupportsVision = true;
                changed = true;
            }
            if (meta.SupportsThink == true && !model.SupportsThink)
            {
                model.SupportsThink = true;
                changed = true;
            }
            if (meta.ThinkEfforts is not null && model.ThinkEfforts is null)
            {
                model.ThinkEfforts = meta.ThinkEfforts;
                changed = true;
            }
            if (meta.InputModalities is not null && model.InputModalities is null)
            {
                model.InputModalities = meta.InputModalities;
                changed = true;
            }
            if (meta.OutputModalities is not null && model.OutputModalities is null)
            {
                model.OutputModalities = meta.OutputModalities;
                changed = true;
            }

            if (changed)
            {
                await db.SaveChangesAsync(ct);
            }
            return; // bước đầu biết → dừng chain
        }
    }
}
