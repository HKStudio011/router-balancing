namespace router_balancing.Services;

/// <summary>Sao chép text vào clipboard — caller tự toast theo kết quả (D-D2).</summary>
public interface IClipboardService
{
    /// <summary>Trả <see langword="false"/> khi trình duyệt từ chối (document không focus, thiếu quyền).</summary>
    Task<bool> CopyAsync(string text);
}
