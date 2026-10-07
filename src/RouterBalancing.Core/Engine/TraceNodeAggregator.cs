namespace RouterBalancing.Core.Engine;

/// <summary>
/// Gộp <see cref="TraceEvent"/> thành <see cref="TraceNodeState"/> cho UI Live Trace —
/// một nguồn sự thật cho merge rule (spec §2.5), anchor và tag ưu tiên.
/// </summary>
/// <param name="terminalTtl">Thời gian node terminal (Finished/Canceled) sống trước khi hết hạn.</param>
public sealed class TraceNodeAggregator(TimeSpan terminalTtl)
{
    /// <summary>Node theo request id — UI đọc để render, tự dọn node hết hạn.</summary>
    public Dictionary<string, TraceNodeState> Nodes { get; } = [];

    /// <summary>
    /// Create-or-merge theo merge rule spec §2.5: <c>Stage</c> của event mới ghi đè,
    /// trừ <see cref="TraceStage.Received"/> trên node <b>đã tồn tại</b> — chỉ merge field
    /// (G2/G3 update không kéo dot về queue khi request đang chạy). Field có giá trị mới thì ghi,
    /// <see langword="null"/> thì giữ giá trị đã biết; <see cref="TraceNodeState.ExpiresAt"/>
    /// = event + <c>terminalTtl</c> với stage terminal, ngược lại <see langword="null"/>.
    /// </summary>
    /// <param name="e">Event cần áp.</param>
    /// <returns>Node sau merge — node mới tạo nếu id chưa có.</returns>
    public TraceNodeState Apply(TraceEvent e)
    {
        if (!Nodes.TryGetValue(e.RequestId, out var node))
        {
            node = new TraceNodeState
            {
                Id = e.RequestId,
                ReceivedAt = e.At,
            };
            Nodes[e.RequestId] = node;
        }

        // Received trên node đã tồn tại GIỮ Stage (spec §2.5: G2/G3 update không kéo dot
        // về queue khi request đang chạy); node mới có Stage = Received sẵn, event khác
        // (DispatchStarted/Attempt/Parked/terminal) luôn ghi đè.
        if (e.Stage is not TraceStage.Received)
            node.Stage = e.Stage;

        node.Model = e.Model;

        // Chỉ ghi field event mang giá trị: event Received/DispatchStarted để Route null —
        // không được xoá route đã biết từ attempt trước.
        if (e.Route is not null)
            node.Route = e.Route;
        if (e.Status is not null)
            node.Status = e.Status;
        if (e.Success is not null)
            node.Success = e.Success;
        if (e.Mode is not null)
            node.Mode = e.Mode;
        if (e.Priority is not null)
            node.Priority = e.Priority;
        if (e.Endpoint is not null)
            node.Endpoint = e.Endpoint;
        if (e.HeadersSent is not null)
            node.HeadersSent = e.HeadersSent;

        // Terminal giữ terminalTtl rồi fade (spec §5.4); event thường xoá hạn để node sống mãi
        node.ExpiresAt = e.Stage is TraceStage.Finished or TraceStage.Canceled
            ? e.At + terminalTtl
            : null;

        return node;
    }

    /// <summary>
    /// Anchor ngữ nghĩa theo stage hiện tại — UI map sang pixel
    /// (Attempt cần layout chip nên resolve ở component).
    /// </summary>
    /// <param name="node">Node cần tra anchor.</param>
    /// <returns>
    /// Received | Parked → <see cref="TraceAnchor.Queue"/>; DispatchStarted → <see cref="TraceAnchor.Dispatch"/>;
    /// Attempt → <see cref="TraceAnchor.Attempt"/>; còn lại → <see cref="TraceAnchor.Client"/>.
    /// </returns>
    public static TraceAnchor AnchorOf(TraceNodeState node) => node.Stage switch
    {
        TraceStage.Received or TraceStage.Parked => TraceAnchor.Queue,
        TraceStage.DispatchStarted => TraceAnchor.Dispatch,
        TraceStage.Attempt => TraceAnchor.Attempt,
        _ => TraceAnchor.Client,
    };

    /// <summary>
    /// Tag màu theo priority — Highest → amber, High → blue, Normal ẩn (spec E)
    /// để tránh noise; UI dùng khi render badge cạnh dot.
    /// </summary>
    /// <param name="priority">Priority của node — <see langword="null"/> khi chưa biết.</param>
    /// <returns>Class CSS tag; <see langword="null"/> khi không hiện tag.</returns>
    public static string? PriorityTag(RequestPriority? priority) => priority switch
    {
        RequestPriority.Highest => "trace-tag--prio-highest",
        RequestPriority.High => "trace-tag--prio-high",
        _ => null,
    };
}
