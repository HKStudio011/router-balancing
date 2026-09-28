using System.Diagnostics.CodeAnalysis;

namespace RouterBalancing.Core.Engine;

/// <summary>Priority queue toàn cục cho request chờ dispatch (spec §2.1).</summary>
public interface IRequestQueue
{
    /// <summary>Bắn sau Enqueue/TryRemove/SetPriority — dispatcher tự Take thì KHÔNG bắn (nó tự drain tiếp), Peek không bắn. Dispatcher dùng làm wake signal.</summary>
    event Action? Changed;

    /// <summary>Thêm request (gán Sequence + EnqueuedAt). Trả <see langword="false"/> nếu id đã tồn tại.</summary>
    bool Enqueue(ProxyRequest request);

    /// <summary>Xem đầu queue theo (Priority, Sequence) mà không lấy ra.</summary>
    bool Peek([NotNullWhen(true)] out ProxyRequest? request);

    /// <summary>Gỡ request theo id — dispatcher gọi sau khi đã giữ slot (atomic với cancel: ai gỡ được thì thắng). KHÔNG fire Changed — dispatcher tự drain tiếp.</summary>
    bool Take(string id, [NotNullWhen(true)] out ProxyRequest? request);

    /// <summary>Gỡ request theo id — cancel API / RequestAborted callback gọi. Fire Changed để dispatcher re-evaluate head.</summary>
    bool TryRemove(string id, [NotNullWhen(true)] out ProxyRequest? request);

    /// <summary>Đổi mức ưu tiên (chưa expose endpoint — cho UI slice sau).</summary>
    bool SetPriority(string id, RequestPriority priority);

    /// <summary>Kiểm tra id còn nằm trong queue không (endpoint dùng khi sinh id tránh va chạm).</summary>
    bool Contains(string id);

    /// <summary>Snapshot toàn bộ item theo thứ tự Priority rồi Sequence — data source cho <c>GET /v1/requests</c>.</summary>
    IReadOnlyList<ProxyRequest> Snapshot();
}
