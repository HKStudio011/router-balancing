namespace RouterBalancing.Core.Engine;

/// <summary>Chọn 1 candidate để dispatch theo mode (spec §3.3).</summary>
public interface IModelSelector
{
    /// <summary>
    /// Chọn candidate còn slot; trả <see langword="null"/> = park (không đủ capacity —
    /// dispatcher không Take, item ở lại queue chờ <c>Exited</c>/<c>Changed</c>).
    /// </summary>
    Task<ModelCandidate?> TrySelectAsync(SelectionSuccess selection, CancellationToken ct);
}
