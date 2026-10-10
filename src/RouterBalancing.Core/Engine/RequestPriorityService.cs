namespace RouterBalancing.Core.Engine;

/// <inheritdoc cref="IRequestPriorityService"/>
/// <param name="queue">Queue request đang chờ dispatch — chỉ đổi được khi còn đứng trong đây.</param>
/// <param name="executions">Danh sách request đang phục vụ — phân biệt NotUpdatable vs NotFound.</param>
public sealed class RequestPriorityService(IRequestQueue queue, IExecutionList executions)
    : IRequestPriorityService
{
    /// <inheritdoc/>
    public RequestPriorityResult SetPriority(string id, RequestPriority priority)
    {
        // SetPriority atomic với Take của dispatcher — true nghĩa là request vẫn trong queue
        // (hoặc đã ở đúng priority này — idempotent, không cần phân biệt)
        if (queue.SetPriority(id, priority))
            return RequestPriorityResult.Updated;

        return executions.Contains(id)
            ? RequestPriorityResult.NotUpdatable
            : RequestPriorityResult.NotFound;
    }
}
