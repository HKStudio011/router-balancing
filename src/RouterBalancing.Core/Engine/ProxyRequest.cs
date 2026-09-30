using Microsoft.AspNetCore.Http;

namespace RouterBalancing.Core.Engine;

/// <summary>Đại diện 1 request đang nằm trong <see cref="IRequestQueue"/> (spec §2.1).</summary>
/// <param name="Id">Id sinh trước validate (endpoint).</param>
/// <param name="Priority">Mức ưu tiên từ header <c>X-Priority</c>.</param>
/// <param name="Model">Chuỗi <c>model</c> gốc client gửi (model id hoặc tên combo).</param>
/// <param name="Body">Body JSON đã buffer — dispatcher truyền thẳng khi serve.</param>
/// <param name="Context">HttpContext gốc — handler ghi response vào đây.</param>
public sealed class ProxyRequest(
    string id, RequestPriority priority, string model, byte[] body, HttpContext context)
{
    /// <summary>Mã request (8 ký tự base36).</summary>
    public string Id { get; } = id;

    /// <summary>Mức ưu tiên hiện tại — đổi được khi demote 1-Highest hoặc SetPriority.</summary>
    public RequestPriority Priority { get; internal set; } = priority;

    /// <summary>Tên model/combo gốc.</summary>
    public string Model { get; } = model;

    /// <summary>Body đã buffer.</summary>
    public byte[] Body { get; } = body;

    /// <summary>HttpContext của client gọi tới.</summary>
    public HttpContext Context { get; } = context;

    /// <summary>Sequence tăng dần toàn cục — key thứ 2 trong bucket (FIFO theo mức).</summary>
    public long Sequence { get; internal set; }

    /// <summary>Thời điểm vào queue — do <see cref="IRequestQueue.Enqueue"/> gán.</summary>
    public DateTimeOffset EnqueuedAt { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>Chỗ dispatcher báo outcome cho endpoint đang await — không dùng khi còn trong queue.</summary>
    public TaskCompletionSource<DispatchOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cặp (provider, model) đã thử + lỗi retryable gần nhất — sống qua park/capacity corner re-enqueue (spec 3C §3.2).</summary>
    public RetryState Retry { get; } = new();
}
