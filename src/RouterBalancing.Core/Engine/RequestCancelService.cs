namespace RouterBalancing.Core.Engine;

/// <inheritdoc cref="IRequestCancelService"/>
/// <param name="queue">Queue request đang chờ dispatch — chỉ huỷ được khi còn đứng trong đây.</param>
/// <param name="executions">Danh sách request đang phục vụ — phân biệt 409 vs 404.</param>
public sealed class RequestCancelService(IRequestQueue queue, IExecutionList executions)
    : IRequestCancelService
{
    /// <inheritdoc/>
    public RequestCancelResult Cancel(string id)
    {
        // TryRemove atomic với Take của dispatcher — ai gỡ được thì thắng,
        // thua nghĩa là request đã bị dispatch (đang phục vụ) hoặc không tồn tại
        if (queue.TryRemove(id, out var removed))
        {
            removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
            return RequestCancelResult.Cancelled;
        }

        return executions.Contains(id) ? RequestCancelResult.NotCancellable : RequestCancelResult.NotFound;
    }
}
