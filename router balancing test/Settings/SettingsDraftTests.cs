using RouterBalancing.Core.Settings;

namespace router_balancing_test.Settings;

/// <summary>
/// Chốt chống đổi default của dataset export (spec dataset-export §7.6) — SettingsPanel là UI
/// nên test ở đây chỉ assert default, không test markup.
/// </summary>
public class SettingsDraftTests
{
    /// <summary>
    /// Export mặc định TẮT, path rỗng (= mặc định AppData), retention 0 = giữ mãi — đổi bất kỳ
    /// giá trị nào là đổi hành vi của người dùng hiện có (mặc định phải fail-open).
    /// </summary>
    [Fact]
    public void SettingsDraft_DatasetDefaults_AreOffForever()
    {
        var draft = new SettingsDraft();

        Assert.False(draft.DatasetExportEnabled);
        Assert.Equal("", draft.DatasetExportPath);
        Assert.Equal(0, draft.DatasetRetentionDays);
    }
}
