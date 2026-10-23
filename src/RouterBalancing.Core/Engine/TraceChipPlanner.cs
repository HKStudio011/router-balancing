namespace RouterBalancing.Core.Engine;

/// <summary>Một tài khoản đang được node trace còn sống tham chiếu — input chia cột chip Account.</summary>
/// <param name="Name">Tên tài khoản (duy nhất trong provider).</param>
/// <param name="Running">Có request đang chạy (DispatchStarted/Attempt) trên tài khoản này.</param>
/// <param name="HasError">Có node lỗi (status ≥ 400 hoặc <c>Success == false</c>) trên tài khoản này.</param>
public sealed record TraceAccountRef(string Name, bool Running, bool HasError);

/// <summary>Kết quả chia chip Account của một provider — phần hiện trực tiếp và phần gộp vào chip "+N".</summary>
/// <param name="Shown">Tài khoản hiện thành chip riêng (tối đa cap, tài khoản đang chạy được ưu tiên).</param>
/// <param name="HiddenActive">Số tài khoản ĐANG CHẠY bị gộp vào chip "+N" (0 nếu mọi TK chạy đều hiện).</param>
/// <param name="OverflowCount">
/// Tổng số TK bị gộp vào chip "+N" = TK bị cap cắt + TK không được tham chiếu (idle).
/// </param>
public sealed record TraceChipPlan(
    IReadOnlyList<TraceAccountRef> Shown, int HiddenActive, int OverflowCount);

/// <summary>
/// Chia danh sách TK của một provider thành chip hiện trực tiếp + chip gộp "+N".
/// Tách khỏi component để test được quy tắc cap (spec Live Trace §5.2/G5): cap không được
/// giấu TK đang chạy mà không có indicator — bug 2026-10-23 (5 request chạy nhưng chỉ 4 chip).
/// </summary>
public static class TraceChipPlanner
{
    /// <summary>Số chip account tối đa hiện trực tiếp trên một lane provider (spec §5.2).</summary>
    public const int MaxChips = 4;

    /// <summary>
    /// Lập kế hoạch chip: sắp TK đang chạy lên trước (stable — giữ thứ tự xuất hiện), cắt còn
    /// <paramref name="maxChips"/> chip; phần còn lại (kể cả TK đang chạy bị cắt và TK idle
    /// không được tham chiếu) dồn vào <see cref="TraceChipPlan.OverflowCount"/>.
    /// </summary>
    /// <param name="referenced">TK đang được node trace còn sống tham chiếu.</param>
    /// <param name="providerAccountCount">
    /// Tổng TK của provider (null = cache chưa có → không biết số idle, vẫn báo phần bị cap cắt).
    /// </param>
    /// <param name="maxChips">Số chip hiện trực tiếp tối đa; mặc định <see cref="MaxChips"/>.</param>
    /// <returns>Kế hoạch chip; không bao giờ <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">Khi <paramref name="referenced"/> là <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="maxChips"/> ≤ 0.</exception>
    public static TraceChipPlan Plan(
        IEnumerable<TraceAccountRef> referenced, int? providerAccountCount, int maxChips = MaxChips)
    {
        ArgumentNullException.ThrowIfNull(referenced);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChips, 1);

        var ordered = referenced.OrderByDescending(a => a.Running).ToList();
        var shown = ordered.Take(maxChips).ToList();
        var hidden = ordered.Skip(maxChips).ToList();

        // Idle = TK provider sở hữu nhưng không node sống nào tham chiếu. Cache thiếu (null)
        // thì bỏ qua phần idle để không hiển thị số sai; phần TK bị cap cắt vẫn luôn báo.
        var idle = providerAccountCount is { } total ? Math.Max(0, total - ordered.Count) : 0;

        return new TraceChipPlan(
            shown,
            HiddenActive: hidden.Count(a => a.Running),
            OverflowCount: hidden.Count + idle);
    }
}
