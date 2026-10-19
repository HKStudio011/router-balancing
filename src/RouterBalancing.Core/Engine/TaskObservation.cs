namespace RouterBalancing.Core.Engine;

/// <summary>
/// Quan sát exception của task bị bỏ rơi (fire-and-forget hoặc nhánh thua của
/// <see cref="Task.WhenAny(Task[])"/>). Task faulted mà không ai đọc exception sẽ bị
/// finalizer ném lại thành <see cref="AggregateException"/> (UnobservedTaskException) —
/// đã gặp thật 2026-10-19: OCE từ <c>pipe.Reader.ReadAsync(ct).AsTask()</c> bị bỏ rơi
/// trong nhánh client-abort của <see cref="RouterBalancing.Core.Server.ProxyApp.RunStreamLoopAsync"/>.
/// </summary>
internal static class TaskObservation
{
    /// <summary>
    /// Đánh dấu <paramref name="task"/> đã được quan sát bằng cách đọc kết quả trong
    /// continuation — non-blocking, không đổi trạng thái/exception của task, không ném.
    /// </summary>
    /// <param name="task">Task bị bỏ rơi cần đánh dấu observed.</param>
    public static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(static t =>
        {
            // GetResult: ném nếu faulted/canceled — chạm tới exception là đánh dấu observed;
            // nuốt chủ đích vì task bị bỏ rơi không còn nơi nào xử lý lỗi.
            try
            {
                t.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
