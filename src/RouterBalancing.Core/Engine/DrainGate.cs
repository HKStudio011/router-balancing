namespace RouterBalancing.Core.Engine;

/// <summary>
/// Cờ tạm dừng (drain) cho luồng update: UI gọi <see cref="BeginDrain"/> trước khi
/// drain request đang chạy; <c>DispatcherLoop</c>/<c>ProxyHost</c> đọc
/// <see cref="IsDraining"/> để dừng nhận request mới, <see cref="Changed"/> để wake
/// kiểm tra lại; <c>UpdateCoordinator</c> gọi <see cref="CancelDrain"/> khi update thất bại.
/// </summary>
/// <remarks>
/// Không cần lock: BeginDrain/CancelDrain chỉ gọi từ UI thread — kiểm tra cờ + gán
/// trong cùng một method nên transition atomic với chính nó; subscriber chỉ đọc cờ
/// SAU khi đã nhận <see cref="Changed"/> (event là happens-before với việc đọc),
/// nên không bao giờ thấy cờ cũ khi đang phản ứng với transition.
/// </remarks>
public sealed class DrainGate
{
    /// <summary>
    /// Có đang trong chế độ drain hay không — <see langword="true"/> sau
    /// <see cref="BeginDrain"/>, <see langword="false"/> sau <see cref="CancelDrain"/>.
    /// </summary>
    public bool IsDraining { get; private set; }

    /// <summary>
    /// Bắn khi <see cref="IsDraining"/> vừa đổi — dispatcher/proxy dùng để wake
    /// và kiểm tra lại cờ; idempotent (không bắn khi gọi BeginDrain/CancelDrain
    /// với trạng thái đã đúng) để subscriber không xử lý thừa.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Bật drain — dừng nhận request mới, chờ request đang chạy hoàn tất.
    /// Idempotent: đang drain rồi thì return, không bắn <see cref="Changed"/>.
    /// </summary>
    public void BeginDrain()
    {
        if (IsDraining)
        {
            return;
        }

        IsDraining = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Tắt drain — quay lại nhận request bình thường (dùng khi update thất bại).
    /// Idempotent: chưa drain thì return, không bắn <see cref="Changed"/>.
    /// </summary>
    public void CancelDrain()
    {
        if (!IsDraining)
        {
            return;
        }

        IsDraining = false;
        Changed?.Invoke();
    }
}
