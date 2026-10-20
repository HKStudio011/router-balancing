using RouterBalancing.Core.Tools;

namespace router_balancing_test.Tools;

public class OpenCodeConfigPathsTests
{
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void DefaultPath_EndsWithConfigOpencodeJson()
    {
        Assert.Equal(
            Path.Combine(Profile, ".config", "opencode", "opencode.json"),
            OpenCodeConfigPaths.DefaultPath);
    }

    [Fact]
    public void Expand_Empty_ReturnsDefault()
    {
        Assert.Equal(OpenCodeConfigPaths.DefaultPath, OpenCodeConfigPaths.Expand(""));
        Assert.Equal(OpenCodeConfigPaths.DefaultPath, OpenCodeConfigPaths.Expand(null));
        Assert.Equal(OpenCodeConfigPaths.DefaultPath, OpenCodeConfigPaths.Expand("   "));
    }

    [Fact]
    public void Expand_Tilde_ReturnsUserProfile()
    {
        Assert.Equal(Profile, OpenCodeConfigPaths.Expand("~"));
    }

    [Fact]
    public void Expand_TildeRelative_Combines()
    {
        Assert.Equal(
            Path.Combine(Profile, "sub", "x.json"),
            OpenCodeConfigPaths.Expand("~/sub/x.json"));
    }

    [Fact]
    public void Expand_AbsolutePath_Unchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), "opencode.json");
        Assert.Equal(path, OpenCodeConfigPaths.Expand(path));
    }
}
