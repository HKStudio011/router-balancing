using RouterBalancing.Core.Engine;

namespace RouterBalancing.Core.Server;

/// <summary>Kestrel host của proxy — Start/Stop/Restart từ UI Settings.</summary>
public interface IProxyHost : IAsyncDisposable
{
    /// <summary>Port đang lắng nghe thực tế; null khi chưa chạy.</summary>
    int? Port { get; }

    bool IsRunning { get; }

    /// <summary>Phát khi IsRunning/Port đổi — UI cập nhật badge trạng thái.</summary>
    event Action? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Dừng rồi chạy lại — dùng sau khi đổi port setting.</summary>
    Task RestartAsync(CancellationToken cancellationToken = default);

    /// <summary>Snapshot id+model request còn trong queue — UI bù node trắng khi feed chưa có (spec trace §5.3).</summary>
    IReadOnlyList<QueuedTraceItem> QueuedSnapshot();

    /// <summary>
    /// Huỷ request đang chờ trong queue — cùng nguồn sự thật với HTTP cancel endpoint
    /// (spec §5.1). Nút Huỷ trong RequestDetailModal gọi qua bridge này.
    /// </summary>
    /// <param name="id">Mã request cần huỷ.</param>
    /// <returns>Kết quả; <see cref="RequestCancelResult.NotRunning"/> khi host chưa chạy.</returns>
    RequestCancelResult CancelRequest(string id);

    /// <summary>
    /// Đọc info request còn trong queue (model + priority hiện tại) — nguồn sự thật
    /// để UI chỉ hiện nút Huỷ/đổi ưu tiên khi request thật sự đang chờ, và để render
    /// giá trị priority đang chọn. Null khi không còn trong queue (đã dispatch /
    /// không tồn tại) hoặc host chưa chạy.
    /// </summary>
    /// <param name="id">Mã request cần tra.</param>
    /// <returns>Info nếu còn trong queue; ngược lại <see langword="null"/>.</returns>
    QueuedRequestInfo? TryGetQueued(string id);

    /// <summary>
    /// Đổi priority request đang chờ — bridge qua <c>app.Services</c> cùng pattern
    /// <see cref="CancelRequest"/>; nguồn sự thật là <see cref="IRequestPriorityService"/>.
    /// </summary>
    /// <param name="id">Mã request cần đổi.</param>
    /// <param name="priority">Priority mới.</param>
    /// <returns>Kết quả; <see cref="RequestPriorityResult.NotRunning"/> khi host chưa chạy.</returns>
    RequestPriorityResult SetRequestPriority(string id, RequestPriority priority);
}

/// <summary>Request còn trong queue — id + model để UI ghép node trace.</summary>
public sealed record QueuedTraceItem(string Id, string Model);

/// <summary>Request còn trong queue — id + model + priority hiện tại cho UI (nút ưu tiên trong modal chi tiết).</summary>
public sealed record QueuedRequestInfo(string Id, string Model, RequestPriority Priority);
