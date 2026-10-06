using System.Globalization;
using RouterBalancing.Core.Engine;

namespace router_balancing.Components.Shared;

/// <summary>
/// Định dạng số liệu cho API Monitor — dùng chung với Live Trace
/// (<c>RequestTrace.FormatDuration</c> delegate sang <see cref="Duration"/>),
/// thiếu dữ liệu luôn hiển thị "—" thay vì số sai.
/// </summary>
internal static class MonitorFormat
{
    /// <summary>Ký hiệu hiển thị khi thiếu dữ liệu — thống nhất mọi cột số liệu.</summary>
    private const string Missing = "—";

    /// <summary>
    /// Định dạng khoảng thời gian: dưới 1s → "123ms", từ 1s → "1.234s"
    /// (ruling Task 7 — không thêm dependency, helper dùng chung).
    /// </summary>
    /// <param name="value">Khoảng thời gian — giá trị âm được coi là 0.</param>
    /// <returns>Chuỗi định dạng ms/s.</returns>
    public static string Duration(TimeSpan value)
    {
        var ms = Math.Max(0, (long)value.TotalMilliseconds);
        return ms < 1000 ? $"{ms}ms" : $"{ms / 1000}.{ms % 1000:000}s";
    }

    /// <summary>Latency = <c>CompletedAt − StartedAt</c>; request chưa xong → "—".</summary>
    /// <param name="record">Record cần định dạng.</param>
    /// <returns>Chuỗi thời lượng hoặc "—".</returns>
    public static string Latency(ApiCallRecord record) =>
        record.CompletedAt is { } completed ? Duration(completed - record.StartedAt) : Missing;

    /// <summary>
    /// TTFT = <c>FirstTokenAt − StartedAt</c> — non-stream (không có FirstTokenAt) → "—".
    /// </summary>
    /// <param name="record">Record cần định dạng.</param>
    /// <returns>Chuỗi thời lượng hoặc "—".</returns>
    public static string Ttft(ApiCallRecord record) =>
        record.FirstTokenAt is { } first ? Duration(first - record.StartedAt) : Missing;

    /// <summary>
    /// Throughput = <c>CompletionTokens / (CompletedAt − FirstTokenAt)</c> token/giây,
    /// 1 chữ số thập phân (invariant); thiếu token/thời điểm hoặc khoảng ≤ 0 → "—".
    /// </summary>
    /// <param name="record">Record cần định dạng.</param>
    /// <returns>Số token/giây hoặc "—".</returns>
    public static string Throughput(ApiCallRecord record)
    {
        if (record.CompletionTokens is not { } tokens ||
            record.FirstTokenAt is not { } first ||
            record.CompletedAt is not { } completed)
            return Missing;

        var seconds = (completed - first).TotalSeconds;
        if (seconds <= 0)
            return Missing;

        return (tokens / seconds).ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Token hiển thị "↑prompt ↓completion"; thiếu một phần → "—" tại chỗ phần đó,
    /// cả hai phần đều thiếu → "—".
    /// </summary>
    /// <param name="record">Record cần định dạng.</param>
    /// <returns>Chuỗi token hoặc "—".</returns>
    public static string Tokens(ApiCallRecord record) =>
        record.PromptTokens is null && record.CompletionTokens is null
            ? Missing
            : $"↑{record.PromptTokens?.ToString() ?? Missing} ↓{record.CompletionTokens?.ToString() ?? Missing}";
}
