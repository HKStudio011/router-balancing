using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// RR: lọc còn slot → sort (in-flight, ProviderId) → chọn theo cursor toàn cục.
/// Fallback: chỉ xét đúng vị trí đầu — đầy thì park (failover-on-error là 3C).
/// </summary>
public sealed class ModelSelector(IExecutionList executions) : IModelSelector
{
    private long _cursor;

    /// <inheritdoc/>
    public async Task<ModelCandidate?> TrySelectAsync(SelectionSuccess selection, CancellationToken ct)
    {
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
}
