using RouterBalancing.Core.Engine;

namespace router_balancing.Components.Shared;

/// <summary>
/// Bảng màu dot dùng chung cho Live Trace và API Monitor — một nguồn sự thật
/// cho class CSS màu (<c>trace-dot--*</c>), tránh 2 component đổi màu lệch nhau.
/// </summary>
internal static class TraceColors
{
    /// <summary>
    /// Màu dot theo vòng đời trace — spec G2: trắng/xanh/đỏ/vàng/xanh lá
    /// (Received→trắng, DispatchStarted/Attempt→xanh dương, Canceled→vàng,
    /// Finished→xanh lá khi thành công, đỏ khi thất bại).
    /// </summary>
    /// <param name="stage">Stage hiện tại của node.</param>
    /// <param name="success">
    /// Kết quả của node — chỉ có ý nghĩa khi <paramref name="stage"/> là
    /// <see cref="TraceStage.Finished"/>, các stage khác bỏ qua.
    /// </param>
    /// <returns>Class CSS màu dot.</returns>
    public static string ForStage(TraceStage stage, bool? success) => stage switch
    {
        TraceStage.Received => "trace-dot--white",
        TraceStage.DispatchStarted or TraceStage.Attempt => "trace-dot--blue",
        TraceStage.Canceled => "trace-dot--yellow",
        TraceStage.Finished => success == false ? "trace-dot--red" : "trace-dot--green",
        // Parked: dot trắng như Received — khác biệt thể hiện qua ring vàng (spec §3.2)
        TraceStage.Parked => "trace-dot--white",
        _ => "trace-dot--white",
    };

    /// <summary>
    /// Màu dot theo trạng thái record API Monitor — bảng màu khớp legend Live Trace:
    /// Queued→trắng, Running→xanh dương, Done→xanh lá, Error→đỏ, Cancelled→vàng.
    /// </summary>
    /// <param name="state">Trạng thái record cần hiển thị.</param>
    /// <returns>Class CSS màu dot.</returns>
    public static string ForState(ApiCallState state) => state switch
    {
        ApiCallState.Queued => "trace-dot--white",
        ApiCallState.Running => "trace-dot--blue",
        ApiCallState.Done => "trace-dot--green",
        ApiCallState.Error => "trace-dot--red",
        ApiCallState.Cancelled => "trace-dot--yellow",
        _ => "trace-dot--white",
    };
}
