using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Capabilities;

/// <summary>
/// Lối đọc DUY NHẤT ba bảng fact capability (ModelCapabilities / ModelModalities /
/// ModelReasoningEfforts). Mọi consumer (generator config, combo calculator, badge UI,
/// /v1/models của proxy) phải đọc qua interface này — không tự query entity fact,
/// không đọc lại các cột JSON cũ trên Models (C-H).
/// </summary>
public interface IModelCapabilityReader
{
    /// <summary>Đọc view của một model; model không có dòng fact nào → <see cref="ModelCapabilityView.Empty"/>.</summary>
    /// <param name="modelId">PK <c>Models.Id</c>.</param>
    Task<ModelCapabilityView> ReadAsync(long modelId, CancellationToken ct = default);

    /// <summary>
    /// Đọc view của nhiều model với đúng MỘT query cho mỗi bảng (không N+1).
    /// Mọi model được yêu cầu đều có mặt trong kết quả; model không có fact → Empty.
    /// </summary>
    /// <param name="modelIds">Các PK <c>Models.Id</c> cần đọc (trùng lặp được gộp).</param>
    Task<IReadOnlyDictionary<long, ModelCapabilityView>> ReadManyAsync(
        IReadOnlyList<long> modelIds, CancellationToken ct = default);
}

/// <inheritdoc cref="IModelCapabilityReader"/>
public sealed class ModelCapabilityReader : IModelCapabilityReader
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public ModelCapabilityReader(IDbContextFactory<RouterBalancingDbContext> db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<ModelCapabilityView> ReadAsync(long modelId, CancellationToken ct = default)
    {
        var views = await ReadManyAsync([modelId], ct);
        return views.TryGetValue(modelId, out var view) ? view : ModelCapabilityView.Empty;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, ModelCapabilityView>> ReadManyAsync(
        IReadOnlyList<long> modelIds, CancellationToken ct = default)
    {
        var views = new Dictionary<long, ModelCapabilityView>();
        if (modelIds.Count == 0) return views;

        using var db = _db.CreateDbContext();
        // Một query cho mỗi bảng, bất kể số model (ruling Task 3 — không N+1):
        // Contains dịch thành IN tham số hóa, không vòng query theo model.
        // AsNoTracking: reader không mutate fact — bao gồm cả việc không cache
        // entity làm chậm các lần đọc liên tiếp trong một phiên UI.
        var capabilityRows = await db.ModelCapabilities.AsNoTracking()
            .Where(f => modelIds.Contains(f.ModelId))
            .ToListAsync(ct);
        var modalityRows = await db.ModelModalities.AsNoTracking()
            .Where(f => modelIds.Contains(f.ModelId))
            .ToListAsync(ct);
        var effortRows = await db.ModelReasoningEfforts.AsNoTracking()
            .Where(f => modelIds.Contains(f.ModelId))
            .ToListAsync(ct);

        var capabilitiesByModel = capabilityRows.GroupBy(f => f.ModelId).ToDictionary(g => g.Key, g => g.ToList());
        var modalitiesByModel = modalityRows.GroupBy(f => f.ModelId).ToDictionary(g => g.Key, g => g.ToList());
        var effortsByModel = effortRows.GroupBy(f => f.ModelId).ToDictionary(g => g.Key, g => g.ToList());

        // Key có mặt trong dictionary = đã biết; value = NOT KnownAbsent (C-I/§7):
        // KnownAbsent=0 → true, KnownAbsent=1 → false, không có dòng → key không có.
        // Model không có bất kỳ dòng nào phải nhận đúng thể chia sẻ Empty (test so
        // Equal với ModelCapabilityView.Empty).
        foreach (var modelId in modelIds.Distinct())
        {
            var caps = capabilitiesByModel.TryGetValue(modelId, out var foundCaps) ? foundCaps : [];
            var mods = modalitiesByModel.TryGetValue(modelId, out var foundMods) ? foundMods : [];
            var efforts = effortsByModel.TryGetValue(modelId, out var foundEfforts) ? foundEfforts : [];
            if (caps.Count == 0 && mods.Count == 0 && efforts.Count == 0)
            {
                views[modelId] = ModelCapabilityView.Empty;
                continue;
            }

            var capabilityMap = new Dictionary<CapabilityKind, bool>();
            foreach (var row in caps)
                capabilityMap[row.Capability] = !row.KnownAbsent;
            var modalityMap = new Dictionary<(ModalityDirection, ModalityKind), bool>();
            foreach (var row in mods)
                modalityMap[(row.Direction, row.Modality)] = !row.KnownAbsent;
            var effortSet = new HashSet<string>(efforts.Select(r => r.Level));

            views[modelId] = new ModelCapabilityView(capabilityMap, modalityMap, effortSet);
        }

        return views;
    }
}
