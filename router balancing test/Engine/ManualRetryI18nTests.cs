using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Localization;

namespace router_balancing_test.Engine;

public class ManualRetryI18nTests
{
    /// <summary>
    /// Regression pin: key do helper sinh ra phải tra được trong CẢ 2 ngôn ngữ —
    /// đúng hình camelCase (spec §3.6), không phải nguyên tử PascalCase hay toàn bộ thường.
    /// </summary>
    [Fact]
    public void HelperKeys_ResolveInBothEnglishAndVietnamese()
    {
        foreach (var level in Enum.GetValues<ManualRetryLevel>())
        {
            var key = ManualRetryI18n.LevelKey(level);
            Assert.True(Translations.English.ContainsKey(key),
                $"EN thiếu key cấp '{key}' (level={level})");
            Assert.True(Translations.Vietnamese.ContainsKey(key),
                $"VI thiếu key cấp '{key}' (level={level})");
        }

        foreach (var reason in Enum.GetValues<ManualRetryReason>())
        {
            var key = ManualRetryI18n.ReasonKey(reason);
            Assert.True(Translations.English.ContainsKey(key),
                $"EN thiếu key lý do '{key}' (reason={reason})");
            Assert.True(Translations.Vietnamese.ContainsKey(key),
                $"VI thiếu key lý do '{key}' (reason={reason})");
        }
    }
}
