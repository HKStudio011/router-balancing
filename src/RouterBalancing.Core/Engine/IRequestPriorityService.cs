namespace RouterBalancing.Core.Engine;

/// <summary>
/// Kết quả gọi <see cref="IRequestPriorityService.SetPriority"/> — map sang toast ở UI
/// (nút đổi ưu tiên trong RequestDetailModal); cùng pattern với <see cref="RequestCancelResult"/>.
/// </summary>
public enum RequestPriorityResult
{
    /// <summary>Request còn trong queue — đã đổi priority (idempotent: đổi sang giá trị cũ cũng trả kết quả này).</summary>
    Updated,

    /// <summary>Request đang được phục vụ — đã rời queue nên không đổi được (khác <see cref="NotFound"/> để toast đúng ngữ điệu).</summary>
    NotUpdatable,

    /// <summary>Không tìm thấy — id sai, đã huỷ rồi hoặc đã xong.</summary>
    NotFound,

    /// <summary>
    /// Proxy host chưa chạy — CHỈ <c>ProxyHost.SetRequestPriority</c> trả giá trị này;
    /// <see cref="RequestPriorityService"/> không biết trạng thái host nên không bao giờ trả.
    /// </summary>
    NotRunning,
}

/// <summary>
/// Đổi priority request đang chờ — nguồn sự thật cho nút đổi ưu tiên trong UI,
/// dùng cùng <see cref="IRequestQueue.SetPriority"/> với luật 1-Highest và publish trace G3.
/// </summary>
public interface IRequestPriorityService
{
    /// <summary>
    /// Đổi priority của request theo id nếu còn trong queue.
    /// </summary>
    /// <param name="id">Mã request cần đổi.</param>
    /// <param name="priority">Priority mới — thăng lên Highest sẽ hạ các Highest khác (luật 1-Highest).</param>
    /// <returns>Kết quả — xem <see cref="RequestPriorityResult"/>.</returns>
    RequestPriorityResult SetPriority(string id, RequestPriority priority);
}
