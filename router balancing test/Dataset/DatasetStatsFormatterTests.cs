using System.Globalization;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Localization;

namespace router_balancing_test.Dataset;

public class DatasetStatsFormatterTests
{
    [Fact]
    public void DatasetStatsFormatter_EnglishAndVietnamese_RenderEveryCounterAtItsLabel()
    {
        var stats = new DatasetStats(
            RecordsWritten: 1,
            SkippedTooLarge: 2,
            SkippedTruncated: 3,
            SkippedEmpty: 4,
            SkippedRefusal: 5,
            SkippedQueueFull: 6,
            SkippedSynthetic: 7,
            BytesWritten: 9_437_184,
            CurrentFile: "requests-2026-11-19.jsonl",
            LargeRecordsWritten: 12,
            PayloadRecordsWritten: 34,
            ExternalizedPayloadCount: 56,
            ExternalizedPayloadBytes: 82_732_646);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // Số thập phân phải độc lập culture của test runner; production giữ convention string.Format hiện có.
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var englishOutput = DatasetStatsFormatter.Format(
                stats, key => Translations.English[key]);
            var vietnameseOutput = DatasetStatsFormatter.Format(
                stats, key => Translations.Vietnamese[key]);

            Assert.Equal(
                "1 records written · 9 MB · skipped: 2 too large, 3 truncated, 4 empty, 5 refusal, 6 queue full, 7 synthesized title · 12 large records · 34 payload manifests · 56 externalized files · 78.9 MiB externalized",
                englishOutput);
            Assert.Equal(
                "Đã ghi 1 dòng · 9 MB · bỏ qua: 2 quá cỡ, 3 cụt, 4 rỗng, 5 từ chối, 6 đầy hàng đợi, 7 title tổng hợp · 12 dòng large · 34 manifest payload · 56 file externalized · 78.9 MiB externalized",
                vietnameseOutput);
            Assert.Contains("12 large records", englishOutput);
            Assert.Contains("34 payload manifests", englishOutput);
            Assert.Contains("56 file externalized", vietnameseOutput);
            Assert.Contains("78.9 MiB externalized", vietnameseOutput);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
