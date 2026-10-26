using System.Text.RegularExpressions;

namespace router_balancing_test.Components;

/// <summary>
/// Guard chống tái phát bug "nút luôn bị disabled" (Providers, 26/10/2026):
/// Razor kết thúc implicit expression tại khoảng trắng đầu tiên, nên
/// <c>disabled="@_busy || _testingAccounts"</c> compile thành chuỗi
/// <c>"False || _testingAccounts"</c>. Thuộc tính boolean chỉ cần xuất hiện là
/// control bị disable — value không được evaluate. Biểu thức nhiều toán hạng
/// bắt buộc bọc trong <c>@(...)</c>.
/// </summary>
public partial class RazorBooleanAttributeTests
{
    /// <summary>
    /// Boolean attribute HTML (lowercase) có value bắt đầu bằng <c>@</c> nhưng không
    /// phải explicit expression <c>@(...)</c>. Chỉ quét attribute HTML lowercase —
    /// component parameter PascalCase (vd. <c>Open="@..."</c>) là chuỗi bình thường.
    /// </summary>
    [GeneratedRegex(
        "(?<![\\w:-])(?:disabled|checked|selected|readonly|required|autofocus|multiple|hidden|open|novalidate|formnovalidate|inert|itemscope|allowfullscreen|async|defer)=\"@(?!\\()(?<value>[^\"]*)\"")]
    private static partial Regex UnparenthesizedBooleanAttribute();

    [Fact]
    public void RazorBooleanAttributes_DoNotUseUnparenthesizedExpressionsContainingWhitespace()
    {
        var repoRoot = FindRepoRoot();
        var razorRoot = Path.Combine(repoRoot, "router-balancing");

        var violations = Directory.EnumerateFiles(razorRoot, "*.razor", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, index) => (file: f, line, index)))
            .SelectMany(x => UnparenthesizedBooleanAttribute().Matches(x.line)
                .Where(m => m.Groups["value"].Value.Any(char.IsWhiteSpace))
                .Select(m => $"{Path.GetRelativePath(repoRoot, x.file)}:{x.index + 1}: {x.line.Trim()}"))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Boolean attribute dùng implicit expression chứa khoảng trắng — Razor cắt biểu thức "
            + "tại khoảng trắng và render phần còn lại thành chuỗi, khiến control luôn bị disabled. "
            + "Bọc toàn bộ biểu thức trong @(...):\n" + string.Join("\n", violations));
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
