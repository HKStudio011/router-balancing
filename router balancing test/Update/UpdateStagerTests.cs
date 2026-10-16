using RouterBalancing.Updater;

namespace router_balancing_test.Update;

/// <summary>
/// Test staging/unlock/copy của updater — cốt lõi chống "extract dở dang làm
/// lẫn runtime hai kiểu trong appDir" (bug host ".NET missing" sau update).
/// </summary>
public class UpdateStagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rb-stager-" + Guid.NewGuid().ToString("N"));

    private string StagingDir => Path.Combine(_root, "staging");
    private string AppDir => Path.Combine(_root, "app");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        // ExtractToStaging dùng thư mục staging %TEMP% cố định — dọn sau mỗi test
        var sharedStaging = Path.Combine(Path.GetTempPath(), "RouterBalancing_Update", "staging");
        if (Directory.Exists(sharedStaging))
            Directory.Delete(sharedStaging, recursive: true);
    }

    private void CreateDirs()
    {
        Directory.CreateDirectory(StagingDir);
        Directory.CreateDirectory(AppDir);
    }

    private static string MakeZip(string dir, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "update.zip");
        using var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }

    [Fact]
    public void ExtractToStaging_ExtractsFilesAndSubdirectories()
    {
        var zip = MakeZip(_root, ("app.exe", "exe"), ("wwwroot/index.html", "<html/>"));

        var staging = UpdateStager.ExtractToStaging(zip);

        Assert.Equal("exe", File.ReadAllText(Path.Combine(staging, "app.exe")));
        Assert.Equal("<html/>", File.ReadAllText(Path.Combine(staging, "wwwroot", "index.html")));
    }

    [Fact]
    public void ExtractToStaging_WhenZipBroken_ThrowsWithoutTouchingAppDir()
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "bad.zip");
        File.WriteAllText(zip, "not a zip");
        CreateDirs();
        var sentinel = Path.Combine(AppDir, "untouched.txt");
        File.WriteAllText(sentinel, "x");

        Assert.ThrowsAny<InvalidDataException>(() => UpdateStager.ExtractToStaging(zip));

        // Trích fail ở staging — appDir không được đụng tới
        Assert.Equal("x", File.ReadAllText(sentinel));
    }

    [Fact]
    public void WaitUntilWritable_WhenAllUnlocked_ReturnsEmptyLockedList()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        File.WriteAllText(Path.Combine(AppDir, "a.dll"), "old");

        var locked = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(5));

        Assert.Empty(locked);
    }

    [Fact]
    public void WaitUntilWritable_WhenFileLocked_ReturnsLockedFileAfterTimeout()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        var target = Path.Combine(AppDir, "a.dll");
        File.WriteAllText(target, "old");

        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromMilliseconds(400));

            Assert.Contains(target, locked);
        }
    }

    [Fact]
    public void WaitUntilWritable_MultipleFilesLocked_ReturnsAllOfThem()
    {
        // Chẩn đoán timeout phải liệt kê ĐỦ file lock, không dừng ở file đầu —
        // nếu không user chỉ thấy 1 trong nhiều locker.
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        File.WriteAllText(Path.Combine(StagingDir, "b.dll"), "new");
        var targetA = Path.Combine(AppDir, "a.dll");
        var targetB = Path.Combine(AppDir, "b.dll");
        File.WriteAllText(targetA, "old");
        File.WriteAllText(targetB, "old");

        using (File.Open(targetA, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (File.Open(targetB, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromMilliseconds(400));

            Assert.Equal(2, locked.Count);
            Assert.Contains(targetA, locked);
            Assert.Contains(targetB, locked);
        }
    }

    [Fact]
    public async Task WaitUntilWritable_WhenLockReleasedDuringWait_ReturnsEmpty()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        var target = Path.Combine(AppDir, "a.dll");
        File.WriteAllText(target, "old");

        var handle = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var releaser = Task.Run(async () =>
        {
            await Task.Delay(700);
            handle.Dispose();
        });

        var locked = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(10));
        await releaser;

        Assert.Empty(locked);
    }

    [Fact]
    public void WaitUntilWritable_WhenFileOnlyInStaging_IgnoresIt()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "brand-new.dll"), "new");

        // File mới chưa có ở appDir — không thể bị lock, không được block
        var locked = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(5));

        Assert.Empty(locked);
    }

    [Fact]
    public void CopyDirectory_OverwritesExisting_AndCreatesSubdirectories()
    {
        CreateDirs();
        Directory.CreateDirectory(Path.Combine(StagingDir, "wwwroot"));
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        File.WriteAllText(Path.Combine(StagingDir, "wwwroot", "index.html"), "<html/>");
        File.WriteAllText(Path.Combine(AppDir, "a.dll"), "old");

        UpdateStager.CopyDirectory(StagingDir, AppDir);

        Assert.Equal("new", File.ReadAllText(Path.Combine(AppDir, "a.dll")));
        Assert.Equal("<html/>", File.ReadAllText(Path.Combine(AppDir, "wwwroot", "index.html")));
    }

    private const string SelfContainedRuntimeConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"}]}}""";

    private const string FrameworkDependentRuntimeConfig =
        """{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}""";

    /// <summary>Staging đúng layout self-contained: runtimeconfig includedFrameworks + hostfxr cạnh nhau.</summary>
    private void CreateSelfContainedStaging(string runtimeConfig = SelfContainedRuntimeConfig, bool hostfxr = true)
    {
        Directory.CreateDirectory(StagingDir);
        File.WriteAllText(Path.Combine(StagingDir, "router-balancing.runtimeconfig.json"), runtimeConfig);
        if (hostfxr)
            File.WriteAllText(Path.Combine(StagingDir, "hostfxr.dll"), "bin");
    }

    [Fact]
    public void VerifySelfContained_WhenSelfContainedLayout_ReturnsTrue()
    {
        CreateSelfContainedStaging();

        Assert.True(UpdateStager.VerifySelfContained(StagingDir));
    }

    [Fact]
    public void VerifySelfContained_WhenFrameworkDependentRuntimeConfig_ReturnsFalse()
    {
        // Zip FDD ghi đè appDir = bug ".NET missing" — phải chặn trước khi copy
        CreateSelfContainedStaging(runtimeConfig: FrameworkDependentRuntimeConfig);

        Assert.False(UpdateStager.VerifySelfContained(StagingDir));
    }

    [Fact]
    public void VerifySelfContained_WhenHostfxrMissing_ReturnsFalse()
    {
        CreateSelfContainedStaging(hostfxr: false);

        Assert.False(UpdateStager.VerifySelfContained(StagingDir));
    }

    [Fact]
    public void VerifySelfContained_WhenRuntimeConfigMissing_ReturnsFalse()
    {
        Directory.CreateDirectory(StagingDir);
        File.WriteAllText(Path.Combine(StagingDir, "hostfxr.dll"), "bin");

        Assert.False(UpdateStager.VerifySelfContained(StagingDir));
    }

    [Fact]
    public void VerifySelfContained_WhenRuntimeConfigBrokenJson_ReturnsFalse()
    {
        CreateSelfContainedStaging(runtimeConfig: "{not json");

        Assert.False(UpdateStager.VerifySelfContained(StagingDir));
    }
}
