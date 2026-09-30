using System.Text.RegularExpressions;

namespace router_balancing_test.Components;

/// <summary>
/// Guard chống tái phát bug "entity hiện text hỏng": Razor KHÔNG decode HTML entity
/// trong component parameter — <c>Icon="&amp;#128218;"</c> được compile thành chuỗi
/// literal rồi <c>@Icon</c> encode thêm 1 lần → UI hiện <c>&amp;#128218;</c> thay vì emoji
/// (bug trang Combos, 30/09/2026). Chỉ chặn entity trọn vẹn làm giá trị attribute —
/// entity ở text node (✓, &amp;times;) là markup thường, browser tự decode, không chặn.
/// </summary>
public partial class RazorParameterEntityTests
{
    /// <summary>Attribute có dạng <c>tên="&#nnnn;"</c> hoặc nhiều entity liền nhau — tức entity chiếm trọn giá trị.</summary>
    [GeneratedRegex("[\\w:.-]+=\"(?:&#\\d+;)+\"")]
    private static partial Regex WholeValueEntityAttribute();

    [Fact]
    public void RazorAttributes_DoNotUseHtmlEntitiesAsEntireValue()
    {
        var repoRoot = FindRepoRoot();
        var razorRoot = Path.Combine(repoRoot, "router-balancing");

        var violations = Directory.EnumerateFiles(razorRoot, "*.razor", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, index) => (file: f, line, index)))
            .Where(x => WholeValueEntityAttribute().IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(repoRoot, x.file)}:{x.index + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "HTML entity làm giá trị attribute — Razor không decode khi truyền component parameter, "
            + "UI sẽ hiện text hỏng. Dùng ký tự trực tiếp (UTF-8):\n" + string.Join("\n", violations));
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "router-balancing.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Không tìm thấy repo root (router-balancing.slnx) từ {AppContext.BaseDirectory}");
    }
}
