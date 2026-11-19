using System.Runtime.CompilerServices;

// SettingsPanel thuộc assembly MAUI riêng; friend access giữ formatter internal thay vì nới public API.
[assembly: InternalsVisibleTo("router-balancing")]

namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Định dạng dòng thống kê dataset từ resource của ngôn ngữ đang hiển thị.
/// </summary>
internal static class DatasetStatsFormatter
{
    /// <summary>
    /// Đưa toàn bộ counter vào đúng placeholder {0}–{11} của resource thống kê.
    /// </summary>
    /// <param name="stats">Snapshot counter tại thời điểm render.</param>
    /// <param name="translate">Tra cứu resource theo key, do call site UI cung cấp.</param>
    /// <returns>Dòng thống kê đã localize.</returns>
    internal static string Format(DatasetStats stats, Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(translate);

        return string.Format(translate("settings.status.datasetStats"),
            stats.RecordsWritten,
            Math.Round(stats.BytesWritten / 1024d / 1024d, 1),
            stats.SkippedTooLarge,
            stats.SkippedTruncated,
            stats.SkippedEmpty,
            stats.SkippedRefusal,
            stats.SkippedQueueFull,
            stats.SkippedSynthetic,
            stats.LargeRecordsWritten,
            stats.PayloadRecordsWritten,
            stats.ExternalizedPayloadCount,
            Math.Round(stats.ExternalizedPayloadBytes / 1024d / 1024d, 1));
    }
}
