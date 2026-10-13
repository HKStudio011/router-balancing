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
    public void WaitUntilWritable_WhenAllUnlocked_ReturnsTrue()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        File.WriteAllText(Path.Combine(AppDir, "a.dll"), "old");

        var ok = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(5));

        Assert.True(ok);
    }

    [Fact]
    public void WaitUntilWritable_WhenFileLocked_ReturnsFalseAfterTimeout()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "a.dll"), "new");
        var target = Path.Combine(AppDir, "a.dll");
        File.WriteAllText(target, "old");

        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ok = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromMilliseconds(400));

            Assert.False(ok);
        }
    }

    [Fact]
    public async Task WaitUntilWritable_WhenLockReleasedDuringWait_ReturnsTrue()
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

        var ok = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(10));
        await releaser;

        Assert.True(ok);
    }

    [Fact]
    public void WaitUntilWritable_WhenFileOnlyInStaging_IgnoresIt()
    {
        CreateDirs();
        File.WriteAllText(Path.Combine(StagingDir, "brand-new.dll"), "new");

        // File mới chưa có ở appDir — không thể bị lock, không được block
        var ok = UpdateStager.WaitUntilWritable(StagingDir, AppDir, TimeSpan.FromSeconds(5));

        Assert.True(ok);
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
}
