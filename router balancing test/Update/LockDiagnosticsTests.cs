using RouterBalancing.Updater;

namespace router_balancing_test.Update;

/// <summary>
/// Báo cáo chẩn đoán khi update timeout: danh sách file lock (cap 20) +
/// process giữ lock — ghi ra console updater và %TEMP%\RouterBalancing_Update\update.log.
/// </summary>
public class LockDiagnosticsTests
{
    [Fact]
    public void BuildReport_CapsAtTwentyFiles_WithOverflowCount()
    {
        var files = Enumerable.Range(0, 25).Select(i => $@"C:\app\file{i}.dll").ToList();

        var report = LockDiagnostics.BuildReport(files);

        Assert.Contains("file0.dll", report);
        Assert.Contains("file19.dll", report);
        Assert.DoesNotContain("file20.dll", report);
        Assert.Contains("5 more", report);
    }

    [Fact]
    public void BuildReport_EmptyList_MentionsNoLockedFiles()
    {
        var report = LockDiagnostics.BuildReport([]);

        Assert.Contains("No locked files", report);
    }

    [Fact]
    public void AppendToFile_CreatesLogUnderTempUpdateDirectory()
    {
        var path = LockDiagnostics.ReportPath;
        try
        {
            LockDiagnostics.AppendToFile("unit-test-report-line");

            Assert.True(File.Exists(path));
            Assert.Contains("unit-test-report-line", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
