using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Combos;

/// <inheritdoc/>
public sealed class ComboCapabilityCalculator : IComboCapabilityCalculator
{
    /// <summary>Combo không có member khả dụng → mọi field "chưa rõ", không phải false.</summary>
    private static readonly ComboCapabilities AllUnknown = new(null, null, null, null, null);

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly IModelCapabilityReader _capabilities;

    /// <inheritdoc/>
    public ComboCapabilityCalculator(IDbContextFactory<RouterBalancingDbContext> db, IModelCapabilityReader capabilities)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(capabilities);
        _db = db;
        _capabilities = capabilities;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<long, ComboCapabilities>> ComputeAllAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var combos = await db.Combos.Include(c => c.Items).ToListAsync(ct);
        var models = await db.Models.ToDictionaryAsync(m => m.Id, ct);

        // Capability model đọc qua reader (C-H) — một lần cho mọi model, một query/bảng;
        // ContextWindow KHÔNG phải capability nên vẫn lấy từ entity Models.
        var views = await _capabilities.ReadManyAsync(models.Keys.ToList(), ct);

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
                        var view = views.TryGetValue(modelId, out var found) ? found : ModelCapabilityView.Empty;
                        members.Add(FromView(view) with { ContextWindow = model.ContextWindow });
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

    private static ComboCapabilities FromView(ModelCapabilityView view) =>
        // Enabled=false vẫn tính: capabilities là thông tin mô tả, không phải filter selection.
        // Audio/Video của combo ánh xạ theo chiều INPUT (đúng nghĩa cột SupportsAudio/SupportsVideo cũ).
        // List rỗng = view không có khẳng định thuận nào → null (chưa biết), không phải "biết là không có".
        new(null, view.Vision, view.AudioInput, view.VideoInput, view.Reasoning)
        {
            Tools = view.Tools,
            InputModalities = view.InputModalities.Count > 0 ? view.InputModalities : null,
            OutputModalities = view.OutputModalities.Count > 0 ? view.OutputModalities : null,
        };

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
                And(acc.Thinking, m.Thinking))
            {
                Tools = And(acc.Tools, m.Tools),
                InputModalities = Intersect(acc.InputModalities, m.InputModalities),
                OutputModalities = Intersect(acc.OutputModalities, m.OutputModalities),
            };
        }

        return acc;
    }

    /// <summary>Giao hai danh sách khẳng định thuận; một bên chưa rõ (null) → giao cũng chưa rõ.</summary>
    private static IReadOnlyList<string>? Intersect(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null || b is null ? null : a.Intersect(b, StringComparer.Ordinal).ToArray();

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
