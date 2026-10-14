using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Combos;

/// <inheritdoc/>
public sealed class ComboCapabilityCalculator : IComboCapabilityCalculator
{
    /// <summary>Combo không có member khả dụng → mọi field "chưa rõ", không phải false.</summary>
    private static readonly ComboCapabilities AllUnknown = new(null, null, null, null, null);

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    /// <inheritdoc/>
    public ComboCapabilityCalculator(IDbContextFactory<RouterBalancingDbContext> db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<long, ComboCapabilities>> ComputeAllAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var combos = await db.Combos.Include(c => c.Items).ToListAsync(ct);
        var models = await db.Models.ToDictionaryAsync(m => m.Id, ct);

        var comboById = combos.ToDictionary(c => c.Id);
        var memo = new Dictionary<long, ComboCapabilities>();

        ComboCapabilities Compute(long comboId, HashSet<long> visiting)
        {
            if (memo.TryGetValue(comboId, out var cached))
            {
                return cached;
            }

            // Cycle guard: DB có thể chứa cycle (data thô lọt qua validation của UI) —
            // skip edge quay lại thay vì throw/treo; /v1/models vẫn phải trả được.
            visiting.Add(comboId);
            var members = new List<ComboCapabilities>();
            foreach (var item in comboById[comboId].Items)
            {
                if (item.TargetModelId is { } modelId)
                {
                    if (models.TryGetValue(modelId, out var model))
                    {
                        members.Add(FromModel(model));
                    }
                }
                else if (item.TargetComboId is { } childId && !visiting.Contains(childId))
                {
                    members.Add(Compute(childId, visiting));
                }
            }

            visiting.Remove(comboId);
            var result = Combine(members);
            memo[comboId] = result;
            return result;
        }

        var all = new Dictionary<long, ComboCapabilities>(combos.Count);
        foreach (var combo in combos)
        {
            all[combo.Id] = Compute(combo.Id, []);
        }

        return all;
    }

    private static ComboCapabilities FromModel(Model model) =>
        // Enabled=false vẫn tính: capabilities là thông tin mô tả, không phải filter selection
        new(model.ContextWindow, model.SupportsVision, model.SupportsAudio, model.SupportsVideo, model.SupportsThink);

    private static ComboCapabilities Combine(List<ComboCapabilities> members)
    {
        if (members.Count == 0)
        {
            return AllUnknown;
        }

        var acc = members[0];
        for (var i = 1; i < members.Count; i++)
        {
            var m = members[i];
            acc = new ComboCapabilities(
                AndContext(acc.ContextWindow, m.ContextWindow),
                And(acc.Vision, m.Vision),
                And(acc.Audio, m.Audio),
                And(acc.Video, m.Video),
                And(acc.Thinking, m.Thinking));
        }

        return acc;
    }

    /// <summary>
    /// AND 3 mức: false "thắng" null vì false = đã rõ là KHÔNG hỗ trợ, null = chưa rõ —
    /// bất kỳ member nào false là đủ kết luận combo không hỗ trợ (spec D2).
    /// </summary>
    private static bool? And(bool? a, bool? b) =>
        a is false || b is false
            ? false
            : a is null || b is null
                ? null
                : true;

    /// <summary>Min các giá trị khác null; bất kỳ member nào ctx chưa rõ → cả combo chưa rõ (spec D2).</summary>
    private static int? AndContext(int? a, int? b) =>
        a is null || b is null ? null : Math.Min(a.Value, b.Value);
}
