namespace RouterBalancing.Core.Engine;

/// <summary>
/// Kết quả gọi <see cref="IRequestCancelService.Cancel"/> — map sang HTTP
/// (200/409/404) ở cancel endpoint và sang toast ở UI (spec §5.1).
/// </summary>
public enum RequestCancelResult
{
    /// <summary>Request còn trong queue — đã gỡ và báo outcome <see cref="DispatchOutcome.Cancelled"/>.</summary>
    Cancelled,

    /// <summary>Request đang được phục vụ — không huỷ được (endpoint trả 409 <c>not_cancellable</c>).</summary>
    NotCancellable,

    /// <summary>Không tìm thấy — id sai, đã huỷ rồi hoặc đã xong (endpoint trả 404 <c>request_not_found</c>).</summary>
    NotFound,

    /// <summary>
    /// Proxy host chưa chạy — CHỈ <c>ProxyHost.CancelRequest</c> trả giá trị này;
    /// <see cref="RequestCancelService"/> không biết trạng thái host nên không bao giờ trả.
    /// </summary>
    NotRunning,
}

/// <summary>
/// Huỷ request đang chờ — nguồn sự thật DUY NHẤT cho cả HTTP cancel endpoint
/// lẫn nút Huỷ trong UI (spec §5.1), tránh 2 nơi diverge về logic state.
/// </summary>
public interface IRequestCancelService
{
    /// <summary>
    /// Gỡ request theo id và báo outcome <see cref="DispatchOutcome.Cancelled"/> nếu còn trong queue.
    /// </summary>
    /// <param name="id">Mã request cần huỷ.</param>
    /// <returns>Kết quả — xem <see cref="RequestCancelResult"/>.</returns>
    RequestCancelResult Cancel(string id);
}
