using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Chọn candidate từ <see cref="SelectionSuccess"/>:
/// Flat (Root=null, model-id): RR lọc còn slot → sort (in-flight, ProviderId) → cursor toàn cục;
/// Fallback chỉ xét vị trí đầu — đầy thì park (failover-on-error là 3C).
/// Tree (Root!=null, combo composition): đi đệ quy theo mode CỦA TỪNG CẤP —
/// FB=đơn vị đầu-or-park, RR=đơn vị còn lá eligible (min-inFlight, cursor).
/// </summary>
public sealed class ModelSelector(IExecutionList executions) : IModelSelector
{
    private long _cursor;

    /// <inheritdoc/>
    public async Task<ModelCandidate?> TrySelectAsync(SelectionSuccess selection, CancellationToken ct)
    {
        // Tree path (composition): đi đệ quy theo mode CỦA TỪNG CẤP — FB=đơn vị đầu-or-park,
        // RR=lọc đơn vị còn lá eligible, sort min-inFlight, cursor. Flat path giữ nguyên y hệt.
        if (selection.Root is { } root)
            return await SelectInNodeAsync(root, ct);

        var candidates = selection.Candidates;
        if (candidates.Count == 0)
            return null;

        if (selection.Mode == ComboMode.Fallback)
        {
            var first = candidates[0];
            return await executions.CanEnterAsync(first.Provider.Id, ct) ? first : null;
        }

        List<ModelCandidate> eligible = [];
        foreach (var candidate in candidates)
        {
            if (await executions.CanEnterAsync(candidate.Provider.Id, ct))
                eligible.Add(candidate);
        }
        if (eligible.Count == 0)
            return null;

        var inFlight = new Dictionary<long, int>();
        foreach (var candidate in eligible)
            inFlight[candidate.Provider.Id] = executions.GetInFlight(candidate.Provider.Id);

        var ordered = eligible
            .OrderBy(c => inFlight[c.Provider.Id])
            .ThenBy(c => c.Provider.Id)
            .ToList();

        // Cursor tăng mỗi lượt chọn — tie-break RR trong nhóm load bằng nhau; test ổn định nhờ % count
        var cursor = (uint)Interlocked.Read(ref _cursor);
        Interlocked.Increment(ref _cursor);
        return ordered[(int)(cursor % (uint)ordered.Count)];
    }

    /// <summary>
    /// Đi 1 nút cây theo mode của CHÍNH nút đó (composition đệ quy, đã duyệt plan Task 4):
    /// lá → CanEnter ? candidate : null; FB unit → đúng đơn vị đầu (descendant null → park,
    /// KHÔNG nhảy kế); RR unit → lọc đơn vị còn lá eligible, sort (minInFlight, tiebreak),
    /// cursor pick rồi descend.
    /// </summary>
    private async Task<ModelCandidate?> SelectInNodeAsync(SelectionNode node, CancellationToken ct)
    {
        switch (node)
        {
            case CandidateLeaf leaf:
                return await executions.CanEnterAsync(leaf.Candidate.Provider.Id, ct)
                    ? leaf.Candidate
                    : null;
            case ComboUnit { Mode: ComboMode.Fallback } fb:
                // Park luật đệ quy: đơn vị đầu không serve được → null (park), không nhảy item kế
                return fb.Children.Count == 0
                    ? null
                    : await SelectInNodeAsync(fb.Children[0], ct);
            case ComboUnit rr:
                return await SelectInRoundRobinUnitAsync(rr, ct);
            default:
                return null;
        }
    }

    /// <summary>
    /// RR unit: đơn vị (con) nào còn lá eligible = ứng viên; sort key = min in-flight các lá
    /// hợp lệ của đơn vị (đã duyệt lựa chọn 1), tie-break theo tên đơn vị/leaf — deterministic.
    /// Cursor toàn cục Interlocked như flat path.
    /// </summary>
    private async Task<ModelCandidate?> SelectInRoundRobinUnitAsync(ComboUnit unit, CancellationToken ct)
    {
        var eligible = new List<(SelectionNode Node, string Tiebreak, int MinInFlight)>();
        foreach (var child in unit.Children)
        {
            if (await MinInFlightOfEligibleAsync(child, ct) is { } minInFlight)
                eligible.Add((child, TiebreakOf(child), minInFlight));
        }
        if (eligible.Count == 0)
            return null;

        var ordered = eligible
            .OrderBy(e => e.MinInFlight)
            .ThenBy(e => e.Tiebreak, StringComparer.Ordinal)
            .ToList();
        // Chỉ consume cursor khi có lựa chọn thật (>1) — unit 1-eligible descend thẳng, không tốn tick.
        // Nếu không, RR unit lồng nhau (mỗi cấp +1) sẽ lệch phase: root luôn thấy cursor chẵn →
        // không bao giờ quay (test TwoUnits_CursorRotates fail).
        if (ordered.Count == 1)
            return await SelectInNodeAsync(ordered[0].Node, ct);

        var cursor = (uint)Interlocked.Read(ref _cursor);
        Interlocked.Increment(ref _cursor);
        return await SelectInNodeAsync(ordered[(int)(cursor % (uint)ordered.Count)].Node, ct);
    }

    /// <summary>
    /// Min in-flight các lá ELIGIBLE (CanEnter) của subtree — null nếu không còn lá eligible
    /// (= đơn vị không đủ điều kiện RR). Dùng cả làm eligibility check cho đơn vị.
    /// </summary>
    private async Task<int?> MinInFlightOfEligibleAsync(SelectionNode node, CancellationToken ct)
    {
        switch (node)
        {
            case CandidateLeaf leaf:
                return await executions.CanEnterAsync(leaf.Candidate.Provider.Id, ct)
                    ? executions.GetInFlight(leaf.Candidate.Provider.Id)
                    : null;
            case ComboUnit unit:
                int? min = null;
                foreach (var child in unit.Children)
                {
                    if (await MinInFlightOfEligibleAsync(child, ct) is { } childMin
                        && (min is null || childMin < min))
                    {
                        min = childMin;
                    }
                }
                return min;
            default:
                return null;
        }
    }

    private static string TiebreakOf(SelectionNode node) => node switch
    {
        CandidateLeaf leaf => $"{leaf.Candidate.Provider.Id}:{leaf.Candidate.Model.ModelId}",
        ComboUnit unit => unit.Name,
        _ => string.Empty,
    };
}
