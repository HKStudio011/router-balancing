namespace RouterBalancing.Core.Engine;

/// <summary>
/// Gate quota CẤP PROVIDER sống qua request: khi upstream báo lỗi quota mà cap không
/// tính theo key/session/IP (OpenCode Zen free tier — <c>FreeUsageLimitError</c>), xoay
/// tài khoản kế tiếp cũng không thể thành công, nên request sau phải fail nhanh thay vì
/// gọi upstream thêm lần nữa.
/// </summary>
public interface IProviderQuotaGate
{
    /// <summary>
    /// Mở (hoặc gia hạn) window chặn cho <paramref name="providerId"/> ngay sau một attempt
    /// fail vì quota toàn cục.
    /// </summary>
    /// <param name="providerId">Provider vừa trả lỗi quota cap-provider.</param>
    /// <param name="failure">Thất bại của attempt — status/body để replay cho request sau.</param>
    /// <param name="fallbackTtl">
    /// Độ dài window khi upstream KHÔNG nói gì (setting <c>providerQuotaBlockSec</c>).
    /// <see cref="TimeSpan.Zero"/> hoặc âm = tắt gate, không mở window.
    /// </param>
    void Report(long providerId, RetryState.Failure failure, TimeSpan fallbackTtl);

    /// <summary>
    /// Provider đang trong window chặn? <paramref name="block"/> là failure đã ghi, với
    /// <c>RetryAfter</c> = số giây CÒN LẠI (header gửi client phải đếm ngược, không lặp lại
    /// giá trị gốc).
    /// </summary>
    /// <param name="providerId">Provider cần xét.</param>
    /// <param name="block">Snapshot để replay; <see langword="null"/> khi không chặn.</param>
    /// <returns><see langword="true"/> khi còn trong window.</returns>
    bool TryGetBlock(long providerId, out RetryState.Failure? block);

    /// <summary>
    /// Hủy window của provider — dùng khi cấu hình provider thay đổi (thêm/sửa tài khoản,
    /// gán proxy): người dùng vừa cho điều kiện mới (key khác, IP khác) nên phải được thử
    /// lại ngay thay vì chờ hết window.
    /// </summary>
    /// <param name="providerId">Provider vừa bị sửa cấu hình.</param>
    void Clear(long providerId);

    /// <summary>
    /// Ảnh chụp window đang mở cho UI (badge cooldown trong Live Trace) — providerId → thời
    /// điểm hết window; entry đã hết hạn bị loại (UI tắt badge mà không cần refresh lại).
    /// </summary>
    IReadOnlyDictionary<long, DateTimeOffset> SnapshotBlocked();
}
