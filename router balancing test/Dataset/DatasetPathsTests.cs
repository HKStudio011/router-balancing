using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Dataset;

public class DatasetPathsTests
{
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Resolve_EmptyOrNull_ReturnsAppDataSubfolder()
    {
        var expected = Path.Combine(StoragePathProvider.GetDataDirectory(), "dataset");
        Assert.Equal(expected, DatasetPaths.Resolve(null));
        Assert.Equal(expected, DatasetPaths.Resolve(""));
        Assert.Equal(expected, DatasetPaths.Resolve("   "));
    }

    [Fact]
    public void Resolve_TildePath_ExpandsUserProfile()
    {
        Assert.Equal(Path.Combine(Profile, "rb-ds"), DatasetPaths.Resolve("~/rb-ds"));
        // Separator ngược (Windows) cũng phải chuẩn hoá về DirectorySeparatorChar như OpenCodeConfigPaths.Expand.
        Assert.Equal(Path.Combine(Profile, "rb-ds"), DatasetPaths.Resolve("~\\rb-ds"));
        Assert.Equal(Profile, DatasetPaths.Resolve("~"));
    }

    [Fact]
    public void Resolve_AbsolutePath_KeptVerbatim()
    {
        var path = Path.Combine(Path.GetTempPath(), "dataset-dir");
        Assert.Equal(path, DatasetPaths.Resolve(path));
    }

    [Fact]
    public void FileName_UsesUtcDayAndJsonlExtension()
    {
        Assert.Equal("requests-2026-11-06.jsonl", DatasetPaths.FileName(new DateOnly(2026, 11, 6)));
    }
}
