using RouterBalancing.Core.Localization;

namespace router_balancing_test.Localization;

public class JsonTranslationLoaderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "rb-translations-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteFile(string name, string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_WhenDirectoryMissing_ReturnsEmpty()
    {
        var result = JsonTranslationLoader.Load(Path.Combine(_dir, "nope"));

        Assert.Empty(result);
    }

    [Fact]
    public void Load_WhenFileValid_ReturnsEntriesByLanguageCode()
    {
        WriteFile("ja.json", """{ "nav.dashboard": "ダッシュボード" }""");

        var result = JsonTranslationLoader.Load(_dir);

        Assert.True(result.ContainsKey("ja"));
        Assert.Equal("ダッシュボード", result["ja"]["nav.dashboard"]);
    }

    [Fact]
    public void Load_WhenCodeHasRegionTag_Accepted()
    {
        WriteFile("zh-Hans.json", """{ "nav.dashboard": "仪表板" }""");

        var result = JsonTranslationLoader.Load(_dir);

        Assert.True(result.ContainsKey("zh-Hans"));
    }

    [Theory]
    [InlineData("Japanese.json")]
    [InlineData("JA.json")]
    [InlineData("j.json")]
    public void Load_WhenFileNameNotLanguageCode_SkipsFile(string name)
    {
        WriteFile(name, """{ "a": "b" }""");

        var result = JsonTranslationLoader.Load(_dir);

        Assert.Empty(result);
    }

    [Fact]
    public void Load_WhenJsonBroken_SkipsFileWithoutThrowing()
    {
        WriteFile("fr.json", "{ not valid json");

        var result = JsonTranslationLoader.Load(_dir);

        Assert.Empty(result);
    }

    [Fact]
    public void Load_WhenFileValid_OtherFilesStillLoaded()
    {
        WriteFile("fr.json", "{ broken");
        WriteFile("ja.json", """{ "a": "b" }""");

        var result = JsonTranslationLoader.Load(_dir);

        Assert.True(result.ContainsKey("ja"));
        Assert.False(result.ContainsKey("fr"));
    }
}
